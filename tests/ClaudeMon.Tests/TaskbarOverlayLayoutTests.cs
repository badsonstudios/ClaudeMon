namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class TaskbarOverlayLayoutTests
{
    [Fact]
    public void Compute_AnchorsLeftOfNotificationArea_WhenNotifyLeftKnown()
    {
        // Taskbar spans x:0..1920, notification area starts at x:1700, overlay is 52 wide.
        var (x, y) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: 1700, width: 52);

        Assert.Equal(1700 - 52, x); // ends exactly at the notification area's left edge
        Assert.Equal(1040, y);      // top-aligned with the taskbar
    }

    [Fact]
    public void Compute_AnchorsToTaskbarRight_WhenNotifyLeftUnknownAndNoReserve()
    {
        // Secondary taskbars typically expose no tray, so notifyLeft is null.
        var (x, y) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: null, width: 52);

        Assert.Equal(1920 - 52, x);
        Assert.Equal(1040, y);
    }

    [Fact]
    public void Compute_ReservesClockSpace_OnSecondaryTaskbar()
    {
        // No notifyLeft (secondary taskbar): the overlay must sit left of the reserved clock
        // space, not on the taskbar's right edge where the windowless clock is drawn.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: null, width: 52,
            rightReserve: 120);

        Assert.Equal(1920 - 120 - 52, x);
    }

    [Fact]
    public void Compute_IgnoresReserve_WhenNotifyLeftKnown()
    {
        // The primary taskbar anchors exactly to its TrayNotifyWnd; the reserve is irrelevant.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: 1700, width: 52,
            rightReserve: 120);

        Assert.Equal(1700 - 52, x);
    }

    [Theory]
    [InlineData(20)]    // positive nudge moves right
    [InlineData(-30)]   // negative nudge moves left
    public void Compute_AppliesHorizontalOffset_OnSecondaryTaskbar(int offset)
    {
        // Secondary taskbars anchor to an estimated clock reserve (no notifyLeft). Verify the
        // secondary nudge shifts from that anchor.
        var baseX = 1920 - 120 - 52; // taskbar right − clock reserve − width
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: null, width: 52,
            rightReserve: 120, horizontalOffset: offset);

        Assert.Equal(baseX + offset, x);
    }

    [Theory]
    [InlineData(20)]    // positive nudge moves right, toward the tray
    [InlineData(-30)]   // negative nudge moves left, opening a gap from the tray
    public void Compute_AppliesHorizontalOffset_OnPrimaryTaskbar(int offset)
    {
        // The primary taskbar anchors exactly to its TrayNotifyWnd (notifyLeft known). Verify
        // the primary nudge shifts from that exact anchor. (Which offset is passed — the
        // IsPrimary selection — lives in TaskbarOverlayWindow.Reposition, which needs a real
        // window; only the placement math is covered here.)
        var baseX = 1700 - 52; // notification area left − width
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: 1700, width: 52,
            rightReserve: 0, horizontalOffset: offset);

        Assert.Equal(baseX + offset, x);
    }

    [Fact]
    public void Compute_ZeroOffset_KeepsExactTrayAnchoring_OnPrimaryTaskbar()
    {
        // Load-bearing regression guard: the default (0) must reproduce the pre-nudge exact
        // primary anchoring — existing installs are visually unchanged until adjusted.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: 1700, width: 52,
            rightReserve: 0, horizontalOffset: 0);

        Assert.Equal(1700 - 52, x);
    }

    [Fact]
    public void Compute_ClampsToRightEdge_WhenOffsetPushesPastIt()
    {
        // A large positive nudge must not push the readout off the right edge of the taskbar.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 1920), notifyLeft: 1700, width: 52,
            rightReserve: 0, horizontalOffset: 1000);

        Assert.Equal(1920 - 52, x);
    }

    [Fact]
    public void Compute_UsesScreenCoordinates_OnAMonitorLeftOfPrimary()
    {
        // A monitor positioned to the left of the primary has negative X coordinates.
        var (x, y) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: -1920, Top: 1040, Right: 0), notifyLeft: null, width: 52);

        Assert.Equal(0 - 52, x);
        Assert.Equal(1040, y);
    }

    [Fact]
    public void Compute_ClampsToTaskbarLeft_WhenOverlayWiderThanAvailableSpace()
    {
        // A readout wider than the taskbar must not spill off the left edge.
        // Unclamped x would be 130 - 52 = 78, which is left of the taskbar's left edge (100),
        // so it is clamped back to 100.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 100, Top: 0, Right: 130), notifyLeft: null, width: 52);

        Assert.Equal(100, x);
    }

    [Fact]
    public void Compute_DoesNotClamp_WhenOverlayFits()
    {
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 0, Right: 1920), notifyLeft: null, width: 52);

        Assert.Equal(1920 - 52, x);
        Assert.True(x >= 0);
    }

    [Fact]
    public void Compute_ClampsToLeftEdge_WhenNegativeOffsetPushesPastIt_OnSecondaryTaskbar()
    {
        // Dragging the position nudge fully left on a narrow secondary taskbar (reserve set)
        // must pin to the left edge, not run the readout off-screen.
        var (x, _) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(Left: 0, Top: 1040, Right: 300), notifyLeft: null, width: 52,
            rightReserve: 120, horizontalOffset: -1000);

        Assert.Equal(0, x);
    }
}
