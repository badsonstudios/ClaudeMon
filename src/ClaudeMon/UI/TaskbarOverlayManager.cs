namespace ClaudeMon.UI;

using ClaudeMon.Models;
using ClaudeMon.Services;
using Microsoft.Win32;

/// <summary>
/// Owns one <see cref="TaskbarOverlayWindow"/> per Windows taskbar — the primary plus
/// every secondary-monitor taskbar — so the usage readout appears on all of them. The
/// live set is reconciled whenever the display layout changes (monitor plugged or
/// unplugged, "show taskbar on all displays" toggled). This mirrors the
/// <see cref="TaskbarOverlayWindow"/> surface so <see cref="TrayApplication"/> drives the
/// multi-monitor case exactly as it did the single one.
/// </summary>
/// <remarks>
/// All members are expected to run on the UI thread: <see cref="TrayApplication"/> calls
/// them from its constructor, the settings dialog, and the synchronized usage callback,
/// and <see cref="SystemEvents.DisplaySettingsChanged"/> is raised on the UI thread of a
/// WinForms app. Overlays (WinForms <c>Form</c>s) must be created on that thread.
/// </remarks>
public sealed class TaskbarOverlayManager : IDisposable
{
    /// <summary>The latest reading to seed onto overlays created after a monitor connects.</summary>
    private readonly record struct OverlayReading(TaskbarOverlayMarker Marker, TaskbarReading Reading);

    /// <summary>Raised when any overlay is clicked, carrying that readout's screen bounds.</summary>
    public event EventHandler<System.Drawing.Rectangle>? OverlayClicked;

    // Keyed by monitor device name (e.g. \\.\DISPLAY1) — one overlay per taskbar.
    private readonly Dictionary<string, TaskbarOverlayWindow> _overlays = new();
    private readonly Logger _logger;

    // Presentation settings seeded onto every overlay (and any created later).
    private TaskbarTextColor _labelColor = TaskbarTextColor.White;
    private TaskbarTextColor _numberColor = TaskbarTextColor.Auto;
    private TaskbarStyle _style = TaskbarStyle.Numbers;
    private TaskbarBarWidth _barWidth = TaskbarBarWidth.Standard;
    private int _sizePercent = 100;
    private UsageColorMode _colorMode = UsageColorMode.Pace;
    private bool _showSession = true;
    private bool _showWeekly;
    private bool _showTimeToReset;
    private bool _allMonitors;
    private int _primaryHorizontalOffset;
    private int _secondaryHorizontalOffset;
    private bool _enabled;

    // Latest reading, retained so an overlay created after a monitor connects shows the
    // current value immediately instead of staying blank. Starts as the waiting marker so
    // the readout is visibly alive from the moment it appears, before the first poll lands.
    private OverlayReading _reading = new(TaskbarOverlayMarker.Waiting, default);

    private bool _disposed;

    public TaskbarOverlayManager(Logger logger)
    {
        _logger = logger;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Set the text colour presets on every overlay (and on ones created later).</summary>
    public void SetColors(TaskbarTextColor labelColor, TaskbarTextColor numberColor)
    {
        _labelColor = labelColor;
        _numberColor = numberColor;
        foreach (var overlay in _overlays.Values)
            overlay.SetColors(labelColor, numberColor);
    }

    /// <summary>Set the readout style (numbers vs bar) on every overlay (and ones created later).</summary>
    public void SetStyle(TaskbarStyle style)
    {
        _style = style;
        foreach (var overlay in _overlays.Values)
            overlay.SetStyle(style);
    }

    /// <summary>Set the bar-style width on every overlay (and ones created later).</summary>
    public void SetBarWidth(TaskbarBarWidth barWidth)
    {
        _barWidth = barWidth;
        foreach (var overlay in _overlays.Values)
            overlay.SetBarWidth(barWidth);
    }

    /// <summary>Set the readout size (percent) on every overlay (and ones created later).</summary>
    public void SetSize(int percent)
    {
        _sizePercent = percent;
        foreach (var overlay in _overlays.Values)
            overlay.SetSize(percent);
    }

    /// <summary>Set the usage colour mode (pace vs level) on every overlay (and ones created later).</summary>
    public void SetColorMode(UsageColorMode colorMode)
    {
        _colorMode = colorMode;
        foreach (var overlay in _overlays.Values)
            overlay.SetColorMode(colorMode);
    }

    /// <summary>Choose the readout elements (session/weekly/countdown) on every overlay.</summary>
    public void SetDisplay(bool session, bool weekly, bool timeToReset)
    {
        _showSession = session;
        _showWeekly = weekly;
        _showTimeToReset = timeToReset;
        foreach (var overlay in _overlays.Values)
            overlay.SetDisplay(session, weekly, timeToReset);
    }

    /// <summary>
    /// Choose whether the readout appears on every monitor's taskbar (true) or only the
    /// primary (false). Reconciles the live overlay set, so toggling adds or removes the
    /// secondary-monitor readouts immediately.
    /// </summary>
    public void SetAllMonitors(bool allMonitors)
    {
        _allMonitors = allMonitors;
        Reconcile();
    }

    /// <summary>
    /// Set the horizontal position nudges on every overlay (and on ones created later).
    /// Each overlay applies the primary or secondary value by whether its taskbar is
    /// currently the primary — see <see cref="TaskbarOverlayWindow.SetHorizontalOffsets"/>.
    /// </summary>
    public void SetHorizontalOffsets(int primary, int secondary)
    {
        _primaryHorizontalOffset = primary;
        _secondaryHorizontalOffset = secondary;
        foreach (var overlay in _overlays.Values)
            overlay.SetHorizontalOffsets(primary, secondary);
    }

    /// <summary>
    /// Show or hide the taskbar display feature. When enabled, an overlay is created for
    /// every current taskbar; when disabled, all overlays are torn down (so the feature
    /// being off costs no windows at all).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
            Reconcile();
        else
            DisposeAllOverlays();
    }

