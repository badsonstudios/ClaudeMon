namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Pure placement math for the usage flyout. The flyout opens centred above its click anchor
/// and must land entirely inside the working area of the monitor that owns the anchor — never
/// straddling a monitor boundary (issue #104). Kept pure (same pattern as
/// <see cref="DialogPlacement.CenterIn"/>) so the edge/flip/oversize cases are unit-testable;
/// callers re-run it whenever the flyout's size changes between compute and show (a move onto
/// a differently-scaled monitor resizes the form under Per-Monitor-V2).
/// </summary>
internal static class FlyoutPlacement
{
    /// <summary>Gap in pixels between the anchor point and the nearest flyout edge.</summary>
    internal const int AnchorGap = 4;

    /// <summary>
    /// The top-left that places a flyout of <paramref name="size"/> centred above
    /// <paramref name="anchor"/>, flipped below when there's no room above, and clamped so it
    /// never leaves <paramref name="area"/>. When the flyout is larger than the area, the
    /// top/left edges win so content starts on-screen.
    /// </summary>
    public static Point Compute(Rectangle area, Point anchor, Size size)
    {
        var x = anchor.X - size.Width / 2;
        var y = anchor.Y - size.Height - AnchorGap;

        x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));

        if (y < area.Top)
            y = anchor.Y + AnchorGap; // flip below if no room above
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));

        return new Point(x, y);
    }
}
