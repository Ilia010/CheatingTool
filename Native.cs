using System;
using System.Runtime.InteropServices;

namespace CheatingTool;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("Shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

/// <summary>Прячет окно Chrome: уводит за пределы экрана и убирает из панели задач / Alt+Tab.</summary>
internal static class ChromeWindow
{
    private const int OffScreen = -32000;

    public static void HideOffScreen(uint processId)
    {
        try
        {
            Native.EnumWindows((hWnd, _) =>
            {
                Native.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != processId || !Native.IsWindowVisible(hWnd)) return true;

                int style = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hWnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW);
                Native.SetWindowPos(hWnd, IntPtr.Zero, OffScreen, OffScreen, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Окно уже закрыто или недоступно — ничего страшного.
        }
    }
}

internal readonly record struct MonitorMetrics(
    int Left, int Top, int Width, int Height,
    int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight,
    int Dpi);

internal static class Displays
{
    public static MonitorMetrics UnderCursor()
    {
        Native.GetCursorPos(out var pt);
        var h = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        return FromHandle(h);
    }

    public static MonitorMetrics Primary()
    {
        var h = Native.MonitorFromWindow(Native.GetDesktopWindow(), Native.MONITOR_DEFAULTTOPRIMARY);
        return FromHandle(h);
    }

    private static MonitorMetrics FromHandle(IntPtr hMonitor)
    {
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(hMonitor, ref info))
            return new MonitorMetrics(0, 0, 1920, 1080, 0, 0, 1920, 1040, 96);

        int dpi = 96;
        try
        {
            if (Native.GetDpiForMonitor(hMonitor, 0, out uint dx, out _) == 0 && dx > 0)
                dpi = (int)dx;
        }
        catch { }

        var r = info.rcMonitor;
        var w = info.rcWork;
        return new MonitorMetrics(
            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top,
            dpi);
    }
}
