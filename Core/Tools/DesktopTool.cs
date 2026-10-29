using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using WinForms = System.Windows.Forms;

namespace Thokolosi.Core.Tools;

/// <summary>
/// Drives any desktop app through Windows UI Automation: list windows, see a window's buttons/fields,
/// click, type and press keys. Works for Teams, Slack desktop, Outlook, Explorer, most apps.
/// </summary>
public class DesktopTool : ITool
{
    public string Name => "desktop";

    public JsonObject Definition { get; } = Json.Def("desktop",
        "Control desktop apps on this PC with Windows UI Automation. " +
        "Actions: windows (list open windows), focus (bring a window to front), inspect (list a window's controls: type, name, id, state), " +
        "click (click/invoke/toggle a control by name), type (enter text into a control or the focused field), keys (send keystrokes, SendKeys syntax: ^c = Ctrl+C, %{F4} = Alt+F4, {ENTER}, {TAB}). " +
        "Typical flow: windows -> inspect window -> click/type. Use screenshot_screen when a control has no name.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["windows", "focus", "inspect", "click", "type", "keys"] },
            "window": { "type": "string", "description": "Part of the window title or process name, e.g. 'Teams', 'Notepad'." },
            "name": { "type": "string", "description": "click/type: part of the control's name (or its automation id)." },
            "control_type": { "type": "string", "description": "Optional filter: Button, Edit, CheckBox, MenuItem, ListItem, TabItem, Hyperlink..." },
            "index": { "type": "integer", "description": "If several controls match, which one (0-based). Default 0." },
            "filter": { "type": "string", "description": "inspect: only list controls whose name contains this." },
            "text": { "type": "string", "description": "type: text to enter." },
            "keys": { "type": "string", "description": "keys: SendKeys string." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "click" or "type" or "keys";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "click" => $"Desktop: clicking '{input.Str("name")}' in {input.Str("window")}",
        "type" => $"Desktop: typing into {input.Str("window")} {input.Str("name")}: {Json.Truncate(input.Str("text"), 200)}",
        "keys" => $"Desktop: pressing {input.Str("keys")}",
        "inspect" => $"Desktop: inspecting {input.Str("window")}",
        "focus" => $"Desktop: switching to {input.Str("window")}",
        _ => "Desktop: listing windows"
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => StaRunner.Run(() => Execute(input));

    static ToolResult Execute(JsonObject input)
    {
        switch (input.Str("action"))
        {
            case "windows":
            {
                var sb = new StringBuilder();
                foreach (var w in TopWindows())
                    sb.AppendLine($"{w.Current.Name}  [{Native.ProcessName(new IntPtr(w.Current.NativeWindowHandle))}]");
                return ToolResult.Text(sb.ToString());
            }
            case "focus":
            {
                var w = FindWindow(input.Str("window"));
                Native.Focus(new IntPtr(w.Current.NativeWindowHandle));
                return ToolResult.Text($"Focused '{w.Current.Name}'.");
            }
            case "inspect":
            {
                var w = FindWindow(input.Str("window"));
                var filter = input.Str("filter");
                var sb = new StringBuilder($"Window: {w.Current.Name}\n");
                int i = 0;
                foreach (var e in Controls(w, 400))
                {
                    var name = e.Current.Name ?? "";
                    var id = e.Current.AutomationId ?? "";
                    if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !id.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Length == 0 && id.Length == 0) continue;
                    sb.AppendLine($"{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}: \"{Json.Truncate(name, 100)}\"{(id.Length > 0 ? $" id={id}" : "")}{State(e)}{(e.Current.IsEnabled ? "" : " (disabled)")}");
                    if (++i >= 250) { sb.AppendLine("... more controls not shown; use filter"); break; }
                }
                return ToolResult.Text(sb.ToString());
            }
            case "click":
            {
                var w = FindWindow(input.Str("window"));
                var e = FindControl(w, input.Str("name"), input.Str("control_type"), input.Int("index", 0));
                return ToolResult.Text(Activate(e));
            }
            case "type":
            {
                var text = input.Str("text");
                if (!string.IsNullOrEmpty(input.Str("window")))
                {
                    var w = FindWindow(input.Str("window"));
                    Native.Focus(new IntPtr(w.Current.NativeWindowHandle));
                    if (!string.IsNullOrEmpty(input.Str("name")))
                    {
                        var e = FindControl(w, input.Str("name"), input.Str("control_type"), input.Int("index", 0));
                        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly)
                        {
                            ((ValuePattern)vp).SetValue(text);
                            return ToolResult.Text("Text entered.");
                        }
                        e.SetFocus();
                    }
                }
                Thread.Sleep(150);
                WinForms.SendKeys.SendWait(EscapeSendKeys(text));
                return ToolResult.Text("Typed.");
            }
            case "keys":
            {
                if (!string.IsNullOrEmpty(input.Str("window")))
                    Native.Focus(new IntPtr(FindWindow(input.Str("window")).Current.NativeWindowHandle));
                Thread.Sleep(150);
                WinForms.SendKeys.SendWait(input.Str("keys"));
                return ToolResult.Text("Keys sent.");
            }
            default:
                return ToolResult.Error("Unknown action.");
        }
    }

