namespace ClaudeMon.Models;

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

/// <summary>A recent per-entry cost sample used for the burn-rate window (persisted).</summary>
public record RecentCostSample(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("usd")] double CostUsd,
    [property: JsonPropertyName("tok")] long Tokens
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
    double? BurnRateUsdPerHour
);

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

/// <summary>
/// The full breakdown for one timeframe: per-model rows, per-project rows
/// (both sorted by cost, then tokens, descending), and the grand totals.
/// </summary>
public record LocalUsageBreakdown(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<BreakdownRow> ByModel,
    IReadOnlyList<BreakdownRow> ByProject,
    BreakdownRow Totals);

/// <summary>
/// The two sums the budget alerts compare against their caps: today (local
/// calendar day) and the current local calendar week (Monday through today).
/// </summary>
public record LocalBudgetTotals(
    DateOnly Today,
    double TodayUsd,
    DateOnly WeekStartMonday,
    double WeekUsd);
