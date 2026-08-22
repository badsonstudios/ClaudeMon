namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// Glues the correlated limit log together (issue #184): on every successful usage poll it
/// runs the pure <see cref="LimitWindowTracker"/> over the poll's limits, the local scanner's
/// cumulative tokens-by-model, and the configured plan, then appends the sample and any
/// finalized windows via <see cref="LimitLogStore"/> and persists the advanced state.
///
/// Thread-safe via its own lock: the poll serializes itself, but
/// <see cref="FinalizeMissedOnStartup"/> runs on the UI thread and must not interleave with a
/// poll already in flight. Recording is strictly best-effort — any failure is logged and
/// swallowed so the log can never break monitoring (the same contract as the history store).
/// </summary>
public sealed class LimitLogRecorder
{
    private readonly LimitLogStore _store;
    private readonly Func<IReadOnlyDictionary<string, ModelTokens>?> _tokensByModel;
    private readonly Func<ClaudePlan?> _plan;
    private readonly Logger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CapacityEstimateRecorder? _capacity;
    private readonly object _lock = new();
    private LimitLogState _state = new() { Version = LimitLogState.CurrentVersion };

    public LimitLogRecorder(
        LimitLogStore store,
        Func<IReadOnlyDictionary<string, ModelTokens>?> tokensByModel,
        Func<ClaudePlan?> plan,
        Logger? logger = null,
        Func<DateTimeOffset>? clock = null,
        CapacityEstimateRecorder? capacity = null)
    {
        _store = store;
        _tokensByModel = tokensByModel;
        _plan = plan;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _capacity = capacity;
    }

    /// <summary>
    /// Loads the persisted state and finalizes, best-effort, every window whose reset time
    /// passed while the app wasn't running — flagged incomplete by the tracker rather than
    /// silently wrong. Call once at startup, before polling starts.
    /// </summary>
    public void FinalizeMissedOnStartup()
    {
        lock (_lock)
        {
            try
            {
                _state = _store.LoadState() ?? _state;

                var (finalized, newState) = LimitWindowTracker.FinalizeExpired(_state, _clock(), _plan());
                if (finalized.Count == 0)
                    return;

                foreach (var record in finalized)
                    _store.AppendWindow(record);
                _state = newState;
                _store.SaveState(_state);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Limit log startup catch-up failed: {ex.Message}");
            }
        }
    }

    /// <summary>One successful poll's worth of recording. Never throws.</summary>
    public void Record(UsageResponse usage)
    {
        lock (_lock)
        {
            try
            {
                var result = LimitWindowTracker.Observe(
                    _state, _clock(), usage, SafeTokens(), _plan());

                _store.AppendSample(result.Sample);
                foreach (var record in result.Finalized)
                    _store.AppendWindow(record);

                _state = result.NewState;
                _store.SaveState(_state);

                // The implied-capacity engine (issue #185) consumes the very sample just
                // logged — the same record the cold-start backfill replays, so the two paths
                // are byte-identical inputs. Its own never-throws contract keeps this safe.
                _capacity?.Observe(result.Sample);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Limit log recording failed: {ex.Message}");
            }
        }
    }

    // A scanner fault must cost only the tokens half of the sample, not the sample: the
    // tracker already degrades gracefully on null totals (keeps the baseline, logs tok: null).
    private IReadOnlyDictionary<string, ModelTokens>? SafeTokens()
    {
        try
        {
            return _tokensByModel();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Limit log tokens provider failed: {ex.Message}");
            return null;
        }
    }
}
