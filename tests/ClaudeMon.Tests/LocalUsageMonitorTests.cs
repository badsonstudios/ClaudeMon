namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

/// <summary>
/// <see cref="LocalUsageMonitor"/> is a thin timer shell, but <c>ScanSafely</c> is a real
/// contract: it runs from a timer callback and from <c>Task.Run</c>, so nothing may escape it,
/// and a faulting <c>ScanCompleted</c> subscriber must be distinguished from a faulting scan
/// rather than masquerading as one in the log.
/// </summary>
public class LocalUsageMonitorTests : IDisposable
{
    private readonly string _tempDir;

    public LocalUsageMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-localmon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>A store whose transcript directory does not exist, so every query returns null.</summary>
    private LocalUsageStore CreateStore() => new(
        projectsDir: Path.Combine(_tempDir, "no-such-projects"),
        cachePath: Path.Combine(_tempDir, "cache.json"));

    private Logger CreateLogger() => new(Path.Combine(_tempDir, "logs"));

    // Shared with the writer: Logger silently drops any line it can't append, so a reader that
    // denies write access could destroy the entry under test.
    private static string ReadLog(Logger logger)
    {
        if (!File.Exists(logger.FilePath))
            return "";

        using var stream = new FileStream(
            logger.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ScanSafely_ScanThrows_IsLoggedAndScanCompletedIsNotRaised()
    {
        var logger = CreateLogger();
        var raised = 0;
        using var monitor = new LocalUsageMonitor(
            CreateStore(), logger, () => throw new IOException("transcripts unreadable"));
        monitor.ScanCompleted += (_, _) => raised++;

        monitor.ScanSafely();

        Assert.Equal(0, raised);
        var log = ReadLog(logger);
        Assert.Contains("Local usage scan failed", log);
        Assert.Contains("transcripts unreadable", log);
    }

    [Fact]
    public void ScanSafely_ScanSucceeds_RaisesScanCompletedOnce()
    {
        var scans = 0;
        var raised = 0;
        using var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger(), () => scans++);
        monitor.ScanCompleted += (_, _) => raised++;

        monitor.ScanSafely();

        Assert.Equal(1, scans);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ScanSafely_SubscriberThrows_IsLoggedSeparatelyAndDoesNotEscape()
    {
        var logger = CreateLogger();
        using var monitor = new LocalUsageMonitor(CreateStore(), logger, scan: () => { });
        monitor.ScanCompleted += (_, _) => throw new InvalidOperationException("bad subscriber");

        // Must not escape: in production this runs on a timer thread with nobody to catch it.
        monitor.ScanSafely();

        var log = ReadLog(logger);
        Assert.Contains("scan-completed handler failed", log);
        Assert.Contains("bad subscriber", log);
        // A subscriber fault is not a scan fault; conflating them would send anyone reading the
        // log hunting through the transcript scanner for a bug that isn't there.
        Assert.DoesNotContain("Local usage scan failed", log);
    }

    [Fact]
    public void ScanSafely_WithoutLogger_StillSwallowsBothFailures()
    {
        using var scanFails = new LocalUsageMonitor(
            CreateStore(), logger: null, () => throw new IOException("nope"));
        using var subscriberFails = new LocalUsageMonitor(CreateStore(), logger: null, scan: () => { });
        subscriberFails.ScanCompleted += (_, _) => throw new InvalidOperationException("nope");

        Assert.Null(Record.Exception(scanFails.ScanSafely));
        Assert.Null(Record.Exception(subscriberFails.ScanSafely));
    }

    [Fact]
    public void ScanSafely_NoSubscribers_IsANoOpAfterTheScan()
    {
        var scans = 0;
        using var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger(), () => scans++);

        monitor.ScanSafely();

        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task Start_ScansOffTheCallingThread()
    {
        // The cold-cache scan is deliberately pushed onto the thread pool because the ctor path
        // runs on the UI thread; the scan interval is a minute, so this can only be the kick-off.
        using var scanned = new ManualResetEventSlim();
        var scanThreadId = 0;
        using var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger(), () =>
        {
            scanThreadId = Environment.CurrentManagedThreadId;
            scanned.Set();
        });
        var callerThreadId = Environment.CurrentManagedThreadId;

        monitor.Start();

        Assert.True(await Task.Run(() => scanned.Wait(TimeSpan.FromSeconds(10))));
        Assert.NotEqual(callerThreadId, scanThreadId);
        monitor.Pause();
    }

    [Fact]
    public async Task Resume_ScansImmediatelyRatherThanWaitingOutTheInterval()
    {
        using var scanned = new ManualResetEventSlim();
        using var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger(), () => scanned.Set());

        monitor.Pause();
        monitor.Resume();

        Assert.True(await Task.Run(() => scanned.Wait(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void Queries_NoTranscriptDirectory_AllReturnNull()
    {
        using var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger());

        // Claude Code was never installed, or its projects directory moved: every query has to
        // degrade to null so the UI simply omits the local-usage lines instead of erroring.
        Assert.Null(monitor.Snapshot());
        Assert.Null(monitor.Breakdown(BreakdownTimeframe.Today));
        Assert.Null(monitor.CostSeries(BreakdownTimeframe.ThirtyDays));
        Assert.Null(monitor.BudgetTotals());
    }

    [Fact]
    public void Dispose_AfterStart_DoesNotThrowAndIsIdempotent()
    {
        var monitor = new LocalUsageMonitor(CreateStore(), CreateLogger(), scan: () => { });
        monitor.Start();

        Assert.Null(Record.Exception(monitor.Dispose));
        Assert.Null(Record.Exception(monitor.Dispose));
    }
}
