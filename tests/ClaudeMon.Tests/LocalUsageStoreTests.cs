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
        string model = "claude-fable-5")
    {
        var idPart = msgId is null ? "" : $"\"id\":\"{msgId}\",";
        var reqPart = reqId is null ? "" : $"\"requestId\":\"{reqId}\",";
        var ts = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return "{\"type\":\"assistant\"," + reqPart
            + $"\"timestamp\":\"{ts}\",\"message\":{{" + idPart
            + $"\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},"
            + "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";
    }

    private string WriteTranscript(string name, params string[] lines)
    {
        var path = Path.Combine(_projectsDir, "proj-a", name);
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
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddDays(-10));

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
