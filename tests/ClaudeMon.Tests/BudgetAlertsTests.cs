namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class BudgetAlertsTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);      // a Wednesday
    private static readonly DateOnly Monday = new(2026, 7, 13);

    private static LocalBudgetTotals Totals(double todayUsd, double weekUsd = 0) =>
        new(Today, todayUsd, Monday, weekUsd);

    private static BudgetSettings Daily(double cap = 10.0) =>
        new() { DailyEnabled = true, DailyCapUsd = cap };

    [Fact]
    public void Evaluate_CrossingEachStep_FiresOncePerStepWithEscalatingIcons()
    {
        var (state, a1) = BudgetAlerts.Evaluate(Totals(5.0), Daily(), null);
        var m1 = Assert.Single(a1);
        Assert.Equal(ToolTipIcon.Info, m1.Icon);
        Assert.Contains("50%", m1.Text);
        Assert.Contains("estimated", m1.Text);

        var (state2, a2) = BudgetAlerts.Evaluate(Totals(8.1), Daily(), state);
        Assert.Equal(ToolTipIcon.Warning, Assert.Single(a2).Icon);

        var (state3, a3) = BudgetAlerts.Evaluate(Totals(9.6), Daily(), state2);
        Assert.Equal(ToolTipIcon.Error, Assert.Single(a3).Icon);

        // Past 100%: 95 is the top step and it already fired — silence.
        var (_, a4) = BudgetAlerts.Evaluate(Totals(12.0), Daily(), state3);
        Assert.Empty(a4);
    }

    [Fact]
    public void Evaluate_JumpingSeveralSteps_FiresOnlyTheHighest()
    {
        var (state, alerts) = BudgetAlerts.Evaluate(Totals(9.7), Daily(), null);

        var alert = Assert.Single(alerts);
        Assert.Contains("95% reached", alert.Title);
        Assert.Equal(95, state.DailyFiredPct);
    }

    [Fact]
    public void Evaluate_SamePeriodSameValue_NoRefire()
    {
        // Models a restart: the persisted state comes back in unchanged.
        var (state, first) = BudgetAlerts.Evaluate(Totals(8.5), Daily(), null);
        Assert.Single(first);

        var (again, second) = BudgetAlerts.Evaluate(Totals(8.5), Daily(), state);
        Assert.Empty(second);
        Assert.Equal(state, again);
    }

    [Fact]
    public void Evaluate_NewDay_ResetsDailyLatch()
    {
        var (state, _) = BudgetAlerts.Evaluate(Totals(9.9), Daily(), null);
        Assert.Equal(95, state.DailyFiredPct);

        var nextDay = new LocalBudgetTotals(Today.AddDays(1), 6.0, Monday, 0);
        var (newState, alerts) = BudgetAlerts.Evaluate(nextDay, Daily(), state);

        Assert.Contains("50%", Assert.Single(alerts).Title);
        Assert.Equal(50, newState.DailyFiredPct);
    }

    [Fact]
    public void Evaluate_NewWeek_ResetsWeeklyLatch()
    {
        var budgets = new BudgetSettings { WeeklyEnabled = true, WeeklyCapUsd = 100.0 };
        var (state, _) = BudgetAlerts.Evaluate(Totals(0, weekUsd: 96.0), budgets, null);
        Assert.Equal(95, state.WeeklyFiredPct);

        var nextWeek = new LocalBudgetTotals(Monday.AddDays(7), 0, Monday.AddDays(7), 55.0);
        var (newState, alerts) = BudgetAlerts.Evaluate(nextWeek, budgets, state);

        Assert.Contains("Weekly", Assert.Single(alerts).Title);
        Assert.Equal(50, newState.WeeklyFiredPct);
    }

    [Fact]
    public void Evaluate_DailyAndWeekly_CanFireTogether()
    {
        var budgets = new BudgetSettings
        {
            DailyEnabled = true,
            DailyCapUsd = 10.0,
            WeeklyEnabled = true,
            WeeklyCapUsd = 50.0,
        };

        var (_, alerts) = BudgetAlerts.Evaluate(Totals(6.0, weekUsd: 41.0), budgets, null);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Title.StartsWith("Daily"));
        Assert.Contains(alerts, a => a.Title.StartsWith("Weekly"));
    }

    [Theory]
    [InlineData(false, 10.0)]  // disabled
    [InlineData(true, 0.0)]    // zero cap
    [InlineData(true, -5.0)]   // nonsense cap
    public void Evaluate_DisabledOrInvalidCap_NeverFires(bool enabled, double cap)
    {
        var budgets = new BudgetSettings { DailyEnabled = enabled, DailyCapUsd = cap };
        var (_, alerts) = BudgetAlerts.Evaluate(Totals(1000.0), budgets, null);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_BelowFirstStep_NoAlertButStateTracksPeriod()
    {
        var (state, alerts) = BudgetAlerts.Evaluate(Totals(2.0), Daily(), null);

        Assert.Empty(alerts);
        Assert.Equal("2026-07-15", state.DailyPeriod);
        Assert.Equal(0, state.DailyFiredPct);
    }

    [Fact]
    public void Evaluate_MessageText_CarriesAmountsAndEstimateCaveat()
    {
        var (_, alerts) = BudgetAlerts.Evaluate(Totals(8.2), Daily(), null);

        var text = Assert.Single(alerts).Text;
        Assert.Contains("$8.20 of $10.00", text);
        Assert.Contains("(82%)", text);
        Assert.Contains("estimated from local transcripts", text);
    }
}
