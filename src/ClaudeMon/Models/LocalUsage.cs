namespace ClaudeMon.Models;

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

/// <summary>
/// One deduplicated assistant-message usage record parsed from a Claude Code
/// transcript line (~/.claude/projects/**/*.jsonl). Only the usage numbers,
/// model id, ids, timestamp, and the session's working-directory path (the
/// project display name) are materialized — never message content.
/// </summary>
public record LocalUsageEntry(
    DateTimeOffset Timestamp,
    string Model,
    string? DedupeKey,
    long InputTokens,
    long OutputTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long CacheReadTokens,
    string? Cwd = null)
{
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheWrite5mTokens + CacheWrite1hTokens + CacheReadTokens;
}

/// <summary>Running totals for one local calendar day (persisted).</summary>
public record LocalDayTotals
{
    [JsonPropertyName("in")] public long InputTokens { get; init; }
    [JsonPropertyName("out")] public long OutputTokens { get; init; }
    [JsonPropertyName("cw")] public long CacheWriteTokens { get; init; }
    [JsonPropertyName("cr")] public long CacheReadTokens { get; init; }
    [JsonPropertyName("usd")] public double CostUsd { get; init; }
    // True when a model missing from the pricing table contributed tokens, so the
    // cost shown for this day is incomplete (or absent entirely).
    [JsonPropertyName("unpriced")] public bool HasUnpricedModels { get; init; }

    public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;
}

/// <summary>Per-transcript-file scan position (persisted, keyed by full path).</summary>
public record FileScanState(
    [property: JsonPropertyName("off")] long Offset,
    [property: JsonPropertyName("mtime")] DateTimeOffset LastWriteUtc
);

/// <summary>
/// A recent per-entry cost sample used for the burn-rate window (persisted).
/// <see cref="CacheReadTokens"/> is carried so the flat-plan projection (issue #202) can
/// discount cache reads the way the capacity estimator does — additive, so a cache written
/// before it existed deserializes it as 0 (the burn window is 30 minutes; the seam heals
/// itself) and needs no version bump.
/// </summary>
public record RecentCostSample(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("usd")] double CostUsd,
    [property: JsonPropertyName("tok")] long Tokens,
    [property: JsonPropertyName("cr")] long CacheReadTokens = 0
);

/// <summary>
/// The on-disk cache for the local-usage scanner
/// (%LocalAppData%\ClaudeMon\local-usage.json): per-file offsets so polls never
/// re-read old bytes, per-(day, project, model) aggregate cells, learned
/// project display paths, the recent dedupe keys (value = entry timestamp, so
/// the set can be pruned), and the recent cost samples.
///
/// <see cref="Version"/> guards the schema: a mismatch (including the phase-1
/// flat per-day format, which deserializes with 0) discards the cache and
/// rebuilds from the transcripts — flat totals cannot be split into cells, and
/// the transcripts are the source of truth anyway.
/// </summary>
public record LocalUsageCacheFile
{
    public const int CurrentVersion = 2;

    // Deliberately NO initializer: System.Text.Json only overwrites properties
    // present in the JSON, so a default of CurrentVersion would make a cache
    // with no "v" field (the phase-1 format) masquerade as current and smuggle
    // its stale byte offsets past the guard. Absent must deserialize as 0;
    // Save() stamps the real version explicitly.
    [JsonPropertyName("v")] public int Version { get; init; }
    [JsonPropertyName("files")] public Dictionary<string, FileScanState> Files { get; init; } = new();
    // day "yyyy-MM-dd" (local) → cell key "project|model" → totals.
    [JsonPropertyName("cells")] public Dictionary<string, Dictionary<string, LocalDayTotals>> Cells { get; init; } = new();
    // project dir name under ~/.claude/projects → real cwd path from the transcripts.
    [JsonPropertyName("projects")] public Dictionary<string, string> ProjectPaths { get; init; } = new();
    [JsonPropertyName("keys")] public Dictionary<string, DateTimeOffset> RecentDedupeKeys { get; init; } = new();
    [JsonPropertyName("recent")] public List<RecentCostSample> RecentCosts { get; init; } = new();
    // Days that aged out of the window before the warehouse could accept them
    // (issue #126) — their transcripts are gone, so this is the last copy.
    // Additive and normally empty: absent in a cache written before it existed
    // (and ignored by an older build reading a newer cache), so it needs no
    // version bump — nothing about the fields above changed shape.
    [JsonPropertyName("unbanked")] public Dictionary<string, Dictionary<string, LocalDayTotals>> UnbankedDays { get; init; } = new();
}

