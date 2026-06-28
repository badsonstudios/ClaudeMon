namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Pure geometry for a compact line sparkline: maps a series of utilization
/// values (0–100) to points inside a rectangle, with the y-axis inverted so
/// higher utilization is drawn higher. Kept free of GDI+ so it is unit-testable.
/// </summary>
public static class Sparkline
{
    private const double MaxPct = 100.0;

    /// <summary>
    /// Maps <paramref name="values"/> evenly across the width of <paramref name="rect"/>,
    /// scaling each value against a fixed 0–100 axis. Returns an empty array for
    /// fewer than two points (nothing to draw a line between).
    /// </summary>
    /// <remarks>
    /// The x-axis is sample <i>index</i>, not elapsed time: points are spaced evenly
    /// regardless of the gap between samples. With steady polling this matches wall
    /// time closely; after an offline gap a long interval renders the same width as a
    /// short one. This is an intentional simplification for a compact sparkline.
    /// </remarks>
    public static PointF[] BuildPoints(IReadOnlyList<double> values, Rectangle rect)
    {
        if (values is null || values.Count < 2 || rect.Width <= 0 || rect.Height <= 0)
            return Array.Empty<PointF>();

        var points = new PointF[values.Count];
        var stepX = (float)rect.Width / (values.Count - 1);

        for (var i = 0; i < values.Count; i++)
        {
            var clamped = Math.Clamp(values[i], 0.0, MaxPct);
            var x = rect.Left + stepX * i;
            // Invert: 0% sits on the bottom edge, 100% on the top edge.
            var y = rect.Bottom - (float)(clamped / MaxPct) * rect.Height;
            points[i] = new PointF(x, y);
        }

        return points;
    }
}
