namespace ClaudeMon.UI;

using System.Drawing;
using System.Runtime.InteropServices;

/// <summary>
/// Resolves the monitor the user is currently working on, for dialogs that would otherwise
/// open on the primary monitor and go unnoticed (issue #108).
///
/// "Where the user is" is taken from the <em>foreground window</em>, never the mouse cursor:
/// cursor-following placement is exactly what #88 removed, because a dialog popped by a
/// background timer would land on whichever side monitor the cursor happened to be idling on.
/// The foreground window is a deterministic, deliberate signal — it's the window the user last
/// interacted with.
/// </summary>
internal static class ForegroundMonitor
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Whether a foreground window is a usable "the user is over here" signal. Pure, so the
    /// fallback rules are unit-testable without a desktop.
    /// </summary>
    /// <param name="foreground">The foreground window handle (<c>IntPtr.Zero</c> if none).</param>
    /// <param name="shell">The desktop/shell window handle, from <c>GetShellWindow</c>.</param>
    /// <param name="foregroundProcessId">Owning process of <paramref name="foreground"/>, 0 if unknown.</param>
    /// <param name="ownProcessId">This process.</param>
    /// <param name="visible">Whether <paramref name="foreground"/> is visible.</param>
    /// <param name="minimized">Whether <paramref name="foreground"/> is minimized.</param>
    internal static bool IsUsable(
        IntPtr foreground,
        IntPtr shell,
        uint foregroundProcessId,
        uint ownProcessId,
        bool visible,
        bool minimized)
    {
        // Nothing is foreground at all.
        if (foreground == IntPtr.Zero)
            return false;

        // The desktop is foreground — "empty desktop". Progman lives on the primary monitor
        // anyway, so this mostly documents the intent rather than changing the answer.
        if (shell != IntPtr.Zero && foreground == shell)
            return false;

        // Couldn't identify the owner, so we can't rule out our own window.
        if (foregroundProcessId == 0)
            return false;

        // Our own window is foreground: the tray menu dropdown, the flyout, the overlay, or an
        // earlier dialog. Following ourselves would be circular, so fall back to the primary
        // monitor. (Whether a tray-menu-triggered check actually lands here depends on whether
        // Windows has already restored foreground to the previously active app by then — both
        // outcomes are reasonable, so this isn't relied on either way.)
        if (foregroundProcessId == ownProcessId)
            return false;

        // A hidden or minimized window's monitor says nothing about where the user is looking.
        // Note a window cloaked on another virtual desktop still reports visible; following it
        // is deliberately accepted rather than special-cased, since the user did last interact
        // with it and its monitor is still a better guess than the primary.
        return visible && !minimized;
    }

    /// <summary>
    /// Whether <paramref name="area"/> still describes somewhere a window can actually live.
    /// <see cref="Screen"/> data is cached and refreshed on display-change events, and a
    /// working area can also be carried around by a caller for a while (the update prompt can
    /// sit open indefinitely before the download dialog reuses its area). If the monitor was
    /// unplugged in the meantime, placing a dialog there would cause the very "lost window"
    /// symptom #108 exists to fix.
    /// </summary>
    internal static bool IsUsableArea(Rectangle area)
        => !area.IsEmpty && area.IntersectsWith(SystemInformation.VirtualScreen);

    /// <summary>
    /// The working area of the monitor holding the foreground window, or <c>null</c> when there
    /// isn't a usable one (see <see cref="IsUsable"/>) — the caller owns the fallback, which
    /// keeps this class from depending back on <see cref="DialogPlacement"/>.
    /// Never throws: placement is cosmetic and must not be able to take the update flow down.
    /// </summary>
    public static Rectangle? TryWorkingArea()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return null;

            // The return value is the owning thread id, and 0 means the call failed — a more
            // direct failure signal than inspecting the out parameter.
            var threadId = GetWindowThreadProcessId(foreground, out var pid);

            if (!IsUsable(
                    foreground,
                    GetShellWindow(),
                    threadId == 0 ? 0 : pid,
                    (uint)Environment.ProcessId,
                    IsWindowVisible(foreground),
                    IsIconic(foreground)))
            {
                return null;
            }

            // Resolved only after the gate above: Screen.FromHandle uses
            // MONITOR_DEFAULTTONEAREST and would happily return a plausible-looking screen for
            // a handle we've already rejected.
            var area = Screen.FromHandle(foreground).WorkingArea;
            return IsUsableArea(area) ? area : null;
        }
        catch
        {
            return null;
        }
    }
}
