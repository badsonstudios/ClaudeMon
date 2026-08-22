namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class LimitHistoryChartLayoutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Rectangle Plot = new(100, 0, 200, 100);

    private static LimitHistorySlot Slot(
        double? capacity, int series = 0, bool low = false,
        double tokens = 0, double peak = 0) =>
        new(T0, series, capacity, low, tokens, peak);

    [Fact]
    public void ComputeCapacity_PositionsPointsOnTheSharedSlotAxis()
    {
        var slots = new[] { Slot(1_000_000), Slot(null), Slot(500_000, series: 1, low: true), Slot(2_000_000) };
        var ticks = new double[] { 0, 1_000_000, 2_000_000 };

        var geometry = LimitHistoryChartLayout.ComputeCapacity(slots, [], ticks, Plot, maxLabels: 10);

        Assert.Equal(2_000_000, geometry.AxisMax);
        Assert.Equal(3, geometry.Points.Count); // the null-capacity slot draws nothing

        // Four slots over 200px = 50px each; centers at 125, 225, 275.
        Assert.Equal(125f, geometry.Points[0].X);
        Assert.Equal(225f, geometry.Points[1].X);
        Assert.Equal(275f, geometry.Points[2].X);

        // Inverted y: 1M of a 2M axis sits mid-plot; 2M on the top edge.
        Assert.Equal(50f, geometry.Points[0].Y);
        Assert.Equal(0f, geometry.Points[2].Y);

        Assert.True(geometry.Points[1].Low);
        Assert.Equal(1, geometry.Points[1].Series);
    }

    [Fact]
    public void ComputeCapacity_PlanMarkersLandOnTheirSlotCenters_OutOfRangeDropped()
    {
        var slots = new[] { Slot(1_000_000), Slot(1_000_000) };
        var geometry = LimitHistoryChartLayout.ComputeCapacity(
            slots, [(1, "Pro"), (7, "ghost")], [0.0, 1_000_000.0], Plot, 10);

        var marker = Assert.Single(geometry.PlanMarkers);
        Assert.Equal(250f, marker.X); // slot 1 of 2 over 200px
        Assert.Equal("Pro", marker.Label);
    }

    [Fact]
    public void ComputeCapacity_EmptyOrDegenerate_ReturnsEmptyGeometry()
    {
        Assert.Empty(LimitHistoryChartLayout.ComputeCapacity(
            [], [], [0.0, 1.0], Plot, 10).Points);
        Assert.Empty(LimitHistoryChartLayout.ComputeCapacity(
            [Slot(1.0)], [], [], Plot, 10).Points);
        Assert.Empty(LimitHistoryChartLayout.ComputeCapacity(
            [Slot(1.0)], [], [0.0, 1.0], new Rectangle(0, 0, 0, 100), 10).Points);
    }

    [Fact]
    public void ComputeCapacity_NonFiniteOrNegativeCapacity_DrawsNoPoint()
    {
        var slots = new[] { Slot(double.NaN), Slot(-5), Slot(1_000_000) };
        var geometry = LimitHistoryChartLayout.ComputeCapacity(
            slots, [], [0.0, 1_000_000.0], Plot, 10);

        Assert.Single(geometry.Points);
    }

    [Fact]
    public void ComputeUtilization_BarsScaleToTokens_PercentsToTheFixedRightAxis()
    {
        var slots = new[]
        {
            Slot(null, tokens: 1_000_000, peak: 50),
            Slot(null, tokens: 2_000_000, peak: 100),
            Slot(null, tokens: 0, peak: 0),
        };
        var geometry = LimitHistoryChartLayout.ComputeUtilization(
            slots, [0.0, 1_000_000.0, 2_000_000.0], Plot, maxBarWidth: 40, maxLabels: 10);

        Assert.Equal(3, geometry.Bars.Count);
        Assert.Equal(50f, geometry.Bars[0].Height);   // half the 2M axis
        Assert.Equal(100f, geometry.Bars[1].Height);  // full height
        Assert.Equal(0f, geometry.Bars[2].Height);    // no tokens, no bar

        Assert.Equal(50f, geometry.PercentPoints[0].Y);  // 50% mid-plot
        Assert.Equal(0f, geometry.PercentPoints[1].Y);   // 100% on the top edge
        Assert.Equal(100f, geometry.PercentPoints[2].Y); // 0% on the bottom edge
    }

    [Fact]
    public void ComputeUtilization_ClampsAbsurdPercentsIntoTheAxis()
    {
        var slots = new[] { Slot(null, tokens: 1, peak: 250), Slot(null, tokens: 1, peak: -10) };
        var geometry = LimitHistoryChartLayout.ComputeUtilization(
            slots, [0.0, 1.0], Plot, 40, 10);

        Assert.Equal(0f, geometry.PercentPoints[0].Y);
        Assert.Equal(100f, geometry.PercentPoints[1].Y);
    }

    [Fact]
    public void FormatAxisTokens_UsesTheAppsTokenShorthand()
    {
        Assert.Equal("0", LimitHistoryChartLayout.FormatAxisTokens(0));
        Assert.Equal("500K", LimitHistoryChartLayout.FormatAxisTokens(500_000));
        Assert.Equal("60M", LimitHistoryChartLayout.FormatAxisTokens(60_000_000));
        Assert.Equal("0", LimitHistoryChartLayout.FormatAxisTokens(-5)); // clamped, not "-5"
    }
}
