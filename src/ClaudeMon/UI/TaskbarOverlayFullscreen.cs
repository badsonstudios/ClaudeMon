namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Decides whether a taskbar readout should hide because a fullscreen app is covering its
/// monitor (issue #123). The readout is a topmost overlay that re-asserts its z-order every
/// keep-alive tick, so without this it floats over fullscreen games and fullscreen RDP
/// sessions — content whose whole point is that the taskbar (and everything glued to it) is
/// hidden. Pure so the rules are unit-testable; <see cref="TaskbarOverlayWindow"/> supplies
/// the live foreground-window facts each tick.
/// </summary>
internal static class TaskbarOverlayFullscreen
{
    // Windows that legitimately cover a whole monitor (taskbar strip included) without
    // meaning "a fullscreen app is running": the desktop itself (Progman, or WorkerW when
    // the wallpaper is re-parented there), the alt-tab / task-view surfaces (Win11's XAML
    // host, Win10's MultitaskingViewFrame) and Win10's fullscreen-Start CoreWindow — all
    // monitor-sized for the moment they're open and must not blink the readout. (Fullscreen
    // UWP *apps* foreground as ApplicationFrameWindow, not CoreWindow, so they still hide
    // it.) The taskbars are included because clicking the clock or tray foregrounds them.
    private static readonly string[] IgnoredClasses =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "Windows.UI.Core.CoreWindow",
    ];

    /// <summary>
    /// True when <paramref name="foreground"/> (the current foreground window's bounds) fully
    /// covers <paramref name="taskbar"/> (the taskbar window's bounds) and is neither shell
    /// furniture nor one of our own windows. Keying on the taskbar strip rather than the whole
    /// monitor asks exactly the question that matters — "is the taskbar buried?" — so a
    /// maximized window (which stops at the working area) never hides the readout, including
    /// under taskbar auto-hide, where the working area spans the full monitor and a
    /// monitor-containment test would wrongly trip.
    /// </summary>
    internal static bool ShouldHide(
        Rectangle foreground, string foregroundClass, bool ownProcess, Rectangle taskbar)
    {
        if (ownProcess || taskbar.IsEmpty)
            return false;

        foreach (var ignored in IgnoredClasses)
        {
            if (string.Equals(ignored, foregroundClass, StringComparison.Ordinal))
                return false;
        }

        return foreground.Contains(taskbar);
    }
}
