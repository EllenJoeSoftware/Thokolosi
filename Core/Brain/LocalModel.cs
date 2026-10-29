using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Brain;

public class LocalModelSettings
{
    /// <summary>Use a local model (Ollama) when Claude is unreachable, or when the budget cap says "local".</summary>
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.2";
}

/// <summary>
/// Offline fallback through Ollama (ollama.com) running on this PC. It answers from the model's own knowledge plus
/// the recent chat; it can't use Thokolosi's tools, so it says so when a task needs them.
/// </summary>
public static class LocalModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<string> ChatAsync(LocalModelSettings s, string system, JsonArray claudeMessages, CancellationToken ct)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        // Keep only plain text from the last few turns.
        foreach (var m in claudeMessages.TakeLast(12))
        {
            var role = m?["role"]?.GetValue<string>();
            var text = string.Join("\n", (m?["content"] as JsonArray ?? new JsonArray())
                .Where(b => b?["type"]?.GetValue<string>() == "text")
                .Select(b => b!["text"]!.GetValue<string>()));
            if (string.IsNullOrWhiteSpace(text) || role == null) continue;
            msgs.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }
        var body = new JsonObject { ["model"] = s.Model, ["messages"] = msgs, ["stream"] = false };
        using var resp = await Http.PostAsync(s.Url.TrimEnd('/') + "/api/chat",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("Local model error: " + (json?["error"]?.GetValue<string>() ?? resp.StatusCode.ToString()));
        return json?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    public static async Task<bool> AvailableAsync(LocalModelSettings s)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var r = await Http.GetAsync(s.Url.TrimEnd('/') + "/api/tags", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
