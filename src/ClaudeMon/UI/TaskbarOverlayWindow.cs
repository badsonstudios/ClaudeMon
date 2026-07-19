namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// The "no usage numbers" placeholder an overlay can show instead of a reading: the neutral
/// sign-in-expired em dash, or the waiting "…" shown while no reading exists yet. Both
/// bypass the Numbers/Bar style and are cleared by the next
/// <see cref="TaskbarOverlayWindow.UpdateUsage"/>.
/// </summary>
internal enum TaskbarOverlayMarker
{
    None,
    SignInExpired,
    Waiting,
}

/// <summary>
/// A borderless, always-on-top window that paints the current Claude usage percentage
/// directly over the right end of one Windows taskbar, just left of that taskbar's system
/// tray / clock. One instance is created per taskbar (the primary and each
/// secondary-monitor taskbar) by <see cref="TaskbarOverlayManager"/>. A left-click raises
/// <see cref="Clicked"/> (wired to open the detail flyout) without ever taking focus.
/// </summary>
/// <remarks>
/// This is the lightweight alternative to a deskband COM shell extension: it needs no
/// COM registration and works on Windows 11 (where deskbands are unsupported). The
/// content is drawn with per-pixel alpha via <c>UpdateLayeredWindow</c> so anti-aliased
/// text shows cleanly over the taskbar (a colour-keyed <c>TransparencyKey</c> can't —
/// it fringes anti-aliased glyphs). A low-frequency timer re-asserts the topmost
/// z-order and position so the overlay survives taskbar clicks and layout changes, and
/// the window is marked <c>NonRudeHWND</c> so Win11's Rude Window Manager doesn't bury it
/// behind the primary taskbar (see <see cref="Reposition"/>).
/// The instance re-finds its taskbar each tick by monitor device name, so it follows
/// Explorer restarts (which recreate the taskbar windows) and hides itself while its
/// taskbar is absent. Positioning is best-effort for a horizontal taskbar.
/// </remarks>
public sealed class TaskbarOverlayWindow : Form
{
    private readonly System.Windows.Forms.Timer _keepAliveTimer;

    // The monitor whose taskbar this overlay paints on; re-resolved to a live taskbar
    // window each reposition so the overlay survives Explorer restarts.
    private readonly string _targetMonitorDevice;

    // Best-effort diagnostics for transient native failures; null when not supplied.
    private readonly Logger? _logger;

    // Drives the keep-alive loop and whether Reposition shows or hides the overlay.
    private bool _enabled;

    // User nudges applied to the computed X (positive = right, negative = left). The primary
    // and secondary taskbars each have their own: the primary is anchored exactly to its tray
    // while the secondary anchor is estimated around a non-queryable clock, so one shared
    // value would behave inconsistently. Which one applies is decided per reposition, since
    // the taskbar (and its primary-ness) is re-resolved every tick.
    private int _primaryHorizontalOffset;
    private int _secondaryHorizontalOffset;

    private double? _percentage;
    private double? _fiveHourFraction;
    private double? _sevenDayPercentage;
    private double? _sevenDayFraction;
    private DateTimeOffset? _fiveHourResetAt;

    // Which elements the readout shows (session %, weekly %, reset countdown). All-off is
    // normalized to session-only at composition time so the readout never renders empty.
    private bool _showSession = true;
    private bool _showWeekly;
    private bool _showTimeToReset;
    private bool _showPercentSign;

    private TaskbarOverlayMarker _marker;
    private TaskbarTextColor _labelColor = TaskbarTextColor.White;
    private TaskbarTextColor _numberColor = TaskbarTextColor.Auto;
    private TaskbarStyle _style = TaskbarStyle.Numbers;
    private TaskbarBarWidth _barWidth = TaskbarBarWidth.Standard;
    private UsageColorMode _colorMode = UsageColorMode.Pace;
    private int _x;
    private int _y;
    // Physical (device-pixel) size of the overlay window, pushed to SetWindowPos/UpdateLayeredWindow.
    private int _width = IconRenderer.MinTaskbarWidth;
    private int _height = 40;

