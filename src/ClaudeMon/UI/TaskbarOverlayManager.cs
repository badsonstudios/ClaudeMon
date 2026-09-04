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
/// taskbar (the <c>TaskbarCreated</c> broadcast), when the machine resumes or the session is
/// unlocked, and on a steady health timer that both creates missing readouts and rebuilds
/// broken ones — so starting before Explorer at login, or waking from sleep, still ends with a
/// readout. This mirrors the
/// <see cref="TaskbarOverlayWindow"/> surface so <see cref="TrayApplication"/> drives the
/// multi-monitor case exactly as it did the single one.
/// </summary>
/// <remarks>
/// All members are expected to run on the UI thread: <see cref="TrayApplication"/> calls
/// them from its constructor, the settings dialog, and the synchronized usage callback,
/// and the <see cref="SystemEvents"/> notifications used here (display settings, power mode,
/// session switch) are raised on the UI thread of a WinForms app — <c>SystemEvents</c> shares
/// the STA thread that first subscribes rather than starting one of its own. Overlays (WinForms
/// <c>Form</c>s) must be created on that thread.
/// </remarks>
public sealed class TaskbarOverlayManager : IDisposable
{
    /// <summary>The latest reading to seed onto overlays created after a monitor connects.</summary>
    private readonly record struct OverlayReading(TaskbarOverlayMarker Marker, TaskbarReading Reading);

    /// <summary>Raised when any overlay is clicked, carrying that readout's screen bounds.</summary>
    public event EventHandler<System.Drawing.Rectangle>? OverlayClicked;

    /// <summary>
    /// Raised when any overlay is middle-clicked (or Ctrl+left-clicked): the request to cycle
    /// the readout's metric. Not per-monitor — the metric is one setting, so cycling on any
    /// taskbar moves every readout together.
    /// </summary>
    public event EventHandler? CycleRequested;

    // Keyed by monitor device name (e.g. \\.\DISPLAY1) — one overlay per taskbar.
    private readonly Dictionary<string, TaskbarOverlayWindow> _overlays = new();
    private readonly Logger _logger;
    private readonly TaskbarCreatedListener _taskbarCreatedListener;

    // Re-reconciles every couple of seconds for as long as the feature is enabled. It has two
    // jobs, and the second one is why #62's fix wasn't enough (#199):
    //  - Create readouts that don't exist yet. Apps launched from the Run key race Explorer's
    //    shell initialization at login: start before Explorer has created the taskbar windows
    //    and the startup Reconcile finds nothing. The TaskbarCreated broadcast below is the
    //    canonical recovery hook; this backstops the narrow window where that broadcast fires
    //    before our listener window exists, and any other "enabled but zero overlays" state.
    //  - Rebuild readouts that exist but are broken. #62's backstops only ever ran while the
    //    overlay set was EMPTY, so a window that was present and dead (frozen keep-alive, lost
    //    z-order, stale coordinates, blank layered surface) was never healed and the Settings
    //    toggle was the only cure. Health is checked here, on every tick, for that reason.
    private readonly System.Windows.Forms.Timer _healthTimer;

    // Runs the short burst of extra checks after the world changed underneath us (resume,
    // unlock, or a detected process gap) — see TaskbarHealPolicy.SettleIntervalMs. One-shot per
    // attempt: each tick stops it and schedules the next interval.
    private readonly System.Windows.Forms.Timer _settleTimer;
    private int _settleAttempt;

    // When the health timer last ran, so an outsized gap can be recognised as "this process
    // wasn't running in between" — the machine slept — rather than a late tick.
    private long _lastHealthCheckTicks = Environment.TickCount64;

    // Whether the last Reconcile found no taskbars, so the empty/recovered log lines fire
    // once per transition instead of on every retry tick.
    private bool _noTaskbarsFound;

    // Devices whose overlay-creation failure has already been logged, so a persistent
    // failure doesn't re-WARN on every 2-second retry tick — once per device until it
    // succeeds (or its taskbar goes away and comes back).
    private readonly HashSet<string> _creationFailureLogged = new();

    // Per-device health bookkeeping for the check above, keyed like _overlays.
    private readonly TaskbarHealTracker _healTracker = new();

    // Reentrancy guard for TryReconcile — see the comment there.
    private bool _reconciling;

