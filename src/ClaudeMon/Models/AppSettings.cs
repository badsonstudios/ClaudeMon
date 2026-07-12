namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// How aggressively the pace early-warning fires, as a pace-ratio trigger (usage ÷ the fraction
/// of the reset window already elapsed). <see cref="Early"/> warns on a small overshoot,
/// <see cref="Late"/> waits until you're well over pace. See <see cref="AlertThresholds.PaceRatioTrigger"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaceSensitivity
{
    Early,
    Balanced,
    Late,
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

/// <summary>
/// How usage is coloured everywhere it appears (tray icon, taskbar number, flyout).
/// <see cref="Pace"/> colours by usage relative to how far through the reset window you are
/// (so 38% used at 5% elapsed reads red); <see cref="Level"/> colours by the absolute
/// percentage (the original behaviour).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageColorMode
{
    Pace,
    Level,
}

/// <summary>
/// The selectable visual style of the taskbar usage readout. <see cref="Numbers"/> is the
/// stacked label + percentage text (the original look); <see cref="Bar"/> draws a compact
/// horizontal usage bar with hour/day dividers and a time-in-window tick (mirrors the flyout
/// bars), pace-coloured so "am I ahead of the clock?" reads at a glance.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskbarStyle
{
    Numbers,
    Bar,
}

/// <summary>
/// The width of the <see cref="TaskbarStyle.Bar"/> readout. Wider bars give the hour/day
/// dividers and time tick more room, so pace reads more precisely; narrower bars take less of
/// the taskbar. Only applies to the bar style.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskbarBarWidth
{
    Compact,
    Standard,
    Wide,
    ExtraWide,
}

/// <summary>
/// The size of the taskbar readout, as a multiplier on the per-monitor DPI scale
/// (<see cref="TaskbarSizeExtensions.Factor"/>: 75% / 100% / 125% / 150%).
/// <see cref="Standard"/> (100%) is exactly the DPI-only rendering, so existing installs
/// look unchanged. Larger sizes are capped by the taskbar height — the readout is laid out
/// against the space that actually fits, so it never clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskbarSize
{
    Small,
    Standard,
    Large,
    ExtraLarge,
}

public static class TaskbarSizeExtensions
{
    /// <summary>The scale multiplier a <see cref="TaskbarSize"/> applies on top of the monitor DPI scale.</summary>
    public static float Factor(this TaskbarSize size) => size switch
    {
        TaskbarSize.Small => 0.75f,
        TaskbarSize.Large => 1.25f,
        TaskbarSize.ExtraLarge => 1.5f,
        _ => 1f, // Standard
    };
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

    /// <summary>How usage is coloured (tray icon, taskbar, flyout). Defaults to pace-aware.</summary>
    [JsonPropertyName("colorMode")]
    public UsageColorMode ColorMode { get; init; } = UsageColorMode.Pace;

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

    /// <summary>
    /// The visual style of the readout. Defaults to <see cref="TaskbarStyle.Numbers"/> (the
    /// original stacked label + percentage), so existing installs look unchanged until the
    /// user opts into the <see cref="TaskbarStyle.Bar"/> style.
    /// </summary>
    [JsonPropertyName("style")]
    public TaskbarStyle Style { get; init; } = TaskbarStyle.Numbers;

    /// <summary>
    /// Width of the bar-style readout (only applies when <see cref="Style"/> is
    /// <see cref="TaskbarStyle.Bar"/>). Defaults to <see cref="TaskbarBarWidth.Standard"/>.
    /// </summary>
    [JsonPropertyName("barWidth")]
    public TaskbarBarWidth BarWidth { get; init; } = TaskbarBarWidth.Standard;

    /// <summary>
    /// The readout size, multiplied onto the per-monitor DPI scale (applies to both styles and
    /// every monitor's overlay). Defaults to <see cref="TaskbarSize.Standard"/> (100%), which is
    /// exactly the DPI-only rendering — an absent key changes nothing on upgrade.
    /// </summary>
    [JsonPropertyName("size")]
    public TaskbarSize Size { get; init; } = TaskbarSize.Standard;

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

    /// <summary>
    /// When true, the readout is shown on every monitor's taskbar (on setups where Windows
    /// shows the taskbar on all displays), not just the primary. Off by default — opt-in.
    /// </summary>
    [JsonPropertyName("allMonitors")]
    public bool AllMonitors { get; init; }

    /// <summary>
    /// Horizontal nudge in pixels applied to the readout on secondary-monitor taskbars only:
    /// negative moves it left, positive moves it right. Lets you fine-tune the spacing from
    /// the clock, whose width on secondary taskbars can only be estimated. The primary is
    /// anchored exactly to its tray and is unaffected. 0 by default.
    /// </summary>
    [JsonPropertyName("horizontalOffset")]
    public int HorizontalOffset { get; init; }
}

public record AlertThresholds
{
    /// <summary>
    /// Whether the pace early-warning fires — a heads-up when your usage relative to how far
    /// through the 5-hour window you are means you're on track to run out before it resets.
    /// On by default.
    /// </summary>
    [JsonPropertyName("paceAlertsEnabled")]
    public bool PaceAlertsEnabled { get; init; } = true;

    /// <summary>How aggressively the pace early-warning fires. Defaults to <see cref="PaceSensitivity.Balanced"/>.</summary>
    [JsonPropertyName("paceSensitivity")]
    public PaceSensitivity PaceSensitivity { get; init; } = PaceSensitivity.Balanced;

    /// <summary>
    /// Absolute near-cap backstop: a critical "almost out" alert fires once 5-hour usage reaches
    /// this percentage, regardless of pace — the safety net for "you're nearly out". Default 90.
    /// </summary>
    [JsonPropertyName("nearCapWarning")]
    public int NearCapWarning { get; init; } = 90;

    /// <summary>7-day (weekly) warning percentage — fires once weekly usage crosses it. Default 50.</summary>
    [JsonPropertyName("sevenDayWarning")]
    public int SevenDayWarning { get; init; } = 50;

    /// <summary>
    /// The pace ratio (usage ÷ window-elapsed fraction) that triggers the early-warning at the
    /// configured <see cref="PaceSensitivity"/>. A ratio of 1 is exactly on pace; higher means
    /// burning faster than the clock. Not persisted — derived from the sensitivity.
    /// </summary>
    [JsonIgnore]
    public double PaceRatioTrigger => PaceSensitivity switch
    {
        PaceSensitivity.Early => 1.25,
        PaceSensitivity.Late => 2.0,
        _ => 1.5,
    };
}

public record NotificationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("notifyOnReset")]
    public bool NotifyOnReset { get; init; }
}