    // The readout is laid out in logical (96-DPI) units — the hand-tuned sizes IconRenderer expects
    // — then scaled to physical pixels so it looks the same apparent size on every monitor.
    // _scale is the content scale (monitorDpi / 96 × the user's Size setting); _monitorScale is
    // the DPI factor alone, for things that track real taskbar UI the Size setting must not
    // resize (the secondary-taskbar clock reserve and the horizontal-offset nudge).
    private float _scale = 1f;
    private float _monitorScale = 1f;
    private float _sizeFactor = 1f;
    private int _logicalWidth = IconRenderer.MinTaskbarWidth;
    private int _logicalHeight = 40;

    // Inputs the cached _width was last measured for, so the 500 ms keep-alive tick
    // doesn't re-measure (allocating fonts + a bitmap) when nothing changed. The segments key
    // is the composed number-row texts, which covers the display toggles, the values, AND the
    // countdown's minute rollovers in one comparison.
    private string? _widthSegmentsKey;
    private int _widthHeight;
    private TaskbarOverlayMarker _widthMarker;
    private TaskbarStyle _widthStyle = TaskbarStyle.Numbers;
    private TaskbarBarWidth _widthBarWidth = TaskbarBarWidth.Standard;

    // The segments key last painted, so the keep-alive tick repaints when the countdown text
    // changes (~once a minute) even though the geometry usually doesn't (monospace digits).
    private string? _paintedSegmentsKey;

    // Cached clock reserve for secondary taskbars (whose clock has no queryable window),
    // re-measured only when the taskbar height changes — see IconRenderer.MeasureTaskbarClockReserve.
    private int _clockReserve;
    private int _clockReserveHeight = -1;

    // Dirty-tracking for the 500 ms keep-alive: skip the (now DPI-sized — up to ~4x the pixels at
    // 200%) bitmap rebuild + UpdateLayeredWindow when neither the geometry, DPI scale, taskbar
    // light/dark theme, nor the readout content changed since the last paint. Topmost z-order and
    // NonRudeHWND are still re-asserted every tick (that's the keep-alive's job). Starts dirty so
    // the first paint always happens; content setters mark it dirty.
    private bool _contentDirty = true;
    private int _paintedX, _paintedY, _paintedWidth, _paintedHeight;
    private float _paintedScale = float.NaN;
    private bool _paintedLight;

