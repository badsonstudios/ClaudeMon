namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Composes the flyout's implied-capacity lines (issue #185), e.g.
/// "5-hour: ≈8.1M of ≈61M tokens (est.)" — the estimated token position behind the official
/// percentage. Pure and culture-invariant (the <see cref="LocalCostText"/> pattern): an empty
/// list means nothing is drawn. The hide rule lives here, where it is unit-testable: a line
/// exists only for a limit whose estimate is <see cref="CapacityConfidence.Medium"/> or
/// better AND whose official percentage is in the current payload — a capacity with no live
/// percent has nothing to be "of".
/// </summary>
public static class CapacityReadoutText
{
    /// <summary>At most this many lines (session, overall weekly, tightest scoped weekly).</summary>
    internal const int MaxLines = 3;

    public static IReadOnlyList<string> Compose(
        IReadOnlyList<ImpliedCapacity>? estimates, UsageResponse? usage)
    {
        if (estimates is null || estimates.Count == 0 || usage is null)
            return Array.Empty<string>();

        var lines = new List<string>(MaxLines);

        AddLine(lines, estimates, usage, "session", scopeModel: null, "5-hour");
        AddLine(lines, estimates, usage, "weekly_all", scopeModel: null, "7-day");

        // Of the scoped weekly caps, only the tightest (highest current %) earns a line —
        // the same budgeting the tray tooltip applies (LimitDisplay.MostConstrainedScopedWeekly).
        var scoped = LimitDisplay.MostConstrainedScopedWeekly(usage);
        if (scoped?.Scope?.Model?.DisplayName is { } scopeName)
            AddLine(lines, estimates, usage, "weekly_scoped", scopeName, $"Weekly ({scopeName})");

        return lines;
    }

    private static void AddLine(
        List<string> lines, IReadOnlyList<ImpliedCapacity> estimates, UsageResponse usage,
        string kind, string? scopeModel, string label)
    {
        var estimate = estimates.FirstOrDefault(e =>
            Normalize(e.Kind) == kind && Normalize(e.ScopeModel) == Normalize(scopeModel));
        if (estimate is null || estimate.Confidence < CapacityConfidence.Medium)
            return;

        if (CurrentPercent(usage, kind, scopeModel) is not { } pct)
            return;

        var capacity = estimate.CapacityWeightedTokens;
        if (capacity <= 0)
            return;

        var used = pct / 100.0 * capacity;
        var unit = estimate.EquivalentModel is { } model ? $"{model} tokens" : "tokens";
        lines.Add(
            $"{label}: ≈{LocalCostText.FormatTokens((long)Math.Round(used))}" +
            $" of ≈{LocalCostText.FormatTokens((long)Math.Round(capacity))} {unit} (est.)");
    }

    // The official percentage for a limit kind, from limits[] when present (same legacy
    // fallback as everything else: five_hour/seven_day map to session/weekly_all). On
    // duplicate (kind, scope) entries the highest percent wins — the same dedup rule as
    // LimitDisplay and the trackers, so this line can never disagree with the usage row
    // drawn right above it.
    private static double? CurrentPercent(UsageResponse usage, string kind, string? scopeModel)
    {
        if (usage.Limits is { Count: > 0 } limits)
        {
            var percents = limits
                .Where(l => Normalize(l.Kind) == kind
                    && Normalize(l.Scope?.Model?.DisplayName) == Normalize(scopeModel))
                .Select(l => l.Percent)
                .Where(p => p is not null)
                .ToList();
            return percents.Count > 0 ? percents.Max() : null;
        }

        return kind switch
        {
            "session" => usage.FiveHour?.UtilizationPct,
            "weekly_all" => usage.SevenDay?.UtilizationPct,
            _ => null,
        };
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}