    // Presentation settings seeded onto every overlay (and any created later).
    private TaskbarTextColor _labelColor = TaskbarTextColor.White;
    private TaskbarTextColor _numberColor = TaskbarTextColor.Auto;
    private TaskbarStyle _style = TaskbarStyle.Numbers;
    private TaskbarBarWidth _barWidth = TaskbarBarWidth.Standard;
    private int _sizePercent = 100;
    private UsageColorMode _colorMode = UsageColorMode.Pace;
    private TaskbarMetricSelection _metrics = TaskbarMetricSelection.SessionOnly;
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
        // Resume and unlock are the two moments users report the readout going missing across:
        // the overlay windows survive both, so nothing else in this class would ever notice.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _taskbarCreatedListener = new TaskbarCreatedListener(OnTaskbarCreated);
        _healthTimer = new System.Windows.Forms.Timer { Interval = TaskbarHealPolicy.CheckIntervalMs };
        _healthTimer.Tick += (_, _) => OnHealthTick();
        _settleTimer = new System.Windows.Forms.Timer();
        _settleTimer.Tick += (_, _) => OnSettleTick();
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
    /// Choose the readout elements (session/weekly/time-to-limit/countdown) and whether
    /// percentages carry a trailing % sign, on every overlay.
    /// </summary>
    public void SetDisplay(TaskbarMetricSelection metrics, bool percentSign)
    {
        _metrics = metrics;
        _showPercentSign = percentSign;
        foreach (var overlay in _overlays.Values)
            overlay.SetDisplay(metrics, percentSign);
    }

    /// <summary>
    /// Flash a metric name on every readout after a click-to-cycle (see
    /// <see cref="TaskbarOverlayWindow.ShowMetricHint"/>). All of them, not just the one
    /// clicked: they all just changed, so they should all say so.
    /// </summary>
    public void ShowMetricHint(string text)
    {
        foreach (var overlay in _overlays.Values)
            overlay.ShowMetricHint(text);
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
            // Baselined here so the first health tick after a long spell with the feature off
            // isn't mistaken for the process having been suspended.
            _lastHealthCheckTicks = Environment.TickCount64;
            // Guarded: this runs from the TrayApplication constructor at login, exactly
            // when taskbar enumeration is most likely to misbehave — a throw here must
            // not take the app down (the health timer will heal it).
            TryReconcile("Taskbar overlay reconcile on enable failed");
        }
        else
        {
            _healthTimer.Stop();
            _settleTimer.Stop();
            // Reset the transition flag so re-enabling with a taskbar present doesn't log
            // a spurious "Taskbar appeared" — nothing appeared, the user toggled a setting.
            // The failure-log dampener and the health bookkeeping reset too: toggling the
            // feature off and on is the natural "try again" gesture, and that retry's
            // diagnostics should be logged, on readouts that no longer exist to have a history.
            _noTaskbarsFound = false;
            _creationFailureLogged.Clear();
            _healTracker.Clear();
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

    private void OnOverlayCycleRequested(object? sender, EventArgs e) => CycleRequested?.Invoke(this, e);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A resolution or DPI change usually moves the readout, which repaints it anyway — but
        // a monitor that merely woke up can come back at the identical geometry with its
        // layered content gone, and then only a forced repaint brings the readout back.
        RefreshAllOverlays();
        TryReconcile("Taskbar overlay reconcile failed");
    }

    // Explorer broadcasts TaskbarCreated when it (re)creates the taskbar — at login, and
    // after an Explorer restart. Reconciling here closes the startup race (see the
    // _healthTimer remarks) with no polling delay.
    private void OnTaskbarCreated()
    {
        RefreshAllOverlays();
        TryReconcile("Taskbar overlay reconcile after TaskbarCreated failed");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            BeginHeal("system resumed from sleep");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (TaskbarHealPolicy.IsResumeLike(e.Reason))
            BeginHeal($"session event {e.Reason}");
    }

    /// <summary>
    /// The steady health tick: reconcile (which now also health-checks the live readouts), or,
    /// if the machine was evidently asleep between two ticks, run the full heal instead.
    /// </summary>
    private void OnHealthTick()
    {
        var now = Environment.TickCount64;
        var sinceLast = now - _lastHealthCheckTicks;
        _lastHealthCheckTicks = now;

        // The tick source keeps counting through suspend while this timer doesn't, so an
        // outsized gap means the process wasn't running in between. Driving the heal from the
        // gap as well as from PowerModeChanged matters: on modern-standby machines the power
        // event is unreliable, and this path needs no event at all.
        if (TaskbarHealPolicy.IsSystemGap(sinceLast))
            BeginHeal($"no health check for {sinceLast / 1000}s (sleep, hibernate, or a stalled process)");
        else
            TryReconcile("Taskbar overlay health reconcile failed");
    }

    /// <summary>
    /// Recovery for "the world changed underneath us" — resume, unlock/reconnect, or a detected
    /// process gap. Repaints every readout (a layered window can survive with its content lost,
    /// which the keep-alive's dirty check would never notice), re-checks health now, and then
    /// re-checks on a short settling schedule: monitor topology, DPI and the taskbar itself are
    /// usually still churning for several seconds after the event.
    /// </summary>
    private void BeginHeal(string reason)
    {
        if (_disposed || !_enabled) return;

        _logger.Info($"Taskbar readout heal: {reason} — repainting and re-checking.");
        RefreshAllOverlays();
        _healTracker.ClearStrikes();
        TryReconcile("Taskbar overlay heal reconcile failed");

        _settleAttempt = 0;
        ScheduleNextSettle();
    }

