namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// A single point in the rolling usage history: the 5-hour (and, when present,
/// 7-day) utilization percentage captured at <see cref="Timestamp"/>.
/// </summary>
public record UsageSample(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("h5")] double FiveHourPct,
    [property: JsonPropertyName("d7")] double? SevenDayPct
);
