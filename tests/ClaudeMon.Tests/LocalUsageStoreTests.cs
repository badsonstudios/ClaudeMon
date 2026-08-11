namespace ClaudeMon.Tests;

using System.Globalization;
using System.Text;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class LocalUsageStoreTests : IDisposable
{
    // BOM-less UTF-8, matching what Claude Code actually writes to transcripts;
    // Encoding.UTF8 would prepend a BOM to every fixture file.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly string _tempDir;
    private readonly string _projectsDir;
    private readonly string _cachePath;
    // The injected "now": local noon today, so tests that place entries a few
    // hours around it can never straddle local midnight and go flaky.
    private readonly DateTimeOffset _now = new(DateTime.Now.Date.AddHours(12));

    public LocalUsageStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-local-{Guid.NewGuid():N}");
        _projectsDir = Path.Combine(_tempDir, "projects");
        _cachePath = Path.Combine(_tempDir, "local-usage.json");
        Directory.CreateDirectory(Path.Combine(_projectsDir, "proj-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static PricingTable Pricing() => new(new Dictionary<string, ModelPricing>
    {
        // Round numbers so expected costs are easy to compute by hand:
        // $10/MTok input, $50/MTok output, cache write $12.5/$20, cache read $1.
        ["claude-fable-5"] = new(10.0, 50.0, 12.5, 20.0, 1.0),
    });

    private LocalUsageStore Store(PricingTable? pricing = null, string? projectsDir = null) =>
        new(projectsDir ?? _projectsDir, _cachePath, pricing ?? Pricing(), clock: () => _now);

    private static string Line(
        DateTimeOffset timestamp,
        string? msgId,
        string? reqId,
        long input = 100,
        long output = 200,
        string model = "claude-fable-5",
        string? cwd = null,
        long cacheWrite = 0,
        long cacheRead = 0)
    {
        var idPart = msgId is null ? "" : $"\"id\":\"{msgId}\",";
        var reqPart = reqId is null ? "" : $"\"requestId\":\"{reqId}\",";
        var cwdPart = cwd is null ? "" : $"\"cwd\":\"{cwd.Replace("\\", "\\\\")}\",";
        var ts = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return "{\"type\":\"assistant\"," + reqPart + cwdPart
            + $"\"timestamp\":\"{ts}\",\"message\":{{" + idPart
            + $"\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},"
            + $"\"cache_creation_input_tokens\":{cacheWrite},\"cache_read_input_tokens\":{cacheRead}}}}}}}";
    }

    private string WriteTranscript(string name, params string[] lines) =>
        WriteTranscriptTo("proj-a", name, lines);

    private string WriteTranscriptTo(string project, string name, params string[] lines)
    {
        var dir = Path.Combine(_projectsDir, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")), Utf8NoBom);
        return path;
    }

    [Fact]
    public void ScanOnce_ReadsEntriesAndAggregatesToday()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-90), "msg_1", "req_1", input: 100, output: 200),
            Line(_now.AddMinutes(-80), "msg_2", "req_2", input: 300, output: 400));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(1000, snap.TotalTokens);
        // (400 * $10 + 600 * $50) / 1M = $0.034
        Assert.Equal(0.034, snap.CostUsd, precision: 10);
        Assert.False(snap.HasUnpricedModels);
    }

    [Fact]
    public void ScanOnce_AppendedLines_OnlyNewBytesParsed()
    {
        // Entries WITHOUT ids have no dedupe key, so a full re-read would double
        // count them — single-counted totals prove the offset logic works.
        var path = WriteTranscript("s1.jsonl", Line(_now.AddMinutes(-30), null, null, input: 100, output: 0));

        var store = Store();
        store.ScanOnce();
        Assert.Equal(100, store.Snapshot()!.TotalTokens);

        File.AppendAllText(path, Line(_now.AddMinutes(-5), null, null, input: 50, output: 0) + "\n");
        store.ScanOnce();
        Assert.Equal(150, store.Snapshot()!.TotalTokens);

        // A third scan with nothing new must not change anything either.
        store.ScanOnce();
        Assert.Equal(150, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_PartialTrailingLine_DeferredUntilNewlineArrives()
    {
        var complete = Line(_now.AddMinutes(-30), null, null, input: 100, output: 0);
        var partial = Line(_now.AddMinutes(-5), null, null, input: 50, output: 0);
        var half = partial[..(partial.Length / 2)];

        var path = Path.Combine(_projectsDir, "proj-a", "s1.jsonl");
        File.WriteAllText(path, complete + "\n" + half, Utf8NoBom);

        var store = Store();
        store.ScanOnce();
        // The half-written line is not parsed (and not half-counted).
        Assert.Equal(100, store.Snapshot()!.TotalTokens);

        File.AppendAllText(path, partial[half.Length..] + "\n");
        store.ScanOnce();
        Assert.Equal(150, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_DuplicateMessageAcrossFiles_CountedOnce()
    {
        var duplicate = Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0);
        WriteTranscript("s1.jsonl", duplicate);
        WriteTranscript("s2.jsonl", duplicate);

        var store = Store();
        store.ScanOnce();

        Assert.Equal(100, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_StreamingDuplicateLinesSameFile_CountedOnce()
    {
        // Streaming writes several lines per assistant message, all carrying the
        // same message id + request id and the same usage totals.
        var duplicate = Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0);
        WriteTranscript("s1.jsonl", duplicate, duplicate, duplicate);

        var store = Store();
        store.ScanOnce();

        Assert.Equal(100, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_ShrunkFile_ReReadWithoutDoubleCounting()
    {
        var first = Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0);
        var second = Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 50, output: 0);
        var path = WriteTranscript("s1.jsonl", first, second);

        var store = Store();
        store.ScanOnce();
        Assert.Equal(150, store.Snapshot()!.TotalTokens);

        // Truncate to just the first entry: the file is re-read from 0, but the
        // dedupe keys stop the retained entry from counting twice.
        File.WriteAllText(path, first + "\n", Utf8NoBom);
        store.ScanOnce();
        Assert.Equal(150, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_NewFileDiscovered_Included()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0));

        var store = Store();
        store.ScanOnce();
        Assert.Equal(100, store.Snapshot()!.TotalTokens);

        // New session file in a new project directory, discovered without any watcher.
        Directory.CreateDirectory(Path.Combine(_projectsDir, "proj-b"));
        File.WriteAllText(
            Path.Combine(_projectsDir, "proj-b", "s2.jsonl"),
            Line(_now.AddMinutes(-5), "msg_2", "req_2", input: 40, output: 0) + "\n",
            Utf8NoBom);

        store.ScanOnce();
        Assert.Equal(140, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_StaleFileOnFirstScan_FastForwardedWithoutParsing()
    {
        // The file CONTAINS a today-stamped entry, but its mtime says it hasn't
        // been written in 10 days — a first scan must fast-forward it unread
        // (this is what keeps a cold start over a big old history cheap).
        var path = WriteTranscript("old.jsonl", Line(_now, "msg_1", "req_1", input: 100, output: 0));
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddDays(-40));

        var store = Store();
        store.ScanOnce();

        Assert.Null(store.Snapshot());
    }

    [Fact]
    public void ScanOnce_MissingProjectsDir_SnapshotNullWithoutThrowing()
    {
        var store = Store(projectsDir: Path.Combine(_tempDir, "does-not-exist"));

        var ex = Record.Exception(() => store.ScanOnce());

        Assert.Null(ex);
        Assert.Null(store.Snapshot());
        Assert.False(store.IsAvailable);
    }

    [Fact]
    public void Snapshot_UnknownModel_TokensCountedCostFlaggedUnpriced()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 200, model: "claude-new-hotness-6"));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(300, snap.TotalTokens);
        Assert.Equal(0, snap.CostUsd);
        Assert.True(snap.HasUnpricedModels);
    }

    [Fact]
    public void Snapshot_BundledPricing_DayOfCurrentModels_IsRealCostNotAFloor()
    {
        // The bundled table — not the round-numbers fake — because the point is
        // that a day of today's models is fully priced, so the flyout shows a
        // real "Today: $…" and the chart doesn't hatch the bar (#161).
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 1_000_000, output: 1_000_000, model: "claude-opus-5"),
            Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 1_000_000, output: 1_000_000, model: "claude-sonnet-5"));

        var store = Store(PricingTable.LoadEmbedded());
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        // opus-5: $5 + $25. sonnet-5: $3 + $15.
        Assert.Equal(48.0, snap.CostUsd, precision: 10);
        Assert.False(snap.HasUnpricedModels);
    }

    [Fact]
    public void Snapshot_BurnRate_FromRecentWindow()
    {
        // 1M output tokens 10 minutes ago = $50 in the 30-minute window → $100/hr.
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-10), "msg_1", "req_1", input: 0, output: 1_000_000));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.NotNull(snap.BurnRateUsdPerHour);
        Assert.Equal(100.0, snap.BurnRateUsdPerHour.Value, precision: 6);
    }

    [Fact]
    public void Snapshot_BurnRate_NullWhenIdle()
    {
        // Activity today, but hours ago: the day totals show, the rate doesn't.
        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-4), "msg_1", "req_1", input: 100, output: 0));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.Null(snap.BurnRateUsdPerHour);
    }

    [Fact]
    public void Snapshot_TodayKeyedByLocalDate()
    {
        // One entry late yesterday (local), one early today (local): only
        // today's shows in the snapshot.
        var todayLocal = _now.ToLocalTime().Date;
        var lateYesterday = new DateTimeOffset(todayLocal.AddMinutes(-30), _now.ToLocalTime().Offset);
        var earlyToday = new DateTimeOffset(todayLocal.AddMinutes(30), _now.ToLocalTime().Offset);

        WriteTranscript("s1.jsonl",
            Line(lateYesterday, "msg_1", "req_1", input: 999, output: 0),
            Line(earlyToday, "msg_2", "req_2", input: 111, output: 0));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(111, snap.TotalTokens);
        // The snapshot says which local day it is about, so the flyout's "Today" line can't
        // quietly go stale across midnight while the app stays open.
        Assert.Equal(DateOnly.FromDateTime(todayLocal), snap.LocalDate);
    }

    [Fact]
    public void Snapshot_SplitsCacheTokensOutOfTheTotal()
    {
        // The flyout reports cache writes and reads separately from the plain input/output
        // traffic, so the store has to carry the split, not just the sum.
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1",
                input: 100, output: 200, cacheWrite: 40, cacheRead: 1000));

        var store = Store();
        store.ScanOnce();

        var snap = store.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(40, snap.CacheWriteTokens);
        Assert.Equal(1000, snap.CacheReadTokens);
        Assert.Equal(1340, snap.TotalTokens);
    }

    [Fact]
    public void Cache_PersistsAcrossReload_NoRecount()
    {
        // Undeduped entries (no ids): if the reloaded store re-read the file,
        // the totals would double.
        WriteTranscript("s1.jsonl", Line(_now.AddMinutes(-30), null, null, input: 100, output: 0));

        var store = Store();
        store.ScanOnce();
        Assert.Equal(100, store.Snapshot()!.TotalTokens);

        var reloaded = Store();
        reloaded.Load();
        reloaded.ScanOnce();
        Assert.Equal(100, reloaded.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void Load_CorruptCache_StartsFreshWithoutThrowing()
    {
        File.WriteAllText(_cachePath, "this is not json");
        WriteTranscript("s1.jsonl", Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0));

        var store = Store();
        var ex = Record.Exception(() => store.Load());
        store.ScanOnce();

        Assert.Null(ex);
        Assert.Equal(100, store.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ReadCompleteLines_ReturnsOffsetPastLastCompleteLine()
    {
        var bytes = Encoding.UTF8.GetBytes("first\nsecond\npartial");
        using var stream = new MemoryStream(bytes);
        var lines = new List<string>();

        var consumed = LocalUsageStore.ReadCompleteLines(stream, 0, lines.Add);

        Assert.Equal(new[] { "first", "second" }, lines);
        Assert.Equal(Encoding.UTF8.GetByteCount("first\nsecond\n"), consumed);
    }

    [Fact]
    public void ReadCompleteLines_StartsAtGivenOffset()
    {
        var prefix = "already-consumed\n";
        var bytes = Encoding.UTF8.GetBytes(prefix + "new-line\n");
        using var stream = new MemoryStream(bytes);
        var lines = new List<string>();

        var consumed = LocalUsageStore.ReadCompleteLines(
            stream, Encoding.UTF8.GetByteCount(prefix), lines.Add);

        Assert.Equal(new[] { "new-line" }, lines);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void ReadCompleteLines_CrLf_TrimsCarriageReturn()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("line-one\r\nline-two\r\n"));
        var lines = new List<string>();

        LocalUsageStore.ReadCompleteLines(stream, 0, lines.Add);

        Assert.Equal(new[] { "line-one", "line-two" }, lines);
    }

    [Fact]
    public void Breakdown_AggregatesPerModelAndPerProject()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddMinutes(-90), "msg_1", "req_1", input: 0, output: 100_000, model: "claude-fable-5"),
            Line(_now.AddMinutes(-80), "msg_2", "req_2", input: 0, output: 200_000, model: "claude-other-1"));
        WriteTranscriptTo("proj-b", "s2.jsonl",
            Line(_now.AddMinutes(-70), "msg_3", "req_3", input: 0, output: 400_000, model: "claude-fable-5"));

        var store = Store();
        store.ScanOnce();

        var breakdown = store.Breakdown(BreakdownTimeframe.Today);
        Assert.NotNull(breakdown);

        // By model: fable summed across both projects; the unknown model kept separately.
        Assert.Equal(2, breakdown.ByModel.Count);
        var fable = Assert.Single(breakdown.ByModel, r => r.Key == "claude-fable-5");
        Assert.Equal(500_000, fable.OutputTokens);
        // fable priced at $50/MTok output: 0.5M → $25.
        Assert.Equal(25.0, fable.CostUsd, precision: 10);
        var other = Assert.Single(breakdown.ByModel, r => r.Key == "claude-other-1");
        Assert.True(other.HasUnpricedModels);
        Assert.Equal(0.0, other.CostUsd);

        // By project: proj-a carries both models (and the unpriced flag).
        Assert.Equal(2, breakdown.ByProject.Count);
        var projA = Assert.Single(breakdown.ByProject, r => r.Key == "proj-a");
        Assert.Equal(300_000, projA.OutputTokens);
        Assert.True(projA.HasUnpricedModels);
        var projB = Assert.Single(breakdown.ByProject, r => r.Key == "proj-b");
        Assert.Equal(400_000, projB.OutputTokens);
        Assert.False(projB.HasUnpricedModels);

        Assert.Equal(700_000, breakdown.Totals.TotalTokens);
        // Only the fable tokens price: 500K output × $50/MTok = $25.
        Assert.Equal(25.0, breakdown.Totals.CostUsd, precision: 10);
        Assert.True(breakdown.Totals.HasUnpricedModels);
    }

    [Fact]
    public void Breakdown_TimeframeFiltering()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 1, output: 0),
            Line(_now.AddDays(-3), "msg_2", "req_2", input: 10, output: 0),
            Line(_now.AddDays(-10), "msg_3", "req_3", input: 100, output: 0),
            Line(_now.AddDays(-29), "msg_4", "req_4", input: 1000, output: 0),
            Line(_now.AddDays(-31), "msg_5", "req_5", input: 10_000, output: 0));

        var store = Store();
        store.ScanOnce();

        Assert.Equal(1, store.Breakdown(BreakdownTimeframe.Today)!.Totals.TotalTokens);
        Assert.Equal(11, store.Breakdown(BreakdownTimeframe.SevenDays)!.Totals.TotalTokens);
        // 31 days ago is outside the retention window entirely.
        Assert.Equal(1111, store.Breakdown(BreakdownTimeframe.ThirtyDays)!.Totals.TotalTokens);
    }

    [Theory]
    [InlineData(BreakdownTimeframe.Today, 0)]
    [InlineData(BreakdownTimeframe.SevenDays, 6)]
    [InlineData(BreakdownTimeframe.ThirtyDays, 29)]
    public void Breakdown_ReportsTheInclusiveDateRangeItCovers(BreakdownTimeframe timeframe, int daysBack)
    {
        // The window heading is drawn from these dates, so "Last 7 days" has to mean today plus
        // the six before it — an off-by-one here would label a range the tables don't contain.
        WriteTranscript("s1.jsonl", Line(_now.AddMinutes(-30), "msg_1", "req_1"));

        var store = Store();
        store.ScanOnce();

        var today = DateOnly.FromDateTime(_now.ToLocalTime().DateTime);
        var breakdown = store.Breakdown(timeframe);

        Assert.NotNull(breakdown);
        Assert.Equal(today.AddDays(-daysBack), breakdown.FromDate);
        Assert.Equal(today, breakdown.ToDate);
    }

    [Fact]
    public void Breakdown_ModelIdVariants_MergeToOneRow()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0, model: "claude-fable-5"),
            Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 50, output: 0, model: "claude-fable-5-20260101"));

        var store = Store();
        store.ScanOnce();

        var row = Assert.Single(store.Breakdown(BreakdownTimeframe.Today)!.ByModel);
        Assert.Equal("claude-fable-5", row.Key);
        Assert.Equal(150, row.InputTokens);
    }

    [Fact]
    public void Breakdown_ProjectDisplayName_FromCwdWithRawKeyFallback()
    {
        WriteTranscriptTo("c--Projects-ClaudeMon", "s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", cwd: @"C:\Projects\ClaudeMon"));
        WriteTranscriptTo("proj-no-cwd", "s2.jsonl",
            Line(_now.AddMinutes(-20), "msg_2", "req_2"));

        var store = Store();
        store.ScanOnce();

        var rows = store.Breakdown(BreakdownTimeframe.Today)!.ByProject;
        Assert.Equal(@"C:\Projects\ClaudeMon",
            Assert.Single(rows, r => r.Key == "c--Projects-ClaudeMon").DisplayName);
        Assert.Equal("proj-no-cwd",
            Assert.Single(rows, r => r.Key == "proj-no-cwd").DisplayName);
    }

    [Fact]
    public void Breakdown_SortedByCostDescending()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 0, output: 10_000, model: "claude-fable-5"),
            Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 0, output: 500_000, model: "claude-fable-5-fast"));

        var store = Store();
        store.ScanOnce();

        var byModel = store.Breakdown(BreakdownTimeframe.Today)!.ByModel;
        Assert.True(byModel[0].CostUsd >= byModel[^1].CostUsd);
    }

    [Fact]
    public void Breakdown_MissingProjectsDir_ReturnsNull()
    {
        var store = Store(projectsDir: Path.Combine(_tempDir, "nope"));
        store.ScanOnce();
        Assert.Null(store.Breakdown(BreakdownTimeframe.ThirtyDays));
    }

    [Fact]
    public void Breakdown_NoDataInTimeframe_EmptyRowsZeroTotals()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddDays(-10), "msg_1", "req_1"));

        var store = Store();
        store.ScanOnce();

        var today = store.Breakdown(BreakdownTimeframe.Today);
        Assert.NotNull(today);
        Assert.Empty(today.ByModel);
        Assert.Empty(today.ByProject);
        Assert.Equal(0, today.Totals.TotalTokens);
    }

    // Two projects sharing one model, one of them also using a second (unpriced) model — the
    // smallest interesting model × project cross-product (#112).
    private LocalUsageStore CrossProductStore()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddMinutes(-90), "msg_1", "req_1", input: 0, output: 100_000, model: "claude-fable-5"),
            Line(_now.AddMinutes(-80), "msg_2", "req_2", input: 0, output: 200_000, model: "claude-other-1"));
        WriteTranscriptTo("proj-b", "s2.jsonl",
            Line(_now.AddMinutes(-70), "msg_3", "req_3", input: 0, output: 400_000, model: "claude-fable-5"));

        var store = Store();
        store.ScanOnce();
        return store;
    }

    private static BreakdownPair Pair(LocalUsageBreakdown breakdown, string project, string model) =>
        Assert.Single(breakdown.Pairs, p => p.ProjectKey == project && p.ModelKey == model);

    [Fact]
    public void Breakdown_Pairs_KeepThePairingTheAxisTablesFoldAway()
    {
        var breakdown = CrossProductStore().Breakdown(BreakdownTimeframe.Today);
        Assert.NotNull(breakdown);

        // Three of the four possible combinations were actually used — proj-b never ran the
        // unpriced model, so that pair simply doesn't exist.
        Assert.Equal(3, breakdown.Pairs.Count);
        Assert.Equal(100_000, Pair(breakdown, "proj-a", "claude-fable-5").Totals.OutputTokens);
        Assert.Equal(400_000, Pair(breakdown, "proj-b", "claude-fable-5").Totals.OutputTokens);

        var unpriced = Pair(breakdown, "proj-a", "claude-other-1");
        Assert.Equal(200_000, unpriced.Totals.OutputTokens);
        Assert.True(unpriced.Totals.HasUnpricedModels);
        Assert.Equal(0.0, unpriced.Totals.CostUsd);
    }

    [Fact]
    public void Breakdown_Pairs_SumToTheAxisRowsAndTheGrandTotals()
    {
        var breakdown = CrossProductStore().Breakdown(BreakdownTimeframe.Today);
        Assert.NotNull(breakdown);

        // The property the drill-down is built on: whichever axis you slice by, one key's pairs
        // add up to exactly that key's row in the table.
        foreach (var row in breakdown.ByModel)
        {
            var cells = breakdown.Pairs.Where(p => p.ModelKey == row.Key).ToList();
            Assert.Equal(row.TotalTokens, cells.Sum(p => p.Totals.TotalTokens));
            Assert.Equal(row.CostUsd, cells.Sum(p => p.Totals.CostUsd), precision: 10);
        }

        foreach (var row in breakdown.ByProject)
        {
            var cells = breakdown.Pairs.Where(p => p.ProjectKey == row.Key).ToList();
            Assert.Equal(row.TotalTokens, cells.Sum(p => p.Totals.TotalTokens));
            Assert.Equal(row.CostUsd, cells.Sum(p => p.Totals.CostUsd), precision: 10);
        }

        Assert.Equal(breakdown.Totals.TotalTokens, breakdown.Pairs.Sum(p => p.Totals.TotalTokens));
    }

    [Fact]
    public void Breakdown_Pairs_UseTheProjectDisplayNameFromCwd()
    {
        WriteTranscriptTo("c--Projects-ClaudeMon", "s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", cwd: @"C:\Projects\ClaudeMon"));

        var store = Store();
        store.ScanOnce();

        var pair = Assert.Single(store.Breakdown(BreakdownTimeframe.Today)!.Pairs);
        Assert.Equal("c--Projects-ClaudeMon", pair.ProjectKey);
        Assert.Equal(@"C:\Projects\ClaudeMon", pair.ProjectDisplayName);
    }

    [Fact]
    public void Breakdown_Pairs_RespectTheTimeframe()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 1, output: 0));
        WriteTranscriptTo("proj-b", "s2.jsonl",
            Line(_now.AddDays(-3), "msg_2", "req_2", input: 10, output: 0));

        var store = Store();
        store.ScanOnce();

        // proj-b's usage is three days old, so only the wider window pairs it.
        Assert.Equal("proj-a",
            Assert.Single(store.Breakdown(BreakdownTimeframe.Today)!.Pairs).ProjectKey);
        Assert.Equal(2, store.Breakdown(BreakdownTimeframe.SevenDays)!.Pairs.Count);
    }

    [Fact]
    public void Breakdown_Pairs_MergeModelVariantsAndSpanDays()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0, model: "claude-fable-5"),
            Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 50, output: 0, model: "claude-fable-5-20260101"),
            Line(_now.AddDays(-2), "msg_3", "req_3", input: 7, output: 0, model: "claude-fable-5"));

        var store = Store();
        store.ScanOnce();

        // One pair, not three: the dated variant normalizes onto the plain id the way the ByModel
        // row does, and a pair sums across every day in the timeframe.
        var pair = Assert.Single(store.Breakdown(BreakdownTimeframe.SevenDays)!.Pairs);
        Assert.Equal("claude-fable-5", pair.ModelKey);
        Assert.Equal(157, pair.Totals.InputTokens);
    }

    [Fact]
    public void Breakdown_Pairs_EmptyWhenNothingInRange()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddDays(-10), "msg_1", "req_1"));

        var store = Store();
        store.ScanOnce();

        Assert.Empty(store.Breakdown(BreakdownTimeframe.Today)!.Pairs);
    }

    [Fact]
    public void CostSeries_HasOnePointPerDayInTheTimeframe_EndingToday()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddHours(-1), "msg_1", "req_1"));

        var store = Store();
        store.ScanOnce();

        var today = DateOnly.FromDateTime(_now.ToLocalTime().DateTime);
        var series = store.CostSeries(BreakdownTimeframe.SevenDays);
        Assert.NotNull(series);
        Assert.Equal(7, series.Days.Count);
        Assert.Equal(today.AddDays(-6), series.FromDate);
        Assert.Equal(today, series.ToDate);
        // Contiguous and in ascending date order — the chart's x-axis is the list order.
        for (var i = 0; i < series.Days.Count; i++)
            Assert.Equal(series.FromDate.AddDays(i), series.Days[i].Date);
    }

    [Fact]
    public void CostSeries_PlacesEachDaysCostOnItsOwnDay()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000),  // $5 today
            Line(_now.AddDays(-2), "msg_2", "req_2", input: 0, output: 200_000));  // $10 two days back

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.SevenDays)!;
        Assert.Equal(5.0, series.Days[^1].CostUsd, precision: 10);
        Assert.Equal(10.0, series.Days[^3].CostUsd, precision: 10);
        Assert.Equal(15.0, series.TotalCostUsd, precision: 10);
        Assert.Equal(10.0, series.MaxCostUsd, precision: 10);
    }

    [Fact]
    public void CostSeries_DaysWithoutUsage_ReadZeroRatherThanBeingOmitted()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000));

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.SevenDays)!;
        // A gap in usage must stay a visible gap in the chart, not close up.
        Assert.Equal(7, series.Days.Count);
        Assert.All(series.Days.Take(6), d => Assert.Equal(0.0, d.CostUsd));
        Assert.False(series.HasUnpricedModels);
    }

    [Fact]
    public void CostSeries_FlagsOnlyTheDaysAnUnpricedModelTouched()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000, model: "claude-fable-5"),
            Line(_now.AddDays(-1), "msg_2", "req_2", input: 0, output: 100_000, model: "claude-other-1"));

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.SevenDays)!;
        Assert.False(series.Days[^1].HasUnpricedModels);  // today: fully priced
        Assert.True(series.Days[^2].HasUnpricedModels);   // yesterday: a floor of $0
        Assert.Equal(0.0, series.Days[^2].CostUsd);
        Assert.True(series.HasUnpricedModels);
    }

    [Fact]
    public void CostSeries_Today_IsASinglePoint()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000),
            Line(_now.AddDays(-1), "msg_2", "req_2", input: 0, output: 200_000));

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.Today)!;
        var day = Assert.Single(series.Days);
        Assert.Equal(series.FromDate, series.ToDate);
        Assert.Equal(5.0, day.CostUsd, precision: 10);
    }

    [Fact]
    public void CostSeries_ThirtyDays_CoversTheWholeRetentionWindow()
    {
        WriteTranscript("s1.jsonl",
            Line(_now.AddDays(-29), "msg_1", "req_1", input: 0, output: 100_000),  // the oldest day in range
            Line(_now.AddDays(-31), "msg_2", "req_2", input: 0, output: 900_000)); // outside it entirely

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.ThirtyDays)!;
        Assert.Equal(30, series.Days.Count);
        Assert.Equal(DateOnly.FromDateTime(_now.AddDays(-29).ToLocalTime().DateTime), series.Days[0].Date);
        Assert.Equal(5.0, series.Days[0].CostUsd, precision: 10);
        // The window is inclusive of today, so it reaches back exactly 29 days and no further.
        Assert.Equal(5.0, series.TotalCostUsd, precision: 10);
    }

    [Fact]
    public void LocalCostSeries_WithNoDays_ReportsZerosRatherThanThrowing()
    {
        // The store can't produce this (every timeframe is at least a day), but the chart
        // reads these aggregates before it knows what it was handed.
        var empty = new LocalCostSeries(
            new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 4), []);

        Assert.Equal(0.0, empty.MaxCostUsd);
        Assert.Equal(0.0, empty.TotalCostUsd);
        Assert.False(empty.HasUnpricedModels);
    }

    [Fact]
    public void CostSeries_NoUsageAtAll_IsAllZerosNotNull()
    {
        WriteTranscript("s1.jsonl", Line(_now.AddDays(-10), "msg_1", "req_1"));

        var store = Store();
        store.ScanOnce();

        var series = store.CostSeries(BreakdownTimeframe.Today)!;
        Assert.Equal(0.0, series.MaxCostUsd);
        Assert.Equal(0.0, series.TotalCostUsd);
    }

    [Fact]
    public void CostSeries_MissingProjectsDir_ReturnsNull()
    {
        var store = Store(projectsDir: Path.Combine(_tempDir, "nope"));
        store.ScanOnce();
        Assert.Null(store.CostSeries(BreakdownTimeframe.ThirtyDays));
    }

    [Fact]
    public void CostSeries_TotalMatchesTheBreakdownItIsAggregatedFrom()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000),
            Line(_now.AddDays(-3), "msg_2", "req_2", input: 0, output: 300_000));
        WriteTranscriptTo("proj-b", "s2.jsonl",
            Line(_now.AddDays(-5), "msg_3", "req_3", input: 0, output: 200_000));

        var store = Store();
        store.ScanOnce();

        // Same cells, sliced two ways — the chart and the tables must never disagree.
        Assert.Equal(
            store.Breakdown(BreakdownTimeframe.SevenDays)!.Totals.CostUsd,
            store.CostSeries(BreakdownTimeframe.SevenDays)!.TotalCostUsd,
            precision: 10);
    }

    [Fact]
    public void BudgetTotals_WeekIsMondayThroughToday()
    {
        var todayLocal = DateOnly.FromDateTime(_now.ToLocalTime().DateTime);
        var monday = todayLocal.AddDays(-(((int)todayLocal.DayOfWeek + 6) % 7));
        // Noon on Monday is always inside the week; noon the day before never is.
        var mondayNoon = new DateTimeOffset(
            monday.ToDateTime(new TimeOnly(12, 0)), _now.ToLocalTime().Offset);
        var sundayNoon = mondayNoon.AddDays(-1);

        WriteTranscript("s1.jsonl",
            Line(_now.AddHours(-1), "msg_1", "req_1", input: 0, output: 100_000),  // $5 today
            Line(mondayNoon, "msg_2", "req_2", input: 0, output: 200_000),          // $10 in week
            Line(sundayNoon, "msg_3", "req_3", input: 0, output: 400_000));         // $20 before week

        var store = Store();
        store.ScanOnce();

        var totals = store.BudgetTotals();
        Assert.NotNull(totals);
        Assert.Equal(monday, totals.WeekStartMonday);
        // Monday noon can BE today (when today is Monday): then today = $5 + $10.
        var expectedToday = monday == todayLocal ? 15.0 : 5.0;
        Assert.Equal(expectedToday, totals.TodayUsd, precision: 10);
        Assert.Equal(15.0, totals.WeekUsd, precision: 10);
    }

    [Fact]
    public void Load_V1Cache_DiscardedAndRescanned()
    {
        // A phase-1 cache: no "v" field, flat "days" totals, and — critically —
        // a POPULATED files map whose offset says the transcript was already
        // fully read. The whole cache must be discarded: inheriting that offset
        // would skip the rescan and silently lose the pre-upgrade history
        // (which is exactly what happened when Version defaulted to
        // CurrentVersion — an absent "v" deserialized as current).
        var path = WriteTranscript("s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0));
        var length = new FileInfo(path).Length;
        var mtime = File.GetLastWriteTimeUtc(path).ToString("o");
        File.WriteAllText(_cachePath,
            $"{{\"files\":{{{System.Text.Json.JsonSerializer.Serialize(path)}:{{\"off\":{length},\"mtime\":\"{mtime}\"}}}}," +
            "\"days\":{\"2026-01-01\":{\"in\":999,\"out\":999,\"cw\":0,\"cr\":0,\"usd\":99.0}},\"keys\":{},\"recent\":[]}");

        var store = Store();
        store.Load();
        store.ScanOnce();

        // The transcript was re-read from 0 (offset not inherited) and the
        // stale flat totals are gone (not doubled, not surviving).
        Assert.Equal(100, store.Snapshot()!.TotalTokens);
        Assert.Equal(100, store.Breakdown(BreakdownTimeframe.ThirtyDays)!.Totals.TotalTokens);
    }

    [Fact]
    public void Cache_V2RoundTrip_PreservesCellsAndProjectPaths()
    {
        WriteTranscriptTo("proj-a", "s1.jsonl",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0, cwd: @"C:\Real\Path"));

        var store = Store();
        store.ScanOnce();

        var reloaded = Store();
        reloaded.Load();
        // No rescan needed — the breakdown comes straight from the cache.
        var rows = reloaded.Breakdown(BreakdownTimeframe.Today)!.ByProject;
        Assert.Equal(@"C:\Real\Path", Assert.Single(rows).DisplayName);
        Assert.Equal(100, reloaded.Snapshot()!.TotalTokens);
    }

    [Fact]
    public void ScanOnce_MalformedLinesSkipped_RestStillCount()
    {
        WriteTranscript("s1.jsonl",
            "not json at all",
            Line(_now.AddMinutes(-30), "msg_1", "req_1", input: 100, output: 0),
            "{\"type\":\"assistant\",\"broken",
            Line(_now.AddMinutes(-20), "msg_2", "req_2", input: 50, output: 0));

        var store = Store();
        store.ScanOnce();

        Assert.Equal(150, store.Snapshot()!.TotalTokens);
    }
}
