namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// Glues the implied-capacity engine (issue #185) to the app: fed one
/// <see cref="LimitLogSample"/> per successful poll by <see cref="LimitLogRecorder"/>, it
/// advances the pure <see cref="CapacityEstimator"/> and persists the compact state; the UI
/// thread reads <see cref="Snapshot"/>. On startup, a missing or version-mismatched state
/// file triggers a one-time bounded backfill — the last <see cref="BackfillWindow"/> of
/// samples streamed from the log — so estimates don't restart from zero on every install or
/// schema bump. The estimator's monotonic guard makes the backfill/live handoff seamless.
///
/// Thread-safe via its own lock; everything is best-effort and never throws into the poll
/// (the <see cref="LimitLogRecorder"/> contract).
/// </summary>
public sealed class CapacityEstimateRecorder
{
    /// <summary>How far back the cold-start backfill reads — at most four month files.</summary>
    internal static readonly TimeSpan BackfillWindow = TimeSpan.FromDays(90);

    private readonly LimitLogStore _store;
    private readonly Func<ClaudePlan?> _plan;
    private readonly Logger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private CapacityEstimateState _state = new() { Version = CapacityEstimateState.CurrentVersion };

    public CapacityEstimateRecorder(
        LimitLogStore store,
        Func<ClaudePlan?> plan,
        Logger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _plan = plan;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Loads the persisted state, or rebuilds it from the log's recent samples when there is
    /// none to load. Call once at startup, before polling starts, so backfill and live
    /// samples can never interleave.
    /// </summary>
    public void LoadOrBackfillOnStartup()
    {
        lock (_lock)
        {
            try
            {
                var loaded = _store.LoadCapacityState();
                if (loaded is not null)
                {
                    _state = loaded;
                    return;
                }

                // The historical plan is unknowable here, so the current one is assumed for
                // the whole backfill (no spurious ring clears); a plan that actually changed
                // recently surfaces as dispersion, and live changes clear rings properly.
                var now = _clock();
                var plan = _plan();
                var count = 0;
                foreach (var sample in _store.ReadSamples(now - BackfillWindow, now))
                {
                    _state = CapacityEstimator.Observe(_state, sample, plan);
                    count++;
                }

                if (count > 0)
                {
                    _store.SaveCapacityState(_state);
                    _logger?.Info($"Implied-capacity state rebuilt from {count} logged samples.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Implied-capacity startup load failed: {ex.Message}");
            }
        }
    }

    /// <summary>One poll's sample (called by <see cref="LimitLogRecorder"/>). Never throws.</summary>
    public void Observe(LimitLogSample sample)
    {
        lock (_lock)
        {
            try
            {
                _state = CapacityEstimator.Observe(_state, sample, _plan());
                _store.SaveCapacityState(_state);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Implied-capacity update failed: {ex.Message}");
            }
        }
    }

    /// <summary>The current estimates for the UI. Never throws; empty on any failure.</summary>
    public IReadOnlyList<ImpliedCapacity> Snapshot()
    {
        // Only the reference is read under the lock: the state is an immutable record, so
        // the UI thread computes on its own snapshot instead of waiting behind a poll
        // thread's state-file write.
        CapacityEstimateState state;
        lock (_lock) { state = _state; }

        try
        {
            return CapacityEstimator.Estimates(state);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Implied-capacity snapshot failed: {ex.Message}");
            return Array.Empty<ImpliedCapacity>();
        }
    }
}
