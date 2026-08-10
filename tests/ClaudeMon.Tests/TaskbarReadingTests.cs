namespace ClaudeMon.Tests;

using ClaudeMon.Monitoring;
using ClaudeMon.UI;

public class TaskbarReadingTests
{
    [Fact]
    public void OptionalMembers_DefaultToNoDataRatherThanZero()
    {
        // The overlay draws the optional elements only when it has something to draw. A
        // reading built from the 5-hour number alone must therefore say "unknown" for the
        // rest — a 0% seven-day bar or a 1970 reset countdown would be a confident lie.
        var reading = new TaskbarReading(42.0, FiveHourFraction: null, SevenDayPct: null, SevenDayFraction: null);

        Assert.Equal(42.0, reading.FiveHourPct);
        Assert.Null(reading.FiveHourFraction);
        Assert.Null(reading.SevenDayPct);
        Assert.Null(reading.SevenDayFraction);
        Assert.Null(reading.FiveHourResetAt);
    }

    [Fact]
    public void DefaultTimeToLimit_IsNoEstimate()
    {
        // TimeToLimit is defaulted rather than nullable, so a reading that predates any
        // burn-rate projection has to render as "—" instead of a bogus span.
        var reading = new TaskbarReading(10.0, 0.25, 5.0, 0.5);

        Assert.Equal(TimeToLimitKind.NoEstimate, reading.TimeToLimit.Kind);
        Assert.Null(reading.TimeToLimit.Eta);
    }

    [Fact]
    public void CarriesEveryElementTheOverlayDraws()
    {
        var resetAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var reading = new TaskbarReading(
            FiveHourPct: 73.5,
            FiveHourFraction: 0.4,
            SevenDayPct: 21.0,
            SevenDayFraction: 0.6,
            FiveHourResetAt: resetAt,
            TimeToLimit: TimeToLimitEstimate.Projection(TimeSpan.FromMinutes(90)));

        Assert.Equal(73.5, reading.FiveHourPct);
        Assert.Equal(0.4, reading.FiveHourFraction);
        Assert.Equal(21.0, reading.SevenDayPct);
        Assert.Equal(0.6, reading.SevenDayFraction);
        // An absolute timestamp, not a remaining span: the overlay ticks the countdown down
        // between polls, so it needs the target, not a snapshot of how far away it was.
        Assert.Equal(resetAt, reading.FiveHourResetAt);
        Assert.Equal(TimeToLimitKind.Projection, reading.TimeToLimit.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), reading.TimeToLimit.Eta);
    }

    [Fact]
    public void EqualReadings_CompareEqual()
    {
        var a = new TaskbarReading(50.0, 0.5, 10.0, 0.1, null, TimeToLimitEstimate.Safe);
        var b = new TaskbarReading(50.0, 0.5, 10.0, 0.1, null, TimeToLimitEstimate.Safe);

        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { FiveHourPct = 51.0 });
    }
}
