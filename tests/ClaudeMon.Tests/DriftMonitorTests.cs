namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public class DriftMonitorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _logDir;
    private DateTimeOffset _now = T0;

    public DriftMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-drift-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tempDir, "limit-log");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private DriftMonitor Monitor() =>
        new(new LimitLogStore(_logDir), clock: () => _now);

    private static AppSettings Settings(bool notifications = true, bool drift = true) => new()
    {
        Notifications = new NotificationSettings { Enabled = notifications },
        AlertThresholds = new AlertThresholds { DriftAlertsEnabled = drift },
    };

    private static ImpliedCapacity Estimate(double capacity) =>
        new("session", null, capacity, null, CapacityConfidence.Medium, 12, 0, null, null);

    private IReadOnlyList<DriftAlertMessage> RunDays(
        DriftMonitor monitor, int days, Func<int, double> capacityOf, AppSettings? settings = null)
    {
        var alerts = new List<DriftAlertMessage>();
        for (var day = 0; day < days; day++)
        {
            alerts.AddRange(monitor.Evaluate([Estimate(capacityOf(day))], settings ?? Settings(), _now));
            _now += TimeSpan.FromDays(1);
        }

        return alerts;
    }

    [Fact]
    public void Evaluate_PersistsStateAcrossARestart_WithoutReAlerting()
    {
        var monitor = Monitor();
        monitor.LoadOnStartup();
        var alerts = RunDays(monitor, 13, day => day < 10 ? 100_000_000 : 70_000_000);
        Assert.Single(alerts);

        // Restart mid-episode: the persisted latch keeps the same drift quiet.
        var restarted = Monitor();
        restarted.LoadOnStartup();
        Assert.Empty(RunDays(restarted, 3, _ => 70_000_000));
    }

    [Fact]
    public void Acknowledge_RoundTripsThroughTheStore()
    {
        var monitor = Monitor();
        monitor.LoadOnStartup();
        Assert.Single(RunDays(monitor, 13, day => day < 10 ? 100_000_000 : 70_000_000));

        monitor.Acknowledge();

        // A fresh monitor sees the acknowledgment: still in drift, still quiet.
        var restarted = Monitor();
        restarted.LoadOnStartup();
        Assert.Empty(RunDays(restarted, 3, _ => 70_000_000));
        var state = new LimitLogStore(_logDir).LoadDriftState();
        Assert.NotNull(Assert.Single(state!.Keys).AcknowledgedAt);
    }

    [Fact]
    public void Evaluate_GatesRouteThroughToTheDetector()
    {
        var monitor = Monitor();
        monitor.LoadOnStartup();

        // Drift begins with the drift toggle off: deferred, not dropped.
        Assert.Empty(RunDays(monitor, 13,
            day => day < 10 ? 100_000_000 : 70_000_000, Settings(drift: false)));

        Assert.Single(monitor.Evaluate([Estimate(70_000_000)], Settings(), _now));
    }

    [Fact]
    public void Evaluate_StoreFailure_IsContainedAndStillEvaluatesInMemory()
    {
        File.WriteAllText(_logDir, "in the way"); // every persist fails
        var monitor = Monitor();
        monitor.LoadOnStartup();

        var alerts = RunDays(monitor, 13, day => day < 10 ? 100_000_000 : 70_000_000);

        Assert.Single(alerts); // the in-memory series still detected the drift
    }
}
