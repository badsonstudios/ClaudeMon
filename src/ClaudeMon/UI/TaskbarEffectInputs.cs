namespace ClaudeMon.UI;

using ClaudeMon.Models;

/// <summary>
/// Turns a usage response into the session and weekly readings the escalation effects judge
/// (see <see cref="TaskbarEffectTracker"/>). The level is the usage colour logic —
/// <see cref="IconRenderer.GetUsageLevel"/> under the user's colour mode — raised by the API's
/// severity floor, the same classification the flyout's bars use.
/// </summary>
/// <remarks>
/// A window that has ended is skipped: the API keeps returning its old, past <c>resets_at</c>
/// until new usage opens a window, and with no elapsed fraction pace mode would fall back to
/// the absolute level — turning a well-paced 95% red, and setting the taskbar on fire, the
/// moment the window it described was over.
/// </remarks>
internal static class TaskbarEffectInputs
{
    /// <summary>
    /// Below this utilisation a limit triggers no effect, whatever its colour. Early in a window
    /// the pace colouring is twitchy — a few percent used in the first minutes reads as far
    /// ahead of pace — and smoke off the back of 5% usage is noise, not a warning.
    /// </summary>
    internal const double MinPercentForEffects = 20;

    public static IReadOnlyList<TaskbarEffectReading> From(UsageResponse usage, UsageColorMode mode)
    {
        var readings = new List<TaskbarEffectReading>(2);

        // The legacy fields are what the readout displays; limits[] supplies the severity (and
        // stands in for a missing legacy field). Without limits[] the severity is Normal, the
        // same assumption the flyout makes.
        var session = FindLimit(usage, "session");
        if ((usage.FiveHour ?? session?.ToBucket()) is { IsExpired: false } fiveHour)
        {
            readings.Add(Reading(TaskbarEffectTracker.SessionKey, fiveHour, UsageWindows.FiveHour,
                session?.SeverityLevel ?? LimitSeverity.Normal, mode));
        }

        var weekly = FindLimit(usage, "weekly_all");
        if ((usage.SevenDay ?? weekly?.ToBucket()) is { IsExpired: false } sevenDay)
        {
            readings.Add(Reading(TaskbarEffectTracker.WeeklyKey, sevenDay, UsageWindows.SevenDay,
                weekly?.SeverityLevel ?? LimitSeverity.Normal, mode));
        }

        return readings;
    }

    private static TaskbarEffectReading Reading(
        string key, UsageBucket bucket, TimeSpan window, LimitSeverity severity, UsageColorMode mode)
    {
        // Still reported below the threshold (as green), so the tracker keeps following the
        // limit's window and its resets.
        var level = bucket.UtilizationPct < MinPercentForEffects
            ? UsageLevel.Green
            : IconRenderer.ApplySeverityFloor(
                IconRenderer.GetUsageLevel(bucket.UtilizationPct, bucket.ElapsedFraction(window), mode),
                severity);
        return new TaskbarEffectReading(key, level, bucket.UtilizationPct, bucket.ResetAt);
    }

    // The highest-percent unscoped entry of a kind (matching LimitDisplay's de-dup rule) — a
    // model-scoped entry is a different bucket and mustn't lend its severity to the overall one.
    private static UsageLimit? FindLimit(UsageResponse usage, string kind) =>
        usage.Limits?
            .Where(l => string.Equals(l.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(l.Scope?.Model?.DisplayName))
            .OrderByDescending(l => l.Percent ?? 0)
            .FirstOrDefault();
}
