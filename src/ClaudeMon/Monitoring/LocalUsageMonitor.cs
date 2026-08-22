namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// Drives <see cref="LocalUsageStore"/> on a timer, the same shape as
/// <see cref="UsageMonitor"/>: scans run on thread-pool threads, exceptions
/// are caught and logged (a scan failure must never tear the app down), and
/// <see cref="Pause"/>/<see cref="Resume"/> stop the work while the
/// workstation is locked. Scans are cheap at steady state — unchanged files
/// are skipped by offset/mtime without being opened — so a short interval
/// keeps the flyout line fresh without meaningful cost.
/// </summary>
public sealed class LocalUsageMonitor : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    private readonly LocalUsageStore _store;
    private readonly Logger? _logger;
    private readonly Action _scan;
    private readonly System.Timers.Timer _timer;

    public LocalUsageMonitor(LocalUsageStore store, Logger? logger = null)
        : this(store, logger, scan: null)
    {
    }

    /// <summary>
    /// Test seam: replaces the scan step. <see cref="LocalUsageStore.ScanOnce"/> swallows its own
    /// per-file IO failures by design, so there is no way to make a real store throw — and the
    /// two-tier exception handling in <see cref="ScanSafely"/> is exactly the behaviour that has
    /// to be verified. Production always takes the <c>null</c> path.
    /// </summary>
    internal LocalUsageMonitor(LocalUsageStore store, Logger? logger, Action? scan)
    {
        _store = store;
        _logger = logger;
        _scan = scan ?? store.ScanOnce;
        _timer = new System.Timers.Timer(ScanInterval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => ScanSafely();
    }

    public void Start()
    {
        _timer.Start();
        // First scan off the UI thread: a cold cache against a large transcript
        // history takes real time, and the ctor path runs on the UI thread.
        _ = Task.Run(ScanSafely);
    }

    public void Pause() => _timer.Stop();

    public void Resume()
    {
        _timer.Start();
        _ = Task.Run(ScanSafely);
    }

    /// <summary>
    /// Raised on the scan thread after each successful scan pass — the budget
    /// check hangs off this so it re-evaluates as soon as new usage lands.
    /// Subscribers marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler? ScanCompleted;

    /// <summary>Today's totals for the UI (null = nothing to show).</summary>
    public LocalUsageSnapshot? Snapshot() => _store.Snapshot();

    /// <summary>The breakdown tables for the Usage &amp; costs window.</summary>
    public LocalUsageBreakdown? Breakdown(BreakdownTimeframe timeframe) => _store.Breakdown(timeframe);

    /// <summary>The per-day cost series behind the Usage &amp; costs window's chart.</summary>
    public LocalCostSeries? CostSeries(BreakdownTimeframe timeframe) => _store.CostSeries(timeframe);

    /// <summary>The sums the budget alerts compare against their caps.</summary>
    public LocalBudgetTotals? BudgetTotals() => _store.BudgetTotals();

    /// <summary>Cumulative tokens by model for the correlated limit log (null = unavailable).</summary>
    public Dictionary<string, ModelTokens>? TokensByModel() => _store.TokensByModel();

    // Timer/Task.Run entry point: nothing may escape a fire-and-forget callback.
    // Internal rather than private so tests can drive it synchronously instead of
    // racing the timer and Task.Run.
    internal void ScanSafely()
    {
        try
        {
            _scan();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Local usage scan failed: {ex.Message}");
            return;
        }

        // Outside the scan's try so a subscriber fault isn't logged as a scan
        // failure — and it still must not escape this fire-and-forget callback.
        try
        {
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Local usage scan-completed handler failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
