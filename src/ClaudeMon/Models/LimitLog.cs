namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// The schema of the correlated limit log (issue #184): an append-only, never-pruned record
/// under %LocalAppData%\ClaudeMon\limit-log\ pairing the usage API's utilization percentages
/// with the local transcripts' token totals — the two halves of "how much do I actually get
/// per window". One <see cref="LimitLogSample"/> per successful poll; one
/// <see cref="LimitWindowRecord"/> per finished limit window. Every JSONL line carries its own
/// schema version (<see cref="SchemaVersion"/>) so files survive concatenation, torn trailing
/// lines, and future format changes — readers skip lines they don't understand. The app itself
/// never reads the JSONL back; only the small <see cref="LimitLogState"/> file round-trips.
///
/// Delivery is at-least-once: a crash between appending a window record and saving the state
/// re-emits that record on the next launch, so readers should dedupe window records on
/// (kind, model, end).
/// </summary>
public static class LimitLogSchema
{
    public const int SchemaVersion = 1;
}

/// <summary>
/// Token totals for one model, in the four categories the local scanner tracks. All four are
/// kept — capacity estimation (#185) may weight cache reads differently from fresh tokens.
/// </summary>
public record ModelTokens(
    [property: JsonPropertyName("in")] long InputTokens,
    [property: JsonPropertyName("out")] long OutputTokens,
    [property: JsonPropertyName("cw")] long CacheWriteTokens,
    [property: JsonPropertyName("cr")] long CacheReadTokens)
{
    public static readonly ModelTokens Zero = new(0, 0, 0, 0);

    public ModelTokens Plus(ModelTokens other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheWriteTokens + other.CacheWriteTokens,
        CacheReadTokens + other.CacheReadTokens);

    /// <summary>
    /// The per-category growth from <paramref name="previous"/> to this total, each clamped at
    /// zero: cumulative totals can dip when the scanner's retention window prunes old days (or
    /// its cache rebuilds), and a prune must read as "no new burn", never as negative burn.
    /// </summary>
    public ModelTokens DeltaFrom(ModelTokens previous) => new(
        Math.Max(0, InputTokens - previous.InputTokens),
        Math.Max(0, OutputTokens - previous.OutputTokens),
        Math.Max(0, CacheWriteTokens - previous.CacheWriteTokens),
        Math.Max(0, CacheReadTokens - previous.CacheReadTokens));

    public bool IsZero =>
        InputTokens == 0 && OutputTokens == 0 && CacheWriteTokens == 0 && CacheReadTokens == 0;
}

/// <summary>
/// One <c>limits[]</c> entry as logged in a sample — every field verbatim from
/// <see cref="UsageLimit"/> (raw strings preserved, mirroring that record's philosophy: an
/// unknown future kind must log cleanly, not get dropped). <see cref="ScopeModel"/> flattens
/// the scope's model display name.
/// </summary>
public record LimitSnapshot(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("pct")] double? Percent,
    [property: JsonPropertyName("sev")] string? Severity,
    [property: JsonPropertyName("resets")] DateTimeOffset? ResetsAt,
    [property: JsonPropertyName("active")] bool? IsActive,
    [property: JsonPropertyName("model")] string? ScopeModel);

/// <summary>
/// One line of samples-YYYY-MM.jsonl: everything the usage API said about every limit at
/// <see cref="Timestamp"/>, plus the local scanner's cumulative token totals by model at that
/// instant. <see cref="TokensByModel"/> is cumulative within the scanner's retention window —
/// not globally monotonic (it dips when old days age out; see
/// <see cref="ModelTokens.DeltaFrom"/>) — and null when the scanner is unavailable.
/// </summary>
public record LimitLogSample(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("limits")] IReadOnlyList<LimitSnapshot> Limits,
    [property: JsonPropertyName("tok")] IReadOnlyDictionary<string, ModelTokens>? TokensByModel)
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = LimitLogSchema.SchemaVersion;
}

