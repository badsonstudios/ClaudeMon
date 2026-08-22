namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// Glues the pure <see cref="DriftDetector"/> to the app (issue #186), the
/// <see cref="CapacityEstimateRecorder"/> shape: state loads from drift.json at startup,
/// <see cref="Evaluate"/> runs per poll on the UI thread right after the usage alerts, and
/// acknowledgment arrives from the Limit history tab. Best-effort throughout — a drift check
/// can never break monitoring.
///
/// Known, accepted quirk (shared with the service-status alert): NotifyIcon shows one balloon,
/// so a drift alert landing in the same poll as a usage alert overwrites it. Drift fires at
/// most once per episode, and the ntfy push still delivers both.
/// </summary>
public sealed class DriftMonitor
{
    private readonly LimitLogStore _store;
    private readonly Logger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private DriftState _state = new() { Version = DriftState.CurrentVersion };

    public DriftMonitor(LimitLogStore store, Logger? logger = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Loads the persisted state. Call once at startup, before polling starts.</summary>
    public void LoadOnStartup()
    {
        lock (_lock)
        {
            try
            {
                _state = _store.LoadDriftState() ?? _state;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Drift state load failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One poll's drift check over the current estimates. The alert gate (master notifications
    /// toggle, the drift toggle, snooze) is resolved here so the detector's deferral semantics
    /// get the truth; returns the alerts to show (empty when gated or quiet).
    /// </summary>
    public IReadOnlyList<DriftAlertMessage> Evaluate(
        IReadOnlyList<ImpliedCapacity> estimates, AppSettings settings, DateTimeOffset now)
    {
        lock (_lock)
        {
            try
            {
                var canNotify = settings.Notifications.Enabled
                    && settings.AlertThresholds.DriftAlertsEnabled
                    && !settings.Notifications.IsSnoozed(now);

                var (alerts, newState) = DriftDetector.Observe(
                    _state, now, estimates, settings.Plan,
                    settings.AlertThresholds.DriftThresholdPercent, canNotify);

                _state = newState;
                _store.SaveDriftState(_state);
                return alerts;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Drift evaluation failed: {ex.Message}");
                return Array.Empty<DriftAlertMessage>();
            }
        }
    }

    /// <summary>The user opened the Limit history tab — the evidence was seen; quiet the episode.</summary>
    public void Acknowledge()
    {
        lock (_lock)
        {
            try
            {
                var (changed, newState) = DriftDetector.Acknowledge(_state, _clock());
                if (!changed)
                    return;

                _state = newState;
                _store.SaveDriftState(_state);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Drift acknowledgment failed: {ex.Message}");
            }
        }
    }
}
