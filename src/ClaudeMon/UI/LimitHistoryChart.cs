namespace ClaudeMon.UI;

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using ClaudeMon.Monitoring;

/// <summary>Which of the tab's two questions the chart is answering.</summary>
internal enum LimitHistoryChartMode
{
    /// <summary>Implied capacity per window over time — the throttle-drift evidence.</summary>
    Capacity,

    /// <summary>Tokens per window (bars) against how full it peaked (% overlay).</summary>
    Utilization,
}

/// <summary>
/// The Limit history chart (issue #186), hand-drawn in the <see cref="CostChart"/> vein:
/// geometry from the pure <see cref="LimitHistoryChartLayout"/>, theme colors, DPI-scaled at
/// paint time. Capacity mode draws one point series per limit kind with plan-change markers
/// and hollow low-confidence points; utilization mode draws token bars with a peak-% overlay
/// on a fixed right axis. Excluded from the coverage gate like <see cref="CostChart"/> — every
/// decision is in the layout class; this only paints.
/// </summary>
internal sealed class LimitHistoryChart : Control
{
    private const int AxisGap = 6;
    private const int LabelGap = 4;
    private const int RightPad = 8;
    private const int MaxBarWidth = 40;
    private const int MinLabelGap = 14;
    private const int MaxYTicks = 5;
    private const int PointRadius = 3;

    private const string EmptyText = "no recorded limit windows yet — they accumulate as you use Claude";
    private const string LowNote = "Hollow points are low-confidence; capacities count only this machine's tokens (est.).";

    // static readonly, NOT const — the coverlet/Cecil trap documented on CostChart.LabelFlags.
    private static readonly TextFormatFlags LabelFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    // One color per series index; series = kind, assigned by the form. Chosen to read on both
    // themes and to keep the first series on the app's accent like the cost chart's bars.
    private static readonly Color[] SeriesColors =
    [
        Color.FromArgb(0xD9, 0x7A, 0x3D), // the app accent family (matches Theme.HeaderAccent tones)
        Color.FromArgb(0x4A, 0x90, 0xD9), // blue
        Color.FromArgb(0x8E, 0x6B, 0xC8), // purple
        Color.FromArgb(0x3D, 0xA8, 0x7E), // teal
        Color.FromArgb(0x9E, 0x9E, 0x9E), // gray for overflow kinds
    ];

    private IReadOnlyList<LimitHistorySlot> _slots = [];
    private IReadOnlyList<string> _seriesLabels = [];
    private IReadOnlyList<(int Slot, string Label)> _planMarkers = [];
    private LimitHistoryChartMode _mode;
    private Font? _axisFont;

