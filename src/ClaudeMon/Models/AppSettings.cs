namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertMode
{
    Threshold,
    Progressive,
}

/// <summary>
/// Preset colors for taskbar overlay text. <see cref="Auto"/> means "colour by usage
/// level" (the green/yellow/orange/red threshold colouring) and is only meaningful for
/// the percentage number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskbarTextColor
{
    Auto,
    White,
    Black,
    LightGray,
    DarkGray,
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

    /// <summary>Whether ClaudeMon checks GitHub for newer releases (daily + on demand).</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// The newest release version the user has already been notified about, so we
    /// notify only once per new version (across restarts). Internal state, not a user
    /// setting — preserved automatically by the settings <c>with</c>-expression save.
    /// </summary>
    [JsonPropertyName("lastNotifiedVersion")]
    public string? LastNotifiedVersion { get; init; }

    [JsonPropertyName("configVersion")]
    public int ConfigVersion { get; init; } = 1;

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);
}

public record TaskbarDisplaySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("labelColor")]
    public TaskbarTextColor LabelColor { get; init; } = TaskbarTextColor.White;

    [JsonPropertyName("numberColor")]
    public TaskbarTextColor NumberColor { get; init; } = TaskbarTextColor.Auto;

    /// <summary>
    /// When true, the taskbar overlay also shows the 7-day usage next to the 5-hour
    /// one, slash-separated (<c>5hr / 7day</c>). Off by default → 5-hour only.
    /// </summary>
    [JsonPropertyName("showSevenDay")]
    public bool ShowSevenDay { get; init; }
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
