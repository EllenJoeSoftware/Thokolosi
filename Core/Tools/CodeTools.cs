using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thokolosi.Core.Tools;

/// <summary>Fast recursive search across a code folder (skips bin/obj/node_modules/.git).</summary>
public class SearchFilesTool : ITool
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "packages", ".venv", "venv", "__pycache__", ".next", "Pods" };

    public string Name => "search_files";

    public JsonObject Definition { get; } = Json.Def("search_files",
        "Search a folder tree (e.g. a git repo) for files by name and/or for text inside files. " +
        "Returns matching file paths, or file:line: text for content matches. Skips bin, obj, node_modules, .git and similar.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Root folder to search." },
            "file_pattern": { "type": "string", "description": "File name wildcard, e.g. *.cs or *Controller*. Default *." },
            "regex": { "type": "string", "description": "Regex to find inside files (case-insensitive). Omit to just list files." },
            "max_results": { "type": "integer", "description": "Default 200." }
          },
          "required": ["path"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => $"Searching {input.Str("path")} for {input.Str("regex", input.Str("file_pattern", "*"))}";

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var root = Environment.ExpandEnvironmentVariables(input.Str("path"));
        if (!Directory.Exists(root)) return ToolResult.Error($"Folder not found: {root}");
        var pattern = input.Str("file_pattern", "*");
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "*";
        var rxText = input.Str("regex");
        Regex? rx = string.IsNullOrEmpty(rxText) ? null : new Regex(rxText, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        int max = Math.Clamp(input.Int("max_results", 200), 1, 2000);

        var sb = new StringBuilder();
        int hits = 0, filesScanned = 0;
        foreach (var file in Walk(root, pattern))
        {
            ct.ThrowIfCancellationRequested();
            if (rx == null)
            {
                sb.AppendLine(Path.GetRelativePath(root, file));
                if (++hits >= max) break;
                continue;
            }
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 2_000_000) continue;
                filesScanned++;
                int lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (line.Length > 2000 || !rx.IsMatch(line)) continue;
                    sb.AppendLine($"{Path.GetRelativePath(root, file)}:{lineNo}: {line.Trim()}");
                    if (++hits >= max) break;
                }
            }
            catch { /* binary or locked file */ }
            if (hits >= max) break;
        }
        if (hits == 0) return ToolResult.Text(rx == null ? "No files matched." : $"No matches ({filesScanned} files searched).");
        if (hits >= max) sb.AppendLine($"... stopped at {max} results");
        return ToolResult.Text(Json.Truncate(sb.ToString(), 50000));
    }, ct);

    static IEnumerable<string> Walk(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(dir, pattern); dirs = Directory.GetDirectories(dir); } catch { }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}

/// <summary>Precise find-and-replace edit, the safe way to change code.</summary>
public class EditFileTool : ITool
{
    public string Name => "edit_file";

    public JsonObject Definition { get; } = Json.Def("edit_file",
        "Edit a text file by replacing an exact snippet. old_text must appear exactly once (include surrounding lines to make it unique) " +
        "unless replace_all is true. Prefer this over write_file for changing existing code.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "old_text": { "type": "string", "description": "Exact text currently in the file, including whitespace." },
            "new_text": { "type": "string", "description": "Replacement text." },
            "replace_all": { "type": "boolean" }
          },
          "required": ["path", "old_text", "new_text"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => true;
    public string Describe(JsonObject input) =>
        $"Editing {input.Str("path")}\n--- old\n{Json.Truncate(input.Str("old_text"), 500)}\n+++ new\n{Json.Truncate(input.Str("new_text"), 500)}";

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Str("path"));
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        var text = await File.ReadAllTextAsync(path, ct);
        var oldText = input.Str("old_text");
        var newText = input.Str("new_text");

        // Tolerate the model sending \n when the file uses \r\n.
        if (!text.Contains(oldText) && text.Contains("\r\n") && !oldText.Contains("\r\n"))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        int count = CountOf(text, oldText);
        if (count == 0) return ToolResult.Error("old_text was not found in the file. Read the file again and copy the exact text.");
        if (count > 1 && !input.Bool("replace_all")) return ToolResult.Error($"old_text appears {count} times. Add more surrounding context or set replace_all.");

        var updated = input.Bool("replace_all") ? text.Replace(oldText, newText) : ReplaceFirst(text, oldText, newText);
        Undo.SnapshotStore.Capture(path, "edit_file");
        await File.WriteAllTextAsync(path, updated, ct);
        return ToolResult.Text($"Edited {path} ({count} replacement{(count == 1 ? "" : "s")}).");
    }

    static int CountOf(string s, string sub)
    {
        if (sub.Length == 0) return 0;
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    static string ReplaceFirst(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        return s[..i] + b + s[(i + a.Length)..];
    }
}
