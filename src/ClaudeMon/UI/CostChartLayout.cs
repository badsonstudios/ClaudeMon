namespace ClaudeMon.UI;

using System.Drawing;
using System.Globalization;

/// <summary>One y-axis gridline: the cost it marks and the pixel row it sits on.</summary>
internal readonly record struct CostChartTick(double Value, int Y);

/// <summary>
/// Everything <see cref="CostChart"/> paints, in physical pixels: the cost the top of
/// the plot represents, the gridlines, one bar per day, and which days can afford an
/// x-axis label.
/// </summary>
internal readonly record struct CostChartGeometry(
    double AxisMax,
    IReadOnlyList<CostChartTick> YTicks,
    IReadOnlyList<RectangleF> Bars,
    IReadOnlyList<int> LabelledDays);

/// <summary>
/// Pure geometry for the cost-over-time chart: round y-axis tick values, their pixel
/// rows, a bar rectangle per day, and how many x labels fit. Kept free of GDI so the
/// value→pixel mapping is unit-testable, mirroring <see cref="TaskbarBarLayout"/> and
/// <see cref="TabStripLayout"/>. All inputs are physical pixels — the control scales its
/// logical metrics before calling.
/// </summary>
internal static class CostChartLayout
{
    /// <summary>
    /// The finest the axis ever subdivides. Cents are the smallest unit the app talks
    /// about (<c>LocalCostText</c> renders anything below half a cent as "&lt;$0.01"), so
    /// a day of near-zero spend gets a $0–$0.01 axis rather than one labelled in mills.
    /// </summary>
    internal const double MinTickStep = 0.01;

    /// <summary>The fraction of a day's slot the bar fills; the rest is the gap.</summary>
    private const float BarFill = 0.7f;

    // Tolerance for the "is this already a round number" comparisons below. Tick values
    // are sums of a step that is rarely exact in binary (0.1 + 0.1 + 0.1 = 0.30000000000000004),
    // so every magnitude test needs a little slack or it picks the next step up.
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Lays out <paramref name="costs"/> (one entry per day, oldest first) as columns in
    /// <paramref name="plot"/>, scaled against <paramref name="ticks"/> — the axis from
    /// <see cref="NiceTicks"/>, whose top is a round number at or above the costliest day —
    /// rather than against the raw maximum, so bars and gridlines can't disagree. The caller
    /// passes the ticks in because it has to measure their labels to know where the plot
    /// starts. Returns empty geometry for no days, no axis, or a degenerate rectangle; the
    /// control draws its empty state instead.
    /// </summary>
    public static CostChartGeometry Compute(
        IReadOnlyList<double> costs, IReadOnlyList<double> ticks, Rectangle plot,
        int maxBarWidth, int maxLabels)
    {
        var axisMax = ticks is { Count: > 0 } ? ticks[^1] : 0.0;
        if (costs is null || costs.Count == 0 || plot.Width <= 0 || plot.Height <= 0
            || !double.IsFinite(axisMax) || axisMax <= 0)
        {
            return new CostChartGeometry(0.0, [], [], []);
        }

        return new CostChartGeometry(
            axisMax,
            TickRows(ticks, axisMax, plot),
            BarRects(costs, axisMax, plot, maxBarWidth),
            LabelledDays(costs.Count, maxLabels));
    }

    /// <summary>
    /// Gridline values from $0 up to at or just above <paramref name="maxValue"/>, stepping
    /// by a round 1/2/5 × 10ⁿ amount (never finer than <see cref="MinTickStep"/>).
    /// <paramref name="maxTicks"/> is the ceiling on gridlines including the zero line; the
    /// result can come in under it, since a round step matters more than an exact count.
    /// Always returns at least two values, so even an all-zero series has a real axis.
    /// </summary>
    public static IReadOnlyList<double> NiceTicks(double maxValue, int maxTicks)
    {
        // Two lines (zero plus a top) is the least that can still be an axis.
        var lines = Math.Max(2, maxTicks);
        if (!double.IsFinite(maxValue) || maxValue <= 0)
            return [0.0, MinTickStep];

        var step = NiceStep(maxValue / (lines - 1));
        // The step is rounded up, so this can only come out at or below lines - 1.
        var count = Math.Max(1, (int)Math.Ceiling(maxValue / step - Epsilon));

        var ticks = new double[count + 1];
        for (var i = 0; i <= count; i++)
            ticks[i] = Math.Round(i * step, 10);

        return ticks;
    }

