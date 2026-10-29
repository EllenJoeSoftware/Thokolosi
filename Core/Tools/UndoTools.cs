using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;
using Thokolosi.Core.Undo;

namespace Thokolosi.Core.Tools;

/// <summary>Lists and restores file snapshots taken before Thokolosi changed files.</summary>
public class UndoTool : ITool
{
    public string Name => "undo";

    public JsonObject Definition { get; } = Json.Def("undo",
        "Undo file changes Thokolosi made. Files are snapshotted before every write/edit/delete. Actions: list (recent snapshots, " +
        "optional path filter), restore (id), restore_last (most recent snapshot, optionally for a path). A restore is itself undoable.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "restore", "restore_last"] },
            "id": { "type": "string", "description": "restore: snapshot id from list" },
            "path": { "type": "string", "description": "list/restore_last: only snapshots whose original path contains this" },
            "limit": { "type": "integer", "description": "list: how many (default 20)" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") != "list";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "restore" => $"Undo: restoring snapshot {input.Str("id")}",
        "restore_last" => $"Undo: restoring last change{(input.Str("path") is { Length: > 0 } p ? " to " + p : "")}",
        _ => "Undo: listing snapshots"
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var filter = input.Str("path").Trim();
            switch (input.Str("action"))
            {
                case "list":
                {
                    var list = SnapshotStore.List(Math.Clamp(input.Int("limit", 20), 1, 200), filter.Length > 0 ? filter : null);
                    if (list.Count == 0) return ToolResult.Text("No snapshots" + (filter.Length > 0 ? $" for '{filter}'." : "."));
                    var sb = new StringBuilder();
                    foreach (var s in list)
                        sb.AppendLine($"{s.Id} | {s.Time:MM-dd HH:mm:ss} | {s.OriginalPath} | " +
                                      $"{(s.Existed ? $"{s.Size / 1024.0:0.#} KB" : "did not exist")} | {s.Reason}");
                    return ToolResult.Text(Json.Truncate(sb.ToString(), 30000));
                }
                case "restore":
                {
                    var id = input.Str("id").Trim();
                    if (id.Length == 0) return ToolResult.Error("id is required (see action list).");
                    return ToolResult.Text(SnapshotStore.Restore(id));
                }
                case "restore_last":
                {
                    // Skip the safety snapshots that restores themselves create.
                    var last = SnapshotStore.List(50, filter.Length > 0 ? filter : null)
                        .FirstOrDefault(s => !s.Reason.StartsWith("before restoring", StringComparison.Ordinal));
                    if (last == null) return ToolResult.Error("No snapshot to restore" + (filter.Length > 0 ? $" for '{filter}'." : "."));
                    return ToolResult.Text($"[{last.Id}: {last.Reason}] " + SnapshotStore.Restore(last.Id));
                }
                default:
                    return ToolResult.Error("Unknown action.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error(ex.Message);
        }
    }, ct);
}

/// <summary>Deletes files/folders by moving them to the Windows Recycle Bin, so they can be recovered.</summary>
public class RecycleFilesTool : ITool
{
    public string Name => "recycle_files";

    public JsonObject Definition { get; } = Json.Def("recycle_files",
        "Delete files or folders by moving them to the Windows Recycle Bin (recoverable). Prefer this over Remove-Item for deleting user files.",
        """
        {
          "type": "object",
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Absolute file or folder paths. Environment variables are expanded." }
          },
          "required": ["paths"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => true;

    public string Describe(JsonObject input)
    {
        var paths = Paths(input);
        return $"Recycling {paths.Count} item(s):\n" + string.Join("\n", paths.Take(15)) + (paths.Count > 15 ? $"\n... and {paths.Count - 15} more" : "");
    }

    static List<string> Paths(JsonObject input)
    {
        if (input["paths"] is JsonArray a)
            return a.Select(n => n?.ToString() ?? "").Where(s => s.Trim().Length > 0).ToList();
        var single = input.Str("paths");
        return single.Length > 0 ? new List<string> { single } : new List<string>();
    }

    static bool Protected(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
        var trimmed = full.TrimEnd('\\', '/');
        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConfig.Dir,
        };
        if (blocked.Any(b => !string.IsNullOrEmpty(b) && string.Equals(trimmed, b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) return true;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(win) && trimmed.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var paths = Paths(input);
        if (paths.Count == 0) return ToolResult.Error("paths is required.");
        var sb = new StringBuilder();
        int ok = 0, failed = 0;
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim())); }
            catch (Exception ex) { sb.AppendLine($"FAILED {p}: {ex.Message}"); failed++; continue; }
            try
            {
                if (Protected(full)) { sb.AppendLine($"REFUSED {full}: protected system or top-level folder."); failed++; continue; }
                if (File.Exists(full))
                    FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else if (Directory.Exists(full))
                    FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else { sb.AppendLine($"NOT FOUND {full}"); failed++; continue; }
                sb.AppendLine($"Recycled {full}");
                ActivityLog.Write("RECYCLE", full);
                ok++;
            }
            catch (Exception ex) { sb.AppendLine($"FAILED {full}: {ex.Message}"); failed++; }
        }
        sb.Insert(0, $"{ok} moved to the Recycle Bin{(failed > 0 ? $", {failed} not" : "")}.\n");
        return ToolResult.Text(Json.Truncate(sb.ToString(), 20000), ok == 0);
    }, ct);
}
