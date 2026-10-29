using System.IO;

namespace Thokolosi.Core;

/// <summary>
/// Every action Thokolosi takes is written to %APPDATA%\Thokolosi\logs\activity-YYYYMMDD.log,
/// so silent-mode work can always be audited afterwards.
/// </summary>
public static class ActivityLog
{
    static readonly object Gate = new();

    public static void Write(string kind, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.LogDir);
                var path = Path.Combine(AppConfig.LogDir, $"activity-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {kind,-9} {text.Replace("\r", "").Replace("\n", "\n                     ")}\n");
            }
        }
        catch { /* logging must never break the agent */ }
    }
}
