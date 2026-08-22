namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Monitoring;

/// <summary>One plotted window for the Limit history chart — the form derives these from the loaded page.</summary>
internal sealed record LimitHistorySlot(
    DateTimeOffset End,
    int Series,
    double? Capacity,
    bool LowQuality,
    double WeightedTokens,
    double PeakPercent);

/// <summary>A capacity point in physical pixels; Low points render hollow/dimmed.</summary>
internal readonly record struct LimitChartPoint(float X, float Y, int Series, bool Low);

/// <summary>A plan-change annotation: the vertical marker's x and the plan's name.</summary>
internal readonly record struct LimitChartMarker(float X, string Label);

/// <summary>Everything the capacity-over-time mode paints, in physical pixels.</summary>
internal readonly record struct LimitCapacityGeometry(
    double AxisMax,
    IReadOnlyList<CostChartTick> YTicks,
    IReadOnlyList<LimitChartPoint> Points,
    IReadOnlyList<LimitChartMarker> PlanMarkers,
    IReadOnlyList<int> LabelledSlots);

/// <summary>Everything the utilization mode paints: token bars against a peak-% overlay.</summary>
internal readonly record struct LimitUtilizationGeometry(
    double AxisMax,
    IReadOnlyList<CostChartTick> YTicks,
    IReadOnlyList<RectangleF> Bars,
    IReadOnlyList<PointF> PercentPoints,
    IReadOnlyList<int> LabelledSlots);

/// <summary>
/// Pure geometry for the Limit history chart (issue #186), the <see cref="CostChartLayout"/>
/// pattern: all inputs in physical pixels, all decisions unit-testable, no GDI. One slot per
/// finalized window, evenly spaced — windows are discrete buckets like days are, and even
/// spacing keeps a fortnight away from the keyboard from compressing the recent story.
/// Token ticks reuse <see cref="CostChartLayout.NiceTicks"/> (the 1/2/5×10ⁿ ladder is
/// unit-agnostic); labels format through <see cref="LocalCostText.FormatTokens"/>.
/// </summary>
internal static class LimitHistoryChartLayout
{
    /// <summary>The fraction of a slot the utilization bar fills.</summary>
    private const float BarFill = 0.7f;

    /// <summary>Token-axis label: "0", "500K", "60M".</summary>
    public static string FormatAxisTokens(double tokens) =>
        LocalCostText.FormatTokens((long)Math.Round(Math.Max(0, tokens)));

    /// <summary>
    /// The capacity-over-time geometry: one point per slot that has a derivable capacity,
    /// positioned on the shared slot axis so every series stays aligned with the table's
    /// chronology; plan markers land on their slot's center line.
    /// </summary>
    public static LimitCapacityGeometry ComputeCapacity(
        IReadOnlyList<LimitHistorySlot> slots,
        IReadOnlyList<(int Slot, string Label)> planMarkers,
        IReadOnlyList<double> ticks,
        Rectangle plot,
        int maxLabels)
    {
        var axisMax = ticks is { Count: > 0 } ? ticks[^1] : 0.0;
        if (slots is null || slots.Count == 0 || plot.Width <= 0 || plot.Height <= 0
            || !double.IsFinite(axisMax) || axisMax <= 0)
        {
            return new LimitCapacityGeometry(0.0, [], [], [], []);
        }

        var slotWidth = (float)plot.Width / slots.Count;
        var points = new List<LimitChartPoint>();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Capacity is not { } capacity || !double.IsFinite(capacity) || capacity < 0)
                continue;

            points.Add(new LimitChartPoint(
                SlotCenter(plot, slotWidth, i),
                (float)(plot.Bottom - plot.Height * Math.Min(1.0, capacity / axisMax)),
                slot.Series,
                slot.LowQuality));
        }

        var markers = new List<LimitChartMarker>();
        foreach (var (slotIndex, label) in planMarkers ?? [])
        {
            if (slotIndex >= 0 && slotIndex < slots.Count)
                markers.Add(new LimitChartMarker(SlotCenter(plot, slotWidth, slotIndex), label));
        }

        return new LimitCapacityGeometry(
            axisMax,
            TickRows(ticks, axisMax, plot),
            points,
            markers,
            CostChartLayout.LabelledDays(slots.Count, maxLabels));
    }

    /// <summary>
    /// The utilization geometry: a token bar per window on the left axis, its peak % as an
    /// overlay point on a fixed 0–100 right axis — "how fast did recent windows fill".
    /// </summary>
    public static LimitUtilizationGeometry ComputeUtilization(
        IReadOnlyList<LimitHistorySlot> slots,
        IReadOnlyList<double> ticks,
        Rectangle plot,
        int maxBarWidth,
        int maxLabels)
    {
        var axisMax = ticks is { Count: > 0 } ? ticks[^1] : 0.0;
        if (slots is null || slots.Count == 0 || plot.Width <= 0 || plot.Height <= 0
            || !double.IsFinite(axisMax) || axisMax <= 0)
        {
            return new LimitUtilizationGeometry(0.0, [], [], [], []);
        }

        var slotWidth = (float)plot.Width / slots.Count;
        var width = Math.Max(1f, Math.Min(slotWidth * BarFill, maxBarWidth > 0 ? maxBarWidth : float.MaxValue));

        var bars = new RectangleF[slots.Count];
        var percents = new PointF[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var tokens = double.IsFinite(slots[i].WeightedTokens) ? Math.Max(0.0, slots[i].WeightedTokens) : 0.0;
            var height = tokens <= 0
                ? 0f
                : Math.Min(plot.Height, Math.Max(1f, (float)(plot.Height * (tokens / axisMax))));
            var x = Math.Max(plot.Left, plot.Left + slotWidth * i + (slotWidth - width) / 2f);
            bars[i] = new RectangleF(x, plot.Bottom - height, Math.Min(width, plot.Right - x), height);

            var pct = Math.Clamp(slots[i].PeakPercent, 0.0, 100.0);
            percents[i] = new PointF(
                SlotCenter(plot, slotWidth, i),
                (float)(plot.Bottom - plot.Height * (pct / 100.0)));
        }

        return new LimitUtilizationGeometry(
            axisMax,
            TickRows(ticks, axisMax, plot),
            bars,
            percents,
            CostChartLayout.LabelledDays(slots.Count, maxLabels));
    }

    private static float SlotCenter(Rectangle plot, float slotWidth, int index) =>
        plot.Left + slotWidth * index + slotWidth / 2f;

    private static IReadOnlyList<CostChartTick> TickRows(
        IReadOnlyList<double> ticks, double axisMax, Rectangle plot)
    {
        var rows = new List<CostChartTick>(ticks.Count);
        foreach (var value in ticks)
            rows.Add(new CostChartTick(value, plot.Bottom - (int)Math.Round(plot.Height * (value / axisMax))));
        return rows;
    }
}
