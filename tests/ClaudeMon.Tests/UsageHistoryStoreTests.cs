namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Services;

public class UsageHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public UsageHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-hist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "history.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static UsageSample SampleAt(DateTimeOffset t, double h5 = 10, double? d7 = 5) =>
        new(t, h5, d7);

    [Fact]
    public void Record_PersistsAcrossReload()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path);
        store.Record(SampleAt(now.AddMinutes(-10), h5: 20));
        store.Record(SampleAt(now.AddMinutes(-5), h5: 30));

        // Simulate a restart: a fresh store loading the same file.
        var reloaded = new UsageHistoryStore(_path);
        reloaded.Load();

        var samples = reloaded.Samples;
        Assert.Equal(2, samples.Count);
        Assert.Equal(20, samples[0].FiveHourPct);
        Assert.Equal(30, samples[1].FiveHourPct);
    }

    [Fact]
    public void Record_NullSevenDay_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path);
        store.Record(new UsageSample(now, FiveHourPct: 12, SevenDayPct: null));

        var reloaded = new UsageHistoryStore(_path);
        reloaded.Load();

        var sample = Assert.Single(reloaded.Samples);
        Assert.Equal(12, sample.FiveHourPct);
        Assert.Null(sample.SevenDayPct);
    }

    [Fact]
    public void Record_PrunesSamplesOlderThanMaxAge()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path, maxAge: TimeSpan.FromHours(1));

        store.Record(SampleAt(now.AddHours(-2), h5: 1));  // too old → pruned
        store.Record(SampleAt(now.AddMinutes(-30), h5: 2));
        store.Record(SampleAt(now, h5: 3));

        var samples = store.Samples;
        Assert.Equal(2, samples.Count);
        Assert.DoesNotContain(samples, s => s.FiveHourPct == 1);
    }

    [Fact]
    public void Record_TrimsToMaxCount()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path, maxAge: TimeSpan.FromDays(7), maxCount: 5);

        for (var i = 0; i < 20; i++)
            store.Record(SampleAt(now.AddSeconds(i), h5: i));

        var samples = store.Samples;
        Assert.Equal(5, samples.Count);
        // The oldest were dropped; the newest (15..19) remain in order.
        Assert.Equal(15, samples[0].FiveHourPct);
        Assert.Equal(19, samples[^1].FiveHourPct);
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var store = new UsageHistoryStore(Path.Combine(_tempDir, "nope.json"));
        store.Load();
        Assert.Empty(store.Samples);
    }

    [Fact]
    public void Load_CorruptFile_StartsEmptyWithoutThrowing()
    {
        File.WriteAllText(_path, "this is not json");
        var store = new UsageHistoryStore(_path);

        var ex = Record.Exception(() => store.Load());
        Assert.Null(ex);
        Assert.Empty(store.Samples);
    }

    [Fact]
    public void Recent_ReturnsOnlySamplesWithinWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path, maxAge: TimeSpan.FromDays(1));
        store.Record(SampleAt(now.AddHours(-6), h5: 1));
        store.Record(SampleAt(now.AddMinutes(-10), h5: 2));

        var recent = store.Recent(TimeSpan.FromHours(5));
        Assert.Single(recent);
        Assert.Equal(2, recent[0].FiveHourPct);
    }

    [Fact]
    public void Record_IsThreadSafe()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new UsageHistoryStore(_path, maxAge: TimeSpan.FromDays(7), maxCount: 10_000);

        Parallel.For(0, 200, i => store.Record(SampleAt(now.AddSeconds(i), h5: i % 100)));

        Assert.Equal(200, store.Samples.Count);
    }
}
