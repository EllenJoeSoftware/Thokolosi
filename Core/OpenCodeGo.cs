using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thokolosi.Core;

/// <summary>
/// OpenCode Go (opencode.ai/go): a subscription gateway to open models. Thokolosi addresses them as
/// "opencode-go/&lt;model&gt;". Qwen and MiniMax speak the Anthropic Messages protocol; the rest (Kimi, GLM, DeepSeek,
/// MiMo...) speak OpenAI chat completions, so requests and replies are translated to and from the Anthropic shape
/// the agent loop uses. Both protocols authenticate with "Authorization: Bearer &lt;key&gt;".
/// </summary>
public static class OpenCodeGo
{
    public const string Prefix = "opencode-go/";
    public const string BaseUrl = "https://opencode.ai/zen/go/v1";

    public static bool Handles(string? model) => model != null && model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string RawId(string model) => model[Prefix.Length..];

    // ---------------- live model catalogue ----------------

    /// <summary>One entry of {BaseUrl}/models: which protocol and base URL a model uses, and what it accepts.</summary>
    public record ModelInfo(string Id, string Name, string Api, string BaseUrl, bool Images, bool Tools)
    {
        /// <summary>Protocols Thokolosi can speak: OpenAI chat completions and Anthropic messages.</summary>
        public bool Supported => Api is "chat-completions" or "messages";
    }

    static readonly HttpClient CatalogHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly SemaphoreSlim CatalogGate = new(1, 1);
    static Dictionary<string, ModelInfo>? _catalog;
    static DateTime _catalogAt;

    /// <summary>The gateway's current models (public endpoint, no key needed), cached for an hour.</summary>
    public static async Task<IReadOnlyDictionary<string, ModelInfo>> CatalogAsync(CancellationToken ct, bool refresh = false)
    {
        if (!refresh && _catalog != null && DateTime.UtcNow - _catalogAt < TimeSpan.FromHours(1)) return _catalog;
        await CatalogGate.WaitAsync(ct);
        try
        {
            if (!refresh && _catalog != null && DateTime.UtcNow - _catalogAt < TimeSpan.FromHours(1)) return _catalog;
            var json = JsonNode.Parse(await CatalogHttp.GetStringAsync(BaseUrl + "/models", ct));
            var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in json?["data"] as JsonArray ?? new JsonArray())
            {
                var id = m?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                var api = (m!["api"] as JsonObject)?.FirstOrDefault();
                var inputs = (m["modalities"]?["input"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "") ?? Array.Empty<string>();
                map[id] = new ModelInfo(id,
                    m["name"]?.GetValue<string>() ?? id,
                    api?.Key ?? "chat-completions",
                    api?.Value?["base_url"]?.GetValue<string>()?.TrimEnd('/') ?? BaseUrl,
                    inputs.Any(i => i.StartsWith("image/")),
                    m["capabilities"]?["tools"]?["supported"]?.GetValue<bool>() ?? true);
            }
            _catalog = map;
            _catalogAt = DateTime.UtcNow;
            return map;
        }
        finally { CatalogGate.Release(); }
    }

    /// <summary>
    /// Routing for one model: from the catalogue when reachable; otherwise a best guess (Qwen, MiniMax and Claude
    /// speak messages, the rest chat completions).
    /// </summary>
    public static async Task<ModelInfo> ResolveAsync(string rawId, CancellationToken ct)
    {
        try
        {
            var cat = await CatalogAsync(ct);
            if (cat.TryGetValue(rawId, out var info)) return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* offline catalogue: guess below */ }
        bool Starts(string p) => rawId.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        bool messages = Starts("qwen") || Starts("claude") || (Starts("minimax") && !rawId.Contains("flash", StringComparison.OrdinalIgnoreCase));
        return new ModelInfo(rawId, rawId, messages ? "messages" : "chat-completions", BaseUrl, true, true);
    }

