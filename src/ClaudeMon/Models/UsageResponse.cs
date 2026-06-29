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
    [property: JsonPropertyName("seven_day")] UsageBucket? SevenDay
);

public record UsageBucket(
    [property: JsonPropertyName("utilization")] double UtilizationPct,
    [property: JsonPropertyName("resets_at")] DateTimeOffset? ResetAt
)
{
    public TimeSpan TimeUntilReset => ResetAt is not null && ResetAt > DateTimeOffset.UtcNow
        ? ResetAt.Value - DateTimeOffset.UtcNow
        : TimeSpan.Zero;

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
