namespace ClaudeMon.Tests;

using ClaudeMon.UI;

/// <summary>
/// The fallback rules that decide whether the foreground window is a usable "the user is over
/// here" signal (#108). Pure — no desktop required.
/// </summary>
public class ForegroundMonitorTests
{
    private static readonly IntPtr Shell = new(0x1000);
    private static readonly IntPtr OtherApp = new(0x2000);
    private const uint OwnPid = 4321;
    private const uint OtherPid = 8765;

    private static bool IsUsable(
        IntPtr foreground,
        IntPtr shell,
        uint foregroundPid,
        bool visible = true,
        bool minimized = false)
        => ForegroundMonitor.IsUsable(foreground, shell, foregroundPid, OwnPid, visible, minimized);

    [Fact]
    public void OrdinaryVisibleForeignWindow_IsUsable()
        => Assert.True(IsUsable(OtherApp, Shell, OtherPid));

    [Fact]
    public void NoForegroundWindow_FallsBack()
        => Assert.False(IsUsable(IntPtr.Zero, Shell, OtherPid));

    [Fact]
    public void ShellWindowIsForeground_FallsBack()
    {
        // The desktop is foreground — nothing meaningful to follow.
        Assert.False(IsUsable(Shell, Shell, OtherPid));
    }

    [Fact]
    public void UnknownOwningProcess_FallsBack()
    {
        // GetWindowThreadProcessId failed, so we can't rule out our own window.
        Assert.False(IsUsable(OtherApp, Shell, 0));
    }

    [Fact]
    public void OurOwnWindowIsForeground_FallsBack()
    {
        // The tray dropdown, flyout, overlay, or an earlier dialog. Following ourselves would
        // be circular; the primary monitor is the right answer there anyway.
        Assert.False(IsUsable(OtherApp, Shell, OwnPid));
    }

    [Fact]
    public void HiddenForegroundWindow_FallsBack()
        => Assert.False(IsUsable(OtherApp, Shell, OtherPid, visible: false));

    [Fact]
    public void MinimizedForegroundWindow_FallsBack()
        => Assert.False(IsUsable(OtherApp, Shell, OtherPid, minimized: true));

    [Fact]
    public void ShellHandleUnavailable_DoesNotBlockAnOtherwiseGoodWindow()
    {
        // GetShellWindow can return zero; that must not make every window look like the shell.
        Assert.True(IsUsable(OtherApp, IntPtr.Zero, OtherPid));
    }

    [Fact]
    public void OwnProcessCheckWinsOverVisibility()
    {
        // Order matters only for readability, but pin it: our own visible window is still
        // rejected.
        Assert.False(IsUsable(OtherApp, Shell, OwnPid, visible: true, minimized: false));
    }

    // --- The fallback wiring DialogPlacement layers on top ---

    [Fact]
    public void ForegroundWorkingArea_AlwaysReturnsARealMonitorArea()
    {
        // TryWorkingArea returns null whenever the foreground window is unusable; the caller
        // must turn that into the primary monitor rather than an empty rectangle. Whichever
        // branch runs on the build agent, the result has to be somewhere a dialog can live.
        var area = DialogPlacement.ForegroundWorkingArea();

        Assert.False(area.IsEmpty);
        Assert.True(area.Width > 0 && area.Height > 0);
        Assert.True(area.IntersectsWith(SystemInformation.VirtualScreen));
    }

    [Fact]
    public void PrimaryWorkingArea_IsNonEmpty()
    {
        var area = DialogPlacement.PrimaryWorkingArea();

        Assert.False(area.IsEmpty);
        Assert.True(area.Width > 0 && area.Height > 0);
    }

    // --- Staleness: a caller-supplied area can outlive the monitor it describes ---

    [Fact]
    public void IsUsableArea_RejectsEmpty()
        => Assert.False(ForegroundMonitor.IsUsableArea(Rectangle.Empty));

    [Fact]
    public void IsUsableArea_RejectsAreaOffTheVirtualDesktop()
    {
        // A monitor that was unplugged since the area was captured: its coordinates no longer
        // intersect anything real. Placing a dialog there is the "lost window" symptom itself.
        var unplugged = new Rectangle(100_000, 100_000, 1920, 1080);
        Assert.False(ForegroundMonitor.IsUsableArea(unplugged));
    }

    [Fact]
    public void IsUsableArea_AcceptsALiveMonitorArea()
        => Assert.True(ForegroundMonitor.IsUsableArea(DialogPlacement.PrimaryWorkingArea()));

    [Fact]
    public void ResolveArea_KeepsAUsableRequestedArea()
    {
        // The whole point of threading an area between the two update dialogs: it must survive.
        var requested = DialogPlacement.PrimaryWorkingArea();
        Assert.Equal(requested, DialogPlacement.ResolveArea(requested));
    }

    [Fact]
    public void ResolveArea_DiscardsAStaleRequestedArea()
    {
        var stale = new Rectangle(100_000, 100_000, 1920, 1080);

        var resolved = DialogPlacement.ResolveArea(stale);

        Assert.NotEqual(stale, resolved);
        Assert.True(ForegroundMonitor.IsUsableArea(resolved));
    }

    [Fact]
    public void ResolveArea_NullFallsBackToALiveArea()
    {
        // The tray "Download update" entry point supplies nothing.
        var resolved = DialogPlacement.ResolveArea(null);

        Assert.True(ForegroundMonitor.IsUsableArea(resolved));
    }
}