/// <summary>
/// One month of the long-term usage warehouse
/// (%LocalAppData%\ClaudeMon\usage-warehouse\usage-YYYY-MM.json): the same
/// per-(day, project, model) cells <see cref="LocalUsageCacheFile"/> holds, for
/// days that have rolled past the scanner's 30-day retention.
///
/// <see cref="Version"/> is this file's own schema version, deliberately
/// independent of <see cref="LocalUsageCacheFile.CurrentVersion"/> — bumping the
/// scanner's cache format discards a cache that can be rebuilt from the
/// transcripts, and must never cost warehouse days whose transcripts are gone.
/// A month written by a future version is skipped on read and left untouched on
/// disk, never deleted.
///
/// <see cref="ProjectPaths"/> is carried per month rather than in one shared
/// file so each month stands alone: the scanner drops a learned path when its
/// last live day ages out, so without this a warehoused project would decay to
/// its raw directory name.
/// </summary>
public record UsageWarehouseMonth
{
    public const int CurrentVersion = 1;

    // No initializer, for the same reason LocalUsageCacheFile.Version has none:
    // absent must deserialize as 0 (a foreign file) rather than masquerade as current.
    [JsonPropertyName("v")] public int Version { get; init; }
    // day "yyyy-MM-dd" (local) → cell key "project|model" → totals.
    [JsonPropertyName("days")] public Dictionary<string, Dictionary<string, LocalDayTotals>> Days { get; init; } = new();
    // project dir name under ~/.claude/projects → real cwd path, for the projects in this month.
    [JsonPropertyName("projects")] public Dictionary<string, string> ProjectPaths { get; init; } = new();
}

/// <summary>
/// Warehouse cells for a requested date range, plus the learned project paths
/// that go with them. Days with no warehoused usage are simply absent — callers
/// fall back to the live cells (or to zero), so this is never dense.
/// </summary>
public record UsageWarehouseRange(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, LocalDayTotals>> Days,
    IReadOnlyDictionary<string, string> ProjectPaths)
{
    /// <summary>
    /// The shared "nothing here" instance. Genuinely immutable, not just typed
    /// as read-only: every in-window query is handed this one and none of them
    /// owns it.
    /// </summary>
    public static UsageWarehouseRange Empty { get; } = new(
        ReadOnlyDictionary<string, IReadOnlyDictionary<string, LocalDayTotals>>.Empty,
        ReadOnlyDictionary<string, string>.Empty);

    public bool IsEmpty => Days.Count == 0 && ProjectPaths.Count == 0;
}

/// <summary>
/// What the UI consumes: today's estimated totals plus the recent burn rate.
/// A null snapshot means the feature is absent (no transcript directory or no
/// data for today) and the flyout line is simply not drawn.
/// </summary>
public record LocalUsageSnapshot(
    DateOnly LocalDate,
    double CostUsd,
    bool HasUnpricedModels,
    long TotalTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    double? BurnRateUsdPerHour,
    double? BurnRateTokensPerHour = null,
    double? BurnRateCacheReadTokensPerHour = null
);

/// <summary>
/// Month-to-date estimated cost, for the flat-plan value line (issue #202):
/// "what would this month have cost at API prices?". Same semantics as every
/// other cost figure — list prices, and <see cref="HasUnpricedModels"/> marks
/// the total as a floor rather than an estimate.
/// </summary>
public record LocalMonthTotals(
    DateOnly MonthStart,
    DateOnly Today,
    double CostUsd,
    bool HasUnpricedModels);

