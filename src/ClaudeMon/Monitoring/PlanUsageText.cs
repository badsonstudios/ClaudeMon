namespace ClaudeMon.Monitoring;

using System.Globalization;
using ClaudeMon.Models;

/// <summary>
/// Composes the flyout's flat-plan usage lines (issue #202) — the framing for Max/Pro
/// subscribers, who pay a flat fee and think in limit headroom rather than dollars:
///
///   Today: 8.1M tokens ≈ 13% of a 5-hour window (est.)
///   ~2.1M tok/hr · window full in ~1h 40m (est.)
///   This month: ~$312 of API usage · ≈3.1× your plan (est.)
///
/// Pure and culture-invariant (the <see cref="LocalCostText"/> pattern): an empty list
/// means nothing is drawn, and each fragment degrades independently — no session capacity
/// estimate drops the "% of a window" clause, an idle burn window drops the middle line,
/// an unstated plan price drops the multiple. The capacity-derived fragments use the same
/// gate as the implied-capacity readout (issue #185): confidence at least
/// <see cref="CapacityConfidence.Medium"/> AND a live official percent to anchor to.
/// </summary>
public static class PlanUsageText
{
    /// <summary>
    /// Whether the flyout shows the flat-plan framing instead of the cost line. An
    /// explicit mode wins; <see cref="UsageLineMode.Auto"/> follows the plan setting —
    /// every <see cref="ClaudePlan"/> value is a flat subscription, so stating one is
    /// stating "dollars aren't my meter".
    /// </summary>
    public static bool UseFlatPlanLines(UsageLineMode mode, ClaudePlan? plan) => mode switch
    {
        UsageLineMode.Cost => false,
        UsageLineMode.FlatPlan => true,
        _ => plan is not null,
    };

    public static IReadOnlyList<string> Compose(
        LocalUsageSnapshot? snapshot,
        LocalMonthTotals? month,
        IReadOnlyList<ImpliedCapacity>? capacities,
        UsageResponse? usage,
        double planMonthlyUsd)
    {
        var lines = new List<string>(3);
        var window = SessionWindow(capacities, usage);

        if (snapshot is { TotalTokens: > 0 })
        {
            var today = $"Today: {LocalCostText.FormatTokens(snapshot.TotalTokens)} tokens";
            // The displayed count stays raw (it's what the transcripts say happened); the
            // division against the capacity must be in the estimator's weighted unit or a
            // cache-read-heavy day overstates the share several-fold.
            if (window is { } w)
                today += $" ≈ {FormatShareOfWindow(Weighted(snapshot.TotalTokens, snapshot.CacheReadTokens), w.Capacity)} of a 5-hour window";
            lines.Add(today + " (est.)");

            if (snapshot.BurnRateTokensPerHour is { } rate && rate > 0)
            {
                var burn = $"~{LocalCostText.FormatTokens((long)Math.Round(rate))} tok/hr";
                // Same unit discipline for the projection: weighted rate against weighted
                // capacity, or the "full in" reads far too soon.
                var weightedRate = Weighted(rate, snapshot.BurnRateCacheReadTokensPerHour ?? 0);
                if (window is { } cw && weightedRate > 0 && TimeToFull(cw, weightedRate) is { } toFull)
                    burn += $" · {toFull}";
                lines.Add(burn + " (est.)");
            }
        }

        // The value line: what the month's usage would have cost at API prices — the flat
        // fee's counterweight. Skipped when there's nothing to price ("This month: — …"
        // would be a line that says nothing); a partially-priced floor ("≥$…") still shows.
        if (month is not null && month.CostUsd >= 0.005)
        {
            var value = "This month: " +
                LocalCostText.FormatCostFloorAware(month.CostUsd, month.HasUnpricedModels) +
                " of API usage";
            // The multiple only makes sense against a fully-priced total — a floor would
            // understate it, and understating "how much value" is the one wrong direction.
            if (planMonthlyUsd > 0 && !month.HasUnpricedModels)
                value += $" · ≈{(month.CostUsd / planMonthlyUsd).ToString("0.#", CultureInfo.InvariantCulture)}× plan";
            lines.Add(value + " (est.)");
        }

        return lines;
    }

