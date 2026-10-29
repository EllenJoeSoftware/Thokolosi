using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thokolosi.Core.Undo;

public class SnapshotInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("originalPath")] public string OriginalPath { get; set; } = "";
    [JsonPropertyName("existed")] public bool Existed { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>File name of the saved copy inside the snapshot folder (null when the file didn't exist).</summary>
    [JsonPropertyName("file")] public string? File { get; set; }
}

/// <summary>
/// Copies a file aside before Thokolosi overwrites, edits or deletes it, so the change can be undone.
/// %APPDATA%\Thokolosi\snapshots\{yyyyMMdd-HHmmss-fff}-{shortid}\ holds the copy and manifest.json.
/// Keeps 30 days and at most 2 GB (oldest pruned first). Files over 50 MB are not snapshotted.
/// </summary>
public static class SnapshotStore
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    static DateTime _lastPrune = DateTime.MinValue;

    public static string Root => Path.Combine(AppConfig.Dir, "snapshots");

    /// <summary>
    /// Snapshot the current state of <paramref name="path"/> (also records "didn't exist", so a restore deletes a newly created file).
    /// Returns the snapshot id, or null if it was skipped (directory, too large, unreadable). Never throws.
    /// </summary>
    public static string? Capture(string path, string reason)
    {
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (Directory.Exists(full)) return null;
            if (full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return null;
            var fi = new FileInfo(full);
            if (fi.Exists && fi.Length > MaxFileBytes)
            {
                ActivityLog.Write("SNAPSHOT", $"Skipped {full} ({fi.Length / 1048576} MB > 50 MB limit)");
                return null;
            }

            var now = DateTime.Now;
            var id = $"{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
            var dir = Path.Combine(Root, id);
            Directory.CreateDirectory(dir);
            var info = new SnapshotInfo { Id = id, Time = now, OriginalPath = full, Existed = fi.Exists, Reason = reason ?? "" };
            if (fi.Exists)
            {
                info.File = string.IsNullOrEmpty(fi.Name) ? "content" : fi.Name;
                // FileShare.ReadWrite so files open in other apps (logs, Office autosave) can still be copied.
                using (var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(Path.Combine(dir, info.File), FileMode.CreateNew, FileAccess.Write))
                    src.CopyTo(dst);
                info.Size = fi.Length;
                try { File.SetLastWriteTime(Path.Combine(dir, info.File), fi.LastWriteTime); } catch { }
            }
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(info, JsonOpts));
            ActivityLog.Write("SNAPSHOT", $"{id} {(info.Existed ? "saved" : "noted new file")} {full} ({reason})");
            MaybePrune();
            return id;
        }
        catch (Exception ex)
        {
            ActivityLog.Write("SNAPSHOT", $"Capture failed for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Most recent snapshots first. Optional case-insensitive substring filter on the original path.</summary>
    public static List<SnapshotInfo> List(int n = 20, string? pathFilter = null)
    {
        var list = new List<SnapshotInfo>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var info = Read(dir);
            if (info == null) continue;
            if (!string.IsNullOrEmpty(pathFilter) && !info.OriginalPath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(info);
            if (list.Count >= n) break;
        }
        return list;
    }

    public static SnapshotInfo? Get(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
        var dir = Path.Combine(Root, id);
        return Directory.Exists(dir) ? Read(dir) : null;
    }

    /// <summary>
    /// Puts the file back as it was (or deletes it if it didn't exist at snapshot time). The current state is
    /// snapshotted first, so a restore can itself be undone. Returns a short description; throws on failure.
    /// </summary>
    public static string Restore(string id)
    {
        var info = Get(id) ?? throw new ArgumentException($"Snapshot '{id}' not found.");
        lock (Gate)
        {
            var target = info.OriginalPath;
            var before = Capture(target, $"before restoring {info.Id}");
            var undoNote = before != null ? $" (previous state saved as {before})" : "";
            if (!info.Existed)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    return $"Deleted {target} (it didn't exist at {info.Time:yyyy-MM-dd HH:mm:ss}){undoNote}.";
                }
                return $"{target} already doesn't exist; nothing to do.";
            }
            var copy = Path.Combine(Root, info.Id, info.File ?? "content");
            if (!File.Exists(copy)) throw new FileNotFoundException($"Snapshot copy is missing: {copy}");
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(copy, target, overwrite: true);
            return $"Restored {target} to its state at {info.Time:yyyy-MM-dd HH:mm:ss}{undoNote}.";
        }
    }

    static SnapshotInfo? Read(string dir)
    {
        try
        {
            var m = Path.Combine(dir, "manifest.json");
            return File.Exists(m) ? JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(m)) : null;
        }
        catch { return null; }
    }

    static void MaybePrune()
    {
        lock (Gate)
        {
            if (DateTime.Now - _lastPrune < TimeSpan.FromMinutes(10)) return;
            _lastPrune = DateTime.Now;
        }
        try { Prune(); } catch { }
    }

    /// <summary>Deletes snapshots older than 30 days, then the oldest until the total is under 2 GB.</summary>
    public static void Prune()
    {
        if (!Directory.Exists(Root)) return;
        var cutoff = DateTime.Now - MaxAge;
        var dirs = Directory.EnumerateDirectories(Root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(d => (Dir: d, Info: Read(d), Bytes: DirBytes(d))).ToList();
        long total = dirs.Sum(d => d.Bytes);
        foreach (var d in dirs)
        {
            var time = d.Info?.Time ?? Directory.GetCreationTime(d.Dir);
            if (time >= cutoff && total <= MaxTotalBytes) break; // sorted oldest first
            try { Directory.Delete(d.Dir, true); total -= d.Bytes; } catch { }
        }
    }

    static long DirBytes(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length); } catch { return 0; }
    }
}
