namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class BurnRateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    // Builds samples spaced 5 minutes apart, oldest first.
    private static List<UsageSample> Series(params double[] fiveHourPcts)
    {
        var list = new List<UsageSample>();
        for (var i = 0; i < fiveHourPcts.Length; i++)
            list.Add(new UsageSample(T0.AddMinutes(i * 5), fiveHourPcts[i], null));
        return list;
    }

    [Fact]
    public void Rising_ProjectsTimeToLimit()
    {
        // +2 percentage points every 5 min = 0.4 pct/min. From 60% → 40 pts left → 100 min.
        var samples = Series(50, 52, 54, 56, 58, 60);

        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromHours(4));

        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
        Assert.NotNull(estimate.Eta);
        Assert.Equal(100, estimate.Eta.Value.TotalMinutes, 1);
    }

    [Fact]
    public void Flat_NoEstimate()
    {
        var samples = Series(40, 40, 40, 40);
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 40, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Declining_NoEstimate()
    {
        var samples = Series(60, 55, 50, 45);
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 45, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)] // two points fit any noise perfectly — below the 3-sample floor
    public void TooFewSamples_NoEstimate(int count)
    {
        var samples = Series(Enumerable.Range(0, count).Select(i => 50.0 + i).ToArray());
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 51, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void ZeroTimeSpan_NoEstimate()
    {
        // Three samples at the same instant — no time base for a slope.
        var samples = new List<UsageSample>
        {
            new(T0, 50, null),
            new(T0, 53, null),
            new(T0, 55, null),
        };
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 55, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void AlreadyAtLimit_ReturnsAtLimit_EvenWithTooFewSamples()
    {
        // The at-limit short-circuit must precede the sample-count check.
        var samples = Series(100);
        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 100, timeUntilReset: TimeSpan.FromHours(1));
        Assert.Equal(TimeToLimitKind.AtLimit, estimate.Kind);
    }

    [Fact]
    public void KnownResetAlreadyElapsed_ReturnsSafe()
    {
        // TimeUntilReset == Zero with a known reset means the window is resetting now —
        // the reset beats any projection, so this is the safe case, not a missing estimate.
        var samples = Series(50, 52, 54, 56, 58, 60);
        Assert.Equal(
            TimeToLimitEstimate.Safe,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.Zero));
    }

    [Fact]
    public void VerySteepRise_ProducesSubMinuteEstimate()
    {
        // A rapid burst (~5 pts/min) from 99.5% → only seconds to the cap.
        var samples = new List<UsageSample>
        {
            new(T0, 90, null),
            new(T0.AddMinutes(1), 95, null),
            new(T0.AddMinutes(2), 99.5, null),
        };
        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 99.5, timeUntilReset: TimeSpan.FromHours(2));
        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
        Assert.True(estimate.Eta < TimeSpan.FromMinutes(1), $"expected sub-minute, got {estimate.Eta}");
        Assert.Equal("<1m to limit", BurnRate.FormatTimeToLimit(estimate));
    }

    [Fact]
    public void EtaBeyondReset_ReturnsSafe()
    {
        // 0.4 pct/min from 60% → ~100 min to limit, but the window resets in 30 min. The
        // user's own case from #158: this good-news state used to be an indistinguishable
        // "—" and read as a broken feature.
        var samples = Series(50, 52, 54, 56, 58, 60);
        Assert.Equal(
            TimeToLimitEstimate.Safe,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void EtaWithinReset_ReturnsProjection()
    {
        var samples = Series(50, 52, 54, 56, 58, 60);
        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromHours(3));
        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
    }

    [Fact]
    public void NoResetInfo_StillProjects()
    {
        var samples = Series(50, 52, 54, 56, 58, 60);
        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: null);
        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
        Assert.NotNull(estimate.Eta);
        Assert.Equal(100, estimate.Eta.Value.TotalMinutes, 1);
    }

    // ================================================================
    // Degenerate slopes (issue #100)
    // ================================================================

    [Fact]
    public void NearlyFlatRisingTrend_NoEstimateInsteadOfOverflowing()
    {
        // Usage that is flat to within floating-point noise still yields a *positive*
        // least-squares slope (here ~1e-14 %/min), and 50 points of headroom divided by that
        // is ~5e15 minutes — a finite number far outside TimeSpan's ~1.5e10-minute range.
        // TimeSpan.FromMinutes threw, taking the whole app down when the flyout opened (#100).
        var samples = Series(50, 50, 50.0000000000001);

        var ex = Record.Exception(() =>
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));

        Assert.Null(ex); // no throw...
        Assert.Equal( // ...and no estimate
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));
    }

    [Fact]
    public void NearlyFlatRisingTrend_WithKnownReset_StaysNoEstimate_NotSafe()
    {
        // Pins the guard order (#158): an epsilon-above-flat trend must classify like an
        // exactly-flat one ("—"), not flip to "safe" just because a reset time is known —
        // the noise ceiling runs before the reset comparison.
        var samples = Series(50, 50, 50.0000000000001);

        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Theory]
    // The larger two are finite and convertible — they only exercise the ceiling. The
    // smaller two are the ones that threw before the fix. Both regimes matter: the guard
    // has to reject "absurd" as well as "unrepresentable".
    [InlineData(1e-3)]
    [InlineData(1e-6)]
    [InlineData(1e-9)]
    [InlineData(1e-13)] // about the smallest delta that survives being added to 50
    public void ImperceptibleSlopes_ProjectTooFarToBeUseful_NoEstimate(double delta)
    {
        var samples = Series(50, 50 + delta, 50 + (2 * delta));

        // Guard against a vacuous case: if the delta were annihilated by the addition the
        // samples would be identical, the slope exactly zero, and this would pass without
        // ever reaching the projection math (as double.Epsilon did).
        Assert.NotEqual(samples[0].FiveHourPct, samples[2].FiveHourPct);

        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));
    }

    [Fact]
    public void ProjectionInsideTheCeiling_WithUnknownReset_StillEstimates()
    {
        // +0.05 points per 5-minute sample = 0.01 pct/min ⇒ 10 points of headroom is 1000
        // minutes (~16.7h), inside the 24h ceiling. Pins the bound from below: without this,
        // the ceiling could be tightened to minutes and every other test would still pass.
        var samples = Series(89.90, 89.95, 90.00);

        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: null);

        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
        Assert.NotNull(estimate.Eta);
        Assert.Equal(1000, estimate.Eta.Value.TotalMinutes, 1);
    }

    [Fact]
    public void ProjectionBeyondTheCeiling_WithUnknownReset_NoEstimate()
    {
        // Same shape, shallower: 0.006 pct/min ⇒ ~1667 minutes (~27.8h), past the ceiling.
        var samples = Series(89.94, 89.97, 90.00);

        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: null));
    }

    [Fact]
    public void SlowBurnBeyondTheCeiling_WithKnownReset_StaysNoEstimate_NotSafe()
    {
        // The realistic sibling of the epsilon pin above: a genuine slow burn projecting
        // ~27.8h — past the 24h ceiling — with a known 4h reset. The ceiling wins: a
        // projection too absurd to show is also too absurd to promote to "safe".
        var samples = Series(89.94, 89.97, 90.00);

        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void HugeTimeGapBetweenSamples_DoesNotOverflow()
    {
        // A machine asleep for months between samples: tiny slope over an enormous span.
        var samples = new List<UsageSample>
        {
            new(T0, 50, null),
            new(T0.AddDays(200), 50.000001, null),
            new(T0.AddDays(400), 50.000002, null),
        };

        var ex = Record.Exception(() =>
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));

        Assert.Null(ex);
        // Not merely "didn't throw": a bogus AtLimit would render as "at limit".
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));
    }

    [Fact]
    public void JustUnderTheLimitWithTinySlope_DoesNotOverflow()
    {
        // Minimal headroom shrinks the numerator, but a small enough slope still overflows —
        // the guard must be on the projection, not on the headroom.
        var samples = Series(99.99, 99.99, 99.990000000001);

        var ex = Record.Exception(() =>
            BurnRate.EstimateTimeToLimit(samples, currentPct: 99.99, timeUntilReset: null));

        Assert.Null(ex);
        Assert.Equal(
            TimeToLimitEstimate.NoEstimate,
            BurnRate.EstimateTimeToLimit(samples, currentPct: 99.99, timeUntilReset: null));
    }

    [Fact]
    public void LegitimateSlowClimb_StillProjects()
    {
        // The ceiling must not swallow real, usable projections: +0.25 points per 5-minute
        // sample = 0.05 pct/min, so 10 points of headroom ⇒ 200 minutes, inside the window.
        var samples = Series(89.5, 89.75, 90.0);

        var estimate = BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: TimeSpan.FromHours(4));

        Assert.Equal(TimeToLimitKind.Projection, estimate.Kind);
        Assert.NotNull(estimate.Eta);
        Assert.Equal(200, estimate.Eta.Value.TotalMinutes, 1);
    }

    // ================================================================
    // Formatting — the four kinds (#158)
    // ================================================================

    [Theory]
    [InlineData(35, "~35m to limit")]
    [InlineData(90, "~1h 30m to limit")]
    [InlineData(120, "~2h to limit")]
    [InlineData(130, "~2h 10m to limit")]
    public void Format_Minutes_And_Hours(int minutes, string expected)
    {
        Assert.Equal(
            expected,
            BurnRate.FormatTimeToLimit(TimeToLimitEstimate.Projection(TimeSpan.FromMinutes(minutes))));
    }

    [Fact]
    public void Format_NoEstimate_ShowsDash()
    {
        Assert.Equal("—", BurnRate.FormatTimeToLimit(TimeToLimitEstimate.NoEstimate));
    }

    [Fact]
    public void Format_AtLimit_ShowsAtLimit()
    {
        Assert.Equal("at limit", BurnRate.FormatTimeToLimit(TimeToLimitEstimate.AtLimit));
    }

    [Fact]
    public void Format_Safe_ExplainsTheResetInTheFlyout()
    {
        // The flyout has room to say *why* there's no countdown — the good news must not
        // read like a missing estimate (#158).
        Assert.Equal("safe (resets first)", BurnRate.FormatTimeToLimit(TimeToLimitEstimate.Safe));
    }

    [Fact]
    public void FormatCompact_Safe_ShowsBareSafeWord()
    {
        // Lower-case word style, matching the countdown's "idle".
        Assert.Equal("safe", BurnRate.FormatTimeToLimitCompact(TimeToLimitEstimate.Safe));
    }

    [Fact]
    public void Format_DefaultStruct_IsHonestlyNoEstimate()
    {
        // An uninitialised TaskbarReading carries default(TimeToLimitEstimate); it must
        // render as "no estimate", never as something misleading like "safe" or "at limit".
        Assert.Equal("—", BurnRate.FormatTimeToLimitCompact(default));
        Assert.Equal("—", BurnRate.FormatTimeToLimit(default));
    }

    [Fact]
    public void Format_MalformedProjections_DegradeToHonestStates()
    {
        // EstimateTimeToLimit never builds these, but the positional constructor can't stop
        // a caller from doing so — the formatters must degrade honestly, not compose
        // nonsense like "at limit to limit" or "— to limit".
        var zeroEta = TimeToLimitEstimate.Projection(TimeSpan.Zero);
        Assert.Equal("at limit", BurnRate.FormatTimeToLimitCompact(zeroEta));
        Assert.Equal("at limit", BurnRate.FormatTimeToLimit(zeroEta));

        var noEta = new TimeToLimitEstimate(TimeToLimitKind.Projection);
        Assert.Equal("—", BurnRate.FormatTimeToLimitCompact(noEta));
        Assert.Equal("—", BurnRate.FormatTimeToLimit(noEta));
    }

    [Theory]
    [InlineData(35, "~35m")]
    [InlineData(90, "~1h 30m")]
    [InlineData(120, "~2h")]        // exact hours drop the minutes, as the flyout does
    [InlineData(130, "~2h 10m")]
    public void FormatCompact_DropsOnlyTheSuffix(int minutes, string expected)
    {
        Assert.Equal(
            expected,
            BurnRate.FormatTimeToLimitCompact(TimeToLimitEstimate.Projection(TimeSpan.FromMinutes(minutes))));
    }

    [Fact]
    public void FormatCompact_NoEstimateAndAtLimit_MatchTheFlyoutExactly()
    {
        // Neither state takes the "to limit" suffix, so the two forms are identical here.
        Assert.Equal("—", BurnRate.FormatTimeToLimitCompact(TimeToLimitEstimate.NoEstimate));
        Assert.Equal("at limit", BurnRate.FormatTimeToLimitCompact(TimeToLimitEstimate.AtLimit));
    }

    [Fact]
    public void FormatCompact_SubMinute_MatchesTheFlyoutsWording()
    {
        Assert.Equal(
            "<1m",
            BurnRate.FormatTimeToLimitCompact(TimeToLimitEstimate.Projection(TimeSpan.FromSeconds(20))));
    }

    [Theory]
    [InlineData(82.4)]   // rounds down — the case where Ceiling and Round would disagree
    [InlineData(82.6)]   // rounds up
    [InlineData(0.2)]    // sub-minute
    [InlineData(120)]    // exact hours
    public void FormatCompact_IsExactlyTheFlyoutLineMinusItsSuffix(double minutes)
    {
        // The taskbar element and the flyout line are the same projection shown twice; if the
        // two formatters ever round differently they would contradict each other on screen.
        var estimate = TimeToLimitEstimate.Projection(TimeSpan.FromMinutes(minutes));
        Assert.Equal(
            $"{BurnRate.FormatTimeToLimitCompact(estimate)} to limit", BurnRate.FormatTimeToLimit(estimate));
    }
}