    // Today's (or a rate's) tokens in the capacity estimator's weighted unit: cache reads
    // count CapacityEstimator.CacheReadWeight, everything else counts 1 — the unit
    // CapacityWeightedTokens is denominated in.
    private static double Weighted(double totalTokens, double cacheReadTokens) =>
        totalTokens - (1.0 - CapacityEstimator.CacheReadWeight) * cacheReadTokens;

    /// <summary>
    /// The 5-hour window's estimated capacity and live official percent, or null when the
    /// #185 gate isn't met — the same rule as <see cref="CapacityReadoutText"/>, so this
    /// line can never claim a window size the capacity readout wouldn't stand behind.
    /// </summary>
    private static (double Capacity, double Percent)? SessionWindow(
        IReadOnlyList<ImpliedCapacity>? capacities, UsageResponse? usage)
    {
        var estimate = capacities?.FirstOrDefault(e =>
            Normalize(e.Kind) == "session" && Normalize(e.ScopeModel).Length == 0);
        if (estimate is null || estimate.Confidence < CapacityConfidence.Medium
            || estimate.CapacityWeightedTokens <= 0)
            return null;

        // A capacity denominated in one model's tokens can't honestly divide a mixed-model
        // day — the snapshot has no per-model split. Omitted rather than guessed, the same
        // rule as everything else here.
        if (estimate.EquivalentModel is not null)
            return null;

        if (SessionPercent(usage) is not { } pct)
            return null;

        return (estimate.CapacityWeightedTokens, pct);
    }

    // The live session (5-hour) percent, with the same legacy fallback and same
    // duplicate-entry rule (highest wins) as CapacityReadoutText / LimitDisplay.
    private static double? SessionPercent(UsageResponse? usage)
    {
        if (usage is null)
            return null;

        if (usage.Limits is { Count: > 0 } limits)
        {
            var percents = limits
                .Where(l => Normalize(l.Kind) == "session"
                    && Normalize(l.Scope?.Model?.DisplayName).Length == 0)
                .Select(l => l.Percent)
                .Where(p => p is not null)
                .ToList();
            return percents.Count > 0 ? percents.Max() : null;
        }

        return usage.FiveHour?.UtilizationPct;
    }

    /// <summary>"&lt;1%", "13%", "220%" — today's tokens against one window's capacity. Can
    /// exceed 100%: a day spans several 5-hour windows, and that reading ("you've burned two
    /// windows' worth today") is exactly the flat-plan intuition this line exists for.</summary>
    private static string FormatShareOfWindow(double weightedTokens, double capacity)
    {
        var pct = weightedTokens / capacity * 100.0;
        return pct < 0.5 ? "<1%" : Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// "window full in ~1h 40m" at the current token burn, or null when it isn't worth
    /// saying: already at/over the cap ("window full" says it instead), or further out
    /// than the window is long — the reset will arrive first, so the projection is noise.
    /// </summary>
    private static string? TimeToFull((double Capacity, double Percent) window, double tokensPerHour)
    {
        var remaining = window.Capacity * (1.0 - window.Percent / 100.0);
        if (remaining <= 0)
            return "window full";

        var hours = remaining / tokensPerHour;
        if (hours > UsageWindows.FiveHour.TotalHours)
            return null;

        // Round to whole minutes first — truncating fields would render 29.9999m as "29m".
        var minutes = (int)Math.Round(hours * 60);
        if (minutes < 1)
            return "window full in <1m"; // "<" is its own hedge; "~<1m" would double up.
        var text = minutes >= 60
            ? minutes % 60 > 0 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes / 60}h"
            : $"{minutes}m";
        return $"window full in ~{text}";
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}
