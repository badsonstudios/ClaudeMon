namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public class CapacityEstimateRecorderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _logDir;
    private DateTimeOffset _now = T0;

    public CapacityEstimateRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-capacity-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tempDir, "limit-log");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private CapacityEstimateRecorder Recorder(ClaudePlan? plan = null) =>
        new(new LimitLogStore(_logDir), () => plan, clock: () => _now);

    private static LimitLogSample Sample(DateTimeOffset t, double pct, DateTimeOffset resets, long cumulativeInput) =>
        new(t, [new LimitSnapshot("session", "5h", pct, null, resets, null, null)],
            new Dictionary<string, ModelTokens> { ["opus"] = new(cumulativeInput, 0, 0, 0) });

    // Writes a clean burn (2%/10k tokens per 10-minute poll, windows rolling naturally) to
    // the samples JSONL — the shape the #184 recorder leaves on disk for backfill to find.
    private void WriteBurnToLog(int polls)
    {
        var store = new LimitLogStore(_logDir);
        var t = T0;
        var resets = T0 + UsageWindows.FiveHour;
        var pct = 0.0;
        long cum = 0;
        for (var i = 0; i < polls; i++)
        {
            if (t >= resets)
            {
                resets = t + UsageWindows.FiveHour;
                pct = 0;
            }

            cum += 10_000;
            pct += 2;
            store.AppendSample(Sample(t, pct, resets, cum));
            t += TimeSpan.FromMinutes(10);
        }

        _now = t;
    }

    [Fact]
    public void LoadOrBackfill_NoStateFile_RebuildsFromTheLoggedSamples()
    {
        WriteBurnToLog(polls: 70);

        var recorder = Recorder();
        recorder.LoadOrBackfillOnStartup();

        var estimate = Assert.Single(recorder.Snapshot(), e => e.Kind == "session");
        Assert.True(estimate.Confidence >= CapacityConfidence.Medium);
        Assert.InRange(estimate.CapacityWeightedTokens, 450_000, 550_000); // 5k weighted per point
        // And the rebuilt state was persisted for the next launch.
        Assert.True(File.Exists(Path.Combine(_logDir, "capacity.json")));
    }

    [Fact]
    public void LoadOrBackfill_ValidStateFile_LoadsWithoutRereadingTheLog()
    {
        WriteBurnToLog(polls: 70);
        var first = Recorder();
        first.LoadOrBackfillOnStartup();
        var expected = Assert.Single(first.Snapshot(), e => e.Kind == "session");

        // Corrupt the samples so a re-read would change the answer: a load that ignores the
        // log proves it came from capacity.json alone.
        foreach (var file in Directory.GetFiles(_logDir, "samples-*.jsonl"))
            File.WriteAllText(file, "{torn");

        var second = Recorder();
        second.LoadOrBackfillOnStartup();
        var reloaded = Assert.Single(second.Snapshot(), e => e.Kind == "session");
        Assert.Equal(expected.CapacityWeightedTokens, reloaded.CapacityWeightedTokens);
        Assert.Equal(expected.ObservationCount, reloaded.ObservationCount);
    }

    [Fact]
    public void Observe_AdvancesAndPersists_AcrossARecorderRestart()
    {
        var resets = T0 + UsageWindows.FiveHour;
        var recorder = Recorder();
        recorder.LoadOrBackfillOnStartup();
        recorder.Observe(Sample(T0, 2, resets, 10_000));
        _now += TimeSpan.FromMinutes(10);
        recorder.Observe(Sample(_now, 4, resets, 20_000));

        var restarted = Recorder();
        restarted.LoadOrBackfillOnStartup();
        _now += TimeSpan.FromMinutes(10);
        restarted.Observe(Sample(_now, 6, resets, 30_000));

        var estimate = Assert.Single(restarted.Snapshot(), e => e.Kind == "session");
        Assert.Equal(2, estimate.ObservationCount); // 2 intervals closed across the restart
    }

    [Fact]
    public void Backfill_ThenLive_TheMonotonicGuardMakesTheHandoffSeamless()
    {
        WriteBurnToLog(polls: 10);
        var recorder = Recorder();
        recorder.LoadOrBackfillOnStartup();
        var after = Assert.Single(recorder.Snapshot(), e => e.Kind == "session").ObservationCount;

        // A replay of the last logged sample (the seam between backfill and live) is a no-op.
        var lastLine = File.ReadLines(Directory.GetFiles(_logDir, "samples-*.jsonl").Single()).Last();
        recorder.Observe(JsonSerializer.Deserialize<LimitLogSample>(lastLine)!);

        Assert.Equal(after, Assert.Single(recorder.Snapshot(), e => e.Kind == "session").ObservationCount);
    }

    [Fact]
    public void Observe_StoreFailure_IsContainedAndTheEstimateStillAdvances()
    {
        // A file sitting where the log directory should be makes every persist fail; the
        // in-memory estimate must keep working and nothing may throw into the poll path.
        File.WriteAllText(_logDir, "in the way");
        var recorder = Recorder();
        recorder.LoadOrBackfillOnStartup();

        var resets = T0 + UsageWindows.FiveHour;
        recorder.Observe(Sample(T0, 2, resets, 10_000));
        _now += TimeSpan.FromMinutes(10);
        recorder.Observe(Sample(_now, 4, resets, 20_000));

        var estimate = Assert.Single(recorder.Snapshot(), e => e.Kind == "session");
        Assert.Equal(1, estimate.ObservationCount);
    }
}
