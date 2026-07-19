namespace ClaudeMon.Monitoring;

using System.Globalization;
using ClaudeMon.Models;

/// <summary>One budget notification to show as a tray balloon.</summary>
public record BudgetAlertMessage(string Title, string Text, ToolTipIcon Icon);

/// <summary>
/// The graduated budget-alert ladder (50/80/95% — the Helicone/LiteLLM
/// convention). Pure: takes the current period sums, the configured caps, and
/// the persisted fired-state; returns the new state plus any alerts to show.
///
/// Each threshold fires once per period: the highest step fired so far is
/// latched in <see cref="BudgetAlertState"/> (persisted in settings, so a
/// restart mid-period can't re-fire). Jumping several steps between checks
/// fires only the highest one. Cost within a calendar period is monotonic
/// (ingestion only appends), so no un-latch logic exists or is needed. Period
/// rollover is detected by the period key changing.
/// </summary>
internal static class BudgetAlerts
{
    internal static readonly int[] Ladder = [50, 80, 95];

    public static (BudgetAlertState State, List<BudgetAlertMessage> Alerts) Evaluate(
        LocalBudgetTotals totals, BudgetSettings budgets, BudgetAlertState? state)
    {
        var alerts = new List<BudgetAlertMessage>();
        var dayKey = PeriodKey(totals.Today);
        var weekKey = PeriodKey(totals.WeekStartMonday);

        // A period-key mismatch (new day / new week, or first run) resets that
        // scope's latch to 0.
        var dayFired = state?.DailyPeriod == dayKey ? state.DailyFiredPct : 0;
        var weekFired = state?.WeeklyPeriod == weekKey ? state.WeeklyFiredPct : 0;

        if (budgets.DailyEnabled && budgets.DailyCapUsd > 0)
        {
            var step = HighestStepReached(totals.TodayUsd, budgets.DailyCapUsd);
            if (step > dayFired)
            {
                alerts.Add(Compose("Daily", totals.TodayUsd, budgets.DailyCapUsd, step));
                dayFired = step;
            }
        }

        if (budgets.WeeklyEnabled && budgets.WeeklyCapUsd > 0)
        {
            var step = HighestStepReached(totals.WeekUsd, budgets.WeeklyCapUsd);
            if (step > weekFired)
            {
                alerts.Add(Compose("Weekly", totals.WeekUsd, budgets.WeeklyCapUsd, step));
                weekFired = step;
            }
        }

        return (new BudgetAlertState(dayKey, dayFired, weekKey, weekFired), alerts);
    }

    // The highest ladder step at or below the current percentage (0 = none).
    private static int HighestStepReached(double spentUsd, double capUsd)
    {
        var pct = spentUsd / capUsd * 100.0;
        var reached = 0;
        foreach (var step in Ladder)
        {
            if (pct >= step)
                reached = step;
        }
        return reached;
    }

    private static BudgetAlertMessage Compose(string scope, double spentUsd, double capUsd, int step)
    {
        var pct = (int)Math.Floor(spentUsd / capUsd * 100.0);
        var icon = step switch
        {
            95 => ToolTipIcon.Error,
            80 => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{scope} budget: ~${spentUsd:0.00} of ${capUsd:0.00} ({pct}%) — estimated from local transcripts at list prices.");
        return new BudgetAlertMessage($"{scope} budget — {step}% reached", text, icon);
    }

    private static string PeriodKey(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
