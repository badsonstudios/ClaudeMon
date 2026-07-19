namespace ClaudeMon.UI;

using System.Runtime.InteropServices;
using ClaudeMon.Models;
using ClaudeMon.Services;
using Microsoft.Win32;

/// <summary>
/// Owns one <see cref="TaskbarOverlayWindow"/> per Windows taskbar — the primary plus
/// every secondary-monitor taskbar — so the usage readout appears on all of them. The
/// live set is reconciled whenever the display layout changes (monitor plugged or
/// unplugged, "show taskbar on all displays" toggled), when Explorer (re)creates the
/// taskbar (the <c>TaskbarCreated</c> broadcast), and on a short retry timer while no
/// taskbar has been found — so starting before Explorer at login still ends with a
/// readout. This mirrors the
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
    private readonly TaskbarCreatedListener _taskbarCreatedListener;

    // Retries Reconcile while the feature is enabled but no taskbar has been found yet.
    // Apps launched from the Run key race Explorer's shell initialization at login: if we
    // start before Explorer has created the taskbar windows, the startup Reconcile finds
    // nothing and would otherwise never run again (the per-overlay keep-alive can only heal
    // overlays that already exist). The TaskbarCreated broadcast below is the canonical
    // recovery hook; this timer backstops the narrow window where that broadcast fires
    // before our listener window exists, and any other "enabled but zero overlays" state.
    private readonly System.Windows.Forms.Timer _emptyRetryTimer;

    // Whether the last Reconcile found no taskbars, so the empty/recovered log lines fire
    // once per transition instead of on every retry tick.
    private bool _noTaskbarsFound;

    // Devices whose overlay-creation failure has already been logged, so a persistent
    // failure doesn't re-WARN on every 2-second retry tick — once per device until it
    // succeeds (or its taskbar goes away and comes back).
    private readonly HashSet<string> _creationFailureLogged = new();

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
    private bool _showPercentSign;
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
        _taskbarCreatedListener = new TaskbarCreatedListener(OnTaskbarCreated);
        _emptyRetryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _emptyRetryTimer.Tick += (_, _) => TryReconcile("Taskbar overlay retry reconcile failed");
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

    /// <summary>
    /// Choose the readout elements (session/weekly/countdown) and whether percentages carry
    /// a trailing % sign, on every overlay.
    /// </summary>
    public void SetDisplay(bool session, bool weekly, bool timeToReset, bool percentSign)
    {
        _showSession = session;
        _showWeekly = weekly;
        _showTimeToReset = timeToReset;
        _showPercentSign = percentSign;
        foreach (var overlay in _overlays.Values)
            overlay.SetDisplay(session, weekly, timeToReset, percentSign);
    }

    /// <summary>
    /// Choose whether the readout appears on every monitor's taskbar (true) or only the
    /// primary (false). Reconciles the live overlay set, so toggling adds or removes the
    /// secondary-monitor readouts immediately.
    /// </summary>
    public void SetAllMonitors(bool allMonitors)
    {
        _allMonitors = allMonitors;
        TryReconcile("Taskbar overlay reconcile on all-monitors change failed");
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
        {
            // Guarded: this runs from the TrayApplication constructor at login, exactly
            // when taskbar enumeration is most likely to misbehave — a throw here must
            // not take the app down (the retry timer will heal it).
            TryReconcile("Taskbar overlay reconcile on enable failed");
        }
        else
        {
            _emptyRetryTimer.Stop();
            // Reset the transition flag so re-enabling with a taskbar present doesn't log
            // a spurious "Taskbar appeared" — nothing appeared, the user toggled a setting.
            // The failure-log dampener resets too: toggling the feature off and on is the
            // natural "try again" gesture, and that retry's diagnostics should be logged.
            _noTaskbarsFound = false;
            _creationFailureLogged.Clear();
            DisposeAllOverlays();
        }
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

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        TryReconcile("Taskbar overlay reconcile failed");

    // Explorer broadcasts TaskbarCreated when it (re)creates the taskbar — at login, and
    // after an Explorer restart. Reconciling here closes the startup race (see the
    // _emptyRetryTimer remarks) with no polling delay.
    private void OnTaskbarCreated() =>
        TryReconcile("Taskbar overlay reconcile after TaskbarCreated failed");

    /// <summary>
    /// Reconcile guarded for event-driven callers (system events, window messages, timer
    /// ticks): raised exactly when overlay creation/positioning is most likely to throw —
    /// never let that escape into the message loop and crash the app.
    /// </summary>
    private void TryReconcile(string failureContext)
    {
        if (_disposed || !_enabled) return;

        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            _logger.Warn($"{failureContext}: {ex.Message}");
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

        // The retry-timer decision lives in the finally: even if reconciling throws partway
        // (a caught-and-logged event path), an empty overlay set must still be retrying —
        // otherwise one bad tick recreates exactly the "nothing ever retries" state this
        // timer exists to eliminate.
        try
        {
            var taskbars = TaskbarEnumerator.Enumerate();

            // Log the empty/recovered transitions once each (not per retry tick), so a single
            // boot's log shows whether the login race was hit and when it healed.
            if (taskbars.Count == 0 && !_noTaskbarsFound)
                _logger.Warn("No taskbars found to overlay (Explorer may still be starting) — retrying until one appears.");
            else if (taskbars.Count > 0 && _noTaskbarsFound)
                _logger.Info("Taskbar appeared — creating overlay(s).");
            _noTaskbarsFound = taskbars.Count == 0;

            var present = new HashSet<string>();
            foreach (var taskbar in taskbars)
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
                    // Once per device until it succeeds — the 2-second retry would otherwise
                    // turn a persistent failure into unbounded WARN spam.
                    if (_creationFailureLogged.Add(taskbar.MonitorDevice))
                        _logger.Warn($"Failed to create taskbar overlay for {taskbar.MonitorDevice}: {ex.Message}");
                    overlay?.Dispose();
                    continue;
                }

                _creationFailureLogged.Remove(taskbar.MonitorDevice);
                _overlays[taskbar.MonitorDevice] = overlay;
            }

            // Tear down overlays whose taskbar is no longer present (monitor unplugged, or its
            // taskbar turned off). ToList so we don't mutate the dictionary while enumerating it.
            foreach (var device in _overlays.Keys.Where(k => !present.Contains(k)).ToList())
            {
                DisposeOverlay(_overlays[device]);
                _overlays.Remove(device);
            }

            // A device that left re-arms its failure log line, so a taskbar that goes away
            // and comes back broken is diagnosable again.
            _creationFailureLogged.RemoveWhere(d => !present.Contains(d));
        }
        finally
        {
            // Keep retrying while enabled with nothing to show (login race, or overlay
            // creation failed); stop as soon as an overlay exists — the per-overlay
            // keep-alive owns recovery from there.
            if (_overlays.Count == 0)
                _emptyRetryTimer.Start();
            else
                _emptyRetryTimer.Stop();
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
        overlay.SetDisplay(_showSession, _showWeekly, _showTimeToReset, _showPercentSign);
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
        _emptyRetryTimer.Dispose();
        _taskbarCreatedListener.Dispose();
        DisposeAllOverlays();
    }

    /// <summary>
    /// A hidden top-level window whose only job is to receive the shell's registered
    /// <c>TaskbarCreated</c> broadcast — sent to all top-level windows when Explorer
    /// (re)creates the taskbar — and invoke the given callback. Message-only
    /// (<c>HWND_MESSAGE</c>) windows never receive broadcasts, so this must be a real,
    /// invisible top-level window. Created and messaged on the UI thread.
    /// </summary>
    private sealed class TaskbarCreatedListener : NativeWindow, IDisposable
    {
        // 0 if registration fails (effectively never); guarded in WndProc so a 0 value
        // can't make WM_NULL (also 0) trigger the callback.
        private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        private readonly Action _onTaskbarCreated;

        public TaskbarCreatedListener(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (TaskbarCreatedMessage != 0 && m.Msg == TaskbarCreatedMessage)
                _onTaskbarCreated();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);
    }
}
