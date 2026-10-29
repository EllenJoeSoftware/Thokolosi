using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Thokolosi.Core;

public static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    const int SW_RESTORE = 9;
    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;

    public static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ProcessName(IntPtr h)
    {
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    public static void Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Seconds since the user last touched the keyboard or mouse.</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (Environment.TickCount - (int)info.dwTime) / 1000.0 : 0;
    }
}
