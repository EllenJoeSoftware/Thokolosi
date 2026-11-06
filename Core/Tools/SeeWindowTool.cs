using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using WinForms = System.Windows.Forms;

namespace Thokolosi.Core.Tools;

/// <summary>Lists the visible top-level windows and captures a single window (even if another window covers it).</summary>
public static class OpenWindows
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public record Win(IntPtr Handle, string App, string Title, bool Minimized);

    /// <summary>Visible, non-cloaked windows that have a title (taskbar-style list), front to back.</summary>
    public static List<Win> List()
    {
        var res = new List<Win>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                DwmGetWindowAttribute(h, 14, out int cloaked, sizeof(int));   // DWMWA_CLOAKED: hidden UWP/virtual-desktop windows
                if (cloaked != 0) return true;
                var title = Native.WindowTitle(h);
                if (string.IsNullOrWhiteSpace(title) || title is "Program Manager" or "Windows Input Experience") return true;
                res.Add(new Win(h, Native.ProcessName(h), title, IsIconic(h)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    public static Win? Find(string query)
    {
        var all = List();
        return all.FirstOrDefault(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.App.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>JPEG of one window. Uses PrintWindow so a covered window still renders; falls back to a screen copy.</summary>
    public static byte[]? Capture(IntPtr h, int maxEdge, out string note)
    {
        note = "";
        if (IsIconic(h)) { note = "The window is minimized, so it can't be captured as an image (its text may still be readable)."; return null; }
        if (!GetWindowRect(h, out var r)) return null;
        int w = r.R - r.L, ht = r.B - r.T;
        if (w < 20 || ht < 20) return null;

        using var bmp = new Bitmap(w, ht);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(h, hdc, 2); }            // 2 = PW_RENDERFULLCONTENT (works for Chrome/Electron/UWP)
            finally { g.ReleaseHdc(hdc); }
        }
        if (LooksBlank(bmp))
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, ht));
            note = "This window could not be rendered directly, so this is a copy of that area of the screen (windows on top may appear).";
        }

        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(w, ht));
        int sw = Math.Max(1, (int)(w * scale)), sh = Math.Max(1, (int)(ht * scale));
        using var small = new Bitmap(sw, sh);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, sw, sh);
        }
        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
        small.Save(ms, jpeg, ep);
        return ms.ToArray();
    }

    static bool LooksBlank(Bitmap b)
    {
        int nonBlack = 0, samples = 0;
        for (int y = 5; y < b.Height; y += Math.Max(1, b.Height / 12))
            for (int x = 5; x < b.Width; x += Math.Max(1, b.Width / 12))
            {
                samples++;
                var p = b.GetPixel(x, y);
                if (p.R + p.G + p.B > 12) nonBlack++;
            }
        return samples > 0 && nonBlack == 0;
    }
}

/// <summary>Look inside any open window: read its on-screen text (UI Automation) and/or see a picture of just that window.</summary>
public class SeeWindowTool : ITool
{
    public string Name => "see_window";

