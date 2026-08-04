namespace ClaudeMon.UI;

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

/// <summary>
/// A themed GDI+ column chart of cost per day for the Usage &amp; costs window: one bar per
/// local calendar day of the selected timeframe, a dated x-axis, and a cost y-axis on round
/// gridlines. Hand-drawn in the vein of <see cref="Sparkline"/> and <see cref="ToggleSwitch"/>
/// rather than pulling in a charting dependency, and hand-scaled by the monitor DPI like the
/// rest of the app's custom painting. Geometry comes from <see cref="CostChartLayout"/>.
///
/// Columns (not a line) so that a single day — the "Today" timeframe — is still a real chart
/// rather than a one-point line, and because a day's spend is a discrete bucket, not a sample
/// of a continuous curve. Days whose cost is a floor because an unpriced model contributed are
/// hatched and called out in a footnote, so they can't be read as exact — the same promise the
/// tables make with their "≥$x".
/// </summary>
internal sealed class CostChart : Control
{
    // Logical (96-DPI) metrics, scaled by the current DeviceDpi in Sc.
    private const int AxisGap = 6;      // between the y labels and the plot
    private const int LabelGap = 4;     // between the plot and the x labels
    private const int RightPad = 8;     // headroom for the last x label to centre into
    private const int MaxBarWidth = 56; // caps the "Today" single-bar case
    private const int MinLabelGap = 14; // horizontal breathing room between x labels
    private const int MaxYTicks = 5;

    private const string EmptyNoData = "no local usage data";
    private const string EmptyUnpriced = "cost unavailable — only unpriced models were used";
    private const string FloorNote = "Hatched days include unpriced models — their cost is a floor (≥).";

    private const TextFormatFlags LabelFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    private LocalCostSeries? _series;
    private Font? _axisFont;

