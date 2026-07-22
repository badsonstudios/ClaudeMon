namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public sealed class FlyoutPanel : Form
{
    private readonly Panel _contentPanel;
    private readonly Button _settingsButton;
    private readonly Logger? _logger;
    private UsageResponse? _usage;
    // Display rows for the current usage, built once per data update (not per paint).
    private IReadOnlyList<LimitRow> _rows = Array.Empty<LimitRow>();
    private MonitorStatus _status = MonitorStatus.Initializing;
    private DateTimeOffset? _lastUpdated;
    private IReadOnlyList<double> _history = Array.Empty<double>();
    private TimeSpan? _timeToLimit;
    private UsageColorMode _colorMode = UsageColorMode.Pace;
    // Composed once per data update; null = no local cost data, line not drawn.
    private string? _localCostLine;

    /// <summary>Raised when the flyout's settings button is clicked.</summary>
    public event EventHandler? SettingsRequested;

    private static readonly Color BackgroundColor = Color.FromArgb(30, 30, 30);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);
    private static readonly Color TextColor = Color.FromArgb(220, 220, 220);
    private static readonly Color DimTextColor = Color.FromArgb(140, 140, 140);
    private static readonly Color BarBackgroundColor = Color.FromArgb(50, 50, 50);
    private static readonly Color SparklineColor = Color.FromArgb(120, 170, 255);
    // The time-position marker drawn on each usage bar (where "now" is in the reset window).
    private static readonly Color TimeMarkerColor = Color.FromArgb(235, 235, 235);

    // At least two points are needed to draw a line between samples.
    private bool HasHistory => _history.Count >= 2;

    // True while ShowNear is placing/showing the flyout. A DPI change during that sequence is
    // the expected move-onto-another-monitor resize — ShowNear re-clamps with the final size —
    // not a live monitor-settings change that should dismiss the flyout.
    private bool _placing;

    public FlyoutPanel(Logger? logger = null)
    {
        _logger = logger;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BackgroundColor;
        // Scale the layout ourselves from the live DeviceDpi rather than relying on
        // WinForms' construction-time auto-scale (which bakes in whatever DPI context
        // existed at startup and produced the compressed/overlapping first-launch flyout).
        AutoScaleMode = AutoScaleMode.None;

        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
        };
        _contentPanel.Paint += OnPanelPaint;
        Controls.Add(_contentPanel);

        // A quiet gear in the bottom-right corner that opens Settings. Flat and borderless so
        // it reads as part of the dark flyout; brightens on hover.
        _settingsButton = new Button
        {
            Text = "⚙", // gear
            Font = new Font("Segoe UI Symbol", 12f),
            FlatStyle = FlatStyle.Flat,
            ForeColor = DimTextColor,
            BackColor = BackgroundColor,
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.FlatAppearance.MouseOverBackColor = BorderColor;
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _contentPanel.Controls.Add(_settingsButton);

        Relayout();
    }

    public void UpdateData(
        UsageResponse? usage,
        MonitorStatus status,
        DateTimeOffset? lastUpdated,
        IReadOnlyList<double>? history = null,
        TimeSpan? timeToLimit = null,
        UsageColorMode colorMode = UsageColorMode.Pace,
        LocalUsageSnapshot? localUsage = null)
    {
        _usage = usage;
        _rows = usage is null ? Array.Empty<LimitRow>() : LimitDisplay.BuildRows(usage);
        _status = status;
        _lastUpdated = lastUpdated;
        _history = history ?? Array.Empty<double>();
        _timeToLimit = timeToLimit;
        _colorMode = colorMode;
        _localCostLine = LocalCostText.Compose(localUsage);
        Relayout();
    }

    /// <summary>
    /// Re-measures the flyout for the current <see cref="Control.DeviceDpi"/> and
    /// content, sizes the box to fit, and repaints. Called on construction and
    /// whenever the data changes.
    /// </summary>
    private void Relayout()
    {
        var metrics = FlyoutMetrics.ForDpi(DeviceDpi);
        Size = metrics.ContentSize(
            _status == MonitorStatus.AuthError,
            _rows.Count,
            hasForecast: _usage?.FiveHour is not null,
            hasHistory: HasHistory,
            hasLocalCost: _localCostLine is not null);

        // Gear in the right corner, vertically centred on the status line (the bottom-left text)
        // so it reads as part of that row. The point-size glyph scales with DPI on its own; only
        // the pixel box/margin need scaling.
        var scale = DeviceDpi / 96f;
        var btn = DpiScale.Scale(28, scale);
        var margin = DpiScale.Scale(8, scale);
        var statusCentre = Size.Height - metrics.BottomPadding - metrics.StatusLineHeight / 2;
        _settingsButton.Size = new Size(btn, btn);
        _settingsButton.Location = new Point(
            _contentPanel.ClientSize.Width - btn - margin,
            statusCentre - btn / 2);
        _settingsButton.BringToFront();

        _contentPanel.Invalidate();
    }

    // The app runs Per-Monitor-V2 (see ClaudeMon.csproj), so DeviceDpi tracks the monitor the
    // flyout is shown on and this fires when it moves to a differently-scaled display — re-fitting
    // the hand-drawn layout for the new DPI.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Re-fit the hand-drawn layout for the new DPI. If the popup is open, dismiss it too: it's
        // positioned at open time (ShowNear), so a live DPI change would leave it sized for the
        // new DPI but anchored at the old-DPI coordinates — the next open re-fits and re-anchors.
        // During ShowNear's own show sequence the resize is expected and re-clamped there, so the
        // flyout being opened is not dismissed.
        Relayout();
        if (Visible && !_placing)
            Hide();
    }

    public void ShowNear(Point anchor)
    {
        // Resolve the target monitor from the click anchor once; every placement below clamps
        // against this working area so the flyout can never straddle onto a neighbour.
        var area = Screen.FromPoint(anchor).WorkingArea;

        _placing = true;
        try
        {
            Location = FlyoutPlacement.Compute(area, anchor, Size);
            Show();
            // Landing on a differently-scaled monitor fires DpiChanged → Relayout resizes the
            // form after the placement above was computed — re-clamp with the final size so
            // the resized flyout still sits entirely inside the target monitor (issue #104).
            var placed = FlyoutPlacement.Compute(area, anchor, Size);
            if (Location != placed)
                Location = placed;
        }
        finally
        {
            _placing = false;
        }

        ForceForeground();
    }

    /// <summary>
    /// Bring the flyout to the foreground and make it the OS <em>active</em> window, so its child
    /// controls (the gear) receive clicks and it auto-hides on the next deactivate. The opening
    /// click — the tray icon, or a taskbar readout that posts the open on a clean message-loop
    /// turn (see <see cref="TaskbarOverlayWindow"/>) — gives this process the one-shot right to set
    /// the foreground, so a direct <c>SetForegroundWindow</c> suffices; if the lock ever refuses
    /// it, the flyout simply shows inactive and the user's first click activates it the normal way.
    /// </summary>
    /// <remarks>
    /// An earlier version merged input queues via <c>AttachThreadInput</c> to force the foreground.
    /// That was the bug, not the cure: the merge left the flyout reporting active while its input
    /// queue stayed mis-wired, so the gear silently dropped every click on the readout-open path
    /// (the tray-open path skipped the merge — it was already foreground — and always worked).
    /// </remarks>
    private void ForceForeground()
    {
        if (!IsHandleCreated) return;
        var hwnd = Handle;

        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        BringToFront();
        Activate();

        if (GetForegroundWindow() != hwnd)
            _logger?.Warn("Flyout could not take foreground.");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Drop shadow
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    private void OnPanelPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = _contentPanel.ClientRectangle;
        var m = FlyoutMetrics.ForDpi(DeviceDpi);

        // Border
        using var borderPen = new Pen(BorderColor);
        g.DrawRectangle(borderPen, 0, 0, bounds.Width - 1, bounds.Height - 1);

        var left = m.LeftInset;
        var right = bounds.Width - m.LeftInset;
        var contentWidth = right - left;
        var y = m.TopPadding;

        // Title — fonts are sized in points and so scale with the device DPI on
        // their own; the (now DPI-scaled) advances below keep pace with them.
        using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var textBrush = new SolidBrush(TextColor);
        g.DrawString("ClaudeMon", titleFont, textBrush, left, y);
        y += m.TitleAdvance;

        using var labelFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);
        using var dimBrush = new SolidBrush(DimTextColor);

        if (_status == MonitorStatus.AuthError)
        {
            // Auth expired: replace the usage rows with an actionable message rather
            // than showing the last (now-stale) percentages. The message wraps to ~2
            // lines in the content width; the rect height and the y advance match so the
            // status line below never overlaps it.
            var msgRect = new RectangleF(left, y, contentWidth, m.AuthMessageHeight);
            g.DrawString(MonitorStatusText.SignInExpired, labelFont, textBrush, msgRect);
            y += m.AuthMessageHeight;
        }
        else
        {
            foreach (var row in _rows)
            {
                DrawUsageRow(g, row, left, y, contentWidth, labelFont, textBrush, dimBrush, m);
                y += m.RowAdvance;
            }

            if (_rows.Count == 0)
            {
                g.DrawString("No usage data available", labelFont, dimBrush, left, y);
                y += m.NoDataAdvance;
            }

            if (HasHistory)
            {
                y += m.SparklineGap;
                DrawSparkline(g, new Rectangle(left, y, contentWidth, m.SparklineHeight));
                y += m.SparklineHeight;
            }

            // Pace forecast for the 5-hour window: the projected end-of-window % at the current
            // average rate (the limit-avoidance signal) plus the recent burn-rate "time to limit".
            // Coloured by pace so a runaway pace stands out.
            if (_usage?.FiveHour is not null)
            {
                y += m.ForecastGap;
                var fh = _usage.FiveHour;
                var wf = fh.ElapsedFraction(UsageWindows.FiveHour);
                var color = _colorMode == UsageColorMode.Pace && wf is not null
                    ? IconRenderer.GetUsageColor(fh.UtilizationPct, wf, _colorMode)
                    : DimTextColor;
                using var forecastBrush = new SolidBrush(color);
                g.DrawString(FormatForecast(fh.UtilizationPct, wf), labelFont, forecastBrush, left, y);
                y += m.ForecastHeight;
            }

            // Local cost estimate from the Claude Code transcripts — secondary
            // info, so it draws dim like the status line.
            if (_localCostLine is not null)
            {
                y += m.LocalCostGap;
                g.DrawString(_localCostLine, labelFont, dimBrush, left, y);
                y += m.LocalCostHeight;
            }
        }

        // Status & last updated
        y += m.StatusGap;
        var statusText = FormatStatus();
        g.DrawString(statusText, labelFont, dimBrush, left, y);
    }

    /// <summary>
    /// The 5-hour forecast text: the projected end-of-window % at the current average rate
    /// ("on pace" when ≤100%, i.e. you'll reset before exhausting), plus the recent burn-rate
    /// "time to limit" when it's rising. Falls back to just the time-to-limit with no window data.
    /// </summary>
    private string FormatForecast(double pct, double? windowFraction)
    {
        if (windowFraction is not { } wf)
            return $"5-hour: {BurnRate.FormatTimeToLimit(_timeToLimit)}";

        // Pace ratio → a clear gradient. ≤1 means you'll finish the window under the cap (headroom
        // to use more); >1 means you'd hit 100% before reset and get locked out (no overages).
        var ratio = Pace.Ratio(pct, wf);
        var proj = (int)Math.Round(ratio * 100);
        var projText = proj > 200 ? ">200%" : $"{proj}%";
        return $"5-hour: {PaceLabel(ratio)} · projected {projText}";
    }

    private static string PaceLabel(double ratio) => ratio switch
    {
        < 0.6 => "well below pace",
        < 0.9 => "below pace",
        <= 1.1 => "on pace",
        <= 1.5 => "ahead of pace",
        <= 2.0 => "well ahead of pace",
        _ => "way ahead of pace",
    };

    private void DrawSparkline(Graphics g, Rectangle rect)
    {
        var points = Sparkline.BuildPoints(_history, rect);
        if (points.Length < 2)
            return;

        // Faint baseline so a flat/low trend still reads as a chart, not a stray line.
        using var baselinePen = new Pen(BarBackgroundColor);
        g.DrawLine(baselinePen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);

        using var linePen = new Pen(SparklineColor, Math.Max(1f, rect.Height / 18f));
        g.DrawLines(linePen, points);
    }

    private void DrawUsageRow(
        Graphics g, LimitRow row,
        int x, int y, int width,
        Font font, Brush textBrush, Brush dimBrush, FlyoutMetrics m)
    {
        var (label, bucket, window, segments, severity) = row;
        var pct = bucket.UtilizationPct;
        var pctText = $"{pct:F0}%";
        var resetText = bucket.FormatResetCountdown();
        // Unknown window length (an unrecognized bucket kind): no pace fraction, so the bar
        // colours by absolute level and the segment/time-marker visuals are skipped.
        var windowFraction = window is { } w ? bucket.ElapsedFraction(w) : null;

        // Label and percentage on the same line. The label is bounded so an API-supplied model
        // name ("Weekly (<display_name>)") trims with an ellipsis instead of colliding with the
        // right-aligned percent or clipping at the form edge.
        var pctSize = g.MeasureString(pctText, font);
        var labelWidth = Math.Max(0, width - pctSize.Width - m.BarHeight);
        using (var labelFormat = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
        })
        {
            g.DrawString(label, font, textBrush,
                new RectangleF(x, y, labelWidth, m.LabelToBarGap), labelFormat);
        }

        g.DrawString(pctText, font, textBrush, x + width - pctSize.Width, y);

        y += m.LabelToBarGap;

        // Progress bar
        var barHeight = m.BarHeight;
        var barRect = new Rectangle(x, y, width, barHeight);
        using var barBgBrush = new SolidBrush(BarBackgroundColor);
        using var barBgPath = CreateRoundedRect(barRect, barHeight / 2);
        g.FillPath(barBgBrush, barBgPath);

        if (pct > 0)
        {
            var fillWidth = (int)(width * Math.Min(pct, 100) / 100.0);
            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(x, y, fillWidth, barHeight);
                var barColor = IconRenderer.ApplySeverityFloor(
                    IconRenderer.GetUsageColor(pct, windowFraction, _colorMode), severity);
                using var barFillBrush = new SolidBrush(barColor);
                using var fillPath = CreateRoundedRect(fillRect, barHeight / 2);
                g.FillPath(barFillBrush, fillPath);
            }
        }

        // Faint dividers turn the bar into a time ruler (hours on the 5-hour bar, days on the
        // 7-day bar). Semi-transparent so they read on both the coloured fill and the dark track.
        if (segments > 1)
        {
            using var dividerPen = new Pen(Color.FromArgb(90, 0, 0, 0));
            for (var i = 1; i < segments; i++)
            {
                var dx = x + (int)Math.Round(width * (i / (double)segments));
                g.DrawLine(dividerPen, dx, y, dx, y + barHeight);
            }
        }

        // Time marker: where "now" sits in the reset window. Fill past the marker means you're
        // ahead of pace (burning faster than the clock); fill behind it means headroom.
        int? markerX = null;
        if (windowFraction is { } tf)
        {
            markerX = x + (int)Math.Round(width * tf);
            var overhang = Math.Max(2, barHeight / 3);
            using var markerPen = new Pen(TimeMarkerColor, Math.Max(1.5f, barHeight / 6f));
            g.DrawLine(markerPen, markerX.Value, y - overhang, markerX.Value, y + barHeight + overhang);
        }

        y += barHeight + m.BarBottomGap;

        // Reset countdown, centred under the time marker so "time remaining" sits below "where
        // time is" — clamped to the bar's edges so it stops at an edge rather than spilling past.
        var resetWidth = g.MeasureString(resetText, font).Width;
        float resetX = x;
        if (markerX is { } mx && resetWidth <= width)
            resetX = Math.Clamp(mx - resetWidth / 2f, x, x + width - resetWidth);
        g.DrawString(resetText, font, dimBrush, resetX, y);
    }

    private string FormatStatus()
    {
        var parts = new List<string> { MonitorStatusText.Describe(_status) };

        if (_lastUpdated is not null)
        {
            var ago = DateTimeOffset.UtcNow - _lastUpdated.Value;
            var agoText = ago.TotalSeconds < 60 ? "just now"
                : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago"
                : $"{(int)ago.TotalHours}h ago";
            parts.Add($"Updated {agoText}");
        }

        return string.Join("  ·  ", parts);
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        if (bounds.Width < diameter || bounds.Height < diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
