using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Thokolosi.Core.Voice;

/// <summary>A system-wide keyboard shortcut (e.g. Ctrl+Alt+Space) that works in any app.</summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly int _id;
    readonly HwndSource _source;
    readonly Action _onPressed;
    bool _registered;

    GlobalHotkey(IntPtr hwnd, int id, Action onPressed)
    {
        _hwnd = hwnd;
        _id = id;
        _onPressed = onPressed;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Registers e.g. "Ctrl+Alt+Space". Returns null and an error message if it can't.</summary>
    static int _nextId = 0x7A01;

    public static GlobalHotkey? Register(IntPtr hwnd, string combo, Action onPressed, out string? error)
    {
        error = null;
        uint mods = MOD_NOREPEAT;
        Key key = Key.None;
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) { error = $"Unknown key '{part}' in hotkey '{combo}'."; return null; }
                    break;
            }
        }
        if (key == Key.None) { error = $"Hotkey '{combo}' has no main key."; return null; }

        var hk = new GlobalHotkey(hwnd, Interlocked.Increment(ref _nextId), onPressed);
        if (!RegisterHotKey(hwnd, hk._id, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            hk.Dispose();
            error = $"'{combo}' is already used by another app. Pick a different shortcut in Settings.";
            return null;
        }
        hk._registered = true;
        return hk;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_hwnd, _id);
        _registered = false;
        _source.RemoveHook(Hook);
    }
}
