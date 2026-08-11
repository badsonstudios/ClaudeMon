namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

/// <summary>
/// Defines the collection that serialises every test class touching WinForms' <c>Screen</c> /
/// <c>SystemInformation</c> statics. Their caches are populated non-atomically, so two classes
/// first touching them from different xUnit threads can observe an empty rectangle — an
/// intermittent failure that has nothing to do with the code under test.
/// </summary>
[CollectionDefinition("Desktop metrics", DisableParallelization = true)]
public sealed class DesktopMetricsCollection;

[Collection("Desktop metrics")]
public class DialogPlacementTests
{
    [Fact]
    public void CenterIn_CentersWithinArea()
    {
        // 1920x1040 working area at origin, 400x200 form → centered.
        var p = DialogPlacement.CenterIn(new Rectangle(0, 0, 1920, 1040), new Size(400, 200));
        Assert.Equal(new Point(760, 420), p);
    }

    [Fact]
    public void CenterIn_NonOriginArea_OffsetsFromAreaOrigin()
    {
        // A monitor to the right of the primary starts at x=1920; centering must be relative
        // to the area's own origin, not the virtual desktop's.
        var p = DialogPlacement.CenterIn(new Rectangle(1920, 100, 1000, 800), new Size(400, 200));
        Assert.Equal(new Point(1920 + 300, 100 + 300), p);
    }

    [Fact]
    public void CenterIn_FormLargerThanArea_ClampsToAreaOrigin()
    {
        // Oversized form: keep the top-left (title bar / close box) inside the area rather
        // than centering it off-screen above/left.
        var p = DialogPlacement.CenterIn(new Rectangle(0, 0, 800, 600), new Size(1000, 700));
        Assert.Equal(new Point(0, 0), p);
    }

    [Fact]
    public void CenterIn_ExactFit_LandsOnAreaOrigin()
    {
        var p = DialogPlacement.CenterIn(new Rectangle(50, 60, 400, 200), new Size(400, 200));
        Assert.Equal(new Point(50, 60), p);
    }

    [Fact]
    public void CenterIn_OddPixelRemainder_StaysInsideArea()
    {
        // Integer division bias must never push the form outside the area.
        var area = new Rectangle(0, 0, 101, 101);
        var p = DialogPlacement.CenterIn(area, new Size(100, 100));
        Assert.True(p.X >= area.Left && p.X + 100 <= area.Right);
        Assert.True(p.Y >= area.Top && p.Y + 100 <= area.Bottom);
    }

    // --- PlaceStable: the DPI-resize converge loop (#108) ---

    /// <summary>
    /// Stands in for a form whose size changes when it is moved, the way Per-Monitor-V2 resizes
    /// a window that lands on a differently-scaled monitor.
    /// </summary>
    private sealed class FakeForm(Size initial, params Size[] sizesAfterEachMove)
    {
        private readonly List<Point> _moves = [];
        private Size _size = initial;
        private Point _location;
        private int _moveCount;

        public IReadOnlyList<Point> Moves => _moves;

        public Rectangle Bounds() => new(_location, _size);

        public void Move(Point p)
        {
            _moves.Add(p);
            _location = p;
            // The n-th move adopts the n-th scripted size, if the script supplies one — this is
            // how Per-Monitor-V2 resizes a window that lands on a differently-scaled monitor.
            if (_moveCount < sizesAfterEachMove.Length)
                _size = sizesAfterEachMove[_moveCount];
            _moveCount++;
        }

        /// <summary>Simulates WinForms repositioning the window without changing its size.</summary>
        public void Nudge(Point p) => _location = p;
    }

    [Fact]
    public void PlaceStable_SizeNeverChanges_MovesOnceToCenter()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(new Size(400, 200));

        var result = DialogPlacement.PlaceStable(area, form.Bounds, form.Move);