/// <summary>The selectable ranges of the breakdown window, ending today (local).</summary>
public enum BreakdownTimeframe
{
    Today,
    SevenDays,
    ThirtyDays,
}

/// <summary>
/// One row of a breakdown table — a model (summed across projects) or a
/// project (summed across models). <see cref="Key"/> is the aggregation key
/// (normalized model id / project dir name); <see cref="DisplayName"/> is what
/// the UI shows (for projects, the real path learned from the transcripts).
/// </summary>
public record BreakdownRow(
    string Key,
    string DisplayName,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    double CostUsd,
    bool HasUnpricedModels)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;
}

/// <summary>Which axis of the usage cells a breakdown row belongs to.</summary>
public enum BreakdownAxis
{
    Model,
    Project,
}

/// <summary>
/// One cell of the model × project cross-product, summed over the timeframe
/// (#112) — the pairing <see cref="LocalUsageBreakdown.ByModel"/> and
/// <see cref="LocalUsageBreakdown.ByProject"/> fold away. Each (project, model)
/// combination appears at most once.
/// </summary>
public record BreakdownPair(
    string ProjectKey,
    string ProjectDisplayName,
    string ModelKey,
    LocalDayTotals Totals);

/// <summary>
/// The full breakdown for one timeframe: per-model rows, per-project rows
/// (both sorted by cost, then tokens, descending), and the grand totals.
/// </summary>
public record LocalUsageBreakdown(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<BreakdownRow> ByModel,
    IReadOnlyList<BreakdownRow> ByProject,
    BreakdownRow Totals)
{
    /// <summary>
    /// The same usage split by (project, model) rather than by one axis — what a
    /// drill-down slices (#112). Part of this record on purpose: the drill-down
    /// has to come from the same snapshot as the tables, or a scan landing while
    /// the window is open would let it total more than the row it drills into.
    /// </summary>
    public IReadOnlyList<BreakdownPair> Pairs { get; init; } = [];
}

/// <summary>
/// One side of the model × project cross-product for a single key (#112): the
/// projects a model ran in, or the models a project used. <see cref="Rows"/> is
/// always the <em>other</em> axis to <see cref="Axis"/>, sorted the same way the
/// main tables are (cost, then tokens, descending), and <see cref="Totals"/> is
/// their sum — which is exactly the totals of the selected row it drills into.
/// </summary>
public record LocalUsageDrillDown(
    BreakdownAxis Axis,
    string Key,
    IReadOnlyList<BreakdownRow> Rows,
    BreakdownRow Totals);

/// <summary>
/// One day of the cost-over-time chart. <see cref="HasUnpricedModels"/> carries
/// the same meaning as everywhere else: the cost is a floor, not an exact
/// figure, because a model missing from the pricing table contributed tokens.
/// </summary>
public record DailyCost(DateOnly Date, double CostUsd, bool HasUnpricedModels);

/// <summary>
/// Cost per local calendar day over a timeframe. Deliberately dense — every day
/// from <see cref="FromDate"/> to <see cref="ToDate"/> has a point, days with no
/// usage reading $0 — so the chart's x-axis is evenly dated and a gap in usage
/// looks like a gap rather than silently closing up.
/// </summary>
public record LocalCostSeries(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<DailyCost> Days)
{
    // These three walk Days on every read rather than being cached. Deliberate: the series is
    // bounded at the store's 30-day retention, and the record stays a plain immutable value.
    /// <summary>The costliest day — what the chart scales its y-axis against.</summary>
    public double MaxCostUsd => Days.Count == 0 ? 0.0 : Days.Max(d => d.CostUsd);

    public double TotalCostUsd => Days.Sum(d => d.CostUsd);

    /// <summary>True when any day's cost is a floor (an unpriced model contributed).</summary>
    public bool HasUnpricedModels => Days.Any(d => d.HasUnpricedModels);
}

/// <summary>
/// The two sums the budget alerts compare against their caps: today (local
/// calendar day) and the current local calendar week (Monday through today).
/// </summary>
public record LocalBudgetTotals(
    DateOnly Today,
    double TodayUsd,
    DateOnly WeekStartMonday,
    double WeekUsd);
