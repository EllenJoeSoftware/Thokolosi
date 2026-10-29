using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Brain;

/// <summary>
/// Long-term memory: durable facts and lessons, one per line in %APPDATA%\Thokolosi\memory.md.
/// The file is plain markdown so the user can read and edit it.
/// </summary>
public static class MemoryStore
{
    static readonly object Gate = new();
    public static string Path => System.IO.Path.Combine(AppConfig.Dir, "memory.md");

    const string Header = "# Thokolosi memory\n\nFacts and lessons Thokolosi keeps between chats. Edit or delete lines freely.\n\n";

    public static List<string> Lines()
    {
        lock (Gate)
        {
            if (!File.Exists(Path)) return new();
            return File.ReadAllLines(Path).Where(l => l.TrimStart().StartsWith("- ")).Select(l => l.Trim()).ToList();
        }
    }

    public static string Add(string fact, string? topic)
    {
        fact = Regex.Replace(fact.Trim(), @"\s+", " ");
        if (fact.Length == 0) return "Nothing to remember.";
        lock (Gate)
        {
            var lines = Lines();
            if (lines.Any(l => l.Contains(fact, StringComparison.OrdinalIgnoreCase))) return "Already remembered.";
            if (!File.Exists(Path)) File.WriteAllText(Path, Header);
            var t = string.IsNullOrWhiteSpace(topic) ? "" : $"({topic.Trim().ToLowerInvariant()}) ";
            File.AppendAllText(Path, $"- [{DateTime.Now:yyyy-MM-dd}] {t}{fact}\n");
        }
        return "Remembered.";
    }

    public static int Forget(string match)
    {
        lock (Gate)
        {
            if (!File.Exists(Path) || string.IsNullOrWhiteSpace(match)) return 0;
            var all = File.ReadAllLines(Path).ToList();
            int removed = all.RemoveAll(l => l.TrimStart().StartsWith("- ") && l.Contains(match, StringComparison.OrdinalIgnoreCase));
            File.WriteAllLines(Path, all);
            return removed;
        }
    }

    /// <summary>Memory text for the system prompt (most recent lines if it's very long).</summary>
    public static string ForPrompt(int maxChars = 8000)
    {
        var lines = Lines();
        if (lines.Count == 0) return "(nothing yet)";
        var sb = new StringBuilder();
        foreach (var l in Enumerable.Reverse(lines))
        {
            if (sb.Length + l.Length > maxChars) { sb.Insert(0, "(older entries omitted - use memory search)\n"); break; }
            sb.Insert(0, l + "\n");
        }
        return sb.ToString().TrimEnd();
    }
}

public class MemoryTool : ITool
{
    public string Name => "memory";

    public JsonObject Definition { get; } = Json.Def("memory",
        "Your long-term memory across chats. Save durable facts about the user, their work, systems, preferences, and lessons learned " +
        "(e.g. 'Staging server is ellenjoe-staging, deploy with deploy.ps1', 'Prefers short replies', 'Invoices go to accounts@client.co.za'). " +
        "Don't save secrets (passwords, keys) or one-off details. Actions: remember, search, forget, list.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["remember", "search", "forget", "list"] },
            "fact": { "type": "string", "description": "remember: one self-contained fact." },
            "topic": { "type": "string", "description": "remember: short topic tag, e.g. work, family, farm, code, preferences." },
            "query": { "type": "string", "description": "search/forget: text to match." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "remember" => "Remembering: " + input.Str("fact"),
        "forget" => "Forgetting: " + input.Str("query"),
        var a => "Memory: " + a + " " + input.Str("query")
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        switch (input.Str("action"))
        {
            case "remember": return Task.FromResult(ToolResult.Text(MemoryStore.Add(input.Str("fact"), input.Str("topic"))));
            case "forget":
                var n = MemoryStore.Forget(input.Str("query"));
                return Task.FromResult(ToolResult.Text(n == 0 ? "No matching memory." : $"Forgot {n} line(s)."));
            case "search":
                var q = input.Str("query");
                var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var hits = MemoryStore.Lines().Where(l => words.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                return Task.FromResult(ToolResult.Text(hits.Count == 0 ? "No matches." : string.Join("\n", hits)));
            default:
                var all = MemoryStore.Lines();
                return Task.FromResult(ToolResult.Text(all.Count == 0 ? "Memory is empty." : Json.Truncate(string.Join("\n", all), 30000)));
        }
    }
}
