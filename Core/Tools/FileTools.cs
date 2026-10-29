using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

public class ReadFileTool : ITool
{
    public string Name => "read_file";

    public JsonObject Definition { get; } = Json.Def("read_file",
        "Read a text file from the PC (code, config, logs, csv, markdown...). Returns the contents, optionally a line range.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path. Environment variables like %USERPROFILE% are expanded." },
            "start_line": { "type": "integer", "description": "1-based first line to return (optional)." },
            "max_lines": { "type": "integer", "description": "Maximum lines to return (optional, default 2000)." }
          },
          "required": ["path"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => "Reading " + input.Str("path");

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Str("path"));
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        var lines = await File.ReadAllLinesAsync(path, ct);
        int start = Math.Max(1, input.Int("start_line", 1));
        int max = Math.Clamp(input.Int("max_lines", 2000), 1, 20000);
        var slice = lines.Skip(start - 1).Take(max);
        var sb = new StringBuilder();
        sb.AppendLine($"{path} ({lines.Length} lines; showing from line {start})");
        sb.Append(string.Join("\n", slice));
        return ToolResult.Text(Json.Truncate(sb.ToString(), 60000));
    }
}

public class WriteFileTool : ITool
{
    public string Name => "write_file";

    public JsonObject Definition { get; } = Json.Def("write_file",
        "Create or overwrite a text file on the PC (or append to it). Parent folders are created automatically.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path. Environment variables are expanded." },
            "content": { "type": "string", "description": "Full text to write." },
            "append": { "type": "boolean", "description": "Append instead of overwrite. Default false." }
          },
          "required": ["path", "content"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => true;
    public string Describe(JsonObject input) =>
        (input.Bool("append") ? "Appending to " : "Writing ") + input.Str("path") + $" ({input.Str("content").Length:N0} chars)";

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Str("path"));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var content = input.Str("content");
        Undo.SnapshotStore.Capture(path, input.Bool("append") ? "write_file (append)" : "write_file");
        if (input.Bool("append")) await File.AppendAllTextAsync(path, content, ct);
        else await File.WriteAllTextAsync(path, content, ct);
        return ToolResult.Text($"Wrote {content.Length:N0} characters to {path}");
    }
}

public class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public JsonObject Definition { get; } = Json.Def("list_directory",
        "List the folders and files in a directory with sizes and modified dates.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute folder path. Environment variables are expanded." },
            "pattern": { "type": "string", "description": "Optional filter such as *.cs" }
          },
          "required": ["path"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => "Listing " + input.Str("path");

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Str("path"));
        if (!Directory.Exists(path)) return Task.FromResult(ToolResult.Error($"Folder not found: {path}"));
        var pattern = input.Str("pattern", "*");
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "*";
        var di = new DirectoryInfo(path);
        var sb = new StringBuilder($"{di.FullName}\n");
        int n = 0;
        foreach (var d in di.EnumerateDirectories().OrderBy(d => d.Name))
        {
            if (++n > 500) break;
            sb.AppendLine($"[dir]  {d.Name}");
        }
        foreach (var f in di.EnumerateFiles(pattern).OrderBy(f => f.Name))
        {
            if (++n > 500) { sb.AppendLine("... (more entries not shown)"); break; }
            sb.AppendLine($"{FormatSize(f.Length),9}  {f.LastWriteTime:yyyy-MM-dd HH:mm}  {f.Name}");
        }
        return Task.FromResult(ToolResult.Text(sb.ToString()));
    }

    static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB"
    };
}
