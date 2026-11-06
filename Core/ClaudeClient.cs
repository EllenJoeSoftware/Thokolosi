using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core;

public class ClaudeApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Minimal client for the Anthropic Messages API (no SDK dependency). Models named "opencode-go/..." are sent to
/// OpenCode Go instead (see <see cref="OpenCodeGo"/>), and come back in the same Anthropic shape.
/// </summary>
public class ClaudeClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    const string Endpoint = "https://api.anthropic.com/v1/messages";

    /// <summary>Supplies the OpenCode Go key (set once at startup from the config).</summary>
    public static Func<string?> OpenCodeKey { get; set; } = () => null;

    /// <summary>
    /// Conversation id sent to OpenCode Go as x-opencode-session (required; it keeps prompt caching per conversation).
    /// Each client gets its own; the agent renews it when a new chat starts.
    /// </summary>
    public string SessionId { get; set; } = NewSessionId();

    public static string NewSessionId() => "thokolosi-" + Guid.NewGuid().ToString("N");

    static readonly string UserAgent = "Thokolosi/" + (typeof(ClaudeClient).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    public async Task<JsonObject> SendAsync(string apiKey, JsonObject body, CancellationToken ct, string source = "chat")
    {
        var result = await SendRawAsync(apiKey, body, ct);
        Brain.UsageTracker.Record(body["model"]?.GetValue<string>() ?? "", result["usage"], source);
        return result;
    }

    async Task<JsonObject> SendRawAsync(string apiKey, JsonObject body, CancellationToken ct)
    {
        var model = body["model"]?.GetValue<string>() ?? "";
        var openCode = OpenCodeGo.Handles(model);
        bool chat = false;
        string endpoint = Endpoint, provider = "Claude API";
        if (openCode)
        {
            apiKey = OpenCodeKey() ?? throw new ClaudeApiException("No OpenCode Go API key set. Add it in Settings > General.");
            var info = await OpenCodeGo.ResolveAsync(OpenCodeGo.RawId(model), ct);
            if (!info.Supported)
                throw new ClaudeApiException($"{model} uses OpenCode's '{info.Api}' API, which Thokolosi can't speak yet. Pick another opencode-go model (Kimi, GLM, DeepSeek, Qwen, MiniMax, MiMo...).");
            chat = info.Api == "chat-completions";
            body = chat ? OpenCodeGo.ToChatBody(body, info.Images) : OpenCodeGo.ToMessagesBody(body, info.Images);
            endpoint = info.BaseUrl + (chat ? "/chat/completions" : "/messages");
            provider = "OpenCode Go";
        }
        var payload = body.ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (openCode)
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                req.Headers.Add("x-opencode-session", SessionId);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            }
            else req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = JsonNode.Parse(text)!.AsObject();
                    if (chat && json["error"] != null) throw new ClaudeApiException($"{provider} error: {Extract(text)}");
                    return chat ? OpenCodeGo.FromChatResponse(json, model) : json;
                }

                var code = (int)resp.StatusCode;
                bool retryable = code is 408 or 429 or 500 or 502 or 503 or 504 or 529;
                if (retryable && attempt < 4)
                {
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                throw new ClaudeApiException($"{provider} error {code}: {Extract(text)}", resp.StatusCode);
            }
        }
    }

    /// <summary>One-shot call without tools; returns the reply text.</summary>
    public async Task<string> CompleteAsync(string apiKey, string model, string system, JsonArray userContent, int maxTokens, CancellationToken ct, string source = "background")
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = userContent } }
        };
        var resp = await SendAsync(apiKey, body, ct, source);
        return string.Concat((resp["content"] as JsonArray ?? new JsonArray())
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>()));
    }

    static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

    static string Extract(string body)
    {
        try
        {
            var e = JsonNode.Parse(body)?["error"];
            return (e is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : e?["message"]?.GetValue<string>()) ?? body;
        }
        catch { return body; }
    }
}
