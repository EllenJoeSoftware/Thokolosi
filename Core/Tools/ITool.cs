using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

public interface ITool
{
    string Name { get; }

    /// <summary>Tool definition sent to Claude: name, description, input_schema.</summary>
    JsonObject Definition { get; }

    /// <summary>True if this call changes something and needs approval in Ask mode.</summary>
    bool NeedsApproval(JsonObject input);

    /// <summary>One-line human description shown in the chat activity feed.</summary>
    string Describe(JsonObject input);

    Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct);
}

public class ToolContext(AppConfig config)
{
    public AppConfig Config { get; } = config;
    public bool Silent => Config.SilentMode;
}

public class ToolResult
{
    public JsonArray Content { get; } = new();
    public bool IsError { get; init; }

    public static ToolResult Text(string text, bool isError = false)
    {
        var r = new ToolResult { IsError = isError };
        r.Content.Add(new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(text) ? "(no output)" : text });
        return r;
    }

    public static ToolResult Error(string text) => Text(text, true);

    static readonly HashSet<string> ImageTypes = new() { "image/jpeg", "image/png", "image/gif", "image/webp" };

    public ToolResult AddImage(byte[] data, string mediaType)
    {
        // Claude only accepts these image types and up to ~5 MB; anything else would break the conversation.
        if (!ImageTypes.Contains(mediaType) || data.Length > 3_700_000)
        {
            Content.Add(new JsonObject { ["type"] = "text", ["text"] = $"[image omitted: {mediaType}, {data.Length / 1024} KB]" });
            return this;
        }
        Content.Add(new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = mediaType,
                ["data"] = Convert.ToBase64String(data)
            }
        });
        return this;
    }
}

public static class Json
{
    public static JsonObject Def(string name, string description, string schemaJson) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = JsonNode.Parse(schemaJson)
    };

    public static string Str(this JsonObject o, string key, string fallback = "")
    {
        var n = o[key];
        if (n is null) return fallback;
        try { return n.GetValue<string>(); } catch { return n.ToJsonString(); }
    }

    public static int Int(this JsonObject o, string key, int fallback)
    {
        var n = o[key];
        if (n is null) return fallback;
        try { return n.GetValue<int>(); } catch { }
        try { return (int)n.GetValue<double>(); } catch { }
        return int.TryParse(n.ToString(), out var v) ? v : fallback;
    }

    public static bool Bool(this JsonObject o, string key, bool fallback = false)
    {
        var n = o[key];
        if (n is null) return fallback;
        try { return n.GetValue<bool>(); } catch { }
        return bool.TryParse(n.ToString(), out var v) ? v : fallback;
    }

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n... [truncated {s.Length - max:N0} more characters]";
}
