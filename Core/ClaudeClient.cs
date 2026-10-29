using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core;

public class ClaudeApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Minimal client for the Anthropic Messages API (no SDK dependency).</summary>
public class ClaudeClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    const string Endpoint = "https://api.anthropic.com/v1/messages";

    public async Task<JsonObject> SendAsync(string apiKey, JsonObject body, CancellationToken ct, string source = "chat")
    {
        var result = await SendRawAsync(apiKey, body, ct);
        Brain.UsageTracker.Record(body["model"]?.GetValue<string>() ?? "", result["usage"], source);
        return result;
    }

    async Task<JsonObject> SendRawAsync(string apiKey, JsonObject body, CancellationToken ct)
    {
        var payload = body.ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Add("x-api-key", apiKey);
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
                    return JsonNode.Parse(text)!.AsObject();

                var code = (int)resp.StatusCode;
                bool retryable = code is 408 or 429 or 500 or 502 or 503 or 504 or 529;
                if (retryable && attempt < 4)
                {
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                throw new ClaudeApiException($"Claude API error {code}: {Extract(text)}", resp.StatusCode);
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
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; }
        catch { return body; }
    }
}