    public JsonObject Definition { get; } = Json.Def("see_window",
        "Look inside an open window on the user's PC without opening or focusing anything. Modes: list (all open windows), text (the visible text and fields in " +
        "the window: page text, chat messages, document lines, tabs, buttons), screenshot (a picture of just that window, even if another window is covering it), " +
        "both (default). Use it when the user asks what is in a window, what they are reading or working on, or to read a chat, page or document on screen. " +
        "Windows on the user's 'never look at' list (password managers, banking) are refused. Read-only; to click or type use the desktop tool.",
        """
        {
          "type": "object",
          "properties": {
            "window": { "type": "string", "description": "Part of the window title or process name, e.g. 'Slack', 'Chrome', 'Code'. Not needed for mode list." },
            "mode": { "type": "string", "enum": ["both", "text", "screenshot", "list"] },
            "max_chars": { "type": "integer", "description": "Text limit, default 6000." }
          }
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;   // read-only, same as screenshot_screen

    public string Describe(JsonObject input) => input.Str("mode") == "list" ? "Listing open windows" : $"Looking inside {input.Str("window")}";

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => StaRunner.Run(() => Execute(input, ctx));

    static bool Excluded(AppConfig cfg, string app, string title) =>
        cfg.Watcher.Exclude.Any(x => !string.IsNullOrWhiteSpace(x) &&
            (app.Contains(x, StringComparison.OrdinalIgnoreCase) || title.Contains(x, StringComparison.OrdinalIgnoreCase)));

    static ToolResult Execute(JsonObject input, ToolContext ctx)
    {
        var mode = input.Str("mode", "both");
        if (mode == "list")
        {
            var sb = new StringBuilder();
            foreach (var w in OpenWindows.List())
                sb.AppendLine($"{w.App}: {(Excluded(ctx.Config, w.App, w.Title) ? "(private)" : w.Title)}{(w.Minimized ? " (minimized)" : "")}");
            return ToolResult.Text(sb.ToString());
        }

        var query = input.Str("window").Trim();
        if (query.Length == 0) return ToolResult.Error("window is required (part of the title or app name). Use mode list to see what is open.");
        var win = OpenWindows.Find(query);
        if (win == null) return ToolResult.Error($"No open window matches '{query}'. Use mode list to see what is open.");
        if (Excluded(ctx.Config, win.App, win.Title))
            return ToolResult.Error("That window is on the user's 'never look at' list (Settings > Watch & schedule), so I won't look inside it.");

        int max = Math.Clamp(input.Int("max_chars", 6000), 500, 30000);
        var result = ToolResult.Text($"Window: {win.App}: {win.Title}{(win.Minimized ? " (minimized)" : "")}");

        if (mode is "both" or "text")
        {
            string text;
            try { text = ReadText(AutomationElement.FromHandle(win.Handle), max); }
            catch (Exception ex) { text = "(could not read text: " + ex.Message + ")"; }
            result.Content.Add(new JsonObject { ["type"] = "text", ["text"] = "Visible text and controls:\n" + (text.Length == 0 ? "(none found)" : text) });
        }
        if (mode is "both" or "screenshot")
        {
            var jpg = OpenWindows.Capture(win.Handle, 1568, out var note);
            if (jpg != null) result.AddImage(jpg, "image/jpeg");
            if (note.Length > 0) result.Content.Add(new JsonObject { ["type"] = "text", ["text"] = note });
        }
        return result;
    }

    static string ReadText(AutomationElement root, int max)
    {
        var sb = new StringBuilder();
        string last = "";
        foreach (var e in DesktopTool.Controls(root, 3000))
        {
            string? line = null;
            try
            {
                var ct = e.Current.ControlType;
                var name = (e.Current.Name ?? "").Trim();
                if (ct == ControlType.Edit || ct == ControlType.Document)
                {
                    string? val = null;
                    if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                        val = ((TextPattern)tp).DocumentRange.GetText(2000);
                    else if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                        val = ((ValuePattern)vp).Current.Value;
                    if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        line = $"[{ct.ProgrammaticName.Replace("ControlType.", "")}{(name.Length > 0 ? " " + name : "")}] {val.Trim()}";
                }
                else if (ct == ControlType.Text || ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.Hyperlink ||
                         ct == ControlType.TabItem || ct == ControlType.Header || ct == ControlType.HeaderItem || ct == ControlType.TreeItem ||
                         ct == ControlType.CheckBox || ct == ControlType.RadioButton)
                    line = name;
                else if (ct == ControlType.Button && name.Length is > 0 and <= 40)
                    line = "(button) " + name;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(line) || line == last) continue;
            last = line;
            sb.AppendLine(line.Length > 400 ? line[..400] + "..." : line);
            if (sb.Length >= max) { sb.AppendLine("... [truncated; ask for more with max_chars or a more specific window]"); break; }
        }
        return sb.ToString().TrimEnd();
    }
}
