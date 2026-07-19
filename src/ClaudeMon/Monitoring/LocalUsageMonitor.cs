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
    private readonly System.Timers.Timer _timer;

    public LocalUsageMonitor(LocalUsageStore store, Logger? logger = null)
    {
        _store = store;
        _logger = logger;
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

    /// <summary>Today's totals for the UI (null = nothing to show).</summary>
    public LocalUsageSnapshot? Snapshot() => _store.Snapshot();

    // Timer/Task.Run entry point: nothing may escape a fire-and-forget callback.
    private void ScanSafely()
    {
        try
        {
            _store.ScanOnce();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Local usage scan failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
