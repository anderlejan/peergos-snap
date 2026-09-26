using System.Runtime.InteropServices;
using System.Text;

namespace PeergosSnap.Core;

internal static class Native
{
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_SHOWWINDOW = 0x40, SWP_NOSIZE = 0x1;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int WM_HOTKEY = 0x0312;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    public static PxRect VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static PxRect MonitorAt(int x, int y, bool work = false)
    {
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        var r = work ? mi.rcWork : mi.rcMonitor;
        return new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void MakeToolWindow(IntPtr hwnd, bool clickThrough, bool noActivate)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (clickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    /// <summary>
    /// The visible top-level window under a screen point (skipping our own windows), as its visible frame.
    /// Falls back to the monitor when only the desktop is there.
    /// </summary>
    public static PxRect WindowAt(int x, int y)
    {
        uint self = (uint)Environment.ProcessId;
        IntPtr shell = GetShellWindow();
        PxRect? found = null;
        EnumWindows((h, _) =>
        {
            if (h == shell || !IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;
            var pr = new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (pr.Width < 8 || pr.Height < 8 || !pr.Contains(x, y)) return true;
            found = pr;
            return false;
        }, IntPtr.Zero);
        var mon = MonitorAt(x, y);
        return found is { } f ? f.Intersect(VirtualScreen()) : mon;
    }
}