    public CostChart()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        // Focusable on purpose. The chart is the whole of its tab, so if Tab skipped it the only
        // textual rendering of the data — the accessible description below — would be unreachable
        // by keyboard, and a screen-reader user would go straight from the tab strip to the
        // buttons past a chart they were never told about.
        TabStop = true;
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "Cost per day";
        UpdateAccessibleDescription();
    }

    /// <summary>
    /// The days to plot, or null when there is nothing to show (no transcripts). Assigning
    /// repaints; the parent hands over a fresh series whenever the timeframe changes.
    /// </summary>
    // Runtime-only control (no designer), so nothing serializes this.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public LocalCostSeries? Series
    {
        get => _series;
        set
        {
            _series = value;
            UpdateAccessibleDescription();
            Invalidate();
        }
    }

    // Axis labels sit a notch below the body font so they recede behind the bars.
    private Font AxisFont =>
        _axisFont ??= new Font(Font.FontFamily, Math.Max(6.5f, Font.Size - 1f), Font.Style);

    private int Sc(int logical) => DpiScale.Scale(logical, DpiScale.FactorForDpi(DeviceDpi));

    // Every metric here is scaled from DeviceDpi at paint time, so a move to a differently
    // scaled monitor has to repaint even when the control's size happens not to change.
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
        // Everything drawn here is an axis-aligned rectangle or a 1px line, so antialiasing
        // would only blur it. Set once — nothing below wants it back on.
        g.SmoothingMode = SmoothingMode.None;

        var theme = Theme.Current;
        var series = _series;
        var reason = EmptyReason(series);
        if (series is null || reason is not null)
        {
            TextRenderer.DrawText(
                g, $"({reason ?? EmptyNoData})", Font, ClientRectangle, theme.HintText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            DrawFocusCue(g, theme);
            return;
        }

        var axisFont = AxisFont;
        var lineHeight = TextRenderer.MeasureText(g, "$0", axisFont, Size.Empty, LabelFlags).Height;
        var floors = series.HasUnpricedModels;

        // The axis is resolved before the plot rectangle because its widest label is what
        // decides the left gutter; the same tick list then goes into Compute, so the
        // gridlines and the bars are guaranteed to share one scale.
        var ticks = CostChartLayout.NiceTicks(series.MaxCostUsd, MaxYTicks);
        var gutter = 0;
        foreach (var value in ticks)
        {
            gutter = Math.Max(gutter, TextRenderer
                .MeasureText(g, CostChartLayout.FormatAxisCost(value), axisFont, Size.Empty, LabelFlags).Width);
        }

        var footnoteHeight = floors ? lineHeight + Sc(LabelGap) : 0;
        var plot = Rectangle.FromLTRB(
            gutter + Sc(AxisGap),
            // Half a line of headroom so the topmost tick label, centred on the top
            // gridline, isn't clipped by the control's edge.
            lineHeight / 2,
            Width - Sc(RightPad),
            Height - footnoteHeight - lineHeight - Sc(LabelGap));

        if (plot.Width <= 0 || plot.Height <= 0)
            return; // Too small to draw anything honest.

        // How many dated labels fit without colliding. Measured on the widest label in the
        // series, not just the last one: month abbreviations vary in width, so a series
        // that straddles a month boundary can't be sized from one sample.
        var dateWidth = 0;
        foreach (var day in series.Days)
        {
            dateWidth = Math.Max(dateWidth, TextRenderer
                .MeasureText(g, DateLabel(day.Date), axisFont, Size.Empty, LabelFlags).Width);
        }

        var maxLabels = Math.Max(1, plot.Width / Math.Max(1, dateWidth + Sc(MinLabelGap)));

        var geometry = CostChartLayout.Compute(
            series.Days.Select(d => d.CostUsd).ToList(),
            ticks,
            plot,
            Sc(MaxBarWidth),
            maxLabels);

        DrawGrid(g, theme, geometry, plot, axisFont, lineHeight);
        DrawBars(g, theme, geometry, series);
        DrawDateLabels(g, theme, geometry, series, plot, axisFont);

        if (floors)
        {
            // Ellipsized rather than spilling past the edge when the window is narrow.
            TextRenderer.DrawText(
                g, FloorNote, axisFont,
                new Rectangle(0, Height - lineHeight, Width, lineHeight),
                theme.HintText, LabelFlags | TextFormatFlags.EndEllipsis);
        }

        DrawFocusCue(g, theme);
    }

    // Focus ring only for keyboard users — Windows suppresses focus cues until the keyboard is
    // used, so tabbing here shows the chart is focused while a click leaves no dotted box behind.
    // Same treatment as TabStrip's active tab.
    private void DrawFocusCue(Graphics g, Theme theme)
    {
        if (!Focused || !ShowFocusCues)
            return;

        var focus = ClientRectangle;
        focus.Inflate(-1, -1);
        ControlPaint.DrawFocusRectangle(g, focus, theme.HintText, BackColor);
    }

    private void DrawGrid(
        Graphics g, Theme theme, CostChartGeometry geometry, Rectangle plot, Font axisFont, int lineHeight)
    {
        using var grid = new Pen(theme.Divider);
        using var axis = new Pen(theme.HintText);

        foreach (var tick in geometry.YTicks)
        {
            // The zero line is the axis itself, so it reads a shade stronger than the
            // gridlines above it.
            g.DrawLine(tick.Value <= 0 ? axis : grid, plot.Left, tick.Y, plot.Right, tick.Y);

            var text = CostChartLayout.FormatAxisCost(tick.Value);
            var width = TextRenderer.MeasureText(g, text, axisFont, Size.Empty, LabelFlags).Width;
            TextRenderer.DrawText(
                g, text, axisFont,
                new Point(plot.Left - Sc(AxisGap) - width, tick.Y - lineHeight / 2),
                theme.HintText, LabelFlags);
        }
    }

    private void DrawBars(Graphics g, Theme theme, CostChartGeometry geometry, LocalCostSeries series)
    {
        using var solid = new SolidBrush(theme.HeaderAccent);
        using var hatched = new HatchBrush(HatchStyle.LightUpwardDiagonal, theme.HeaderAccent, BackColor);

        for (var i = 0; i < geometry.Bars.Count && i < series.Days.Count; i++)
        {
            var bar = geometry.Bars[i];
            if (bar.Height <= 0)
                continue;

            g.FillRectangle(series.Days[i].HasUnpricedModels ? hatched : solid, bar);
        }
    }

    private void DrawDateLabels(
        Graphics g, Theme theme, CostChartGeometry geometry, LocalCostSeries series,
        Rectangle plot, Font axisFont)
    {
        var top = plot.Bottom + Sc(LabelGap);
        foreach (var index in geometry.LabelledDays)
        {
            if (index >= series.Days.Count || index >= geometry.Bars.Count)
                continue;

            var text = DateLabel(series.Days[index].Date);
            var width = TextRenderer.MeasureText(g, text, axisFont, Size.Empty, LabelFlags).Width;
            var bar = geometry.Bars[index];
            // Centred under its bar, but never allowed off either edge of the control.
            var x = (int)Math.Round(bar.Left + bar.Width / 2f - width / 2f);
            x = Math.Clamp(x, 0, Math.Max(0, Width - width));

            TextRenderer.DrawText(g, text, axisFont, new Point(x, top), theme.HintText, LabelFlags);
        }
    }

    // Dates are the one thing here that reads better localized — costs stay invariant to
    // match the rest of the app's money formatting.
    private static string DateLabel(DateOnly date) => date.ToString("MMM d", CultureInfo.CurrentCulture);

    /// <summary>
    /// Null when there is a chart to draw, otherwise why there isn't. The painted empty
    /// state and the accessible description both read from this, so they can't tell
    /// different stories about the same data.
    /// </summary>
    private static string? EmptyReason(LocalCostSeries? series)
    {
        // Nothing priced across the whole timeframe leaves no axis worth drawing, so the
        // control says why instead of showing an empty grid.
        if (series is null || series.Days.Count == 0 || series.MaxCostUsd <= 0)
            return series is not null && series.HasUnpricedModels ? EmptyUnpriced : EmptyNoData;

        return null;
    }

    // Screen readers can't see the bars, so the description carries the shape of the data.
    // Money reads through LocalCostText, like every other cost the app speaks aloud.
    private void UpdateAccessibleDescription()
    {
        var series = _series;
        var reason = EmptyReason(series);
        AccessibleDescription = series is null || reason is not null
            ? $"Cost per day — {reason ?? EmptyNoData}."
            : $"Cost per day from {DateLabel(series.FromDate)} to {DateLabel(series.ToDate)}. "
                + $"Highest day {LocalCostText.FormatCost(series.MaxCostUsd)}, "
                + $"total {LocalCostText.FormatCost(series.TotalCostUsd)}"
                + (series.HasUnpricedModels ? ", including days with unpriced models." : ".");

        // The parent re-assigns Series on every timeframe change; without this a screen
        // reader keeps reading the description it cached when the window opened.
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