    // ---------- helpers (also used by the Teams joiner) ----------

    public static List<AutomationElement> TopWindows()
    {
        var list = new List<AutomationElement>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            if (!string.IsNullOrWhiteSpace(w.Current.Name)) list.Add(w);
        return list;
    }

    public static AutomationElement FindWindow(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("window is required");
        var windows = TopWindows();
        return windows.FirstOrDefault(w => w.Current.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => Native.ProcessName(new IntPtr(w.Current.NativeWindowHandle)).Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No open window matches '{query}'. Use action 'windows' to see what is open.");
    }

    public static IEnumerable<AutomationElement> Controls(AutomationElement root, int max)
    {
        // Breadth-first over the control view, bounded so huge trees (browsers, Teams) stay quick.
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        int seen = 0;
        while (queue.Count > 0 && seen < max * 6)
        {
            var (el, depth) = queue.Dequeue();
            AutomationElement? child = null;
            try { child = walker.GetFirstChild(el); } catch { }
            while (child != null)
            {
                seen++;
                yield return child;
                if (depth < 30) queue.Enqueue((child, depth + 1));
                try { child = walker.GetNextSibling(child); } catch { child = null; }
            }
        }
    }

    public static AutomationElement FindControl(AutomationElement window, string name, string controlType, int index)
    {
        var matches = Controls(window, 2000).Where(e =>
        {
            try
            {
                var n = e.Current.Name ?? "";
                var id = e.Current.AutomationId ?? "";
                bool nameOk = n.Contains(name, StringComparison.OrdinalIgnoreCase) || id.Equals(name, StringComparison.OrdinalIgnoreCase);
                bool typeOk = string.IsNullOrEmpty(controlType) || e.Current.ControlType.ProgrammaticName.EndsWith(controlType, StringComparison.OrdinalIgnoreCase);
                return nameOk && typeOk;
            }
            catch { return false; }
        }).Take(index + 1).ToList();
        if (matches.Count <= index) throw new ArgumentException($"No control named '{name}' found. Use 'inspect' to see the controls.");
        return matches[index];
    }

    public static string Activate(AutomationElement e)
    {
        var name = e.Current.Name;
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return $"Clicked '{name}'."; }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return $"Toggled '{name}' (now {((TogglePattern)tp).Current.ToggleState})."; }
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return $"Selected '{name}'."; }
        if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) { ((ExpandCollapsePattern)ep).Expand(); return $"Expanded '{name}'."; }
        if (e.TryGetClickablePoint(out var pt)) { Native.Click((int)pt.X, (int)pt.Y); return $"Mouse-clicked '{name}'."; }
        var r = e.Current.BoundingRectangle;
        if (!r.IsEmpty) { Native.Click((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); return $"Mouse-clicked '{name}'."; }
        throw new InvalidOperationException($"'{name}' can't be clicked.");
    }

    static string State(AutomationElement e)
    {
        try
        {
            if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) return $" [{((TogglePattern)tp).Current.ToggleState}]";
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrEmpty(v)) return $" value=\"{Json.Truncate(v, 60)}\"";
            }
        }
        catch { }
        return "";
    }

    static string EscapeSendKeys(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if ("+^%~(){}[]".Contains(c)) sb.Append('{').Append(c).Append('}');
            else if (c == '\n') sb.Append("{ENTER}");
            else if (c != '\r') sb.Append(c);
        }
        return sb.ToString();
    }
}
