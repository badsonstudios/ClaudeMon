namespace ClaudeMon.UI;

/// <summary>
/// A single usage reading pushed to the taskbar overlays: the 5-hour and (optional) 7-day
/// utilisation percentages plus their window-elapsed fractions (0..1, or null when the reset
/// time is unknown). The fractions drive the bar style's time-in-window tick and pace colouring;
/// the number style ignores them.
/// </summary>
public readonly record struct TaskbarReading(
    double FiveHourPct,
    double? FiveHourFraction,
    double? SevenDayPct,
    double? SevenDayFraction);
