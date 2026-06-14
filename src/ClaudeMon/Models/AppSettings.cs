namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertMode
{
    Threshold,
    Progressive,
}

public record AppSettings
{
    [JsonPropertyName("pollIntervalMinutes")]
    public int PollIntervalMinutes { get; init; } = 5;

    [JsonPropertyName("alertThresholds")]
    public AlertThresholds AlertThresholds { get; init; } = new();

    [JsonPropertyName("notifications")]
    public NotificationSettings Notifications { get; init; } = new();

    [JsonPropertyName("taskbarDisplay")]
    public TaskbarDisplaySettings TaskbarDisplay { get; init; } = new();

    [JsonPropertyName("configVersion")]
    public int ConfigVersion { get; init; } = 1;

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);
}

public record TaskbarDisplaySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public record AlertThresholds
{
    [JsonPropertyName("mode")]
    public AlertMode Mode { get; init; } = AlertMode.Threshold;

    [JsonPropertyName("fiveHourWarning")]
    public int FiveHourWarning { get; init; } = 50;

    [JsonPropertyName("fiveHourCritical")]
    public int FiveHourCritical { get; init; } = 80;

    [JsonPropertyName("sevenDayWarning")]
    public int SevenDayWarning { get; init; } = 50;

    [JsonPropertyName("progressiveStartPct")]
    public int ProgressiveStartPct { get; init; } = 70;
}

public record NotificationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("notifyOnReset")]
    public bool NotifyOnReset { get; init; }
}
