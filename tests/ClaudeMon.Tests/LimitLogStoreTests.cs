namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class LimitLogStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;

    public LimitLogStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-limitlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logDir = Path.Combine(_tempDir, "limit-log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static LimitLogSample Sample(DateTimeOffset t) =>
        new(t, [new LimitSnapshot("session", "5h", 42.0, "normal", t + TimeSpan.FromHours(2), true, null)],
            new Dictionary<string, ModelTokens> { ["opus"] = new(100, 200, 0, 50) });

    private static LimitWindowRecord Window(DateTimeOffset end) =>
        new("session", "5h", null, end - UsageWindows.FiveHour, end, false,
            87.0, 85.5, end - TimeSpan.FromMinutes(2), 57,
            ClaudePlan.Max20x, ClaudePlan.Max20x, false,
            new Dictionary<string, ModelTokens> { ["opus"] = new(1, 2, 3, 4) },
            false, null);

    [Fact]
    public void AppendSample_WritesOneVersionedLinePerCallToTheUtcMonthFile()
    {
        var store = new LimitLogStore(_logDir);
        var t = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        store.AppendSample(Sample(t));
        store.AppendSample(Sample(t + TimeSpan.FromMinutes(5)));

        var path = Path.Combine(_logDir, "samples-2026-08.jsonl");
        var text = File.ReadAllText(path);
        Assert.EndsWith("\n", text);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(LimitLogSchema.SchemaVersion, doc.RootElement.GetProperty("v").GetInt32());
            Assert.Equal("session", doc.RootElement.GetProperty("limits")[0].GetProperty("kind").GetString());
            Assert.Equal(100, doc.RootElement.GetProperty("tok").GetProperty("opus").GetProperty("in").GetInt64());
        }
    }

    [Fact]
    public void AppendSample_MonthNamingIsUtcNotLocal()
    {
        var store = new LimitLogStore(_logDir);
        // Local time on the far side of a month boundary from UTC: the file is named by UTC.
        store.AppendSample(Sample(new DateTimeOffset(2026, 9, 1, 1, 30, 0, TimeSpan.FromHours(10))));

        Assert.True(File.Exists(Path.Combine(_logDir, "samples-2026-08.jsonl")));
    }

    [Fact]
    public void AppendSample_MonthRollover_SplitsFiles()
    {
        var store = new LimitLogStore(_logDir);
        store.AppendSample(Sample(new DateTimeOffset(2026, 8, 31, 23, 58, 0, TimeSpan.Zero)));
        store.AppendSample(Sample(new DateTimeOffset(2026, 9, 1, 0, 3, 0, TimeSpan.Zero)));

        Assert.True(File.Exists(Path.Combine(_logDir, "samples-2026-08.jsonl")));
        Assert.True(File.Exists(Path.Combine(_logDir, "samples-2026-09.jsonl")));
    }

    [Fact]
    public void AppendWindow_LandsInTheMonthOfTheWindowEnd()
    {
        var store = new LimitLogStore(_logDir);
        // The window started in August and ended in September: it belongs to September's file.
        store.AppendWindow(Window(new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.Zero)));

        var path = Path.Combine(_logDir, "windows-2026-09.jsonl");
        var line = File.ReadAllText(path).TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(87.0, doc.RootElement.GetProperty("peakPct").GetDouble());
        Assert.Equal("Max20x", doc.RootElement.GetProperty("plan").GetString());
        // Null fields (scope model, incomplete reason) drop off the line entirely.
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
        Assert.False(doc.RootElement.TryGetProperty("reason", out _));
    }

    [Fact]
    public void SaveState_RoundTrips()
    {
        var store = new LimitLogStore(_logDir);
        var state = new LimitLogState
        {
            Version = LimitLogState.CurrentVersion,
            LastSampleAt = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            LastTokens = new Dictionary<string, ModelTokens> { ["opus"] = new(1, 2, 3, 4) },
            Windows =
            [
                new ActiveWindowState(
                    "session", "5h", null,
                    new DateTimeOffset(2026, 8, 22, 15, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
                    false, 40, 35, new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
                    7, ClaudePlan.Pro, false,
                    new Dictionary<string, ModelTokens> { ["opus"] = new(10, 20, 30, 40) },
                    false, null),
            ],
        };

        store.SaveState(state);
        var loaded = new LimitLogStore(_logDir).LoadState();

        Assert.NotNull(loaded);
        Assert.Equal(state.LastSampleAt, loaded.LastSampleAt);
        Assert.Equal(new ModelTokens(1, 2, 3, 4), loaded.LastTokens!["opus"]);
        var window = Assert.Single(loaded.Windows);
        Assert.Equal(ClaudePlan.Pro, window.PlanAtStart);
        Assert.Equal(new ModelTokens(10, 20, 30, 40), window.TokensByModel["opus"]);
    }

    [Fact]
    public void LoadState_MissingCorruptOrWrongVersion_ReturnsNull()
    {
        var store = new LimitLogStore(_logDir);
        Assert.Null(store.LoadState());

        Directory.CreateDirectory(_logDir);
        var path = Path.Combine(_logDir, "state.json");

        File.WriteAllText(path, "{not json");
        Assert.Null(store.LoadState());

        // A version-less file (absent "v" deserializes as 0) must not masquerade as current.
        File.WriteAllText(path, "{\"lastSampleAt\":\"2026-08-22T12:00:00+00:00\"}");
        Assert.Null(store.LoadState());

        File.WriteAllText(path, "{\"v\":999}");
        Assert.Null(store.LoadState());
    }

    [Fact]
    public void CapacityState_RoundTrips_AndRejectsOtherVersions()
    {
        var store = new LimitLogStore(_logDir);
        var state = new CapacityEstimateState
        {
            Version = CapacityEstimateState.CurrentVersion,
            LastSampleAt = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            LastTokens = new Dictionary<string, ModelTokens> { ["opus"] = new(1, 2, 3, 4) },
            Plan = ClaudePlan.Max20x,
            Limits =
            [
                new LimitCapacityState
                {
                    Kind = "session",
                    Ring = [new CapacityObservation(2.0, 20_000, "opus",
                        new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 22, 11, 10, 0, TimeSpan.Zero))],
                    TotalObservations = 7,
                },
            ],
        };

        store.SaveCapacityState(state);
        var loaded = new LimitLogStore(_logDir).LoadCapacityState();

        Assert.NotNull(loaded);
        Assert.Equal(ClaudePlan.Max20x, loaded.Plan);
        var limit = Assert.Single(loaded.Limits);
        Assert.Equal(7, limit.TotalObservations);
        Assert.Equal(20_000, Assert.Single(limit.Ring).WeightedTokens);

        // A version-less or future-format file must not masquerade as current.
        File.WriteAllText(Path.Combine(_logDir, "capacity.json"), "{\"plan\":\"Pro\"}");
        Assert.Null(new LimitLogStore(_logDir).LoadCapacityState());
    }

    [Fact]
    public void ReadSamples_StreamsTheRangeOldestFirst_SkippingTornAndForeignLines()
    {
        var store = new LimitLogStore(_logDir);
        var july = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var august = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        store.AppendSample(Sample(july));
        store.AppendSample(Sample(august));

        // A future-version line and a torn, unterminated tail (a crash mid-append): the next
        // append heals the tail onto its own line, so only the torn fragment is lost.
        File.AppendAllText(Path.Combine(_logDir, "samples-2026-08.jsonl"),
            "{\"v\":999,\"t\":\"2026-08-22T13:00:00+00:00\",\"limits\":[]}\n{\"v\":1,\"t\":\"2026-08-2");
        store.AppendSample(Sample(august + TimeSpan.FromMinutes(5)));

        var all = store.ReadSamples(july - TimeSpan.FromDays(1), august + TimeSpan.FromHours(1)).ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(july, all[0].Timestamp); // oldest file first

        // The range filter skips months (and samples) outside it.
        var recentOnly = store.ReadSamples(august - TimeSpan.FromDays(1), august + TimeSpan.FromHours(1));
        Assert.Equal(2, recentOnly.Count());

        // A missing directory streams nothing.
        Assert.Empty(new LimitLogStore(Path.Combine(_tempDir, "nope"))
            .ReadSamples(july, august));
    }

    [Fact]
    public void DriftState_RoundTrips_AndRejectsOtherVersions()
    {
        var store = new LimitLogStore(_logDir);
        var state = new DriftState
        {
            Version = DriftState.CurrentVersion,
            Keys =
            [
                new DriftKeyState
                {
                    Kind = "session",
                    Points = [new DriftPoint(new DateOnly(2026, 8, 22), 60_000_000, ClaudePlan.Max20x)],
                    Notified = true,
                    AcknowledgedAt = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
                },
            ],
        };

        store.SaveDriftState(state);
        var loaded = new LimitLogStore(_logDir).LoadDriftState();

        Assert.NotNull(loaded);
        var key = Assert.Single(loaded.Keys);
        Assert.True(key.Notified);
        Assert.Equal(60_000_000, Assert.Single(key.Points).Capacity);
        Assert.Equal(ClaudePlan.Max20x, Assert.Single(key.Points).Plan);

        File.WriteAllText(Path.Combine(_logDir, "drift.json"), "{\"keys\":[]}");
        Assert.Null(new LimitLogStore(_logDir).LoadDriftState());
    }

    [Fact]
    public void ReadWindows_StreamsTheRange_SkippingTornAndForeignLines()
    {
        var store = new LimitLogStore(_logDir);
        var july = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var august = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        store.AppendWindow(Window(july));
        store.AppendWindow(Window(august));
        File.AppendAllText(Path.Combine(_logDir, "windows-2026-08.jsonl"),
            "{\"v\":999,\"kind\":\"x\"}\n{\"v\":1,\"kind\":\"torn");
        store.AppendWindow(Window(august + TimeSpan.FromHours(5)));

        var all = store.ReadWindows(july - TimeSpan.FromDays(1), august + TimeSpan.FromDays(1)).ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(july, all[0].End); // oldest file first

        var recent = store.ReadWindows(august - TimeSpan.FromDays(1), august + TimeSpan.FromDays(1));
        Assert.Equal(2, recent.Count());

        Assert.Empty(new LimitLogStore(Path.Combine(_tempDir, "nope")).ReadWindows(july, august));
    }

    [Fact]
    public void OldestWindowMonth_FindsTheOldestFileWithoutOpeningAny()
    {
        Assert.Null(new LimitLogStore(_logDir).OldestWindowMonth());

        var store = new LimitLogStore(_logDir);
        store.AppendWindow(Window(new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero)));
        store.AppendWindow(Window(new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(new DateTime(2026, 3, 1), store.OldestWindowMonth());
    }

    [Fact]
    public void LoadState_RebuildsTokenDictionariesCaseInsensitively()
    {
        // Deserialization loses the tracker's comparer; a case miss on a model key would
        // silently rebase that model's delta to zero. Case-duplicate keys (a hand-edited
        // file) fold together instead of throwing.
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(Path.Combine(_logDir, "state.json"),
            "{\"v\":1,\"lastTok\":{\"Opus\":{\"in\":10,\"out\":0,\"cw\":0,\"cr\":0}," +
            "\"opus\":{\"in\":5,\"out\":0,\"cw\":0,\"cr\":0}},\"windows\":[]}");

        var loaded = new LimitLogStore(_logDir).LoadState();

        Assert.NotNull(loaded);
        Assert.Equal(15, Assert.Single(loaded.LastTokens!).Value.InputTokens);
        Assert.True(loaded.LastTokens!.ContainsKey("OPUS"));
    }

    [Fact]
    public void Appends_AreBestEffort_UnwritableDirectoryDoesNotThrow()
    {
        // A file sitting where the log directory should be makes every write fail.
        File.WriteAllText(_logDir, "in the way");
        var store = new LimitLogStore(_logDir);

        store.AppendSample(Sample(DateTimeOffset.UtcNow));
        store.AppendWindow(Window(DateTimeOffset.UtcNow));
        store.SaveState(new LimitLogState { Version = LimitLogState.CurrentVersion });
        Assert.Null(store.LoadState());
    }
}
