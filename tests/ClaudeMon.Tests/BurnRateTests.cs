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

        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromHours(4));

        Assert.NotNull(eta);
        Assert.Equal(100, eta.Value.TotalMinutes, 1);
    }

    [Fact]
    public void Flat_ReturnsNull()
    {
        var samples = Series(40, 40, 40, 40);
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 40, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Declining_ReturnsNull()
    {
        var samples = Series(60, 55, 50, 45);
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 45, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)] // two points fit any noise perfectly — below the 3-sample floor
    public void TooFewSamples_ReturnsNull(int count)
    {
        var samples = Series(Enumerable.Range(0, count).Select(i => 50.0 + i).ToArray());
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 51, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void ZeroTimeSpan_ReturnsNull()
    {
        // Three samples at the same instant — no time base for a slope.
        var samples = new List<UsageSample>
        {
            new(T0, 50, null),
            new(T0, 53, null),
            new(T0, 55, null),
        };
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 55, timeUntilReset: TimeSpan.FromHours(4)));
    }

    [Fact]
    public void AlreadyAtLimit_ReturnsZero_EvenWithTooFewSamples()
    {
        // The at-limit short-circuit must precede the sample-count check.
        var samples = Series(100);
        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 100, timeUntilReset: TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.Zero, eta);
    }

    [Fact]
    public void KnownResetAlreadyElapsed_ReturnsNull()
    {
        // TimeUntilReset == Zero with a known reset means the window is resetting now.
        var samples = Series(50, 52, 54, 56, 58, 60);
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.Zero));
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
        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 99.5, timeUntilReset: TimeSpan.FromHours(2));
        Assert.NotNull(eta);
        Assert.True(eta.Value < TimeSpan.FromMinutes(1), $"expected sub-minute, got {eta}");
        Assert.Equal("<1m to limit", BurnRate.FormatTimeToLimit(eta));
    }

    [Fact]
    public void EtaBeyondReset_ReturnsNull()
    {
        // 0.4 pct/min from 60% → ~100 min to limit, but the window resets in 30 min.
        var samples = Series(50, 52, 54, 56, 58, 60);
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void EtaWithinReset_ReturnsValue()
    {
        var samples = Series(50, 52, 54, 56, 58, 60);
        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: TimeSpan.FromHours(3));
        Assert.NotNull(eta);
    }

    [Fact]
    public void NoResetInfo_StillProjects()
    {
        var samples = Series(50, 52, 54, 56, 58, 60);
        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 60, timeUntilReset: null);
        Assert.NotNull(eta);
        Assert.Equal(100, eta.Value.TotalMinutes, 1);
    }

    // ================================================================
    // Degenerate slopes (issue #100)
    // ================================================================

    [Fact]
    public void NearlyFlatRisingTrend_ReturnsNullInsteadOfOverflowing()
    {
        // Usage that is flat to within floating-point noise still yields a *positive*
        // least-squares slope (here ~1e-14 %/min), and 50 points of headroom divided by that
        // is ~5e15 minutes — a finite number far outside TimeSpan's ~1.5e10-minute range.
        // TimeSpan.FromMinutes threw, taking the whole app down when the flyout opened (#100).
        var samples = Series(50, 50, 50.0000000000001);

        var ex = Record.Exception(() =>
            BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));

        Assert.Null(ex); // no throw...
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null)); // ...and no estimate
    }

    [Theory]
    // The larger two are finite and convertible — they only exercise the ceiling. The
    // smaller two are the ones that threw before the fix. Both regimes matter: the guard
    // has to reject "absurd" as well as "unrepresentable".
    [InlineData(1e-3)]
    [InlineData(1e-6)]
    [InlineData(1e-9)]
    [InlineData(1e-13)] // about the smallest delta that survives being added to 50
    public void ImperceptibleSlopes_ProjectTooFarToBeUseful_ReturnNull(double delta)
    {
        var samples = Series(50, 50 + delta, 50 + (2 * delta));

        // Guard against a vacuous case: if the delta were annihilated by the addition the
        // samples would be identical, the slope exactly zero, and this would pass without
        // ever reaching the projection math (as double.Epsilon did).
        Assert.NotEqual(samples[0].FiveHourPct, samples[2].FiveHourPct);

        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));
    }

    [Fact]
    public void ProjectionInsideTheCeiling_WithUnknownReset_StillEstimates()
    {
        // +0.05 points per 5-minute sample = 0.01 pct/min ⇒ 10 points of headroom is 1000
        // minutes (~16.7h), inside the 24h ceiling. Pins the bound from below: without this,
        // the ceiling could be tightened to minutes and every other test would still pass.
        var samples = Series(89.90, 89.95, 90.00);

        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: null);

        Assert.NotNull(eta);
        Assert.Equal(1000, eta.Value.TotalMinutes, 1);
    }

    [Fact]
    public void ProjectionBeyondTheCeiling_WithUnknownReset_ReturnsNull()
    {
        // Same shape, shallower: 0.006 pct/min ⇒ ~1667 minutes (~27.8h), past the ceiling.
        var samples = Series(89.94, 89.97, 90.00);

        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: null));
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
        // Not merely "didn't throw": a bogus TimeSpan.Zero would render as "at limit".
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 50, timeUntilReset: null));
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
        Assert.Null(BurnRate.EstimateTimeToLimit(samples, currentPct: 99.99, timeUntilReset: null));
    }

    [Fact]
    public void LegitimateSlowClimb_StillProjects()
    {
        // The ceiling must not swallow real, usable projections: +0.25 points per 5-minute
        // sample = 0.05 pct/min, so 10 points of headroom ⇒ 200 minutes, inside the window.
        var samples = Series(89.5, 89.75, 90.0);

        var eta = BurnRate.EstimateTimeToLimit(samples, currentPct: 90, timeUntilReset: TimeSpan.FromHours(4));

        Assert.NotNull(eta);
        Assert.Equal(200, eta.Value.TotalMinutes, 1);
    }

    [Theory]
    [InlineData(35, "~35m to limit")]
    [InlineData(90, "~1h 30m to limit")]
    [InlineData(120, "~2h to limit")]
    [InlineData(130, "~2h 10m to limit")]
    public void Format_Minutes_And_Hours(int minutes, string expected)
    {
        Assert.Equal(expected, BurnRate.FormatTimeToLimit(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Format_Null_ShowsDash()
    {
        Assert.Equal("—", BurnRate.FormatTimeToLimit(null));
    }

    [Fact]
    public void Format_Zero_ShowsAtLimit()
    {
        Assert.Equal("at limit", BurnRate.FormatTimeToLimit(TimeSpan.Zero));
    }
}
