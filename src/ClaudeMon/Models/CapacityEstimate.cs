namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// How much the implied-capacity estimate can be trusted (issue #185). The zero value is
/// honestly "no estimate" (the <see cref="Monitoring.TimeToLimitKind"/> pattern), and the
/// readout shows nothing below <see cref="Medium"/> — a wrong number about your limits is
/// worse than none.
/// </summary>
public enum CapacityConfidence
{
    /// <summary>Too few observations, too short a span, or mostly unexplained movement.</summary>
    None,

    /// <summary>Enough observations, but the per-interval capacities disagree too much.</summary>
    Low,

    /// <summary>Enough consistent observations over at least one full window.</summary>
    Medium,

    /// <summary>Many tightly-agreeing observations over multiple windows.</summary>
    High,
}

/// <summary>
/// One closed percent-movement interval for a limit: the % it moved, the weighted local
/// tokens burned while it moved, and when. <see cref="Unexplained"/> intervals (the % moved
/// but local tokens can't account for it — usage from claude.ai, mobile, or another machine)
/// stay in the ring so their fraction attenuates confidence, but are excluded from the
/// estimate itself; the bounded ring ages them out naturally.
/// </summary>
public record CapacityObservation(
    [property: JsonPropertyName("dp")] double DeltaPercent,
    [property: JsonPropertyName("wt")] double WeightedTokens,
    [property: JsonPropertyName("dm")] string? DominantModel,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("end")] DateTimeOffset End,
    [property: JsonPropertyName("ux")] bool Unexplained = false)
{
    /// <summary>Weighted tokens this interval implies for the full 0–100% window.</summary>
    public double ImpliedCapacity => WeightedTokens / (DeltaPercent / 100.0);

    /// <summary>Weighted tokens per percentage point — the unit the explanation floor compares.</summary>
    public double TokensPerPoint => WeightedTokens / DeltaPercent;
}

/// <summary>
/// The open interval a limit key is currently accumulating: the % and reset time it started
/// from, and the weighted-relevant token deltas folded in since. Discarded (never emitted)
/// when a reset boundary, an observation gap, or an implausible jump lands mid-interval.
/// </summary>
public record CapacityAccumulator(
    [property: JsonPropertyName("pct")] double BaselinePercent,
    [property: JsonPropertyName("resets")] DateTimeOffset? BaselineResetsAt,
    [property: JsonPropertyName("start")] DateTimeOffset StartAt,
    [property: JsonPropertyName("tok")] Dictionary<string, ModelTokens> Tokens);

/// <summary>Per-limit-key estimation state: the open accumulator plus the observation ring.</summary>
public record LimitCapacityState
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("model")] public string? ScopeModel { get; init; }
    [JsonPropertyName("acc")] public CapacityAccumulator? Accumulator { get; init; }
    [JsonPropertyName("ring")] public List<CapacityObservation> Ring { get; init; } = new();

    /// <summary>Lifetime closed-interval count — part of the reported sample basis.</summary>
    [JsonPropertyName("total")] public int TotalObservations { get; init; }

    [JsonPropertyName("firstAt")] public DateTimeOffset? FirstObservedAt { get; init; }
}

/// <summary>
/// The persisted estimator state (%LocalAppData%\ClaudeMon\limit-log\capacity.json): a cache
/// of sufficient statistics over the correlated log. Losing it costs nothing but a rebuild —
/// the forever-log is the source of truth, and a version mismatch deliberately discards this
/// file so the estimator re-derives from the samples with the new code.
/// </summary>
public record CapacityEstimateState
{
    public const int CurrentVersion = 1;

    // Deliberately NO initializer — the LocalUsageCacheFile/LimitLogState trap: absent "v"
    // must deserialize as 0 and fail the version gate, not masquerade as current.
    [JsonPropertyName("v")] public int Version { get; init; }

    [JsonPropertyName("lastSampleAt")] public DateTimeOffset? LastSampleAt { get; init; }

    /// <summary>The estimator's own cumulative-tokens baseline (independent of the tracker's).</summary>
    [JsonPropertyName("lastTok")] public Dictionary<string, ModelTokens>? LastTokens { get; init; }

    /// <summary>
    /// The plan the observations were made under. A change clears every ring: the capacity
    /// genuinely changed, and mixing observations across plans is exactly the confusion the
    /// plan stamp exists to prevent.
    /// </summary>
    [JsonPropertyName("plan")] public ClaudePlan? Plan { get; init; }

    [JsonPropertyName("limits")] public List<LimitCapacityState> Limits { get; init; } = new();
}

/// <summary>
/// One limit's implied capacity as reported to the UI: weighted tokens per full window,
/// optionally expressed in a specific model's tokens, with the confidence and the sample
/// basis (how many intervals, over what span, how many unexplained) that justify it.
/// </summary>
public record ImpliedCapacity(
    string? Kind,
    string? ScopeModel,
    double CapacityWeightedTokens,
    string? EquivalentModel,
    CapacityConfidence Confidence,
    int ObservationCount,
    int UnexplainedCount,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt);
