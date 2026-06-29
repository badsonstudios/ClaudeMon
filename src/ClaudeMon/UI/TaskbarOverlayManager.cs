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
    private readonly record struct OverlayReading(bool SignInExpired, TaskbarReading Reading);

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
    private UsageColorMode _colorMode = UsageColorMode.Pace;
    private bool _showSevenDay;
    private bool _allMonitors;
    private int _horizontalOffset;
    private bool _enabled;

    // Latest reading (null until the first poll), retained so an overlay created after a
    // monitor connects shows the current value immediately instead of staying blank.
    private OverlayReading? _reading;

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

    /// <summary>Set the usage colour mode (pace vs level) on every overlay (and ones created later).</summary>
    public void SetColorMode(UsageColorMode colorMode)
    {
        _colorMode = colorMode;
        foreach (var overlay in _overlays.Values)
            overlay.SetColorMode(colorMode);
    }

    /// <summary>Toggle the dual "5hr / 7day" readout on every overlay.</summary>
    public void SetShowSevenDay(bool showSevenDay)
    {
        _showSevenDay = showSevenDay;
        foreach (var overlay in _overlays.Values)
            overlay.SetShowSevenDay(showSevenDay);
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
    /// Set the horizontal position nudge on every overlay (and on ones created later). Only
    /// the secondary-monitor overlays act on it; the primary stays anchored to its tray.
    /// </summary>
    public void SetHorizontalOffset(int offset)
    {
        _horizontalOffset = offset;
        foreach (var overlay in _overlays.Values)
            overlay.SetHorizontalOffset(offset);
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
        _reading = new OverlayReading(SignInExpired: false, reading);
        foreach (var overlay in _overlays.Values)
            overlay.UpdateUsage(reading);
    }

    /// <summary>Switch every overlay to the neutral sign-in-expired marker.</summary>
    public void ShowSignInExpired()
    {
        _reading = new OverlayReading(SignInExpired: true, default);
        foreach (var overlay in _overlays.Values)
            overlay.ShowSignInExpired();
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
        overlay.SetColorMode(_colorMode);
        overlay.SetShowSevenDay(_showSevenDay);
        overlay.SetHorizontalOffset(_horizontalOffset);

        if (_reading is { } reading)
        {
            if (reading.SignInExpired)
                overlay.ShowSignInExpired();
            else
                overlay.UpdateUsage(reading.Reading);
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
