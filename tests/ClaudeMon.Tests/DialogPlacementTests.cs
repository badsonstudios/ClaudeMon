namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

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
}