    private void ScheduleNextSettle()
    {
        _settleTimer.Stop();
        if (_disposed || !_enabled) return;
        if (TaskbarHealPolicy.SettleIntervalMs(_settleAttempt) is not { } interval) return;

        _settleTimer.Interval = interval;
        _settleTimer.Start();
    }

    private void OnSettleTick()
    {
        _settleTimer.Stop();
        _settleAttempt++;
        // Repaint on every settle tick, not just at the event: the display stack can finish
        // coming back a second or two after resume, dropping the content we just pushed.
        RefreshAllOverlays();
        TryReconcile("Taskbar overlay settle reconcile failed");
        ScheduleNextSettle();
    }

    /// <summary>
    /// Force every live readout to repaint on its next keep-alive tick — see
    /// <see cref="TaskbarOverlayWindow.RefreshAfterSystemChange"/> for why that is the one
    /// thing the keep-alive can't fix by itself.
    /// </summary>
    private void RefreshAllOverlays()
    {
        // Snapshot: showing a Form pumps messages, so a reconcile lower down the stack can be
        // mutating _overlays while this runs.
        foreach (var overlay in _overlays.Values.ToList())
            overlay.RefreshAfterSystemChange();
    }

    /// <summary>
    /// Reconcile guarded for event-driven callers (system events, window messages, timer
    /// ticks): raised exactly when overlay creation/positioning is most likely to throw —
    /// never let that escape into the message loop and crash the app.
    /// </summary>
    private void TryReconcile(string failureContext)
    {
        if (_disposed || !_enabled) return;

        // Creating and showing a Form pumps messages, so another timer tick or system event can
        // land mid-reconcile. Nested reconciles would then dispose overlays the outer pass is
        // still working with; there are now several trigger sources, so guard rather than hope.
        // Skipping is safe: the outer pass is already doing this work, and the health tick will
        // come round again in two seconds.
        if (_reconciling) return;

        _reconciling = true;
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            _logger.Warn($"{failureContext}: {ex.Message}");
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>
    /// Brings the live overlay set in line with the taskbars currently present: rebuilds
    /// readouts that exist but are broken, creates an overlay (seeded with the current settings
    /// and reading) for any taskbar that has none, and disposes overlays whose taskbar
    /// (monitor) has gone away.
    /// </summary>
    private void Reconcile()
    {
        if (!_enabled) return;

        // The timer decision lives in the finally: even if reconciling throws partway (a
        // caught-and-logged event path), the health timer must still be running — otherwise one
        // bad tick recreates exactly the "nothing ever retries" state it exists to eliminate.
        try
        {
            var taskbars = TaskbarEnumerator.Enumerate();

            // Log the empty/recovered transitions once each (not per tick), so a single boot's
            // log shows whether the login race was hit and when it healed.
            if (taskbars.Count == 0 && !_noTaskbarsFound)
                _logger.Warn("No taskbars found to overlay (Explorer may still be starting) — retrying until one appears.");
            else if (taskbars.Count > 0 && _noTaskbarsFound)
                _logger.Info("Taskbar appeared — creating overlay(s).");
            _noTaskbarsFound = taskbars.Count == 0;

            // The taskbars that should carry a readout, with the handle just enumerated for
            // each. When multi-monitor is off, only the primary taskbar gets one.
            var wanted = new Dictionary<string, IntPtr>();
            foreach (var taskbar in taskbars)
            {
                // First wins, matching TaskbarEnumerator.FindByDevice — so if two taskbars ever
                // transiently resolve to one monitor, the readout is judged against the same
                // taskbar it glued itself to rather than a different one.
                if (_allMonitors || taskbar.IsPrimary)
                    wanted.TryAdd(taskbar.MonitorDevice, taskbar.Handle);
            }

            // Health pass first, so a readout torn down here is replaced by the creation loop
            // below in this same reconcile — the user never sees the gap.
            foreach (var (device, handle) in wanted)
            {
                if (!_overlays.TryGetValue(device, out var existing) || ShouldKeepOverlay(device, existing, handle))
                    continue;

                DisposeOverlay(existing);
                _overlays.Remove(device);
            }

            foreach (var device in wanted.Keys)
            {
                // Re-checked each iteration: creating a Form pumps messages, so a SetEnabled(false)
                // can land mid-loop and the rest of this pass must not publish new readouts into a
                // disabled manager (nothing would ever tear them down).
                if (!_enabled || _disposed)
                    break;

                if (_overlays.ContainsKey(device))
                    continue;

                // Build the overlay fully before publishing it, so a failure mid-construction
                // disposes the half-built Form rather than leaking an untracked ghost window.
                TaskbarOverlayWindow? overlay = null;
                try
                {
                    overlay = new TaskbarOverlayWindow(device, _logger);
                    overlay.Clicked += OnOverlayClicked;
                    overlay.CycleRequested += OnOverlayCycleRequested;
                    Seed(overlay);
                    overlay.SetEnabled(true);
                }
                catch (Exception ex)
                {
                    // Once per device until it succeeds — the 2-second tick would otherwise
                    // turn a persistent failure into unbounded WARN spam.
                    if (_creationFailureLogged.Add(device))
                        _logger.Warn($"Failed to create taskbar overlay for {device}: {ex.Message}");
                    overlay?.Dispose();
                    continue;
                }

                _creationFailureLogged.Remove(device);
                _overlays[device] = overlay;
            }

            // Tear down overlays whose taskbar is no longer present (monitor unplugged, or its
            // taskbar turned off). ToList so we don't mutate the dictionary while enumerating it.
            foreach (var device in _overlays.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                DisposeOverlay(_overlays[device]);
                _overlays.Remove(device);
            }

            // A device that left re-arms its failure log line and forgets its health history,
            // so a taskbar that goes away and comes back broken is diagnosable again.
            _creationFailureLogged.RemoveWhere(d => !wanted.ContainsKey(d));
            _healTracker.RetainOnly(wanted.Keys);
        }
        finally
        {
            // Unlike #62's version this keeps running once there ARE overlays, not only while
            // there is nothing to show: the per-overlay keep-alive re-asserts geometry and
            // z-order but cannot notice that it has stopped running, or that its window is no
            // longer painting — so something outside the overlay has to keep looking.
            // Conditional because showing a Form pumps messages: a SetEnabled(false) or Dispose
            // that lands mid-reconcile must not have its timer re-armed underneath it.
            if (_enabled && !_disposed)
                _healthTimer.Start();
        }
    }

    /// <summary>
    /// Classifies one readout and decides whether to keep it. Returns false only when it is
    /// broken enough, for long enough, to be worth tearing down and rebuilding — the tolerance
    /// and the rebuild cooldown live in <see cref="TaskbarHealPolicy"/>. Status changes are
    /// logged as transitions rather than per tick, so one bad reboot or sleep cycle leaves a log
    /// that names the failure mode instead of thousands of identical lines.
    /// </summary>
    private bool ShouldKeepOverlay(string device, TaskbarOverlayWindow overlay, IntPtr taskbarHandle)
    {
        TaskbarOverlayStatus status;
        try
        {
            status = overlay.CheckHealth(taskbarHandle);
        }
        catch (Exception ex)
        {
            // A diagnostic that failed is not evidence the readout is broken — keep it.
            _logger.Warn($"Taskbar readout health check failed for {device}: {ex.Message}");
            return true;
        }

        var verdict = _healTracker.Observe(device, status, Environment.TickCount64);

        // The two "hidden on purpose" states are logged as well as the faults: they are the only
        // healthy ways for a readout to be invisible, so a report of "it disappeared" is only
        // diagnosable if the log says when it went and when it came back.
        switch (verdict.Log)
        {
            case TaskbarHealLog.Recovered:
                _logger.Info($"Taskbar readout healthy again on {device} (was {verdict.PreviousStatus}).");
                break;
            case TaskbarHealLog.Suppressed:
                _logger.Info($"Taskbar readout hidden for a fullscreen app on {device}.");
                break;
            case TaskbarHealLog.Waiting:
                _logger.Info($"Taskbar readout waiting for its taskbar on {device}.");
                break;
            case TaskbarHealLog.Fault:
                _logger.Warn($"Taskbar readout unhealthy on {device}: {status}.");
                break;
            case TaskbarHealLog.Rebuilding:
                _logger.Warn(
                    $"Rebuilding taskbar readout on {device} after {verdict.ConsecutiveUnhealthy} unhealthy checks ({status}).");
                break;
        }

        return !verdict.Rebuild;
    }

    /// <summary>Applies the current settings and latest reading to a freshly created overlay.</summary>
    private void Seed(TaskbarOverlayWindow overlay)
    {
        overlay.SetColors(_labelColor, _numberColor);
        overlay.SetStyle(_style);
        overlay.SetBarWidth(_barWidth);
        overlay.SetSize(_sizePercent);
        overlay.SetColorMode(_colorMode);
        overlay.SetDisplay(_metrics, _showPercentSign);
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _healthTimer.Dispose();
        _settleTimer.Dispose();
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
