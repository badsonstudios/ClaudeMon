namespace ClaudeMon.UI;

using System.Runtime.InteropServices;

/// <summary>
/// One Windows taskbar: its window handle plus the device name of the monitor it sits on.
/// The device name (e.g. <c>\\.\DISPLAY1</c>) is a reasonably stable key for "the taskbar
/// on this monitor", so an overlay can re-find its taskbar across Explorer restarts (which
/// recreate the taskbar windows with fresh handles).
/// </summary>
internal readonly record struct TaskbarInfo(IntPtr Handle, string MonitorDevice, bool IsPrimary);

/// <summary>
/// Enumerates the live Windows taskbars: the primary (<c>Shell_TrayWnd</c>) plus one
/// secondary taskbar (<c>Shell_SecondaryTrayWnd</c>) per additional monitor that has the
/// taskbar shown. When "show taskbar on all displays" is off there are no secondary
/// taskbars, so only the primary is returned — exactly the monitors we should draw on.
/// </summary>
internal static class TaskbarEnumerator
{
    public static IReadOnlyList<TaskbarInfo> Enumerate()
    {
        var taskbars = new List<TaskbarInfo>();

        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
            taskbars.Add(new TaskbarInfo(primary, MonitorDeviceOf(primary), IsPrimary: true));

        // Secondary taskbars are top-level windows (children of the desktop) of class
        // Shell_SecondaryTrayWnd — one per secondary monitor. Iterate them all.
        var prev = IntPtr.Zero;
        while (true)
        {
            var hwnd = FindWindowEx(IntPtr.Zero, prev, "Shell_SecondaryTrayWnd", null);
            if (hwnd == IntPtr.Zero)
                break;
            taskbars.Add(new TaskbarInfo(hwnd, MonitorDeviceOf(hwnd), IsPrimary: false));
            prev = hwnd;
        }

        return taskbars;
    }

    /// <summary>Returns the live taskbar on the given monitor, or null if it isn't present.</summary>
    public static TaskbarInfo? FindByDevice(string monitorDevice)
    {
        foreach (var taskbar in Enumerate())
        {
            if (taskbar.MonitorDevice == monitorDevice)
                return taskbar;
        }

        return null;
    }

    private static string MonitorDeviceOf(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.szDevice;

        // Fall back to the handle value so each taskbar still gets a distinct key even if
        // the monitor can't be resolved — better a non-stable key than a colliding one.
        return hwnd.ToString();
    }

    // --- Win32 interop ---

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}
