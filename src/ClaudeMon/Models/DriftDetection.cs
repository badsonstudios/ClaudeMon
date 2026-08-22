namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// One day's confident capacity estimate for a limit key, as the drift detector remembers it
/// (issue #186). Only Medium+ estimates are recorded — the baseline never learns from noise —
/// and each point carries the plan it was observed under so a plan change can never read as
/// throttling.
/// </summary>
public record DriftPoint(
    [property: JsonPropertyName("d")] DateOnly Date,
    [property: JsonPropertyName("cap")] double Capacity,
    [property: JsonPropertyName("plan")] ClaudePlan? Plan);

/// <summary>One limit key's drift state: its daily points and the episode latch.</summary>
public record DriftKeyState
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("model")] public string? ScopeModel { get; init; }
    [JsonPropertyName("points")] public List<DriftPoint> Points { get; init; } = new();

    /// <summary>
    /// True after a drift alert has been shown for the current episode — the once-per-episode
    /// latch. Cleared by recovery (capacity climbing back above the hysteresis band) or by
    /// acknowledgment; set only when the alert was actually emitted, so a gated condition
    /// (alerts off, snoozed) defers rather than silently swallowing the episode.
    /// </summary>
    [JsonPropertyName("notified")] public bool Notified { get; init; }

    /// <summary>
    /// When the user acknowledged the current drift (opened the Limit history tab). Points
    /// recorded before this are excluded from the baseline, so the detector rebuilds its norm
    /// from the accepted new level and only a further material drop fires again.
    /// </summary>
    [JsonPropertyName("ackAt")] public DateTimeOffset? AcknowledgedAt { get; init; }
}

/// <summary>
/// The persisted drift-detector state (%LocalAppData%\ClaudeMon\limit-log\drift.json).
/// Deliberately its own file: it is history plus a latch, not a user setting (so not
/// AppSettings), and it must survive a capacity.json schema rebuild (so not inside it).
/// Losing it costs a quiet week while the baseline re-accumulates — silent, never wrong.
/// </summary>
public record DriftState
{
    public const int CurrentVersion = 1;

    // Deliberately NO initializer — the LocalUsageCacheFile/LimitLogState trap: absent "v"
    // must deserialize as 0 and fail the version gate, not masquerade as current.
    [JsonPropertyName("v")] public int Version { get; init; }

    [JsonPropertyName("keys")] public List<DriftKeyState> Keys { get; init; } = new();
}

/// <summary>A drift alert ready to show: composed title/body plus the key it concerns.</summary>
public record DriftAlertMessage(string Title, string Text, string? Kind, string? ScopeModel);
