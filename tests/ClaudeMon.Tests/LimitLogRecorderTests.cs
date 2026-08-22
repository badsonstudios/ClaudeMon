namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public class LimitLogRecorderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _logDir;
    private DateTimeOffset _now = T0;

    public LimitLogRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-limitrec-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tempDir, "limit-log");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private LimitLogRecorder Recorder(
        Func<IReadOnlyDictionary<string, ModelTokens>?>? tokens = null, ClaudePlan? plan = null) =>
        new(new LimitLogStore(_logDir),
            tokens ?? (() => new Dictionary<string, ModelTokens> { ["opus"] = new(100, 0, 0, 0) }),
            () => plan, clock: () => _now);

    private static UsageResponse SessionAt(double pct, DateTimeOffset resets) =>
        new(null, null, [new UsageLimit("session", "5h", pct, "normal", resets)]);

    private string[] SampleLines() =>
        File.ReadAllLines(Path.Combine(_logDir, $"samples-{T0:yyyy-MM}.jsonl"));

    [Fact]
    public void Record_AppendsExactlyOneSamplePerPoll_AndPersistsState()
    {
        var recorder = Recorder();
        recorder.Record(SessionAt(10, T0 + UsageWindows.FiveHour));
        _now += TimeSpan.FromMinutes(5);
        recorder.Record(SessionAt(12, T0 + UsageWindows.FiveHour));

        Assert.Equal(2, SampleLines().Length);
        var state = new LimitLogStore(_logDir).LoadState();
        Assert.NotNull(state);
        Assert.Equal(_now, state.LastSampleAt);
        Assert.Single(state.Windows);
    }

    [Fact]
    public void Record_RolloverAppendsAWindowRecord()
    {
        var resets = T0 + UsageWindows.FiveHour;
        var recorder = Recorder();
        recorder.Record(SessionAt(80, resets));

        // Next poll after the reset reports the successor window.
        _now = resets + TimeSpan.FromMinutes(3);
        recorder.Record(SessionAt(2, _now + UsageWindows.FiveHour));

        var lines = File.ReadAllLines(Path.Combine(_logDir, $"windows-{resets:yyyy-MM}.jsonl"));
        Assert.Single(lines);
        Assert.Contains("\"kind\":\"session\"", lines[0]);
    }

    [Fact]
    public void Record_StateSurvivesARecorderRestart()
    {
        var resets = T0 + UsageWindows.FiveHour;
        Recorder().Record(SessionAt(40, resets));

        // A fresh recorder (new process) picks the window up from the state file.
        _now += TimeSpan.FromMinutes(5);
        var restarted = Recorder();
        restarted.FinalizeMissedOnStartup();
        restarted.Record(SessionAt(55, resets));

        var state = new LimitLogStore(_logDir).LoadState();
        var window = Assert.Single(state!.Windows);
        Assert.Equal(55, window.PeakPercent);
        Assert.Equal(2, window.SampleCount);
    }

    [Fact]
    public void FinalizeMissedOnStartup_WritesTheMissedWindowAsIncomplete()
    {
        // A baseline poll first, so the window that follows opens covered (seen from its
        // birth) — the incomplete flag below must come from the missed end, not the start.
        var recorder = Recorder();
        recorder.Record(new UsageResponse(null, null, []));
        _now += TimeSpan.FromMinutes(5);
        var resets = _now + UsageWindows.FiveHour - TimeSpan.FromMinutes(2);
        recorder.Record(SessionAt(65, resets));

        // Relaunch hours after the window ended.
        _now = resets + TimeSpan.FromHours(2);
        var restarted = Recorder();
        restarted.FinalizeMissedOnStartup();

        var lines = File.ReadAllLines(Path.Combine(_logDir, $"windows-{resets:yyyy-MM}.jsonl"));
        Assert.Single(lines);
        Assert.Contains("\"incomplete\":true", lines[0]);
        Assert.Contains(LimitWindowRecord.ReasonOfflineAtWindowEnd, lines[0]);
        Assert.Empty(new LimitLogStore(_logDir).LoadState()!.Windows);
    }

    [Fact]
    public void Record_ForwardsTheSampleToTheCapacityRecorder()
    {
        // The one seam between the two recorders: each successful poll's sample must reach
        // the implied-capacity engine too (issue #185).
        var capacity = new CapacityEstimateRecorder(
            new LimitLogStore(_logDir), () => null, clock: () => _now);
        var recorder = new LimitLogRecorder(
            new LimitLogStore(_logDir),
            () => new Dictionary<string, ModelTokens> { ["opus"] = new(10_000, 0, 0, 0) },
            () => null, clock: () => _now, capacity: capacity);

        recorder.Record(SessionAt(10, T0 + UsageWindows.FiveHour));

        var estimate = Assert.Single(capacity.Snapshot(), e => e.Kind == "session");
        Assert.Equal(0, estimate.ObservationCount); // baseline poll — but the key exists:
        Assert.NotNull(estimate);                   // the sample demonstrably arrived.
    }

    [Fact]
    public void Record_TokensProviderThrowing_StillAppendsTheSampleWithoutTokens()
    {
        var recorder = Recorder(tokens: () => throw new InvalidOperationException("scanner broke"));

        // Must not throw out of the poll path, and a scanner fault costs only the tokens half
        // of the sample — the API half still records.
        recorder.Record(SessionAt(10, T0 + UsageWindows.FiveHour));

        var line = Assert.Single(SampleLines());
        Assert.Contains("\"kind\":\"session\"", line);
        Assert.DoesNotContain("\"tok\"", line);
    }
}
