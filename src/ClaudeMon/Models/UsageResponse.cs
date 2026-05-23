namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

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
