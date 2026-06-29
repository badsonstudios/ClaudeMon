namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class TaskbarBarLayoutTests
{
    [Theory]
    [InlineData(0, 0)]      // empty
    [InlineData(50, 50)]    // half
    [InlineData(100, 100)]  // full
    public void Compute_FillTracksPercentage(double pct, int expectedFill)
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 100, pct, windowFraction: null, segments: 5);
        Assert.Equal(expectedFill, geo.FillWidth);
    }

    [Theory]
    [InlineData(150)]   // over-limit reading
    [InlineData(1000)]
    public void Compute_FillClampsToTrack_WhenOverLimit(double pct)
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 80, pct, windowFraction: null, segments: 5);
        Assert.Equal(80, geo.FillWidth);
    }

    [Fact]
    public void Compute_TickIsNull_WhenWindowFractionUnknown()
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 100, pct: 40, windowFraction: null, segments: 5);
        Assert.Null(geo.TickX);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(1.0, 100)]
    public void Compute_TickTracksWindowFraction(double fraction, int expectedTick)
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 100, pct: 40, windowFraction: fraction, segments: 5);
        Assert.Equal(expectedTick, geo.TickX);
    }

    [Theory]
    [InlineData(5, 4)]   // 5-hour bar → 4 interior dividers
    [InlineData(7, 6)]   // 7-day bar → 6 interior dividers
    [InlineData(1, 0)]   // a single segment has no interior dividers
    public void Compute_ProducesSegmentMinusOneDividers(int segments, int expectedCount)
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 140, pct: 40, windowFraction: null, segments);
        Assert.Equal(expectedCount, geo.DividerXs.Count);
    }

    [Fact]
    public void Compute_DividersAreOrderedAndWithinTrack()
    {
        var geo = TaskbarBarLayout.Compute(trackWidth: 140, pct: 40, windowFraction: null, segments: 7);

        var previous = 0;
        foreach (var dx in geo.DividerXs)
        {
            Assert.InRange(dx, 1, 140 - 1); // interior, never on an edge
            Assert.True(dx > previous, "dividers must increase left to right");
            previous = dx;
        }
    }
}