    /// <summary>
    /// Which day indexes get an x-axis label — at most <paramref name="maxLabels"/>, evenly
    /// strided <i>backwards</i> from the last day so today is always labelled (the day the
    /// eye looks for first). Even spacing wins over hitting the cap exactly, so a 7-day
    /// series with room for 6 labels shows 4 rather than bunching them.
    /// </summary>
    public static IReadOnlyList<int> LabelledDays(int count, int maxLabels)
    {
        if (count <= 0 || maxLabels <= 0)
            return [];
        if (count <= maxLabels)
            return Enumerable.Range(0, count).ToList();

        var stride = (int)Math.Ceiling(count / (double)maxLabels);
        var indexes = new List<int>();
        for (var i = count - 1; i >= 0; i -= stride)
            indexes.Add(i);

        indexes.Reverse();
        return indexes;
    }

    /// <summary>
    /// An axis label: "$0", "$0.50", "$5", "$250" — cents only when the step needs them.
    /// Culture-invariant, matching <c>LocalCostText</c>: the app's money always reads the
    /// same way regardless of locale.
    /// </summary>
    public static string FormatAxisCost(double usd)
    {
        var rounded = Math.Round(usd, 2, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.005
            ? "$" + rounded.ToString("0", CultureInfo.InvariantCulture)
            : "$" + rounded.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // The smallest round (1/2/5 × 10ⁿ) amount at or above raw, floored at one cent.
    private static double NiceStep(double raw)
    {
        if (raw <= MinTickStep)
            return MinTickStep;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude; // 1 ≤ normalized < 10
        var multiple =
            normalized <= 1 + Epsilon ? 1
            : normalized <= 2 + Epsilon ? 2
            : normalized <= 5 + Epsilon ? 5
            : 10;

        return Math.Max(MinTickStep, multiple * magnitude);
    }

    // Compute has already established that axisMax is finite and positive.
    private static IReadOnlyList<CostChartTick> TickRows(
        IReadOnlyList<double> ticks, double axisMax, Rectangle plot)
    {
        var rows = new List<CostChartTick>(ticks.Count);
        foreach (var value in ticks)
        {
            // Inverted: $0 on the bottom edge, the axis top on the top edge.
            var y = plot.Bottom - (int)Math.Round(plot.Height * (value / axisMax));
            rows.Add(new CostChartTick(value, y));
        }

        return rows;
    }

    private static IReadOnlyList<RectangleF> BarRects(
        IReadOnlyList<double> costs, double axisMax, Rectangle plot, int maxBarWidth)
    {
        var slot = (float)plot.Width / costs.Count;
        // Without the cap a single day (the "Today" timeframe) would be one plot-wide
        // slab, which reads as a filled panel rather than as a bar.
        var width = slot * BarFill;
        if (maxBarWidth > 0)
            width = Math.Min(width, maxBarWidth);
        width = Math.Max(1f, width);

        var bars = new RectangleF[costs.Count];
        for (var i = 0; i < costs.Count; i++)
        {
            // A non-finite cost can't come out of the store, but NaN would survive every
            // comparison below and reach GDI as a NaN rectangle — treat it as no spend.
            var value = double.IsFinite(costs[i]) ? Math.Max(0.0, costs[i]) : 0.0;
            // Any spend at all draws at least a hairline, so a cheap day beside an
            // expensive one still reads as "some"; a $0 day draws nothing. The upper
            // clamp matters because the tick step is rounded with a hair of slack, so
            // the axis top can land a few ulps below the costliest day.
            var height = value <= 0
                ? 0f
                : Math.Min(plot.Height, Math.Max(1f, (float)(plot.Height * (value / axisMax))));

            // Clamped into the plot: below ~1.5px per day the 1px bar floor is wider than
            // the slot, which would otherwise push the first bar out through the axis.
            var x = Math.Max(plot.Left, plot.Left + slot * i + (slot - width) / 2f);
            var barWidth = Math.Min(width, plot.Right - x);
            bars[i] = new RectangleF(x, plot.Bottom - height, barWidth, height);
        }

        return bars;
    }
}