/// <summary>
/// One line of windows-YYYY-MM.jsonl: a finished limit window rolled up. Identity is
/// (<see cref="Kind"/>, <see cref="ScopeModel"/>) — deliberately not <see cref="Group"/>,
/// whose vocabulary the API has already changed once ("weekly" vs "seven_day"); the group is
/// recorded as metadata only. <see cref="TokensByModel"/> is the burn accumulated while this
/// window was active (all models, so per-model caps can still be analyzed against total burn).
/// The plan is stamped at both ends so a mid-window plan change is visible on the record
/// itself and can never be mistaken for throttling. <see cref="Incomplete"/> windows are
/// best-effort reconstructions (the app wasn't watching the whole time) — flagged rather than
/// silently wrong, per the ticket.
/// </summary>
public record LimitWindowRecord(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("model")] string? ScopeModel,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("end")] DateTimeOffset End,
    [property: JsonPropertyName("startApprox")] bool StartApprox,
    [property: JsonPropertyName("peakPct")] double PeakPercent,
    [property: JsonPropertyName("lastPct")] double LastPercent,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("samples")] int SampleCount,
    [property: JsonPropertyName("plan")] ClaudePlan? Plan,
    [property: JsonPropertyName("planAtStart")] ClaudePlan? PlanAtStart,
    [property: JsonPropertyName("planChanged")] bool PlanChanged,
    [property: JsonPropertyName("tok")] IReadOnlyDictionary<string, ModelTokens> TokensByModel,
    [property: JsonPropertyName("incomplete")] bool Incomplete,
    [property: JsonPropertyName("reason")] string? IncompleteReason)
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = LimitLogSchema.SchemaVersion;

    /// <summary>The window ended while the app wasn't observing, so peak/last/tokens stop at the last sample before the gap.</summary>
    public const string ReasonOfflineAtWindowEnd = "offline_at_window_end";

    /// <summary>The window was already in flight when observation (re)started, so burn before the first sample is missing.</summary>
    public const string ReasonGapSpannedBoundary = "gap_spanned_boundary";
}

/// <summary>One in-flight window's running rollup, persisted in the state file between polls.</summary>
public record ActiveWindowState(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("model")] string? ScopeModel,
    [property: JsonPropertyName("resets")] DateTimeOffset ResetsAt,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("startApprox")] bool StartApprox,
    [property: JsonPropertyName("peakPct")] double PeakPercent,
    [property: JsonPropertyName("lastPct")] double LastPercent,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("samples")] int SampleCount,
    [property: JsonPropertyName("planAtStart")] ClaudePlan? PlanAtStart,
    [property: JsonPropertyName("planChanged")] bool PlanChanged,
    [property: JsonPropertyName("tok")] Dictionary<string, ModelTokens> TokensByModel,
    [property: JsonPropertyName("incomplete")] bool Incomplete,
    [property: JsonPropertyName("reason")] string? IncompleteReason);

/// <summary>
/// The on-disk tracker state (%LocalAppData%\ClaudeMon\limit-log\state.json): the active
/// windows and the last-seen cumulative token totals. This is the only limit-log file the app
/// reads back — startup cost is one small JSON parse regardless of how large the append-only
/// log has grown, which is what keeps memory and startup time flat under forever retention.
/// Losing it loses only active-window continuity: the next windows open flagged incomplete
/// rather than silently wrong.
/// </summary>
public record LimitLogState
{
    public const int CurrentVersion = 1;

    // Deliberately NO initializer — same trap as LocalUsageCacheFile.Version: a default of
    // CurrentVersion would let a version-less (or future-format) file masquerade as current.
    // Absent must deserialize as 0; SaveState stamps the real version explicitly.
    [JsonPropertyName("v")] public int Version { get; init; }

    /// <summary>When the last sample was recorded — the far edge of the last observed poll interval.</summary>
    [JsonPropertyName("lastSampleAt")] public DateTimeOffset? LastSampleAt { get; init; }

    /// <summary>The scanner's cumulative totals at the last sample — the baseline the next delta is measured from.</summary>
    [JsonPropertyName("lastTok")] public Dictionary<string, ModelTokens>? LastTokens { get; init; }

    [JsonPropertyName("windows")] public List<ActiveWindowState> Windows { get; init; } = new();
}