    /// <summary>Push a fresh usage reading to every overlay.</summary>
    public void UpdateUsage(TaskbarReading reading)
    {
        _reading = new OverlayReading(TaskbarOverlayMarker.None, reading);
        foreach (var overlay in _overlays.Values)
            overlay.UpdateUsage(reading);
    }

    /// <summary>Switch every overlay to the neutral sign-in-expired marker.</summary>
    public void ShowSignInExpired()
    {
        _reading = new OverlayReading(TaskbarOverlayMarker.SignInExpired, default);
        foreach (var overlay in _overlays.Values)
            overlay.ShowSignInExpired();
    }

    /// <summary>
    /// Switch every overlay to the waiting marker — shown when no usage reading is available
    /// (before the first poll, or a poll failed with nothing cached), so the readout stays
    /// visibly alive instead of blank. Replaced automatically by the next
    /// <see cref="UpdateUsage"/>. Does not downgrade the sign-in-expired marker: that state
    /// carries actionable information (re-authenticate) and a transient offline poll while
    /// signed out must not blur it into a generic "waiting".
    /// </summary>
    public void ShowWaiting()
    {
        if (_reading.Marker == TaskbarOverlayMarker.SignInExpired) return;

        _reading = new OverlayReading(TaskbarOverlayMarker.Waiting, default);
        foreach (var overlay in _overlays.Values)
            overlay.ShowWaiting();
    }

    private void OnOverlayClicked(object? sender, System.Drawing.Rectangle bounds) => OverlayClicked?.Invoke(this, bounds);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_enabled) return;

        // Raised mid-hotplug, exactly when overlay creation/positioning is most likely to
        // throw — never let that escape into the system-event callback and crash the app.
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Taskbar overlay reconcile failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the live overlay set in line with the taskbars currently present: creates an
    /// overlay (seeded with the current settings and reading) for any taskbar that has none,
    /// and disposes overlays whose taskbar (monitor) has gone away.
    /// </summary>
    private void Reconcile()
    {
        if (!_enabled) return;

        var present = new HashSet<string>();
        foreach (var taskbar in TaskbarEnumerator.Enumerate())
        {
            // When multi-monitor is off, only the primary taskbar gets a readout.
            if (!_allMonitors && !taskbar.IsPrimary)
                continue;

            present.Add(taskbar.MonitorDevice);
            if (_overlays.ContainsKey(taskbar.MonitorDevice))
                continue;

            // Build the overlay fully before publishing it, so a failure mid-construction
            // disposes the half-built Form rather than leaking an untracked ghost window.
            TaskbarOverlayWindow? overlay = null;
            try
            {
                overlay = new TaskbarOverlayWindow(taskbar.MonitorDevice, _logger);
                overlay.Clicked += OnOverlayClicked;
                Seed(overlay);
                overlay.SetEnabled(true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to create taskbar overlay for {taskbar.MonitorDevice}: {ex.Message}");
                overlay?.Dispose();
                continue;
            }

            _overlays[taskbar.MonitorDevice] = overlay;
        }

        // Tear down overlays whose taskbar is no longer present (monitor unplugged, or its
        // taskbar turned off). ToList so we don't mutate the dictionary while enumerating it.
        foreach (var device in _overlays.Keys.Where(k => !present.Contains(k)).ToList())
        {
            DisposeOverlay(_overlays[device]);
            _overlays.Remove(device);
        }
    }

    /// <summary>Applies the current settings and latest reading to a freshly created overlay.</summary>
    private void Seed(TaskbarOverlayWindow overlay)
    {
        overlay.SetColors(_labelColor, _numberColor);
        overlay.SetStyle(_style);
        overlay.SetBarWidth(_barWidth);
        overlay.SetSize(_sizePercent);
        overlay.SetColorMode(_colorMode);
        overlay.SetDisplay(_showSession, _showWeekly, _showTimeToReset);
        overlay.SetHorizontalOffsets(_primaryHorizontalOffset, _secondaryHorizontalOffset);

        switch (_reading.Marker)
        {
            case TaskbarOverlayMarker.SignInExpired:
                overlay.ShowSignInExpired();
                break;
            case TaskbarOverlayMarker.Waiting:
                overlay.ShowWaiting();
                break;
            default:
                overlay.UpdateUsage(_reading.Reading);
                break;
        }
    }

    private void DisposeAllOverlays()
    {
        foreach (var overlay in _overlays.Values)
            DisposeOverlay(overlay);
        _overlays.Clear();
    }

    private static void DisposeOverlay(TaskbarOverlayWindow overlay)
    {
        overlay.SetEnabled(false);
        overlay.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DisposeAllOverlays();
    }
}
