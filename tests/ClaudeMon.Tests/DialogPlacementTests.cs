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

}
