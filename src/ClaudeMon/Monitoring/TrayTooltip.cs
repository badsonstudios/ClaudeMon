namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Composes the tray icon's hover tooltip. Extracted from <see cref="ClaudeMon.TrayApplication"/>
/// so the composition — including the hard <see cref="MaxLength"/> budget — is unit-testable:
/// <c>NotifyIcon.Text</c> throws beyond 127 characters, so this class must guarantee the cap.
/// </summary>
public static class TrayTooltip
{
    /// <summary>The longest string <c>NotifyIcon.Text</c> accepts without throwing.</summary>
    internal const int MaxLength = 127;

    // Keep the scoped-weekly model name compact; the flyout carries the full name.
    private const int MaxScopedNameLength = 10;

    /// <summary>
    /// The tooltip text: the classic 5hr/7day lines, plus — when the response carries a
    /// per-model weekly cap — the tightest one as e.g. <c>"Fable wk: 84% (2d 3h)"</c>. When the
    /// budget is tight the scoped line degrades gracefully (drop its countdown, then the whole
    /// line) rather than ever exceeding <see cref="MaxLength"/>. Without a scoped weekly the
    /// output is identical to the pre-limits[] tooltip.
    /// </summary>
    public static string Compose(UsageResponse usage, MonitorStatus status)
    {
        var scoped = LimitDisplay.MostConstrainedScopedWeekly(usage);

        var text = Build(usage, status, scoped, scopedCountdown: true);
        if (text.Length <= MaxLength)
            return text;

        if (scoped is not null)
        {
            text = Build(usage, status, scoped, scopedCountdown: false);
            if (text.Length <= MaxLength)
                return text;

            text = Build(usage, status, scoped: null, scopedCountdown: false);
            if (text.Length <= MaxLength)
                return text;
        }

        // Last resort — should be unreachable (the base lines are far under budget), but the
        // cap is a hard runtime contract so never trust that.
        return text[..MaxLength];
    }

    private static string Build(
        UsageResponse usage, MonitorStatus status, UsageLimit? scoped, bool scopedCountdown)
    {
        var lines = new List<string> { "ClaudeMon" };

        if (usage.FiveHour is { } fiveHour)
            lines.Add($"5hr: {fiveHour.UtilizationPct:F0}% ({fiveHour.FormatResetCountdown()})");

        if (usage.SevenDay is { } sevenDay)
            lines.Add($"7day: {sevenDay.UtilizationPct:F0}% ({sevenDay.FormatResetCountdown()})");

        if (scoped is not null)
            lines.Add(ScopedLine(scoped, scopedCountdown));

        if (status != MonitorStatus.Connected)
            lines.Add($"[{status}]");

        return string.Join("\n", lines);
    }

    private static string ScopedLine(UsageLimit scoped, bool includeCountdown)
    {
        var name = scoped.Scope?.Model?.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
            name = "Model";
        if (name.Length > MaxScopedNameLength)
            name = name[..(MaxScopedNameLength - 1)] + "…";

        var line = $"{name} wk: {scoped.Percent ?? 0:F0}%";
        if (includeCountdown)
        {
            // "resets 2d 3h" → "2d 3h" (the idle "resets on next use" → "on next use", which
            // still reads fine in the parens; "—" has no prefix and passes through).
            var countdown = scoped.ToBucket().FormatResetCountdown();
            const string prefix = "resets ";
            if (countdown.StartsWith(prefix, StringComparison.Ordinal))
                countdown = countdown[prefix.Length..];
            line += $" ({countdown})";
        }

        return line;
    }
}
