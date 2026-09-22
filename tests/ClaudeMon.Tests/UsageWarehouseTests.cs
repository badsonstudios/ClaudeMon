namespace ClaudeMon.Tests;

using System.Globalization;
using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class UsageWarehouseTests : IDisposable
{
    private readonly string _dir;

    public UsageWarehouseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"claudemon-warehouse-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private UsageWarehouse Warehouse(int retentionDays = 0) =>
        new(_dir, () => retentionDays);

    private static Dictionary<string, Dictionary<string, LocalDayTotals>> Day(
        string dayKey, string cellKey, double usd, long input = 100) =>
        new(StringComparer.Ordinal)
        {
            [dayKey] = new Dictionary<string, LocalDayTotals>(StringComparer.Ordinal)
            {
                [cellKey] = new() { InputTokens = input, CostUsd = usd },
            },
        };

    private static readonly Dictionary<string, string> NoPaths = new();

    private static DateOnly D(string date) =>
        DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string MonthFile(string monthKey) => Path.Combine(_dir, $"usage-{monthKey}.json");

    [Fact]
    public void UpsertDays_ThenReadRange_RoundTripsCells()
    {
        var warehouse = Warehouse();
        warehouse.UpsertDays(
            Day("2026-03-04", "proj-a|claude-fable-5", 1.25, input: 900),
            new Dictionary<string, string> { ["proj-a"] = @"C:\Projects\A" });

        // A fresh instance, so the assertion goes through the file rather than the cache.
        var range = Warehouse().ReadRange(D("2026-03-01"), D("2026-03-31"));

        Assert.Single(range.Days);
        var cell = range.Days["2026-03-04"]["proj-a|claude-fable-5"];
        Assert.Equal(900, cell.InputTokens);
        Assert.Equal(1.25, cell.CostUsd);
        Assert.Equal(@"C:\Projects\A", range.ProjectPaths["proj-a"]);
    }

    [Fact]
    public void UpsertDays_SameDayTwice_ReplacesRatherThanAccumulates()
    {
        var warehouse = Warehouse();
        warehouse.UpsertDays(Day("2026-03-04", "proj-a|m", 1.0, input: 100), NoPaths);
        warehouse.UpsertDays(Day("2026-03-04", "proj-a|m", 1.0, input: 100), NoPaths);

        var cell = Warehouse().ReadRange(D("2026-03-04"), D("2026-03-04"))
            .Days["2026-03-04"]["proj-a|m"];
        Assert.Equal(100, cell.InputTokens);
        Assert.Equal(1.0, cell.CostUsd);
    }

    [Fact]
    public void UpsertDays_AmendedDay_TakesTheNewerFigures()
    {
        var warehouse = Warehouse();
        warehouse.UpsertDays(Day("2026-03-04", "proj-a|m", 1.0, input: 100), NoPaths);
        // A re-scan found more entries for the same day.
        warehouse.UpsertDays(Day("2026-03-04", "proj-a|m", 3.0, input: 400), NoPaths);

        var cell = Warehouse().ReadRange(D("2026-03-04"), D("2026-03-04"))
            .Days["2026-03-04"]["proj-a|m"];
        Assert.Equal(400, cell.InputTokens);
        Assert.Equal(3.0, cell.CostUsd);
    }

    [Fact]
    public void UpsertDays_SplitsAcrossMonthFiles()
    {
        var days = Day("2026-03-31", "p|m", 1.0);
        foreach (var (k, v) in Day("2026-04-01", "p|m", 2.0))
            days[k] = v;

        Warehouse().UpsertDays(days, NoPaths);

        Assert.True(File.Exists(MonthFile("2026-03")));
        Assert.True(File.Exists(MonthFile("2026-04")));

        var range = Warehouse().ReadRange(D("2026-03-30"), D("2026-04-02"));
        Assert.Equal(2, range.Days.Count);
    }

    [Fact]
    public void ReadRange_ClipsToTheRequestedDays()
    {
        var days = Day("2026-03-10", "p|m", 1.0);
        foreach (var (k, v) in Day("2026-03-20", "p|m", 2.0))
            days[k] = v;
        Warehouse().UpsertDays(days, NoPaths);

        var range = Warehouse().ReadRange(D("2026-03-15"), D("2026-03-25"));

        Assert.Equal(["2026-03-20"], range.Days.Keys);
    }

    [Fact]
    public void ReadRange_MissingDirectory_IsEmpty()
    {
        var range = Warehouse().ReadRange(D("2026-03-01"), D("2026-03-31"));

        Assert.True(range.IsEmpty);
        Assert.Empty(range.ProjectPaths);
    }

    [Fact]
    public void ReadRange_InvertedRange_IsEmpty()
    {
        Warehouse().UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths);

        Assert.True(Warehouse().ReadRange(D("2026-03-10"), D("2026-03-01")).IsEmpty);
    }

    [Fact]
    public void ForeignVersionMonth_IsSkippedAndLeftOnDisk()
    {
        Directory.CreateDirectory(_dir);
        // A month written by a hypothetical future schema.
        var foreign = "{\"v\":99,\"days\":{\"2026-03-04\":{\"p|m\":{\"in\":5,\"usd\":9.0}}},\"projects\":{}}";
        File.WriteAllText(MonthFile("2026-03"), foreign);
        Warehouse().UpsertDays(Day("2026-04-04", "p|m", 1.0), NoPaths);

        var range = Warehouse().ReadRange(D("2026-03-01"), D("2026-04-30"));

        // The foreign month contributes nothing...
        Assert.Equal(["2026-04-04"], range.Days.Keys);
        // ...and is not rewritten or deleted — it may hold days nothing can rebuild.
        Assert.Equal(foreign, File.ReadAllText(MonthFile("2026-03")));
    }

    [Fact]
    public void ForeignVersionMonth_RefusesWritesRatherThanClobbering()
    {
        Directory.CreateDirectory(_dir);
        var foreign = "{\"v\":99,\"days\":{},\"projects\":{}}";
        File.WriteAllText(MonthFile("2026-03"), foreign);

        var banked = Warehouse().UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths);

        Assert.Empty(banked);
        Assert.Equal(foreign, File.ReadAllText(MonthFile("2026-03")));
    }

    [Fact]
    public void CorruptMonth_IsSkippedAndLeftOnDisk()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(MonthFile("2026-03"), "{not json at all");

        var warehouse = Warehouse();
        Assert.True(warehouse.ReadRange(D("2026-03-01"), D("2026-03-31")).IsEmpty);
        Assert.Empty(warehouse.UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths));
        Assert.Equal("{not json at all", File.ReadAllText(MonthFile("2026-03")));
    }

    [Fact]
    public void UpsertDays_MalformedDayKey_IsRefused()
    {
        var days = new Dictionary<string, Dictionary<string, LocalDayTotals>>(StringComparer.Ordinal)
        {
            ["not-a-day"] = new(StringComparer.Ordinal) { ["p|m"] = new() { CostUsd = 1.0 } },
        };

        Warehouse().UpsertDays(days, NoPaths);

        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
    }

    [Fact]
    public void UpsertDays_LearnsPathsOnlyForProjectsTheMonthHolds()
    {
        Warehouse().UpsertDays(
            Day("2026-03-04", "proj-a|m", 1.0),
            new Dictionary<string, string>
            {
                ["proj-a"] = @"C:\Projects\A",
                ["proj-b"] = @"C:\Projects\B",
            });

        var month = JsonSerializer.Deserialize<UsageWarehouseMonth>(File.ReadAllText(MonthFile("2026-03")))!;
        Assert.Equal([@"C:\Projects\A"], month.ProjectPaths.Values);
    }

    [Fact]
    public void UpsertDays_KeepsAPathTheScannerHasForgotten()
    {
        var warehouse = Warehouse();
        warehouse.UpsertDays(
            Day("2026-03-04", "proj-a|m", 1.0),
            new Dictionary<string, string> { ["proj-a"] = @"C:\Projects\A" });
        // A later roll-in whose live map no longer knows proj-a (its last live
        // day aged out) must not erase what the warehouse already learned.
        warehouse.UpsertDays(Day("2026-03-05", "proj-a|m", 2.0), NoPaths);

        var range = Warehouse().ReadRange(D("2026-03-01"), D("2026-03-31"));
        Assert.Equal(@"C:\Projects\A", range.ProjectPaths["proj-a"]);
    }

    [Fact]
    public void Prune_UnlimitedRetention_KeepsEverything()
    {
        var warehouse = Warehouse(retentionDays: 0);
        warehouse.UpsertDays(Day("2020-01-02", "p|m", 1.0), NoPaths);

        warehouse.Prune(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));

        Assert.False(Warehouse().ReadRange(D("2020-01-01"), D("2020-01-31")).IsEmpty);
    }

    [Fact]
    public void Prune_DeletesWholeMonthsPastRetention()
    {
        var warehouse = Warehouse(retentionDays: 30);
        warehouse.UpsertDays(Day("2025-12-15", "p|m", 1.0), NoPaths);
        warehouse.UpsertDays(Day("2026-03-01", "p|m", 2.0), NoPaths);

        warehouse.Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        Assert.False(File.Exists(MonthFile("2025-12")));
        Assert.True(File.Exists(MonthFile("2026-03")));
    }

    [Fact]
    public void Prune_TrimsTheCutoffMonthWithoutLosingLaterDays()
    {
        var warehouse = Warehouse(retentionDays: 30);
        var days = Day("2026-02-01", "p|m", 1.0);
        foreach (var (k, v) in Day("2026-02-20", "p|m", 2.0))
            days[k] = v;
        warehouse.UpsertDays(days, NoPaths);

        // Cutoff = 2026-03-04 minus 30 days = 2026-02-02.
        warehouse.Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        var range = Warehouse().ReadRange(D("2026-01-01"), D("2026-03-31"));
        Assert.Equal(["2026-02-20"], range.Days.Keys);
    }

    [Fact]
    public void Prune_DropsPathsForProjectsItPrunedAway()
    {
        var warehouse = Warehouse(retentionDays: 30);
        var days = Day("2026-02-01", "gone|m", 1.0);
        foreach (var (k, v) in Day("2026-02-20", "kept|m", 2.0))
            days[k] = v;
        warehouse.UpsertDays(days, new Dictionary<string, string>
        {
            ["gone"] = @"C:\Projects\Gone",
            ["kept"] = @"C:\Projects\Kept",
        });

        warehouse.Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        var range = Warehouse().ReadRange(D("2026-01-01"), D("2026-03-31"));
        Assert.Equal([@"C:\Projects\Kept"], range.ProjectPaths.Values);
    }

    [Fact]
    public void Prune_LeavesStrayFilesAlone()
    {
        Directory.CreateDirectory(_dir);
        var stray = Path.Combine(_dir, "usage-notes.json");
        File.WriteAllText(stray, "{}");

        Warehouse(retentionDays: 1).Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void LockedMonth_RefusesTheWriteWithoutThrowing()
    {
        var warehouse = Warehouse();
        Assert.NotEmpty(warehouse.UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths));

        // A fresh instance so the month must be re-read from the locked file.
        var blocked = Warehouse();
        using (File.Open(MonthFile("2026-03"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(blocked.UpsertDays(Day("2026-03-05", "p|m", 2.0), NoPaths));
        }

        // The failure is transient, not memoized: the retry lands.
        Assert.NotEmpty(blocked.UpsertDays(Day("2026-03-05", "p|m", 2.0), NoPaths));
        Assert.Equal(2, Warehouse().ReadRange(D("2026-03-01"), D("2026-03-31")).Days.Count);
    }

    [Fact]
    public void UnwritableMonth_DoesNotStrandTheDaysOfAHealthyOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(MonthFile("2026-03"), "{\"v\":99,\"days\":{},\"projects\":{}}");

        var days = Day("2026-03-04", "p|m", 1.0);
        foreach (var (k, v) in Day("2026-04-04", "p|m", 2.0))
            days[k] = v;

        // Only the day belonging to the healthy month comes back as banked.
        Assert.Equal(["2026-04-04"], Warehouse().UpsertDays(days, NoPaths));
    }

    [Fact]
    public void NullFieldsInAMonthFile_AreToleratedNotThrown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            MonthFile("2026-03"),
            $"{{\"v\":{UsageWarehouseMonth.CurrentVersion},\"days\":null,\"projects\":null}}");

        var warehouse = Warehouse();
        Assert.True(warehouse.ReadRange(D("2026-03-01"), D("2026-03-31")).IsEmpty);
        // Readable, just empty — so it is still writable through.
        Assert.NotEmpty(warehouse.UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths));
    }

    [Fact]
    public void CaseDuplicateProjectKeys_AreToleratedNotThrown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            MonthFile("2026-03"),
            $"{{\"v\":{UsageWarehouseMonth.CurrentVersion},\"days\":{{}}," +
            "\"projects\":{\"Proj-A\":\"C:\\\\one\",\"proj-a\":\"C:\\\\two\"}}");

        var range = Warehouse().ReadRange(D("2026-03-01"), D("2026-03-31"));

        Assert.Single(range.ProjectPaths);
    }

    [Fact]
    public void Prune_LeavesAForeignVersionMonthAlone()
    {
        Directory.CreateDirectory(_dir);
        var foreign = "{\"v\":99,\"days\":{\"2020-01-02\":{\"p|m\":{\"in\":5}}},\"projects\":{}}";
        File.WriteAllText(MonthFile("2020-01"), foreign);

        Warehouse(retentionDays: 30).Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        Assert.Equal(foreign, File.ReadAllText(MonthFile("2020-01")));
    }

    [Fact]
    public void Prune_KeepsTheCutoffDayAndDropsTheOneBeforeIt()
    {
        var warehouse = Warehouse(retentionDays: 30);
        var days = Day("2026-02-01", "p|m", 1.0);
        foreach (var (k, v) in Day("2026-02-02", "p|m", 2.0))
            days[k] = v;
        warehouse.UpsertDays(days, NoPaths);

        // 2026-03-04 minus 30 days = 2026-02-02, which is the oldest day kept.
        warehouse.Prune(new DateTimeOffset(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local)));

        Assert.Equal(["2026-02-02"], Warehouse().ReadRange(D("2026-01-01"), D("2026-03-31")).Days.Keys);
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotThrow()
    {
        var warehouse = Warehouse();

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 200; i++)
                warehouse.UpsertDays(Day($"2026-03-{i % 28 + 1:00}", "p|m", i), NoPaths);
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                warehouse.ReadRange(D("2026-03-01"), D("2026-03-31"));
        });

        // The assertion is that neither task faults — the store is documented
        // thread-safe, and the read path hands out copies, never live state.
        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void OldestDay_IsTheEarliestDayOnDisk()
    {
        var warehouse = Warehouse();
        warehouse.UpsertDays(Day("2026-03-04", "p|m", 1.0), NoPaths);
        warehouse.UpsertDays(Day("2025-11-20", "p|m", 1.0), NoPaths);

        Assert.Equal(D("2025-11-20"), Warehouse().OldestDay());
    }

    [Fact]
    public void OldestDay_EmptyWarehouse_IsNull()
    {
        Assert.Null(Warehouse().OldestDay());
    }
}
