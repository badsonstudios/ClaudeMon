namespace ClaudeMon.UI;

/// <summary>
/// A single usage reading pushed to the taskbar overlays: the 5-hour and (optional) 7-day
/// utilisation percentages plus their window-elapsed fractions (0..1, or null when the reset
/// time is unknown). The fractions drive the bar style's time-in-window tick and pace colouring;
/// the number style ignores them. <paramref name="FiveHourResetAt"/> feeds the optional
/// time-left-to-reset element — an absolute timestamp (not a remaining span) so the overlay can
/// tick the countdown down between polls. <paramref name="TimeToLimit"/> feeds the optional
/// time-to-limit element: the burn-rate projection as of this poll (null when no meaningful
/// estimate exists), a remaining span rather than a timestamp because it is an estimate that is
/// only re-derived when new samples arrive, not a clock the overlay can run down on its own.
/// </summary>
public readonly record struct TaskbarReading(
    double FiveHourPct,
    double? FiveHourFraction,
    double? SevenDayPct,
    double? SevenDayFraction,
    DateTimeOffset? FiveHourResetAt = null,
    TimeSpan? TimeToLimit = null);
