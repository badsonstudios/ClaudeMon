namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public sealed class FlyoutPanel : Form
{
    private readonly Panel _contentPanel;
    private UsageResponse? _usage;
    private MonitorStatus _status = MonitorStatus.Initializing;
    private DateTimeOffset? _lastUpdated;

    private static readonly Color BackgroundColor = Color.FromArgb(30, 30, 30);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);
    private static readonly Color TextColor = Color.FromArgb(220, 220, 220);
    private static readonly Color DimTextColor = Color.FromArgb(140, 140, 140);
    private static readonly Color BarBackgroundColor = Color.FromArgb(50, 50, 50);

    public FlyoutPanel()
    {
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

        Relayout();
    }

    public void UpdateData(UsageResponse? usage, MonitorStatus status, DateTimeOffset? lastUpdated)
    {
        _usage = usage;
        _status = status;
        _lastUpdated = lastUpdated;
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
            _usage?.FiveHour is not null,
            _usage?.SevenDay is not null);
        _contentPanel.Invalidate();
    }

    // The app currently runs SystemAware, where DeviceDpi is fixed for the process
    // lifetime and this never fires. It's wired up so the flyout re-fits for free
    // if/when the app opts into PerMonitorV2 (tracked separately).
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    public void ShowNear(Point anchor)
    {
        var screen = Screen.FromPoint(anchor).WorkingArea;

        // Position above and to the left of the anchor (typical for tray popups)
        var x = anchor.X - Width / 2;
        var y = anchor.Y - Height - 4;

        // Keep on screen
        if (x + Width > screen.Right) x = screen.Right - Width;
        if (x < screen.Left) x = screen.Left;
        if (y < screen.Top) y = anchor.Y + 4; // flip below if no room above

        Location = new Point(x, y);
        Show();
        Activate();
    }

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
            if (_usage?.FiveHour is not null)
            {
                DrawUsageRow(g, "5-hour", _usage.FiveHour, left, y, contentWidth,
                    labelFont, textBrush, dimBrush, m);
                y += m.RowAdvance;
            }

            if (_usage?.SevenDay is not null)
            {
                DrawUsageRow(g, "7-day", _usage.SevenDay, left, y, contentWidth,
                    labelFont, textBrush, dimBrush, m);
                y += m.RowAdvance;
            }

            if (_usage?.FiveHour is null && _usage?.SevenDay is null)
            {
                g.DrawString("No usage data available", labelFont, dimBrush, left, y);
                y += m.NoDataAdvance;
            }
        }

        // Status & last updated
        y += m.StatusGap;
        var statusText = FormatStatus();
        g.DrawString(statusText, labelFont, dimBrush, left, y);
    }

    private void DrawUsageRow(
        Graphics g, string label, UsageBucket bucket,
        int x, int y, int width,
        Font font, Brush textBrush, Brush dimBrush, FlyoutMetrics m)
    {
        var pct = bucket.UtilizationPct;
        var pctText = $"{pct:F0}%";
        var resetText = bucket.FormatResetCountdown();

        // Label and percentage on the same line
        g.DrawString(label, font, textBrush, x, y);
        var pctSize = g.MeasureString(pctText, font);
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
                var barColor = IconRenderer.GetColorForPercentage(pct);
                using var barFillBrush = new SolidBrush(barColor);
                using var fillPath = CreateRoundedRect(fillRect, barHeight / 2);
                g.FillPath(barFillBrush, fillPath);
            }
        }

        y += barHeight + m.BarBottomGap;

        // Reset countdown
        g.DrawString(resetText, font, dimBrush, x, y);
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
