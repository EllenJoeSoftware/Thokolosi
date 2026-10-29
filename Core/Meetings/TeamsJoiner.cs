using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Meetings;

/// <summary>
/// Joins Teams meetings through the Teams desktop app (no admin or bot registration needed):
/// opens the meeting link, turns mic/camera off on the pre-join screen, and clicks "Join now".
/// </summary>
public static class TeamsJoiner
{
    static readonly string[] TeamsProcesses = { "ms-teams", "Teams", "msteams" };

    public static async Task<string> JoinAsync(string url, bool muteFirst, CancellationToken ct)
    {
        Process.Start(new ProcessStartInfo(ToDeepLink(url)) { UseShellExecute = true });

        // Wait for the pre-join screen (up to ~90 s, Teams can be slow to start).
        for (int i = 0; i < 45; i++)
        {
            await Task.Delay(2000, ct);
            var result = await StaRunner.Run(() => TryJoinNow(muteFirst));
            if (result != null) return result;
        }
        return "Opened the meeting in Teams, but couldn't find the 'Join now' button. The user may need to click it.";
    }

    /// <summary>Convert https://teams.microsoft.com/... links to msteams: so they open straight in the app, not the browser.</summary>
    static string ToDeepLink(string url)
    {
        var m = Regex.Match(url, @"^https://teams\.microsoft\.com(/.*)$", RegexOptions.IgnoreCase);
        return m.Success ? "msteams:" + m.Groups[1].Value : url;
    }

    static IEnumerable<AutomationElement> TeamsWindows() =>
        DesktopTool.TopWindows().Where(w =>
        {
            var p = Native.ProcessName(new IntPtr(w.Current.NativeWindowHandle));
            return TeamsProcesses.Any(t => p.Equals(t, StringComparison.OrdinalIgnoreCase));
        });

    static string? TryJoinNow(bool mute)
    {
        foreach (var w in TeamsWindows())
        {
            var controls = DesktopTool.Controls(w, 1500).ToList();
            var join = controls.FirstOrDefault(c => Safe(() => c.Current.Name).StartsWith("Join now", StringComparison.OrdinalIgnoreCase));
            if (join == null) continue;

            var notes = new List<string>();
            if (mute)
            {
                foreach (var c in controls)
                {
                    var name = Safe(() => c.Current.Name);
                    // On/off switches are named after the device ("Camera", "Microphone"): On = device on.
                    // Names containing "mute" have inverted meaning, so never toggle those blindly.
                    bool deviceSwitch = Regex.IsMatch(name, @"^(camera|video|microphone|mic)\b", RegexOptions.IgnoreCase) &&
                                        !name.Contains("mute", StringComparison.OrdinalIgnoreCase);
                    bool offButton = Regex.IsMatch(name, @"^(turn camera off|mute|mute microphone|mute mic)$", RegexOptions.IgnoreCase);
                    if (!deviceSwitch && !offButton) continue;
                    try
                    {
                        if (deviceSwitch && c.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) && ((TogglePattern)tp).Current.ToggleState == ToggleState.On)
                        {
                            ((TogglePattern)tp).Toggle();
                            notes.Add($"turned off '{name}'");
                        }
                        else if (offButton && c.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                        {
                            ((InvokePattern)ip).Invoke();
                            notes.Add($"clicked '{name}'");
                        }
                    }
                    catch { }
                }
            }
            DesktopTool.Activate(join);
            return "Joined the meeting in Teams" + (notes.Count > 0 ? " (" + string.Join(", ", notes) + ")" : "") + ".";
        }
        return null;
    }

    /// <summary>True while a Teams call window with a Leave/Hang up button is open.</summary>
    public static Task<bool> InMeetingAsync() => StaRunner.Run(() =>
        TeamsWindows().Any(w => DesktopTool.Controls(w, 1500).Any(c =>
        {
            var n = Safe(() => c.Current.Name);
            return n.StartsWith("Leave", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Hang up", StringComparison.OrdinalIgnoreCase);
        })));

    public static Task<string> LeaveAsync() => StaRunner.Run(() =>
    {
        foreach (var w in TeamsWindows())
        {
            var leave = DesktopTool.Controls(w, 1500).FirstOrDefault(c =>
            {
                var n = Safe(() => c.Current.Name);
                return n.Equals("Leave", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Leave (", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Hang up", StringComparison.OrdinalIgnoreCase);
            });
            if (leave != null) { DesktopTool.Activate(leave); return "Left the meeting."; }
        }
        return "No active Teams meeting found.";
    });

    static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
}
