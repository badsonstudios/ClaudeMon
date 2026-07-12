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

public record AppSettings
{
    /// <summary>
    /// How often usage is polled, in minutes. The effective interval is floored at 2 minutes
    /// (see <see cref="PollInterval"/>): polling every minute made the API refresh fail every
    /// other request, so 1 is no longer offered and a persisted 1 (from an older version or a
    /// hand-edited config) is treated as 2.
    /// </summary>
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

    /// <summary>The effective poll interval — <see cref="PollIntervalMinutes"/> floored at 2.</summary>
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Max(2, PollIntervalMinutes));
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
    /// The readout size as a percentage (25–150), multiplied onto the per-monitor DPI scale
    /// (applies to both styles and every monitor's overlay). Defaults to 100, which is exactly
    /// the DPI-only rendering — an absent key changes nothing on upgrade. Consumers clamp
    /// out-of-range values (see <see cref="UI.TaskbarOverlayWindow.SetSize"/>). Enlargement is
    /// capped by the taskbar height — the readout is laid out against the space that actually
    /// fits, so it never clips.
    /// </summary>
    [JsonPropertyName("sizePercent")]
    public int SizePercent { get; init; } = 100;

    [JsonPropertyName("labelColor")]
    public TaskbarTextColor LabelColor { get; init; } = TaskbarTextColor.White;

    [JsonPropertyName("numberColor")]
    public TaskbarTextColor NumberColor { get; init; } = TaskbarTextColor.Auto;

    /// <summary>
    /// Show the session (5-hour) usage percentage in the readout. On by default — with the
    /// other two display toggles off this reproduces the original 5-hour-only readout.
    /// </summary>
    [JsonPropertyName("showSessionUsage")]
    public bool ShowSessionUsage { get; init; } = true;

    /// <summary>
    /// Show the weekly (7-day) usage percentage in the readout, dot-separated from the other
    /// enabled elements. Off by default.
    /// </summary>
    [JsonPropertyName("showWeeklyUsage")]
    public bool ShowWeeklyUsage { get; init; }

    /// <summary>
    /// Show a compact countdown to the 5-hour reset (<c>1h 23m</c>) in the readout. Numbers
    /// style only — the bar style already encodes time as its tick. Off by default.
    /// </summary>
    [JsonPropertyName("showTimeToReset")]
    public bool ShowTimeToReset { get; init; }

    /// <summary>
    /// The pre-0.11 "Also show 7-day usage" toggle, kept only so configs written by 0.10.x can
    /// be migrated: <c>true</c> maps to <see cref="ShowWeeklyUsage"/> in
    /// <see cref="Configuration.ConfigManager.Load"/>, which then clears this so the next save
    /// drops the key (nulls are omitted). Never read anywhere else.
    /// </summary>
    [JsonPropertyName("showSevenDay")]
    public bool? LegacyShowSevenDay { get; init; }

    /// <summary>
    /// When true, the readout is shown on every monitor's taskbar (on setups where Windows
    /// shows the taskbar on all displays), not just the primary. Off by default — opt-in.
    /// </summary>
    [JsonPropertyName("allMonitors")]
    public bool AllMonitors { get; init; }

    /// <summary>
    /// Horizontal nudge in pixels applied to the readout on secondary-monitor taskbars only:
    /// negative moves it left, positive moves it right. Lets you fine-tune the spacing from
    /// the clock, whose width on secondary taskbars can only be estimated. The primary has
    /// its own independent nudge (<see cref="PrimaryHorizontalOffset"/>) because the two
    /// anchor differently. 0 by default.
    /// </summary>
    [JsonPropertyName("horizontalOffset")]
    public int HorizontalOffset { get; init; }

    /// <summary>
    /// Horizontal nudge in pixels applied to the readout on the primary taskbar only:
    /// negative moves it left, positive moves it right. The primary is anchored exactly to
    /// its tray, so this defaults to 0 (the exact anchoring, unchanged on upgrade) and is
    /// kept separate from <see cref="HorizontalOffset"/>, whose secondary anchor is only an
    /// estimate around a non-queryable clock.
    /// </summary>
    [JsonPropertyName("primaryHorizontalOffset")]
    public int PrimaryHorizontalOffset { get; init; }
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