    public LimitHistoryChart()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "Limit history";
        UpdateAccessibleDescription();
    }

    /// <summary>The loaded windows to plot (chronological), their series labels, and plan markers.</summary>
    public void SetData(
        IReadOnlyList<LimitHistorySlot> slots,
        IReadOnlyList<string> seriesLabels,
        IReadOnlyList<(int Slot, string Label)> planMarkers)
    {
        _slots = slots;
        _seriesLabels = seriesLabels;
        _planMarkers = planMarkers;
        UpdateAccessibleDescription();
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public LimitHistoryChartMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            UpdateAccessibleDescription();
            Invalidate();
        }
    }

    private Font AxisFont =>
        _axisFont ??= new Font(Font.FontFamily, Math.Max(6.5f, Font.Size - 1f), Font.Style);

    private int Sc(int logical) => DpiScale.Scale(logical, DpiScale.FactorForDpi(DeviceDpi));

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _axisFont?.Dispose();
        _axisFont = null;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;

        var theme = Theme.Current;
        if (_slots.Count == 0)
        {
            TextRenderer.DrawText(
                g, $"({EmptyText})", Font, ClientRectangle, theme.HintText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            DrawFocusCue(g, theme);
            return;
        }

        var axisFont = AxisFont;
        var lineHeight = TextRenderer.MeasureText(g, "0", axisFont, Size.Empty, LabelFlags).Height;

        // The capacity axis scales to the confident points: one wild low-fill extrapolation
        // (a 20× multiplier at the 5% floor) must not compress the real series into the
        // bottom of the plot — low points clamp to the top edge instead (the layout caps at
        // 1.0). Falls back to all points when nothing is confident yet.
        var confidentMax = _slots
            .Where(s => !s.LowQuality)
            .Select(s => s.Capacity ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var maxValue = _mode == LimitHistoryChartMode.Capacity
            ? (confidentMax > 0 ? confidentMax : _slots.Max(s => s.Capacity ?? 0))
            : _slots.Max(s => s.WeightedTokens);
        var ticks = CostChartLayout.NiceTicks(maxValue, MaxYTicks);

        var gutter = 0;
        foreach (var value in ticks)
        {
            gutter = Math.Max(gutter, TextRenderer.MeasureText(
                g, LimitHistoryChartLayout.FormatAxisTokens(value), axisFont, Size.Empty, LabelFlags).Width);
        }

        // Utilization mode reserves a right gutter for the 0–100% axis labels.
        var rightGutter = _mode == LimitHistoryChartMode.Utilization
            ? TextRenderer.MeasureText(g, "100%", axisFont, Size.Empty, LabelFlags).Width + Sc(AxisGap)
            : Sc(RightPad);

        var footnote = lineHeight + Sc(LabelGap);
        var plot = Rectangle.FromLTRB(
            gutter + Sc(AxisGap),
            lineHeight / 2 + Sc(LabelGap) + lineHeight, // headroom for the legend row
            Width - rightGutter,
            Height - footnote - lineHeight - Sc(LabelGap));

        if (plot.Width <= 0 || plot.Height <= 0)
            return;

        var dateWidth = 0;
        foreach (var slot in _slots)
        {
            dateWidth = Math.Max(dateWidth, TextRenderer.MeasureText(
                g, DateLabel(slot.End), axisFont, Size.Empty, LabelFlags).Width);
        }

        var maxLabels = Math.Max(1, plot.Width / Math.Max(1, dateWidth + Sc(MinLabelGap)));

        if (_mode == LimitHistoryChartMode.Capacity)
            PaintCapacity(g, theme, axisFont, lineHeight, ticks, plot, maxLabels);
        else
            PaintUtilization(g, theme, axisFont, lineHeight, ticks, plot, maxLabels);

        DrawLegend(g, theme, axisFont);
        TextRenderer.DrawText(
            g, _mode == LimitHistoryChartMode.Capacity ? LowNote : "Bars: tokens per window · points: peak %",
            axisFont, new Rectangle(0, Height - lineHeight, Width, lineHeight),
            theme.HintText, LabelFlags | TextFormatFlags.EndEllipsis);

        DrawFocusCue(g, theme);
    }

    private void PaintCapacity(
        Graphics g, Theme theme, Font axisFont, int lineHeight,
        IReadOnlyList<double> ticks, Rectangle plot, int maxLabels)
    {
        var geometry = LimitHistoryChartLayout.ComputeCapacity(_slots, _planMarkers, ticks, plot, maxLabels);
        DrawGrid(g, theme, geometry.YTicks, plot, axisFont, lineHeight);

        // Plan markers behind the data: a vertical dashed line with the plan's name at the top.
        using (var markerPen = new Pen(theme.HintText) { DashStyle = DashStyle.Dash })
        {
            foreach (var marker in geometry.PlanMarkers)
            {
                g.DrawLine(markerPen, marker.X, plot.Top, marker.X, plot.Bottom);
                TextRenderer.DrawText(
                    g, marker.Label, axisFont,
                    new Point((int)marker.X + 2, plot.Top), theme.HintText, LabelFlags);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bySeries = geometry.Points.GroupBy(p => p.Series);
        foreach (var series in bySeries)
        {
            var color = SeriesColors[Math.Min(series.Key, SeriesColors.Length - 1)];
            using var line = new Pen(color, Math.Max(1f, Sc(1)));
            using var fill = new SolidBrush(color);
            using var hollow = new Pen(color, Math.Max(1f, Sc(1)));
            using var back = new SolidBrush(BackColor);

            var points = series.ToList();
            for (var i = 1; i < points.Count; i++)
                g.DrawLine(line, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);

            var r = Sc(PointRadius);
            foreach (var point in points)
            {
                if (point.Low)
                {
                    // Hollow: the estimate is derivable but stretched — visually distinct per the AC.
                    g.FillEllipse(back, point.X - r, point.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(hollow, point.X - r, point.Y - r, 2 * r, 2 * r);
                }
                else
                {
                    g.FillEllipse(fill, point.X - r, point.Y - r, 2 * r, 2 * r);
                }
            }
        }

        g.SmoothingMode = SmoothingMode.None;
        DrawDateLabels(g, theme, geometry.LabelledSlots, plot, axisFont);
    }

    private void PaintUtilization(
        Graphics g, Theme theme, Font axisFont, int lineHeight,
        IReadOnlyList<double> ticks, Rectangle plot, int maxLabels)
    {
        var geometry = LimitHistoryChartLayout.ComputeUtilization(_slots, ticks, plot, Sc(MaxBarWidth), maxLabels);
        DrawGrid(g, theme, geometry.YTicks, plot, axisFont, lineHeight);

        using (var bar = new SolidBrush(Color.FromArgb(140, SeriesColors[0])))
        {
            foreach (var rect in geometry.Bars)
            {
                if (rect.Height > 0)
                    g.FillRectangle(bar, rect);
            }
        }

        // The right axis: 0 / 50 / 100% reference labels.
        foreach (var pct in new[] { 0, 50, 100 })
        {
            var y = plot.Bottom - (int)Math.Round(plot.Height * (pct / 100.0));
            TextRenderer.DrawText(
                g, pct.ToString(CultureInfo.InvariantCulture) + "%", axisFont,
                new Point(plot.Right + Sc(AxisGap), y - lineHeight / 2), theme.HintText, LabelFlags);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(SeriesColors[1]))
        using (var line = new Pen(SeriesColors[1], Math.Max(1f, Sc(1))))
        {
            var r = Sc(PointRadius) - 1;
            for (var i = 0; i < geometry.PercentPoints.Count; i++)
            {
                var point = geometry.PercentPoints[i];
                if (i > 0)
                    g.DrawLine(line, geometry.PercentPoints[i - 1].X, geometry.PercentPoints[i - 1].Y, point.X, point.Y);
                g.FillEllipse(fill, point.X - r, point.Y - r, 2 * r, 2 * r);
            }
        }

        g.SmoothingMode = SmoothingMode.None;
        DrawDateLabels(g, theme, geometry.LabelledSlots, plot, axisFont);
    }

    private void DrawGrid(
        Graphics g, Theme theme, IReadOnlyList<CostChartTick> yTicks, Rectangle plot,
        Font axisFont, int lineHeight)
    {
        using var grid = new Pen(theme.Divider);
        using var axis = new Pen(theme.HintText);

        foreach (var tick in yTicks)
        {
            g.DrawLine(tick.Value <= 0 ? axis : grid, plot.Left, tick.Y, plot.Right, tick.Y);
            var text = LimitHistoryChartLayout.FormatAxisTokens(tick.Value);
            var width = TextRenderer.MeasureText(g, text, axisFont, Size.Empty, LabelFlags).Width;
            TextRenderer.DrawText(
                g, text, axisFont,
                new Point(plot.Left - Sc(AxisGap) - width, tick.Y - lineHeight / 2),
                theme.HintText, LabelFlags);
        }
    }

    private void DrawDateLabels(
        Graphics g, Theme theme, IReadOnlyList<int> labelled, Rectangle plot, Font axisFont)
    {
        var top = plot.Bottom + Sc(LabelGap);
        var slotWidth = (float)plot.Width / _slots.Count;
        foreach (var index in labelled)
        {
            if (index >= _slots.Count)
                continue;

            var text = DateLabel(_slots[index].End);
            var width = TextRenderer.MeasureText(g, text, axisFont, Size.Empty, LabelFlags).Width;
            var x = (int)Math.Round(plot.Left + slotWidth * index + slotWidth / 2f - width / 2f);
            x = Math.Clamp(x, 0, Math.Max(0, Width - width));
            TextRenderer.DrawText(g, text, axisFont, new Point(x, top), theme.HintText, LabelFlags);
        }
    }

    // A colored swatch and label per series, in one row across the top of the plot area.
    private void DrawLegend(Graphics g, Theme theme, Font axisFont)
    {
        if (_mode != LimitHistoryChartMode.Capacity || _seriesLabels.Count == 0)
            return;

        var x = 0;
        var y = 0;
        var swatch = Math.Max(6, Sc(8));
        for (var i = 0; i < _seriesLabels.Count; i++)
        {
            var color = SeriesColors[Math.Min(i, SeriesColors.Length - 1)];
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, x, y + swatch / 2, swatch, swatch / 2 + 1);

            var text = _seriesLabels[i];
            var width = TextRenderer.MeasureText(g, text, axisFont, Size.Empty, LabelFlags).Width;
            TextRenderer.DrawText(
                g, text, axisFont, new Point(x + swatch + 3, y), theme.HintText, LabelFlags);
            x += swatch + 3 + width + Sc(12);
            if (x > Width)
                break; // Out of row — remaining labels are in the kind filter anyway.
        }
    }

    private void DrawFocusCue(Graphics g, Theme theme)
    {
        if (!Focused || !ShowFocusCues)
            return;

        var focus = ClientRectangle;
        focus.Inflate(-1, -1);
        ControlPaint.DrawFocusRectangle(g, focus, theme.HintText, BackColor);
    }

    private static string DateLabel(DateTimeOffset end) =>
        end.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);

    private void UpdateAccessibleDescription()
    {
        AccessibleDescription = _slots.Count == 0
            ? "Limit history — no recorded windows yet."
            : _mode == LimitHistoryChartMode.Capacity
                ? $"Implied capacity per window over {_slots.Count} recorded windows, " +
                  $"from {DateLabel(_slots[0].End)} to {DateLabel(_slots[^1].End)}. Estimates only."
                : $"Tokens and peak percentage per window over {_slots.Count} recorded windows.";

        if (IsHandleCreated)
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _axisFont?.Dispose();
    }
}
