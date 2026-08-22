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
/// the percentage number. <see cref="MatchTaskbar"/> contrasts with the taskbar theme —
/// light text on a dark taskbar, dark text on a light one — re-evaluated live as the
/// Windows mode changes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskbarTextColor
{
    Auto,
    White,
    Black,
    LightGray,
    DarkGray,
    MatchTaskbar,
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
/// The user's Claude subscription plan, stamped into the correlated limit log
/// (see <see cref="Monitoring.LimitWindowTracker"/>) as context: a plan change must be
/// visible in the log so later capacity analysis never mistakes it for throttling
/// (issue #184). Context only — never a token-budget source; Anthropic publishes none.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaudePlan
{
    Pro,
    Max5x,
    Max20x,
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

    [JsonPropertyName("budgets")]
    public BudgetSettings Budgets { get; init; } = new();

    /// <summary>
    /// The budget-alert latch: the highest ladder step already fired in the
    /// current daily/weekly period, so each threshold alerts once per period
    /// even across restarts. Internal state, not a user setting — kept
    /// top-level (not inside <see cref="BudgetSettings"/>, which the Settings
    /// dialog reconstructs on save) so the <c>with</c>-expression save
    /// preserves it automatically.
    /// </summary>
    [JsonPropertyName("budgetAlertState")]
    public BudgetAlertState? BudgetAlertState { get; init; }

    /// <summary>
    /// The 5-hour and weekly alert fired/latch state (see <see cref="Monitoring.AlertManager"/>),
    /// persisted so a routine app restart doesn't re-fire an alert for a condition you've
    /// already been notified about and that's still true — before this existed, the latch only
    /// lived in memory, so every restart re-armed everything from scratch. Internal state, not a
    /// user setting — kept top-level like <see cref="BudgetAlertState"/>, for the same reason:
    /// the Settings dialog's <c>with</c>-expression save must not silently drop it.
    /// </summary>
    [JsonPropertyName("alertLatchState")]
    public AlertLatchState? AlertLatchState { get; init; }

    /// <summary>
    /// The service-incident latch: the severity of the Anthropic incident ClaudeMon has already
    /// accounted for (see <see cref="Monitoring.ServiceStatusAlerts"/>), or null while the status
    /// page is healthy or nothing has been read yet. Persisted so restarting during a long
    /// incident doesn't raise the same balloon again — in-memory only, an eight-hour maintenance
    /// window cost one notification per restart (#138). Internal state, not a user setting —
    /// kept top-level like <see cref="BudgetAlertState"/>, for the same reason: the Settings
    /// dialog's <c>with</c>-expression save must not silently drop it. Stored by name; the
    /// converter sits on the property rather than on <see cref="ServiceStatusLevel"/> because
    /// the enum is the status page's vocabulary, not a settings type — this file is the only
    /// place it is persisted.
    /// </summary>
    [JsonPropertyName("serviceIncidentLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ServiceStatusLevel? ServiceIncidentLevel { get; init; }

    /// <summary>
    /// The user's Claude plan (Pro / Max 5x / Max 20x), or null when they haven't said.
    /// Stamped into the correlated limit log's window records as context (issue #184);
    /// null serializes as an absent key, so upgrades are no-ops.
    /// </summary>
    [JsonPropertyName("claudePlan")]
    public ClaudePlan? Plan { get; init; }

    [JsonPropertyName("taskbarDisplay")]
    public TaskbarDisplaySettings TaskbarDisplay { get; init; } = new();

    /// <summary>How usage is coloured (tray icon, taskbar, flyout). Defaults to pace-aware.</summary>
    [JsonPropertyName("colorMode")]
    public UsageColorMode ColorMode { get; init; } = UsageColorMode.Pace;

    /// <summary>Whether ClaudeMon checks GitHub for newer releases (daily + on demand).</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// When true, an automatic update check that finds a newer release downloads and installs it
    /// silently instead of prompting — the app restarts on the new version and notifies
    /// afterward. Off by default (updating restarts the app, so it's opt-in). Only meaningful
    /// while <see cref="CheckForUpdates"/> is on; manual checks still prompt either way.
    /// </summary>
    [JsonPropertyName("autoInstallUpdates")]
    public bool AutoInstallUpdates { get; init; }

    /// <summary>
    /// The release version the user chose to suppress ("Skip this version" in the update
    /// dialog): automatic checks won't prompt for it again, though a manual check or a newer
    /// release still will. Replaces the pre-0.12 <c>lastNotifiedVersion</c> ("ballooned once per
    /// version"), whose key is silently dropped on load. Internal state, not a user setting —
    /// preserved automatically by the settings <c>with</c>-expression save.
    /// </summary>
    [JsonPropertyName("ignoredUpdateVersion")]
    public string? IgnoredUpdateVersion { get; init; }

    /// <summary>
    /// The version a silent install was just launched for, written the moment the installer
    /// starts. The next startup compares it to the running version: a match means the update
    /// landed ("Updated to vX" notification), a mismatch means the install didn't happen; the
    /// field is cleared either way. Internal state, not a user setting — preserved automatically
    /// by the settings <c>with</c>-expression save.
    /// </summary>
    [JsonPropertyName("pendingUpdateVersion")]
    public string? PendingUpdateVersion { get; init; }

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
    /// Show the projected time until the 5-hour window hits 100% at the current burn rate
    /// (<c>~1h 23m</c>) in the readout — the same estimate the flyout shows. Numbers style
    /// only (the bar draws no text). Off by default.
    /// </summary>
    [JsonPropertyName("showTimeToLimit")]
    public bool ShowTimeToLimit { get; init; }

    /// <summary>
    /// Render the percentage elements with a trailing <c>%</c> (<c>42% · 17%</c> instead of
    /// <c>42 · 17</c>). Off by default so the compact original look is unchanged.
    /// </summary>
    [JsonPropertyName("showPercentSign")]
    public bool ShowPercentSign { get; init; }

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

    /// <summary>
    /// The readout composition that click-to-cycle treats as home — the multi-element layout its
    /// ring wraps back to, so a middle-click focuses one metric temporarily instead of destroying
    /// what you built (issue #156). Persisted because a half-finished cycle outlives a restart.
    /// <c>null</c> when there is nothing to remember: a readout the ring can already take you to
    /// earns no second, identical-looking stop (see <c>UI.TaskbarMetricCycle.HomeFor</c>).
    /// Settings rewrites this on every save — the toggles below stay the source of truth, so a
    /// home they no longer describe can never reappear on a wrap.
    /// </summary>
    [JsonPropertyName("cycleHome")]
    public TaskbarMetricSelection? CycleHome { get; init; }

    /// <summary>
    /// The four display toggles as one value, for the code that treats them as a set — the
    /// overlay's element composition and the click-to-cycle gesture
    /// (<c>UI.TaskbarMetricCycle</c>). Derived, not persisted: the toggles above remain the
    /// stored form, so cycling and Settings can never disagree.
    /// </summary>
    [JsonIgnore]
    public TaskbarMetricSelection Metrics =>
        new(ShowSessionUsage, ShowWeeklyUsage, ShowTimeToLimit, ShowTimeToReset);

    /// <summary>Copy with the display toggles replaced by <paramref name="metrics"/>.</summary>
    public TaskbarDisplaySettings WithMetrics(TaskbarMetricSelection metrics) => this with
    {
        ShowSessionUsage = metrics.Session,
        ShowWeeklyUsage = metrics.Weekly,
        ShowTimeToLimit = metrics.TimeToLimit,
        ShowTimeToReset = metrics.TimeToReset,
    };
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
    /// Whether the throttle-drift alert fires (issue #186): a notification when the implied
    /// window capacity for a limit drops materially below its trailing 30-day norm — evidence
    /// that the goalposts moved. On by default: being told proactively is the feature's point,
    /// and it can only fire at all once weeks of confident estimates exist.
    /// </summary>
    [JsonPropertyName("driftAlertsEnabled")]
    public bool DriftAlertsEnabled { get; init; } = true;

    /// <summary>
    /// How far (percent) below the 30-day norm the implied capacity must fall to count as
    /// drift. Default 20; the detector adds its own hysteresis on recovery so a value hovering
    /// at the trigger can't fire repeatedly.
    /// </summary>
    [JsonPropertyName("driftThresholdPercent")]
    public int DriftThresholdPercent { get; init; } = 20;

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

/// <summary>
/// Optional estimated-cost budgets (issue #74), checked against the local
/// transcript aggregates. Caps stay configured while toggled off, so
/// re-enabling doesn't lose the value. Daily = local calendar day; weekly =
/// local calendar week, Monday through Sunday.
/// </summary>
public record BudgetSettings
{
    [JsonPropertyName("dailyEnabled")]
    public bool DailyEnabled { get; init; }

    [JsonPropertyName("dailyCapUsd")]
    public double DailyCapUsd { get; init; } = 10.0;

    [JsonPropertyName("weeklyEnabled")]
    public bool WeeklyEnabled { get; init; }

    [JsonPropertyName("weeklyCapUsd")]
    public double WeeklyCapUsd { get; init; } = 50.0;
}

/// <summary>
/// See <see cref="AppSettings.BudgetAlertState"/>. Period keys are
/// "yyyy-MM-dd" (the day, and the week's Monday); FiredPct is the highest
/// ladder step (0/50/80/95) already alerted in that period.
/// </summary>
public record BudgetAlertState(
    [property: JsonPropertyName("day")] string? DailyPeriod,
    [property: JsonPropertyName("dayPct")] int DailyFiredPct,
    [property: JsonPropertyName("week")] string? WeeklyPeriod,
    [property: JsonPropertyName("weekPct")] int WeeklyFiredPct);

/// <summary>
/// See <see cref="AppSettings.AlertLatchState"/>. <see cref="WeeklyBuckets"/> is keyed the same
/// way <c>AlertManager</c>'s own in-memory dictionary is — the bucket's kind, or its model for
/// a per-model cap — so a saved latch reattaches to the right bucket after a restart.
/// </summary>
public record AlertLatchState(
    [property: JsonPropertyName("paceWarningFired")] bool PaceWarningFired,
    [property: JsonPropertyName("nearCapFired")] bool NearCapFired,
    [property: JsonPropertyName("weeklyBuckets")] Dictionary<string, WeeklyBucketLatch> WeeklyBuckets);

public record WeeklyBucketLatch(
    [property: JsonPropertyName("warningFired")] bool WarningFired,
    [property: JsonPropertyName("criticalFired")] bool CriticalFired);

public record NotificationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("notifyOnReset")]
    public bool NotifyOnReset { get; init; }

    /// <summary>
    /// Notify when Anthropic's status page starts reporting an incident (and again if it gets
    /// worse). Off by default: the flyout already shows the state passively, and an outage
    /// balloon is only useful to people who want to be interrupted by one.
    /// </summary>
    [JsonPropertyName("notifyOnServiceIncident")]
    public bool NotifyOnServiceIncident { get; init; }

    /// <summary>
    /// Alerts are suppressed until this instant (issue #14). Null or in the past means not
    /// snoozed — expired values are simply ignored, so nothing needs to clean them up.
    /// Persisted so a snooze survives an app restart.
    /// </summary>
    [JsonPropertyName("snoozeUntil")]
    public DateTimeOffset? SnoozeUntil { get; init; }

    /// <summary>True while <see cref="SnoozeUntil"/> is in the future.</summary>
    public bool IsSnoozed(DateTimeOffset now) => SnoozeUntil is { } until && until > now;

    /// <summary>
    /// ntfy (https://ntfy.sh) topic to push alerts to your phone, in addition to the desktop
    /// balloon — see <see cref="Services.PushNotifier"/>. Null or empty disables push entirely
    /// (the default): an ntfy topic is effectively a shared secret, since anyone who knows an
    /// unauthenticated topic's name can read it, so this has to be something the user opts into
    /// and picks themselves rather than something ClaudeMon generates or ships with.
    /// </summary>
    [JsonPropertyName("pushTopic")]
    public string? PushTopic { get; init; }

    /// <summary>
    /// The ntfy server to push to. Defaults to the public https://ntfy.sh; point this at a
    /// self-hosted instance to keep alerts off the public server entirely.
    /// </summary>
    [JsonPropertyName("pushServerUrl")]
    public string PushServerUrl { get; init; } = "https://ntfy.sh";
}