    public TaskbarOverlayWindow(string targetMonitorDevice, Logger? logger = null)
    {
        _targetMonitorDevice = targetMonitorDevice;
        _logger = logger;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // We drive size/position ourselves via SetWindowPos + UpdateLayeredWindow and scale the
        // content by the target monitor's DPI, so WinForms must not also auto-scale this window.
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(_width, _height);
        Cursor = Cursors.Hand; // signal the readout is clickable

        // Re-glue to the taskbar and re-assert topmost ordering periodically. Clicking
        // the taskbar can push us below it; this brings us back promptly. Guarded by
        // _enabled (not Visible) so the loop keeps running even while we're hidden — that
        // is how we recover when our taskbar momentarily disappears (Explorer restart).
        // This same tick also picks up display-layout changes (resolution/DPI) within 500 ms.
        // The body is wrapped: a transient GDI/Win32 failure (most likely mid-restart) must
        // never escape into the UI message loop and crash the app — the next tick retries.
        _keepAliveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _keepAliveTimer.Tick += (_, _) =>
        {
            if (!_enabled) return;
            try
            {
                Reposition();
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Taskbar overlay reposition failed ({_targetMonitorDevice}): {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Raised on a left-click of the readout, carrying its screen bounds so the flyout can be
    /// anchored just above it. Used to open the detail flyout.
    /// </summary>
    public event EventHandler<Rectangle>? Clicked;

    // Don't steal focus from the taskbar / foreground app when shown.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Layered (per-pixel alpha), out of alt-tab / focus. NOT click-through: the
            // readout is a click target (WM_LBUTTONUP → Clicked); WM_MOUSEACTIVATE is
            // answered with MA_NOACTIVATE so clicking it never steals focus.
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                // Stay a passive readout: don't activate/steal focus when clicked. This also
                // keeps the flyout focused (so it doesn't auto-hide) so the click can toggle it.
                m.Result = MA_NOACTIVATE;
                return;
            case WM_LBUTTONUP:
                // Release the implicit mouse capture taken on the button-press before opening the
                // flyout. This passive overlay otherwise retains capture, so the cursor (its hand
                // shape) and every subsequent click stay routed to the overlay instead of the
                // flyout — leaving the flyout visibly active but mouse-dead (its gear never sees a
                // click, and the readout's hand cursor sticks across the whole flyout).
                ReleaseCapture();
                // Raise asynchronously so the flyout is shown/activated on a clean message-loop
                // turn, after this WS_EX_NOACTIVATE window finishes its own input processing.
                if (IsHandleCreated)
                    BeginInvoke(() => Clicked?.Invoke(this, new Rectangle(_x, _y, _width, _height)));
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Show or hide the overlay live, without restarting the app.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            if (!Visible) Show();
            Reposition();
            _keepAliveTimer.Start();
        }
        else
        {
            _keepAliveTimer.Stop();
            if (Visible) Hide();
        }
    }

    /// <summary>
    /// Update the displayed values and repaint. Which elements actually render is chosen by
    /// <see cref="SetDisplay"/>; the window-elapsed fractions drive the bar style's time tick
    /// and pace colouring, and the reset timestamp feeds the countdown element — all may be
    /// null when the reset time is unknown.
    /// </summary>
    public void UpdateUsage(TaskbarReading reading)
    {
        // A fresh reading clears any placeholder marker (sign-in expired or waiting), so
        // normal display returns automatically once data is available again.
        _marker = TaskbarOverlayMarker.None;
        _percentage = reading.FiveHourPct;
        _fiveHourFraction = reading.FiveHourFraction;
        _sevenDayPercentage = reading.SevenDayPct;
        _sevenDayFraction = reading.SevenDayFraction;
        _fiveHourResetAt = reading.FiveHourResetAt;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Replace the usage readout with a neutral sign-in-expired marker (see
    /// <see cref="IconRenderer.DrawTaskbarSignInExpired"/>) so the overlay never shows a
    /// stale percentage after the Claude Code token expires. Cleared by the next
    /// <see cref="UpdateUsage"/>. Honoured the next time the overlay is shown if currently
    /// disabled.
    /// </summary>
    public void ShowSignInExpired()
    {
        _marker = TaskbarOverlayMarker.SignInExpired;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Replace the usage readout with the waiting marker (see
    /// <see cref="IconRenderer.DrawTaskbarWaiting"/>) shown while no usage reading is
    /// available — the first poll is still outstanding, or polling failed with nothing
    /// cached — so the readout is visibly alive rather than blank. Cleared by the next
    /// <see cref="UpdateUsage"/>. Honoured the next time the overlay is shown if currently
    /// disabled.
    /// </summary>
    public void ShowWaiting()
    {
        _marker = TaskbarOverlayMarker.Waiting;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>Set the text colour presets and repaint live (no restart).</summary>
    public void SetColors(TaskbarTextColor labelColor, TaskbarTextColor numberColor)
    {
        _labelColor = labelColor;
        _numberColor = numberColor;
        _contentDirty = true;
        if (Visible) Redraw();
    }

    /// <summary>
    /// Switch the readout between the number and bar styles. Re-measures the overlay width (the
    /// styles size differently) and repaints live.
    /// </summary>
    public void SetStyle(TaskbarStyle style)
    {
        _style = style;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>Set the bar-style width and re-measure/reposition live (no-op visually for numbers).</summary>
    public void SetBarWidth(TaskbarBarWidth barWidth)
    {
        _barWidth = barWidth;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Set the readout size (percent, clamped to 25–150 so a hand-edited config can't produce a
    /// degenerate scale) and re-measure/reposition live. The factor folds into the content scale
    /// on the next <see cref="Reposition"/>, so it applies to both styles and to the layout
    /// width as well as the paint.
    /// </summary>
    public void SetSize(int percent)
    {
        _sizeFactor = Math.Clamp(percent, 25, 150) / 100f;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Set the usage colour mode (pace vs absolute level). Only the bar style's fill honours it;
    /// the number style uses its own colour presets. Colour-only, so it just repaints.
    /// </summary>
    public void SetColorMode(UsageColorMode colorMode)
    {
        _colorMode = colorMode;
        _contentDirty = true;
        if (Visible) Redraw();
    }

    /// <summary>
    /// Choose which elements the readout shows — session (5-hour) usage, weekly (7-day) usage,
    /// and the reset countdown (Numbers style only; the bar keeps its time tick). Re-measures
    /// the overlay width and repaints live (no restart). All-off falls back to session-only.
    /// </summary>
    public void SetDisplay(bool session, bool weekly, bool timeToReset, bool percentSign)
    {
        _showSession = session;
        _showWeekly = weekly;
        _showTimeToReset = timeToReset;
        _showPercentSign = percentSign;
        _contentDirty = true;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Set the horizontal nudges (positive = right, negative = left) and reposition live.
    /// Which one applies is chosen per reposition by whether this overlay's taskbar is
    /// currently the primary, so the readout stays correctly nudged even if the user changes
    /// which monitor is primary.
    /// </summary>
    public void SetHorizontalOffsets(int primary, int secondary)
    {
        _primaryHorizontalOffset = primary;
        _secondaryHorizontalOffset = secondary;
        if (Visible) Reposition();
    }

    /// <summary>The weekly value to display, or null when the toggle is off or the API didn't return one.</summary>
    private double? WeeklyForDisplay => _showWeekly ? _sevenDayPercentage : null;

    /// <summary>
    /// The countdown element's current text: minute-granular time until the 5-hour reset,
    /// "idle" when the window has expired with no new one started, or the neutral "—" when
    /// the reset time is unknown.
    /// </summary>
    private string CountdownText() =>
        IconRenderer.FormatTaskbarCountdown(
            _fiveHourResetAt is { } resetAt ? resetAt - DateTimeOffset.UtcNow : null);

    /// <summary>
    /// Composes the Numbers-style number row from the enabled elements: session % and weekly %
    /// (each coloured for its own usage level under the Auto preset), the reset countdown in the
    /// neutral label colour, dot-separated. Falls back to session-only rather than an empty row
    /// when nothing is enabled (or the only enabled element has no data).
    /// <paramref name="light"/> is the taskbar theme feeding the MatchTaskbar preset — passed in
    /// (read once per paint) so one frame never mixes themes across the label and segments.
    /// </summary>
    private IconRenderer.TaskbarSegment[] BuildNumberSegments(bool light)
    {
        var pct = _percentage ?? 0;
        var elements = new List<IconRenderer.TaskbarSegment>(3);

        if (_showSession)
            elements.Add(IconRenderer.TaskbarSegment.Percent(pct, IconRenderer.GetTextColor(_numberColor, pct, light), _showPercentSign));

        if (WeeklyForDisplay is { } weekly)
            elements.Add(IconRenderer.TaskbarSegment.Percent(weekly, IconRenderer.GetTextColor(_numberColor, weekly, light), _showPercentSign));

        if (_showTimeToReset)
            elements.Add(new IconRenderer.TaskbarSegment(CountdownText(), IconRenderer.GetTextColor(_labelColor, pct, light)));

        if (elements.Count == 0)
            elements.Add(IconRenderer.TaskbarSegment.Percent(pct, IconRenderer.GetTextColor(_numberColor, pct, light), _showPercentSign));

        return IconRenderer.JoinSegments(elements);
    }

    /// <summary>The measure/repaint cache key for a composed number row — its texts only.</summary>
    private static string SegmentsKey(IconRenderer.TaskbarSegment[] segments) =>
        string.Join("", segments.Select(s => s.Text));

    /// <summary>
    /// Recomputes <see cref="_width"/> from the current readout, but only when the composed
    /// number row, style, or taskbar height changed — measuring allocates fonts and a bitmap,
    /// and this runs on the 500 ms keep-alive tick. The segments are null exactly when the
    /// Numbers row isn't being rendered (bar style, or a placeholder marker).
    /// </summary>
    private void UpdateMeasuredWidth(IconRenderer.TaskbarSegment[]? segments, string? segmentsKey)
    {
        // Measure in logical units (IconRenderer works at 96 DPI), cache on the logical height, and
        // derive the physical window width from the current DPI scale. _width is always refreshed
        // from _logicalWidth because the scale can change (a move to another monitor) even when the
        // logical measurement is unchanged.
        if (_marker != TaskbarOverlayMarker.None)
        {
            if (!(_widthMarker == _marker && _widthHeight == _logicalHeight))
            {
                _logicalWidth = _marker == TaskbarOverlayMarker.SignInExpired
                    ? IconRenderer.MeasureTaskbarSignInExpiredWidth(_logicalHeight)
                    : IconRenderer.MeasureTaskbarWaitingWidth(_logicalHeight);
                _widthMarker = _marker;
                _widthHeight = _logicalHeight;
            }

            _width = Scale(_logicalWidth);
            return;
        }

        if (_widthMarker != TaskbarOverlayMarker.None
            || _widthStyle != _style || _widthBarWidth != _barWidth
            || _widthSegmentsKey != segmentsKey || _widthHeight != _logicalHeight)
        {
            // The bar's width is fixed by the height + width setting (the value doesn't widen it);
            // the number's width grows with the segments shown (non-null whenever we get here
            // in the Numbers style).
            _logicalWidth = _style == TaskbarStyle.Bar
                ? IconRenderer.MeasureTaskbarBarWidth(_logicalHeight, _barWidth)
                : IconRenderer.MeasureTaskbarSegmentsWidth(segments!, _logicalHeight);
            _widthSegmentsKey = segmentsKey;
            _widthHeight = _logicalHeight;
            _widthStyle = _style;
            _widthBarWidth = _barWidth;
            _widthMarker = TaskbarOverlayMarker.None;
        }

        _width = Scale(_logicalWidth);
    }

    /// <summary>Logical → physical pixels for the current monitor's DPI scale.</summary>
    private int Scale(int logical) => DpiScale.Scale(logical, _scale);

    /// <summary>
    /// Clock-reserve width (logical pixels) for a secondary taskbar, cached per logical taskbar
    /// height so the keep-alive tick doesn't re-measure (allocating fonts + a bitmap) when it
    /// hasn't changed. Measured at the monitor-logical height (DPI only, not the Size setting)
    /// because it estimates the real clock; the caller scales the result to physical pixels.
    /// </summary>
    private int ClockReserve(int monitorLogicalHeight)
    {
        if (_clockReserveHeight != monitorLogicalHeight)
        {
            _clockReserve = IconRenderer.MeasureTaskbarClockReserve(monitorLogicalHeight);
            _clockReserveHeight = monitorLogicalHeight;
        }

        return _clockReserve;
    }

    /// <summary>
    /// Positions the overlay against the right edge of its target taskbar, immediately to
    /// the left of the notification area, re-asserts topmost ordering, and repaints. If the
    /// target taskbar isn't present right now (Explorer restarting, or that display's
    /// taskbar was turned off), the overlay hides itself but keeps polling so it reappears
    /// automatically once the taskbar returns.
    /// </summary>
    private void Reposition()
    {
        if (!IsHandleCreated) return;

        var taskbar = TaskbarEnumerator.FindByDevice(_targetMonitorDevice);
        if (taskbar is null || !GetWindowRect(taskbar.Value.Handle, out var rect))
        {
            if (Visible) Hide();
            return;
        }

        // Left edge of the notification area (clock/tray). The primary taskbar exposes a
        // TrayNotifyWnd we can anchor exactly to; secondary taskbars don't (their clock is a
        // windowless XAML surface), so there we reserve estimated clock space at the right.
        int? notifyLeft = null;
        var notifyHwnd = FindWindowEx(taskbar.Value.Handle, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notifyHwnd != IntPtr.Zero && GetWindowRect(notifyHwnd, out var notify))
            notifyLeft = notify.Left;

        // Per-monitor DPI of the target taskbar. Under Per-Monitor-V2 every coordinate here is in
        // real (physical) pixels; we lay the readout out in logical (96-DPI) units and scale to
        // physical by the content scale — the DPI factor times the user's Size setting — so a
        // 150%/200% monitor gets a proportionally larger, crisp readout instead of a
        // bitmap-stretched one, and the Size setting tunes that. The logical layout height is
        // derived from the physical taskbar height at the content scale, so the readout stays
        // vertically centred and can never overflow the taskbar at any size.
        var dpi = GetDpiForWindow(taskbar.Value.Handle);
        _monitorScale = DpiScale.FactorForDpi((int)dpi);
        _scale = _monitorScale * _sizeFactor;

        var taskbarHeight = rect.Bottom - rect.Top; // physical pixels
        _height = taskbarHeight > 0 ? taskbarHeight : _height;
        _logicalHeight = Math.Max(1, (int)Math.Round(_height / _scale));

        // Reserve and offset are physical (they live in the taskbar's physical coordinate space).
        // Both track real taskbar UI (the clock) rather than our content, so they scale by the
        // monitor DPI alone — the Size setting must not move the readout or resize the gap.
        var monitorLogicalHeight = Math.Max(1, (int)Math.Round(_height / _monitorScale));
        var rightReserve = notifyLeft is null
            ? DpiScale.Scale(ClockReserve(monitorLogicalHeight), _monitorScale)
            : 0;

        // The primary and secondary taskbars each take their own nudge (their anchors differ:
        // the primary's is exact, the secondary's is estimated around the clock). Default 0
        // keeps the primary's exact tray anchoring untouched.
        var offset = DpiScale.Scale(
            taskbar.Value.IsPrimary ? _primaryHorizontalOffset : _secondaryHorizontalOffset,
            _monitorScale);

        // Size the overlay to its content so a multi-element readout never clips. The window is
        // right-anchored, so a wider overlay extends leftward and the clock/tray stay put.
        // Re-measure only when the inputs actually change. The composed number row doubles as
        // the countdown's change signal: when its text rolls over a minute the key differs from
        // the painted one, marking the content dirty even though the geometry usually doesn't.
        // Only the Numbers style renders it, so the bar/marker paths skip composing it
        // on every 500 ms tick.
        var segments = _marker == TaskbarOverlayMarker.None && _style == TaskbarStyle.Numbers
            ? BuildNumberSegments(SystemTheme.IsLightWindowsMode())
            : null;
        var segmentsKey = segments is null ? null : SegmentsKey(segments);
        if (segments is not null && segmentsKey != _paintedSegmentsKey)
            _contentDirty = true;
        UpdateMeasuredWidth(segments, segmentsKey);
        (_x, _y) = TaskbarOverlayLayout.Compute(
            new TaskbarRect(rect.Left, rect.Top, rect.Right), notifyLeft, _width, rightReserve, offset);

        if (!Visible) Show();

        // Win11's "Rude Window Manager" (in Explorer) re-asserts the PRIMARY taskbar's
        // topmost z-order over any normal, hit-testable topmost window sitting on it — which
        // would bury this readout behind the taskbar. Secondary taskbars use a lighter path
        // and don't, which is exactly why only the primary's readout went missing once the
        // overlay became clickable (dropping WS_EX_TRANSPARENT). Marking the window
        // NonRudeHWND removes it from that full-screen/"rude" consideration, so Explorer
        // stops re-stacking the taskbar over it. The property is cleared whenever the window
        // is hidden, so it's re-set here on every reposition (after any Show).
        SetProp(Handle, "NonRudeHWND", new IntPtr(1));

        SetWindowPos(
            Handle, HWND_TOPMOST, _x, _y, _width, _height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Repaint only when something visible changed. The z-order re-assert above is cheap and must
        // run every tick; the bitmap rebuild + UpdateLayeredWindow below is the expensive part (and
        // is now ~4x the pixels at 200% DPI), so skip it when geometry, scale, theme, and content
        // all match the last paint.
        if (_contentDirty
            || _x != _paintedX || _y != _paintedY || _width != _paintedWidth || _height != _paintedHeight
            || _scale != _paintedScale || SystemTheme.IsLightWindowsMode() != _paintedLight)
        {
            Redraw();
        }
    }

    /// <summary>Renders the readout to a 32bpp ARGB bitmap and pushes it via UpdateLayeredWindow.</summary>
    private void Redraw()
    {
        // The placeholder markers draw without a percentage; otherwise there's nothing to paint yet.
        if (!IsHandleCreated || (_marker == TaskbarOverlayMarker.None && _percentage is null)) return;

        // Read the taskbar theme once (cached in SystemTheme): it feeds the bar tick contrast and
        // the keep-alive's repaint dirty-check.
        var light = SystemTheme.IsLightWindowsMode();

        using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Draw the readout in logical (96-DPI) coordinates and let the transform scale it up to
            // the physical bitmap for the monitor's DPI. The point-sized fonts render crisply at the
            // scaled size (vector glyphs), so a 150%/200% monitor gets a larger, sharp readout.
            if (_scale != 1f)
                graphics.ScaleTransform(_scale, _scale);

            // Fill with alpha 1 (not 0) so the WHOLE rectangle is a hit target: a layered
            // window hit-tests per-pixel by alpha, and fully-transparent pixels pass clicks
            // through. At 1/255 the fill is visually imperceptible but makes the readout
            // behave like a button — a click (or the hand cursor) anywhere in its bounds counts.
            // Clear ignores the world transform and fills the entire physical bitmap.
            graphics.Clear(Color.FromArgb(1, 0, 0, 0));
            var bounds = new Rectangle(0, 0, _logicalWidth, _logicalHeight);

            if (_marker != TaskbarOverlayMarker.None)
            {
                // Resolve at 0% so the neutral marker isn't usage-coloured under the Auto preset.
                var labelColor = IconRenderer.GetTextColor(_labelColor, 0, light);
                if (_marker == TaskbarOverlayMarker.SignInExpired)
                    IconRenderer.DrawTaskbarSignInExpired(graphics, bounds, labelColor);
                else
                    IconRenderer.DrawTaskbarWaiting(graphics, bounds, labelColor);
            }
            else if (_style == TaskbarStyle.Bar)
            {
                // The session/weekly toggles pick the bars; the countdown doesn't apply (the bar
                // has its own time tick). Nothing enabled (or weekly-only with no weekly data)
                // falls back to the session bar rather than an empty readout.
                var pct = _percentage!.Value;
                var bars = new List<IconRenderer.TaskbarBarSpec>(2);
                if (_showSession)
                    bars.Add(IconRenderer.TaskbarBarSpec.FiveHour(pct, _fiveHourFraction));
                if (WeeklyForDisplay is { } weekly)
                    bars.Add(IconRenderer.TaskbarBarSpec.SevenDay(weekly, _sevenDayFraction));
                if (bars.Count == 0)
                    bars.Add(IconRenderer.TaskbarBarSpec.FiveHour(pct, _fiveHourFraction));

                IconRenderer.DrawTaskbarBar(graphics, bounds, bars, _colorMode, light);
            }
            else
            {
                // Rebuilt (not reused from Reposition) so colour-preset changes that repaint
                // without repositioning (SetColors/SetColorMode) resolve fresh colours.
                var segments = BuildNumberSegments(light);
                var labelColor = IconRenderer.GetTextColor(_labelColor, _percentage!.Value, light);
                IconRenderer.DrawTaskbarSegments(graphics, bounds, labelColor, segments);
                _paintedSegmentsKey = SegmentsKey(segments);
            }
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = _width, cy = _height };
            var source = new POINT { x = 0, y = 0 };
            var dest = new POINT { x = _x, y = _y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(
                Handle, screenDc, ref dest, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }

        // Record what we just painted so the keep-alive tick can skip a redundant repaint until
        // the geometry, DPI scale, taskbar theme, or content changes again.
        _paintedX = _x;
        _paintedY = _y;
        _paintedWidth = _width;
        _paintedHeight = _height;
        _paintedScale = _scale;
        _paintedLight = light;
        _contentDirty = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keepAliveTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // --- Win32 interop ---

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 0x0003;
    private const int WM_LBUTTONUP = 0x0202;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    // Per-monitor DPI of the monitor a window is on (Windows 10 1607+). Returns 0 on failure,
    // in which case we fall back to a 1.0 scale.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
