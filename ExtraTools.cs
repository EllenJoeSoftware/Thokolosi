using Thokolosi.Core;
using Thokolosi.Core.Monitors;
using Thokolosi.Core.Tools;
using Thokolosi.Core.Undo;

namespace Thokolosi;

/// <summary>Wires up the document, Facebook, monitor, timesheet and undo features.</summary>
public static class ExtraTools
{
    static MonitorService? _monitors;

    public static MonitorService? Monitors => _monitors;

    public static void Register(App app)
    {
        var cfg = app.Config;
        _monitors = new MonitorService(() => cfg.Monitors, (title, text) => app.Notify(title, text));
        foreach (var t in new ITool[]
        {
            new DocumentTool(),
            new FacebookTool(() => cfg.Facebook),
            new TimesheetTool(() => cfg.CodeFolders),
            new MonitorsTool(_monitors),
            new UndoTool(),
            new RecycleFilesTool(),
        })
            app.Tools.Add(t);
        _ = Task.Run(SnapshotStore.Prune);
    }

    public static void ReloadMonitors() => _monitors?.Reload();

    /// <summary>Undo the latest file change Thokolosi made (or a specific snapshot).</summary>
    public static string UndoLast(string? id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                var last = SnapshotStore.List(20).FirstOrDefault(s => !s.Reason.StartsWith("before restoring", StringComparison.OrdinalIgnoreCase));
                if (last == null) return "Nothing to undo.";
                id = last.Id;
            }
            return SnapshotStore.Restore(id);
        }
        catch (Exception ex) { return "Undo failed: " + ex.Message; }
    }

    public static void Shutdown() => _monitors?.Dispose();
}
