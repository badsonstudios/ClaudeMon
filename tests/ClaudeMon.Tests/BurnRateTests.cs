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
