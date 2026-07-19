namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// One deduplicated assistant-message usage record parsed from a Claude Code
/// transcript line (~/.claude/projects/**/*.jsonl). Only the usage numbers,
/// model id, ids, and timestamp are materialized — never message content.
/// </summary>
public record LocalUsageEntry(
    DateTimeOffset Timestamp,
    string Model,
    string? DedupeKey,
    long InputTokens,
    long OutputTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long CacheReadTokens)
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
/// re-read old bytes, per-day aggregates, the recent dedupe keys (value = entry
/// timestamp, so the set can be pruned), and the recent cost samples.
/// </summary>
public record LocalUsageCacheFile
{
    [JsonPropertyName("files")] public Dictionary<string, FileScanState> Files { get; init; } = new();
    [JsonPropertyName("days")] public Dictionary<string, LocalDayTotals> Days { get; init; } = new();
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