        Assert.Equal(new Point(760, 420), result);
        Assert.Equal(new Point(760, 420), Assert.Single(form.Moves));
    }

    [Fact]
    public void PlaceStable_SizeChangesAfterMove_RecentersForTheNewSize()
    {
        // The #104 bug class, encoded: moving onto a 150%-scaled secondary monitor resizes the
        // dialog after the position was computed. Centering must use the FINAL size.
        var secondary = new Rectangle(1920, 0, 1920, 1040);
        var form = new FakeForm(new Size(400, 200), new Size(600, 300));

        var result = DialogPlacement.PlaceStable(secondary, form.Bounds, form.Move);

        Assert.Equal(DialogPlacement.CenterIn(secondary, new Size(600, 300)), result);
        Assert.Equal(2, form.Moves.Count);
        // And the final rectangle sits entirely inside the target monitor.
        Assert.True(secondary.Contains(new Rectangle(result, new Size(600, 300))));
    }

    [Fact]
    public void PlaceStable_WindowRepositionedWithoutResize_IsCorrected()
    {
        // WinForms' own WM_DPICHANGED handling honours the suggested rectangle and can move the
        // window without resizing it. Comparing sizes alone would miss that and return a point
        // the form isn't actually at.
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(new Size(400, 200));

        var result = DialogPlacement.PlaceStable(
            area,
            () =>
            {
                // Windows shoves it aside once, immediately after the first move.
                if (form.Moves.Count == 1 && form.Bounds().Location == new Point(760, 420))
                    form.Nudge(new Point(0, 0));
                return form.Bounds();
            },
            form.Move);

        Assert.Equal(new Point(760, 420), result);
        Assert.Equal(2, form.Moves.Count);
        Assert.Equal(new Point(760, 420), form.Bounds().Location);
    }

    [Fact]
    public void PlaceStable_SizeChangesEveryMove_TerminatesAndStaysInsideArea()
    {
        // Pathological: the size never settles. The loop must still terminate, and the clamp
        // invariant must hold so the title bar remains reachable on the target monitor.
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(
            new Size(400, 200), new Size(500, 250), new Size(600, 300), new Size(700, 350));

        var result = DialogPlacement.PlaceStable(area, form.Bounds, form.Move);

        Assert.Equal(3, form.Moves.Count); // capped at maxMoves
        Assert.True(result.X >= area.Left && result.Y >= area.Top);
        Assert.True(area.Contains(result));
    }

    [Fact]
    public void PlaceStable_NonPrimaryMonitor_CentersRelativeToThatMonitor()
    {
        // A secondary monitor to the right: centering is relative to its own origin, not the
        // virtual desktop's.
        var secondary = new Rectangle(1920, 100, 1000, 800);
        var form = new FakeForm(new Size(400, 200));

        var result = DialogPlacement.PlaceStable(secondary, form.Bounds, form.Move);

        Assert.Equal(new Point(1920 + 300, 100 + 300), result);
    }

    [Fact]
    public void PlaceStable_SizeShrinksAfterMove_StillRecenters()
    {
        // The opposite scale direction (150% → 100%): the dialog gets smaller, so the first
        // position is now too far up-left rather than overflowing.
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(new Size(600, 300), new Size(400, 200));

        var result = DialogPlacement.PlaceStable(area, form.Bounds, form.Move);

        Assert.Equal(new Point(760, 420), result);
        Assert.Equal(2, form.Moves.Count);
    }

    [Fact]
    public void PlaceStable_ReturnedPointIsWhereTheFormWasLastMoved()
    {
        // CenterOn discards the return value and relies on the form actually being there.
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(new Size(400, 200), new Size(600, 300));

        var result = DialogPlacement.PlaceStable(area, form.Bounds, form.Move);

        Assert.Equal(form.Moves[^1], result);
        Assert.Equal(form.Bounds().Location, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    public void PlaceStable_MoveBudgetBelowTwo_StillMovesExactlyOnce(int maxMoves)
    {
        // A budget under 1 is treated as 1 rather than skipping placement entirely — a dialog
        // left at the default origin would be the worst outcome.
        var area = new Rectangle(0, 0, 1920, 1040);
        var form = new FakeForm(new Size(400, 200), new Size(600, 300));

        var result = DialogPlacement.PlaceStable(area, form.Bounds, form.Move, maxMoves);

        Assert.Equal(new Point(760, 420), result);
        Assert.Single(form.Moves);
    }

    // --- The Form-facing wrappers. These construct a Form but never show it, so there is no
    // message loop and no window is ever put on screen.

    [Fact]
    public void CenterOn_MovesTheFormToTheCenterOfTheGivenArea()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, Size = new Size(400, 200) };

        DialogPlacement.CenterOn(form, area);

        Assert.Equal(DialogPlacement.CenterIn(area, form.Size), form.Location);
    }

    [Fact]
    public void CenterOnPrimary_LandsInsideThePrimaryWorkingArea()
    {
        // Deliberately not asserting an absolute point: the working area depends on the machine.
        // What matters is that the dialog ends up on the primary monitor rather than wherever
        // the mouse cursor happened to be (issue #88).
        var primary = DialogPlacement.PrimaryWorkingArea();
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, Size = new Size(300, 150) };

        DialogPlacement.CenterOnPrimary(form);

        Assert.Equal(DialogPlacement.CenterIn(primary, form.Size), form.Location);
    }

    // --- Fitting a self-sizing window to the monitor. Every window in the app sizes itself from
    // its content, so all of them can want more height than the screen has: Settings when a tall
    // tab is selected (#139), the About/update dialogs and the Usage & costs window at a large
    // scale factor (#153). The windows need a desktop session, so the decisions — whether it fits,
    // and what the height and the top edge do when it doesn't — are pinned here.

    // A typical FixedDialog's title bar + borders at 96 DPI.
    private const int Chrome = 39;
    private const int MinClient = 200;

    [Fact]
    public void ClampClientHeight_LeavesAFittingWindowAlone()
    {
        // The Alerts tab on a 1080p desktop: plenty of room, so nothing changes and nothing scrolls.
        var (height, scroll) = DialogPlacement.ClampClientHeight(620, 1040, Chrome, MinClient);

        Assert.Equal(620, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_ClampsToTheWorkingAreaLessTheChrome()
    {
        // 12 rows at 150% on a 768-high panel: the window wants more than the monitor has.
        var (height, scroll) = DialogPlacement.ClampClientHeight(930, 728, 58, MinClient);

        Assert.Equal(670, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_ClampsAFixedDialogAtALargeScaleFactor()
    {
        // #153's case: About's ~230 logical of content at 300% wants 690px of client on a 768-high
        // panel whose working area leaves 728 for a 117px-chromed window — the OK button is over
        // the edge. The clamp brings it back and asks for a scrollbar.
        var (height, scroll) = DialogPlacement.ClampClientHeight(690, 728, 117, minClientHeight: 160);

        Assert.Equal(611, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_DoesNotScrollWhenTheWindowExactlyFits()
    {
        var (height, scroll) = DialogPlacement.ClampClientHeight(1001, 1040, Chrome, MinClient);

        Assert.Equal(1001, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_ScrollsOnePixelPastTheFit()
    {
        var (height, scroll) = DialogPlacement.ClampClientHeight(1002, 1040, Chrome, MinClient);

        Assert.Equal(1001, height);
        Assert.True(scroll);
    }

    [Theory]
    [InlineData(0)]     // no monitor reported
    [InlineData(-500)]  // a nonsense working area
    [InlineData(30)]    // a working area smaller than the chrome alone
    public void ClampClientHeight_FallsBackToTheFloorForADegenerateWorkingArea(int workingAreaHeight)
    {
        var (height, scroll) = DialogPlacement.ClampClientHeight(
            800, workingAreaHeight, Chrome, MinClient);

        Assert.Equal(MinClient, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_TreatsNegativeChromeAsNone()
    {
        var (height, scroll) = DialogPlacement.ClampClientHeight(800, 700, -40, MinClient);

        Assert.Equal(700, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_NeverReturnsANegativeHeight()
    {
        var (height, scroll) = DialogPlacement.ClampClientHeight(-100, 1040, Chrome, MinClient);

        Assert.Equal(0, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_DoesNotStretchAShortTabUpToTheFloor()
    {
        // The General tab is two rows; the floor guards the clamp, it is not a minimum size.
        var (height, scroll) = DialogPlacement.ClampClientHeight(150, 1040, Chrome, MinClient);

        Assert.Equal(150, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_KeepsTheFloorEvenWhenItOverflowsTheWorkingArea()
    {
        // Pinning the trade-off in the floor's doc: below chrome + floor the window is deliberately
        // taller than the "monitor", because the alternative is a window too short to use.
        var (height, scroll) = DialogPlacement.ClampClientHeight(800, 200, Chrome, MinClient);

        Assert.Equal(MinClient, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampTop_LeavesAWindowThatAlreadyFitsWhereItIs()
    {
        Assert.Equal(249, DialogPlacement.ClampTop(249, 268, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_SlidesAWindowUpOffTheBottomEdge()
    {
        // The 1366x768 case: centered for the two-row General tab, then switched to a tall tab.
        Assert.Equal(125, DialogPlacement.ClampTop(249, 603, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_PinsAWindowTallerThanTheAreaToTheTopSoItsTitleBarStaysReachable()
    {
        Assert.Equal(0, DialogPlacement.ClampTop(249, 900, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_RespectsAMonitorThatDoesNotStartAtTheOrigin()
    {
        // A monitor above the primary has a negative top; a taskbar on top gives a positive one.
        Assert.Equal(-1080, DialogPlacement.ClampTop(-900, 1200, areaTop: -1080, areaBottom: -40));
        Assert.Equal(1140, DialogPlacement.ClampTop(1300, 500, areaTop: 1080, areaBottom: 1640));
    }

    [Fact]
    public void ClampTop_TreatsANegativeHeightAsZero()
    {
        Assert.Equal(249, DialogPlacement.ClampTop(249, -50, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_AgreesWithCenterInForAWindowTallerThanTheArea()
    {
        // Both clamps promise the same thing — the title bar stays reachable — so they must not
        // disagree about where an oversized window's top edge goes.
        var area = new Rectangle(0, 0, 1366, 728);
        var size = new Size(430, 900);

        Assert.Equal(
            DialogPlacement.CenterIn(area, size).Y,
            DialogPlacement.ClampTop(249, size.Height, area.Top, area.Bottom));
    }

    [Fact]
    public void WorkingAreaFor_AFormWithNoWindowYet_IsThePrimaryWorkingArea()
    {
        // The dialogs lay themselves out once in their constructor, before there is a handle to
        // ask which monitor they are on; they open on a monitor chosen in OnLoad, and an unplaced
        // form sits at the origin, so the primary is the right answer for that first pass.
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, Size = new Size(300, 150) };

        Assert.False(form.IsHandleCreated);
        Assert.Equal(DialogPlacement.PrimaryWorkingArea(), DialogPlacement.WorkingAreaFor(form));
    }

    // FitToMonitor is asserted relative to the real primary working area rather than against
    // absolute pixels: the test machine's monitor is whatever it is. The form is constructed but
    // never shown, so no window reaches the screen.

    [Fact]
    public void FitToMonitor_ContentThatFits_IsTakenAsIsAndDoesNotScroll()
    {
        using var form = new Form { FormBorderStyle = FormBorderStyle.FixedDialog };

        var scroll = DialogPlacement.FitToMonitor(form, 430, 176, minClientHeight: 160);

        Assert.False(scroll);
        Assert.False(form.AutoScroll);
        Assert.Equal(new Size(430, 176), form.ClientSize);
    }

    [Fact]
    public void FitToMonitor_ContentTallerThanTheMonitor_IsCappedAndScrolls()
    {
        using var form = new Form { FormBorderStyle = FormBorderStyle.FixedDialog };
        var area = DialogPlacement.WorkingAreaFor(form);
        var tooTall = area.Height + 500;

        var scroll = DialogPlacement.FitToMonitor(form, 430, tooTall, minClientHeight: 160);

        Assert.True(scroll);
        Assert.True(form.AutoScroll);
        Assert.True(form.Height <= area.Height, $"window {form.Height} > working area {area.Height}");
        // The whole content is still reachable — that is what the scrollbar is for.
        Assert.Equal(tooTall, form.AutoScrollMinSize.Height);
        // ...and no horizontal scrollbar is ever asked for.
        Assert.Equal(0, form.AutoScrollMinSize.Width);
    }

    [Fact]
    public void FitToMonitor_ShrinkingBackToFittingContent_TurnsScrollingOffAgain()
    {
        // Switching from a tall Settings tab back to a short one, or dragging a dialog from a
        // 300% monitor to a 100% one: the scrollbar has to go away, not linger.
        using var form = new Form { FormBorderStyle = FormBorderStyle.FixedDialog };
        var area = DialogPlacement.WorkingAreaFor(form);

        DialogPlacement.FitToMonitor(form, 430, area.Height + 500, minClientHeight: 160);
        var scroll = DialogPlacement.FitToMonitor(form, 430, 176, minClientHeight: 160);

        Assert.False(scroll);
        Assert.False(form.AutoScroll);
        Assert.Equal(Size.Empty, form.AutoScrollMinSize);
        Assert.Equal(new Size(430, 176), form.ClientSize);
    }
}
