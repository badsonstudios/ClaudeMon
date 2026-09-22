namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class PlanUsageTextTests
{
    private static LocalUsageSnapshot Snapshot(
        long tokens, double? tokensPerHour = null, double costUsd = 1.0, bool unpriced = false,
        long cacheRead = 0, double? cacheReadPerHour = null) =>
        new(new DateOnly(2026, 9, 21), costUsd, unpriced, tokens, 0, cacheRead,
            BurnRateUsdPerHour: null, BurnRateTokensPerHour: tokensPerHour,
            BurnRateCacheReadTokensPerHour: cacheReadPerHour);

    private static LocalMonthTotals Month(double costUsd, bool unpriced = false) =>
        new(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 21), costUsd, unpriced);

    private static ImpliedCapacity Session(
        double capacity, CapacityConfidence confidence, string? equivalentModel = null) =>
        new("session", null, capacity, equivalentModel, confidence, 12, 0, null, null);

    private static UsageResponse UsageAt(double sessionPct) =>
        new(null, null, [new UsageLimit("session", null, sessionPct, null, null, null, null)]);

    // --- Mode decision ---

    [Fact]
    public void UseFlatPlanLines_ExplicitModeWins()
    {
        Assert.False(PlanUsageText.UseFlatPlanLines(UsageLineMode.Cost, ClaudePlan.Max20x));
        Assert.True(PlanUsageText.UseFlatPlanLines(UsageLineMode.FlatPlan, plan: null));
    }

    [Fact]
    public void UseFlatPlanLines_AutoFollowsThePlanSetting()
    {
        Assert.False(PlanUsageText.UseFlatPlanLines(UsageLineMode.Auto, plan: null));
        Assert.True(PlanUsageText.UseFlatPlanLines(UsageLineMode.Auto, ClaudePlan.Pro));
        Assert.True(PlanUsageText.UseFlatPlanLines(UsageLineMode.Auto, ClaudePlan.Max5x));
    }

    // --- Today line ---

    [Fact]
    public void Compose_TokensOnly_MakesThePlainTodayLine()
    {
        var lines = PlanUsageText.Compose(Snapshot(1_800_000), null, null, null, 0);

        Assert.Equal("Today: 1.8M tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_ConfidentSessionCapacity_AddsShareOfWindow()
    {
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(13.3), 0);

        Assert.Equal("Today: 8.1M tokens ≈ 13% of a 5-hour window (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_ShareOfWindow_CanExceedOneWindow()
    {
        // A day spans several 5-hour windows; "220%" is the intended reading.
        var lines = PlanUsageText.Compose(
            Snapshot(22_000_000), null,
            [Session(10_000_000, CapacityConfidence.High)], UsageAt(50), 0);

        Assert.Equal("Today: 22M tokens ≈ 220% of a 5-hour window (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_CacheReads_AreDiscountedInTheShare()
    {
        // 10M raw tokens of which 9M are cache reads: weighted = 10M − 0.9×9M = 1.9M, so a
        // 10M-weighted-token window reads 19%, not 100% — the raw count stays in the text,
        // the division happens in the capacity estimator's unit.
        var lines = PlanUsageText.Compose(
            Snapshot(10_000_000, cacheRead: 9_000_000), null,
            [Session(10_000_000, CapacityConfidence.Medium)], UsageAt(50), 0);

        Assert.Equal("Today: 10M tokens ≈ 19% of a 5-hour window (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_TinyShare_ReadsLessThanOnePercent()
    {
        var lines = PlanUsageText.Compose(
            Snapshot(40_000), null,
            [Session(10_000_000, CapacityConfidence.Medium)], UsageAt(1), 0);

        Assert.Equal("Today: 40K tokens ≈ <1% of a 5-hour window (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_ModelDenominatedCapacity_DropsTheShareClause()
    {
        // A capacity in one model's tokens can't honestly divide a mixed-model day.
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(61_000_000, CapacityConfidence.High, equivalentModel: "claude-fable-5")],
            UsageAt(13.3), 0);

        Assert.Equal("Today: 8.1M tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_NonPositiveCapacity_DropsTheShareClause()
    {
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(0, CapacityConfidence.High)], UsageAt(13.3), 0);

        Assert.Equal("Today: 8.1M tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_LegacyPayload_StillAnchorsTheShare()
    {
        var legacy = new UsageResponse(new UsageBucket(13.3, null), null);

        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], legacy, 0);

        Assert.Equal("Today: 8.1M tokens ≈ 13% of a 5-hour window (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_LowConfidenceOrNoLivePercent_DropsTheShareClause()
    {
        var lowConfidence = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(61_000_000, CapacityConfidence.Low)], UsageAt(13.3), 0);
        var noPercent = PlanUsageText.Compose(
            Snapshot(8_100_000), null,
            [Session(61_000_000, CapacityConfidence.High)], new UsageResponse(null, null, null), 0);

        Assert.Equal("Today: 8.1M tokens (est.)", Assert.Single(lowConfidence));
        Assert.Equal("Today: 8.1M tokens (est.)", Assert.Single(noPercent));
    }

    [Fact]
    public void Compose_NoTokensToday_HasNoTodayOrBurnLine()
    {
        Assert.Empty(PlanUsageText.Compose(Snapshot(0, tokensPerHour: 100), null, null, null, 0));
        Assert.Empty(PlanUsageText.Compose(null, null, null, null, 0));
    }

    // --- Burn line ---

    [Fact]
    public void Compose_TokenBurn_MakesTheBurnLine()
    {
        var lines = PlanUsageText.Compose(Snapshot(8_100_000, tokensPerHour: 2_100_000), null, null, null, 0);

        Assert.Equal(2, lines.Count);
        Assert.Equal("~2.1M tok/hr (est.)", lines[1]);
    }

    [Fact]
    public void Compose_BurnWithCapacity_ProjectsTimeToFull()
    {
        // Remaining: 61M × (1 − 0.133) ≈ 52.9M tokens; at 30M tok/hr that's 105.8m ≈ 1h 46m.
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000, tokensPerHour: 30_000_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(13.3), 0);

        Assert.Equal("~30M tok/hr · window full in ~1h 46m (est.)", lines[1]);
    }

    [Fact]
    public void Compose_TimeToFull_UsesTheWeightedBurnRate()
    {
        // Raw rate 60M tok/hr, 40M of it cache reads: weighted = 60M − 0.9×40M = 24M/hr.
        // Remaining 61M × (1 − 0.133) ≈ 52.9M weighted tokens → ≈ 2h 12m, not the ~53m the
        // raw rate would claim. The displayed tok/hr stays raw.
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000, tokensPerHour: 60_000_000, cacheReadPerHour: 40_000_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(13.3), 0);

        Assert.Equal("~60M tok/hr · window full in ~2h 12m (est.)", lines[1]);
    }

    [Fact]
    public void Compose_TimeToFull_MinutesOnlyAndSubMinute()
    {
        // 10M × 10% = 1M weighted remaining at 2M/hr → 30m.
        var minutes = PlanUsageText.Compose(
            Snapshot(1_000_000, tokensPerHour: 2_000_000), null,
            [Session(10_000_000, CapacityConfidence.Medium)], UsageAt(90), 0);
        // 10M × 0.1% = 10K remaining at 10M/hr → seconds.
        var subMinute = PlanUsageText.Compose(
            Snapshot(1_000_000, tokensPerHour: 10_000_000), null,
            [Session(10_000_000, CapacityConfidence.Medium)], UsageAt(99.9), 0);

        Assert.Equal("~2M tok/hr · window full in ~30m (est.)", minutes[1]);
        Assert.Equal("~10M tok/hr · window full in <1m (est.)", subMinute[1]);
    }

    [Fact]
    public void Compose_TimeToFull_ExactHoursDropTheZeroMinutes()
    {
        // 10M × 60% = 6M weighted remaining at 3M/hr → exactly 2h, not "2h 0m".
        var lines = PlanUsageText.Compose(
            Snapshot(1_000_000, tokensPerHour: 3_000_000), null,
            [Session(10_000_000, CapacityConfidence.Medium)], UsageAt(40), 0);

        Assert.Equal("~3M tok/hr · window full in ~2h (est.)", lines[1]);
    }

    [Fact]
    public void Compose_WindowAlreadyFull_SaysSo()
    {
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000, tokensPerHour: 1_000_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(100), 0);

        Assert.Equal("~1M tok/hr · window full (est.)", lines[1]);
    }

    [Fact]
    public void Compose_TimeToFullBeyondTheWindow_IsOmitted()
    {
        // 52.9M remaining at 1M tok/hr is ~53h: the reset comes first, so no projection.
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000, tokensPerHour: 1_000_000), null,
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(13.3), 0);

        Assert.Equal("~1M tok/hr (est.)", lines[1]);
    }

    [Fact]
    public void Compose_IdleBurnWindow_HasNoBurnLine()
    {
        var lines = PlanUsageText.Compose(Snapshot(8_100_000, tokensPerHour: null), null, null, null, 0);

        Assert.Equal("Today: 8.1M tokens (est.)", Assert.Single(lines));
    }

    // --- Month value line ---

    [Fact]
    public void Compose_MonthValue_MakesTheValueLine()
    {
        var lines = PlanUsageText.Compose(null, Month(312.40), null, null, 0);

        Assert.Equal("This month: ~$312 of API usage (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_PlanPrice_AddsTheMultiple()
    {
        var lines = PlanUsageText.Compose(null, Month(312.40), null, null, planMonthlyUsd: 100);

        Assert.Equal("This month: ~$312 of API usage · ≈3.1× plan (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_UnpricedMonth_ShowsTheFloorAndNoMultiple()
    {
        // A partially-priced total is a floor; a multiple against it would understate.
        var lines = PlanUsageText.Compose(null, Month(50, unpriced: true), null, null, planMonthlyUsd: 100);

        Assert.Equal("This month: ≥$50.00 of API usage (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_ZeroCostMonth_HasNoValueLine()
    {
        Assert.Empty(PlanUsageText.Compose(null, Month(0), null, null, planMonthlyUsd: 100));
        Assert.Empty(PlanUsageText.Compose(null, month: null, null, null, 0));
        // Unpriced with nothing priced would render "This month: — of API usage" — a line
        // that says nothing, so it isn't drawn.
        Assert.Empty(PlanUsageText.Compose(null, Month(0.001, unpriced: true), null, null, 0));
    }

    [Fact]
    public void Compose_AllThreeLines_InOrder()
    {
        var lines = PlanUsageText.Compose(
            Snapshot(8_100_000, tokensPerHour: 2_100_000), Month(312.40),
            [Session(61_000_000, CapacityConfidence.Medium)], UsageAt(13.3), planMonthlyUsd: 100);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Today: 8.1M tokens", lines[0]);
        Assert.StartsWith("~2.1M tok/hr", lines[1]);
        Assert.StartsWith("This month: ~$312", lines[2]);
    }
}
