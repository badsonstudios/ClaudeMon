namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// The pixel geometry of the <see cref="TabStrip"/> headers: where each tab's clickable
/// rectangle sits given the measured label widths. Kept as a pure computation (no GDI) so the
/// layout and hit-testing are unit-testable, mirroring <see cref="TaskbarBarLayout"/>.
/// All inputs are physical pixels — the control scales its logical metrics before calling.
/// </summary>
internal static class TabStripLayout
{
    /// <summary>
    /// Lays the tabs out left to right from x = 0: each tab is its label width plus
    /// <paramref name="padding"/> on both sides, tabs are <paramref name="gap"/> apart, and every
    /// tab spans the full <paramref name="height"/>.
    /// </summary>
    public static IReadOnlyList<Rectangle> TabBounds(
        IReadOnlyList<int> labelWidths, int padding, int gap, int height)
    {
        var bounds = new List<Rectangle>(labelWidths.Count);
        var x = 0;
        foreach (var labelWidth in labelWidths)
        {
            var tabWidth = labelWidth + 2 * padding;
            bounds.Add(new Rectangle(x, 0, tabWidth, height));
            x += tabWidth + gap;
        }

        return bounds;
    }

    /// <summary>The index of the tab containing the point, or -1 (a gap or outside the tabs).</summary>
    public static int HitTest(IReadOnlyList<Rectangle> bounds, Point point)
    {
        for (var i = 0; i < bounds.Count; i++)
        {
            if (bounds[i].Contains(point))
                return i;
        }

        return -1;
    }
}