namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class FlyoutPlacementTests
{
    // A 1920x1040 primary working area (40px taskbar already excluded) and a typical flyout.
    private static readonly Rectangle Area = new(0, 0, 1920, 1040);
    private static readonly Size Flyout = new(300, 200);

    private static void AssertInside(Rectangle area, Point p, Size size)
    {
        Assert.True(p.X >= area.Left && p.X + size.Width <= area.Right,
            $"x range [{p.X}, {p.X + size.Width}] outside [{area.Left}, {area.Right}]");
        Assert.True(p.Y >= area.Top && p.Y + size.Height <= area.Bottom,
            $"y range [{p.Y}, {p.Y + size.Height}] outside [{area.Top}, {area.Bottom}]");
    }

    [Fact]
    public void Compute_RoomAbove_CentersAboveAnchor()
    {
        var anchor = new Point(960, 1040); // bottom-centre, tray-ish
        var p = FlyoutPlacement.Compute(Area, anchor, Flyout);
        Assert.Equal(new Point(960 - 150, 1040 - 200 - FlyoutPlacement.AnchorGap), p);
        AssertInside(Area, p, Flyout);
    }

    [Fact]
    public void Compute_AnchorNearRightEdge_ClampsRight()
    {
        var anchor = new Point(1900, 1040);
        var p = FlyoutPlacement.Compute(Area, anchor, Flyout);
        Assert.Equal(1920 - 300, p.X);
        AssertInside(Area, p, Flyout);
    }

    [Fact]
    public void Compute_AnchorNearLeftEdge_ClampsLeft()
    {
        var anchor = new Point(10, 1040);
        var p = FlyoutPlacement.Compute(Area, anchor, Flyout);
        Assert.Equal(0, p.X);
        AssertInside(Area, p, Flyout);
    }

    [Fact]
    public void Compute_NoRoomAbove_FlipsBelow()
    {
        // Taskbar-on-top layout: anchor near the top edge.
        var anchor = new Point(960, 40);
        var p = FlyoutPlacement.Compute(Area, anchor, Flyout);
        Assert.Equal(40 + FlyoutPlacement.AnchorGap, p.Y);
        AssertInside(Area, p, Flyout);
    }

    [Fact]
    public void Compute_FlipBelow_ClampsBottomEdge()
    {
        // Anchor high on a short area: flipping below would spill past the bottom (the
        // straddle-onto-the-monitor-below case) — the bottom edge must clamp.
        var area = new Rectangle(0, 0, 1920, 300);
        var anchor = new Point(960, 150);
        var p = FlyoutPlacement.Compute(area, anchor, Flyout);
        Assert.Equal(300 - 200, p.Y);
        AssertInside(area, p, Flyout);
    }

    [Fact]
    public void Compute_AnchorBelowWorkingArea_ClampsBottomWithoutFlip()
    {
        // The most common real anchor: a tray click puts Cursor.Position ON the taskbar,
        // below the working area. There's room above, so no flip — but the bottom edge
        // must still clamp into the working area (the old code never clamped this path).
        var anchor = new Point(960, 1080);
        var p = FlyoutPlacement.Compute(Area, anchor, Flyout);
        Assert.Equal(1040 - 200, p.Y);
        AssertInside(Area, p, Flyout);
    }

    [Fact]
    public void Compute_NegativeCoordinateArea_StaysOnThatMonitor()
    {
        // A monitor above/left of the primary has negative virtual-desktop coordinates.
        var area = new Rectangle(-1920, -1080, 1920, 1040);
        var anchor = new Point(-10, -40); // near that monitor's bottom-right corner
        var p = FlyoutPlacement.Compute(area, anchor, Flyout);
        AssertInside(area, p, Flyout);
    }

    [Fact]
    public void Compute_NonOriginArea_StaysOnThatMonitor()
    {
        // A secondary monitor to the right of the primary: virtual-desktop coordinates.
        var area = new Rectangle(1920, 200, 1280, 680);
        var anchor = new Point(3190, 880); // bottom-right corner of that monitor
        var p = FlyoutPlacement.Compute(area, anchor, Flyout);
        AssertInside(area, p, Flyout);
    }

    [Fact]
    public void Compute_FlyoutLargerThanArea_PinsTopLeft()
    {
        var area = new Rectangle(100, 100, 200, 150);
        var p = FlyoutPlacement.Compute(area, new Point(200, 250), Flyout);
        Assert.Equal(new Point(100, 100), p);
    }

    [Fact]
    public void Compute_SizeGrowsBetweenComputeAndReclamp_ReclampStaysInside()
    {
        // The mixed-DPI show sequence: placement is computed at 100% scale, then moving onto
        // the 150% monitor resizes the flyout and the caller re-computes with the final size.
        // The re-computed position must contain the larger flyout entirely.
        var area = new Rectangle(1920, 0, 1280, 680);
        var anchor = new Point(3180, 680);
        var initial = FlyoutPlacement.Compute(area, anchor, Flyout);
        AssertInside(area, initial, Flyout);

        var grown = new Size(450, 300); // 150% of the original
        var reclamped = FlyoutPlacement.Compute(area, anchor, grown);
        AssertInside(area, reclamped, grown);
    }
}