    /// <summary>Models worth offering in Settings: tool-capable ones on a protocol Thokolosi speaks.</summary>
    public static async Task<List<string>> UsableModelsAsync(CancellationToken ct) =>
        (await CatalogAsync(ct, refresh: true)).Values.Where(m => m.Supported && m.Tools)
            .Select(m => Prefix + m.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    const string NoImages = "[image omitted: this model can't view images]";

    // ---------------- Anthropic Messages (Qwen, MiniMax) ----------------

    /// <summary>Same body, minus Anthropic-only extras (server web search, prompt-cache markers).</summary>
    public static JsonObject ToMessagesBody(JsonObject body, bool images = true)
    {
        var b = body.DeepClone().AsObject();
        b["model"] = RawId(body["model"]!.GetValue<string>());
        if (b["tools"] is JsonArray tools)
        {
            var keep = new JsonArray();
            foreach (var t in tools)
            {
                if (t is not JsonObject o || o["input_schema"] == null) continue; // server tools (web_search) aren't available
                o.Remove("cache_control");
                keep.Add(o.DeepClone());
            }
            if (keep.Count > 0) b["tools"] = keep; else b.Remove("tools");
        }
        if (b["system"] is JsonArray sys) foreach (var s in sys) (s as JsonObject)?.Remove("cache_control");
        if (b["messages"] is JsonArray msgs)
            foreach (var m in msgs)
                if (m?["content"] is JsonArray content)
                {
                    // Drop blocks from Claude's server tools that another provider can't replay.
                    for (int i = content.Count - 1; i >= 0; i--)
                    {
                        var type = content[i]?["type"]?.GetValue<string>() ?? "";
                        if (type is "server_tool_use" or "web_search_tool_result" or "thinking" or "redacted_thinking") content.RemoveAt(i);
                        else
                        {
                            (content[i] as JsonObject)?.Remove("cache_control");
                            if (!images) StripImages(content, i);
                        }
                    }
                    if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "(searched the web)" });
                }
        return b;
    }

    /// <summary>Replace an image block (or images inside a tool result) with a short note.</summary>
    static void StripImages(JsonArray content, int i)
    {
        var block = content[i];
        if (block?["type"]?.GetValue<string>() == "image") { content[i] = new JsonObject { ["type"] = "text", ["text"] = NoImages }; return; }
        if (block?["content"] is JsonArray inner)
            for (int j = 0; j < inner.Count; j++)
                if (inner[j]?["type"]?.GetValue<string>() == "image") inner[j] = new JsonObject { ["type"] = "text", ["text"] = NoImages };
    }

    // ---------------- OpenAI chat completions (Kimi, GLM, DeepSeek, MiMo...) ----------------

    public static JsonObject ToChatBody(JsonObject body, bool images = true)
    {
        _images = images;
        var messages = new JsonArray();
        var system = SystemText(body["system"]);
        if (system.Length > 0) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (var m in body["messages"] as JsonArray ?? new JsonArray())
        {
            var role = m?["role"]?.GetValue<string>() ?? "user";
            var content = m?["content"];
            if (content is JsonValue v && v.TryGetValue<string>(out var plain))
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = plain });
                continue;
            }
            var blocks = content as JsonArray ?? new JsonArray();
            if (role == "assistant") messages.Add(AssistantMessage(blocks));
            else AddUserBlocks(messages, blocks);
        }

        var result = new JsonObject
        {
            ["model"] = RawId(body["model"]!.GetValue<string>()),
            ["messages"] = messages,
            ["max_tokens"] = body["max_tokens"]?.DeepClone() ?? 8192,
            ["stream"] = false,
        };
        var tools = new JsonArray();
        foreach (var t in body["tools"] as JsonArray ?? new JsonArray())
        {
            if (t is not JsonObject o || o["input_schema"] == null) continue; // skip server tools
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = o["name"]?.DeepClone(),
                    ["description"] = o["description"]?.DeepClone() ?? "",
                    ["parameters"] = o["input_schema"]!.DeepClone(),
                }
            });
        }
        if (tools.Count > 0) result["tools"] = tools;
        return result;
    }

    static string SystemText(JsonNode? system) => system switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join("\n\n", a.Select(b => b?["text"]?.GetValue<string>()).Where(t => !string.IsNullOrEmpty(t))),
        _ => ""
    };

    static JsonObject AssistantMessage(JsonArray blocks)
    {
        var text = new List<string>();
        var calls = new JsonArray();
        foreach (var b in blocks)
        {
            switch (b?["type"]?.GetValue<string>())
            {
                case "text": text.Add(b["text"]?.GetValue<string>() ?? ""); break;
                case "tool_use":
                    calls.Add(new JsonObject
                    {
                        ["id"] = SafeId(b["id"]?.GetValue<string>()),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = b["name"]?.GetValue<string>(),
                            ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString(),
                        }
                    });
                    break;
                // server_tool_use / web_search_tool_result / thinking from Claude turns: nothing to replay here
            }
        }
        var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Count > 0 ? string.Join("\n", text) : null };
        if (calls.Count > 0) msg["tool_calls"] = calls;
        else if (text.Count == 0) msg["content"] = "(done)";
        return msg;
    }

    /// <summary>
    /// A user turn may hold tool results (each becomes a "tool" message, which must come straight after the
    /// assistant's tool calls) plus ordinary text/images (one "user" message after them).
    /// </summary>
    static void AddUserBlocks(JsonArray messages, JsonArray blocks)
    {
        var parts = new JsonArray();
        var resultImages = new List<JsonNode>();
        foreach (var b in blocks)
        {
            switch (b?["type"]?.GetValue<string>())
            {
                case "tool_result":
                    var (text, images) = Flatten(b["content"]);
                    if (b["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isErr) && isErr) text = "ERROR: " + text;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = SafeId(b["tool_use_id"]?.GetValue<string>()),
                        ["content"] = text.Length == 0 ? "(no output)" : text,
                    });
                    foreach (var img in images) resultImages.Add(img);
                    break;
                case "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = b["text"]?.GetValue<string>() ?? "" });
                    break;
                case "image":
                    if (ImagePart(b) is { } p) parts.Add(p);
                    break;
            }
        }
        // Screenshots returned by tools can't ride on a tool message; show them in a following user message.
        if (resultImages.Count > 0)
        {
            var lead = new List<JsonNode> { new JsonObject { ["type"] = "text", ["text"] = "(Image(s) returned by the tool call above:)" } };
            lead.AddRange(resultImages);
            for (int i = 0; i < lead.Count; i++) parts.Insert(i, lead[i]);
        }
        if (parts.Count == 0) return;
        if (parts.All(p => p?["type"]?.GetValue<string>() == "text"))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", parts.Select(p => p!["text"]!.GetValue<string>())) });
        else
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
    }

    static (string Text, List<JsonNode> Images) Flatten(JsonNode? content)
    {
        var images = new List<JsonNode>();
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return (s, images);
        var text = new List<string>();
        foreach (var b in content as JsonArray ?? new JsonArray())
        {
            var type = b?["type"]?.GetValue<string>();
            if (type == "text") text.Add(b!["text"]?.GetValue<string>() ?? "");
            else if (type == "image" && ImagePart(b) is { } p)
            {
                if (p["type"]?.GetValue<string>() == "text") text.Add(NoImages); else images.Add(p);
            }
        }
        return (string.Join("\n", text), images);
    }

    [ThreadStatic] static bool _images;

    static JsonObject? ImagePart(JsonNode? block)
    {
        if (!_images) return new JsonObject { ["type"] = "text", ["text"] = NoImages };
        var src = block?["source"];
        if (src?["type"]?.GetValue<string>() != "base64") return null;
        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = $"data:{src["media_type"]?.GetValue<string>()};base64,{src["data"]?.GetValue<string>()}" }
        };
    }

    /// <summary>Converts a chat-completions reply into the Anthropic Messages shape the agent expects.</summary>
    public static JsonObject FromChatResponse(JsonObject resp, string model)
    {
        var choice = (resp["choices"] as JsonArray)?.FirstOrDefault();
        var msg = choice?["message"];
        var content = new JsonArray();

        var text = msg?["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a => string.Concat(a.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => ""
        };
        // Some reasoning models wrap their thinking in <think> tags inside the content.
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        int n = 0;
        foreach (var c in msg?["tool_calls"] as JsonArray ?? new JsonArray())
        {
            var fn = c?["function"];
            JsonNode input;
            var args = fn?["arguments"];
            try
            {
                input = args is JsonValue av && av.TryGetValue<string>(out var json)
                    ? (string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) ?? new JsonObject())
                    : args?.DeepClone() ?? new JsonObject();
            }
            catch (JsonException) { input = new JsonObject { ["_unparsed_arguments"] = args?.ToString() }; }
            if (input is not JsonObject) input = new JsonObject { ["value"] = input };
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = SafeId(c?["id"]?.GetValue<string>() ?? $"call_{n}"),
                ["name"] = fn?["name"]?.GetValue<string>() ?? "",
                ["input"] = input,
            });
            n++;
        }

        var finish = choice?["finish_reason"]?.GetValue<string>();
        var stop = n > 0 ? "tool_use" : finish switch { "length" => "max_tokens", _ => "end_turn" };
        var u = resp["usage"];
        long Num(string k) => u?[k] is JsonValue x && x.TryGetValue<long>(out var l) ? l : 0;
        return new JsonObject
        {
            ["id"] = resp["id"]?.DeepClone(),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = stop,
            ["usage"] = new JsonObject { ["input_tokens"] = Num("prompt_tokens"), ["output_tokens"] = Num("completion_tokens") },
        };
    }

    /// <summary>Tool-call ids must suit both providers (Anthropic allows only letters, digits, _ and -).</summary>
    static string SafeId(string? id)
    {
        var s = Regex.Replace(id ?? "", @"[^A-Za-z0-9_-]", "_");
        return s.Length == 0 ? "call_" + Guid.NewGuid().ToString("N")[..12] : s;
    }
}
