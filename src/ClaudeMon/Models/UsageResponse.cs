namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>Canonical lengths of the two reset windows, shared by the pace/time visuals.</summary>
public static class UsageWindows
{
    public static readonly TimeSpan FiveHour = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDay = TimeSpan.FromDays(7);
}

public record UsageResponse(
    [property: JsonPropertyName("five_hour")] UsageBucket? FiveHour,
    [property: JsonPropertyName("seven_day")] UsageBucket? SevenDay,
    [property: JsonPropertyName("limits")] IReadOnlyList<UsageLimit>? Limits = null
);

/// <summary>
/// One entry of the usage API's <c>limits[]</c> array — a richer, self-describing quota bucket
/// (session, overall weekly, per-model weekly, and whatever kinds appear next). Every field is
/// a raw string / nullable on purpose: unknown future kinds and severities must deserialize
/// cleanly so they can be rendered generically instead of dropped.
/// </summary>
public record UsageLimit(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("percent")] double? Percent,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("resets_at")] DateTimeOffset? ResetsAt,
    [property: JsonPropertyName("is_active")] bool? IsActive = null,
    [property: JsonPropertyName("scope")] LimitScope? Scope = null
)
{
    /// <summary>
    /// The API's severity string as a typed level. Unrecognized values map to
    /// <see cref="LimitSeverity.Unknown"/> (treated like Normal for colour) rather than failing.
    /// </summary>
    public LimitSeverity SeverityLevel => Severity?.ToLowerInvariant() switch
    {
        null or "normal" => LimitSeverity.Normal,
        "warning" => LimitSeverity.Warning,
        "critical" => LimitSeverity.Critical,
        _ => LimitSeverity.Unknown,
    };

    /// <summary>
    /// View of this limit as a legacy <see cref="UsageBucket"/>, so the countdown/elapsed-fraction
    /// logic is reused verbatim by every display path. A null percent renders as 0.
    /// </summary>
    public UsageBucket ToBucket() => new(Percent ?? 0, ResetsAt);
}

public record LimitScope(
    [property: JsonPropertyName("model")] LimitScopeModel? Model
);

public record LimitScopeModel(
    [property: JsonPropertyName("display_name")] string? DisplayName
);

/// <summary>Anthropic's own judgment of how close a limit is to exhaustion.</summary>
public enum LimitSeverity
{
    Normal,
    Warning,
    Critical,
    Unknown,
}

public record UsageBucket(
    [property: JsonPropertyName("utilization")] double UtilizationPct,
    [property: JsonPropertyName("resets_at")] DateTimeOffset? ResetAt
)
{
    public TimeSpan TimeUntilReset
    {
        get
        {
            if (ResetAt is null)
                return TimeSpan.Zero;

            // Sample the clock once: reading UtcNow for both the guard and the subtraction can
            // return a tiny negative span right at the reset boundary, which would flow into the
            // burn-rate estimate as a non-positive reset and wrongly suppress the time-to-limit.
            var remaining = ResetAt.Value - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// How far through a fixed reset window of length <paramref name="window"/> this bucket is:
    /// 0 (just reset) → 1 (about to reset), or null when the reset time is unknown. Used to show
    /// time-in-window against usage and to colour by pace.
    /// </summary>
    public double? ElapsedFraction(TimeSpan window)
    {
        if (ResetAt is null || window <= TimeSpan.Zero)
            return null;

        var elapsed = 1.0 - TimeUntilReset.TotalSeconds / window.TotalSeconds;
        return Math.Clamp(elapsed, 0.0, 1.0);
    }

    public string FormatResetCountdown()
    {
        var remaining = TimeUntilReset;
        if (remaining == TimeSpan.Zero)
            return "resetting...";

        if (remaining.TotalDays >= 1)
            return $"resets {(int)remaining.TotalDays}d {remaining.Hours}h";

        if (remaining.TotalHours >= 1)
            return $"resets {(int)remaining.TotalHours}h {remaining.Minutes}m";

        return $"resets {remaining.Minutes}m";
    }
}
