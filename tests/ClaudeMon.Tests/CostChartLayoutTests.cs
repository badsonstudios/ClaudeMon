namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class CostChartLayoutTests
{
    // A generous plot so bar widths aren't clamped by the 1px floor in these tests.
    private static readonly Rectangle Plot = new(50, 10, 300, 200);

    // The control's own call shape: the axis is derived from the costliest day, then the
    // same tick list is handed to Compute.
    private static CostChartGeometry Compute(params double[] costs) =>
        CostChartLayout.Compute(
            costs, Ticks(costs), Plot, maxBarWidth: 56, maxLabels: 6);

    private static IReadOnlyList<double> Ticks(IReadOnlyList<double> costs) =>
        CostChartLayout.NiceTicks(costs.Count == 0 ? 0.0 : costs.Max(), maxTicks: 5);

    // --- NiceTicks -----------------------------------------------------------------

    [Fact]
    public void NiceTicks_StartAtZero_AndCoverTheMaximum()
    {
        var ticks = CostChartLayout.NiceTicks(4.2, maxTicks: 5);

        Assert.Equal(0.0, ticks[0]);
        Assert.True(ticks[^1] >= 4.2, "the top gridline must sit at or above the costliest day");
    }

    [Fact]
    public void NiceTicks_AreEvenlySpaced()
    {
        var ticks = CostChartLayout.NiceTicks(37.5, maxTicks: 5);

        var step = ticks[1] - ticks[0];
        for (var i = 1; i < ticks.Count; i++)
            Assert.Equal(step, ticks[i] - ticks[i - 1], precision: 9);
    }

    [Theory]
    [InlineData(10.0, 5.0)]    // 10/4 = 2.5 → rounds up to the 5 step
    [InlineData(100.0, 50.0)]  // one decade up, same shape
    [InlineData(0.4, 0.1)]     // 0.4/4 = 0.1 → already the 1 step at 10⁻¹
    public void NiceTicks_StepIsARoundOneTwoOrFive(double max, double expectedStep)
    {
        var ticks = CostChartLayout.NiceTicks(max, maxTicks: 5);

        Assert.Equal(expectedStep, ticks[1], precision: 9);
    }

    [Fact]
    public void NiceTicks_NeverSubdividesBelowACent()
    {
        // Well under a cent a day: an axis labelled in fractions of a cent would be noise.
        var ticks = CostChartLayout.NiceTicks(0.004, maxTicks: 5);

        Assert.Equal(new[] { 0.0, CostChartLayout.MinTickStep }, ticks);
    }

    [Fact]
    public void NiceTicks_RespectsTheTickCeiling()
    {
        foreach (var max in new[] { 0.03, 1.0, 4.2, 9.9, 37.5, 123.4, 8000.0 })
        {
            var ticks = CostChartLayout.NiceTicks(max, maxTicks: 5);
            Assert.True(ticks.Count <= 5, $"max {max} produced {ticks.Count} gridlines");
            Assert.True(ticks[^1] >= max, $"max {max} was not covered by {ticks[^1]}");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NiceTicks_NoCost_StillYieldsAnAxis(double max)
    {
        var ticks = CostChartLayout.NiceTicks(max, maxTicks: 5);

        // Two lines minimum, and a positive top, so nothing downstream divides by zero.
        Assert.Equal(2, ticks.Count);
        Assert.True(ticks[^1] > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NiceTicks_DegenerateTickCount_FallsBackToTwoLines(int maxTicks)
    {
        var ticks = CostChartLayout.NiceTicks(10.0, maxTicks);

        Assert.Equal(new[] { 0.0, 10.0 }, ticks);
    }

    // --- LabelledDays --------------------------------------------------------------

    [Fact]
    public void LabelledDays_FewDays_LabelsThemAll()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, CostChartLayout.LabelledDays(4, maxLabels: 6));
    }

    [Fact]
    public void LabelledDays_ManyDays_StridesBackFromToday()
    {
        var labels = CostChartLayout.LabelledDays(30, maxLabels: 6);

        Assert.Equal(new[] { 4, 9, 14, 19, 24, 29 }, labels);
    }

    [Theory]
    [InlineData(30, 6)]
    [InlineData(30, 4)]
    [InlineData(30, 1)]
    [InlineData(7, 3)]
    [InlineData(6, 6)]  // exactly at the cap: the boundary between the two branches
    [InlineData(7, 6)]
    public void LabelledDays_AlwaysIncludesTheLastDay_AndNeverExceedsTheCap(int count, int maxLabels)
    {
        var labels = CostChartLayout.LabelledDays(count, maxLabels);

        Assert.Equal(count - 1, labels[^1]);
        Assert.True(labels.Count <= maxLabels);
        for (var i = 1; i < labels.Count; i++)
            Assert.True(labels[i] > labels[i - 1], "labels must be in ascending day order");
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(5, 0)]
    public void LabelledDays_NothingToLabel_ReturnsEmpty(int count, int maxLabels)
    {
        Assert.Empty(CostChartLayout.LabelledDays(count, maxLabels));
    }

    // --- FormatAxisCost ------------------------------------------------------------

    [Theory]
    [InlineData(0.0, "$0")]
    [InlineData(0.004, "$0")]     // rounds away — an axis tick, not a cost readout
    [InlineData(0.005, "$0.01")]  // the flip point
    [InlineData(0.01, "$0.01")]
    [InlineData(0.5, "$0.50")]
    [InlineData(2.5, "$2.50")]
    [InlineData(5.0, "$5")]
    [InlineData(12.4, "$12.40")]
    [InlineData(250.0, "$250")]
    public void FormatAxisCost_ShowsCentsOnlyWhenTheyMatter(double usd, string expected)
    {
        Assert.Equal(expected, CostChartLayout.FormatAxisCost(usd));
    }

    // --- Compute -------------------------------------------------------------------

    [Fact]
    public void Compute_OneBarPerDay_StandingOnTheBaseline()
    {
        var geometry = Compute(1.0, 2.0, 3.0);

        Assert.Equal(3, geometry.Bars.Count);
        Assert.All(geometry.Bars, b => Assert.Equal(Plot.Bottom, b.Bottom, 3));
    }

    [Fact]
    public void Compute_BarHeightsAreProportionalToCost()
    {
        var geometry = Compute(1.0, 2.0, 4.0);

        Assert.Equal(2 * geometry.Bars[0].Height, geometry.Bars[1].Height, 3);
        Assert.Equal(4 * geometry.Bars[0].Height, geometry.Bars[2].Height, 3);
    }

    [Fact]
    public void Compute_TallestBarReachesTheAxisTop_WhenTheMaxIsARoundNumber()
    {
        // $10 max with 5 gridlines gives a $10 axis top, so the bar fills the plot.
        var geometry = Compute(2.0, 10.0);

        Assert.Equal(10.0, geometry.AxisMax, precision: 9);
        Assert.Equal(Plot.Height, geometry.Bars[1].Height, 3);
        Assert.Equal(Plot.Top, geometry.Bars[1].Top, 3);
    }

    [Fact]
    public void Compute_BarsAreScaledAgainstTheAxisTop_NotTheRawMaximum()
    {
        // $4.20 rounds up to a $6 axis, so the tallest bar stops short of the top.
        var geometry = Compute(4.2);

        Assert.True(geometry.AxisMax > 4.2);
        Assert.True(geometry.Bars[0].Height < Plot.Height);
        Assert.Equal(Plot.Height * (4.2 / geometry.AxisMax), geometry.Bars[0].Height, 3);
    }

    [Fact]
    public void Compute_ZeroCostDay_DrawsNoBar()
    {
        var geometry = Compute(5.0, 0.0, 5.0);

        Assert.Equal(0f, geometry.Bars[1].Height);
    }

    [Fact]
    public void Compute_TinyButNonZeroDay_KeepsAHairline()
    {
        // A cent beside a thousand dollars still has to be visible as "something".
        var geometry = Compute(1000.0, 0.01);

        Assert.True(geometry.Bars[1].Height >= 1f);
    }

    [Fact]
    public void Compute_NegativeCost_ClampsToZero()
    {
        var geometry = Compute(5.0, -3.0);

        Assert.Equal(0f, geometry.Bars[1].Height);
    }

    [Fact]
    public void Compute_BarsStayInsideThePlot_AndNeverOverlap()
    {
        var costs = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var geometry = CostChartLayout.Compute(costs, Ticks(costs), Plot, maxBarWidth: 56, maxLabels: 6);

        Assert.All(geometry.Bars, b =>
        {
            Assert.True(b.Left >= Plot.Left, "a bar started left of the plot");
            Assert.True(b.Right <= Plot.Right + 0.001f, "a bar ran past the plot");
            Assert.True(b.Top >= Plot.Top, "a bar rose above the plot");
        });
        for (var i = 1; i < geometry.Bars.Count; i++)
            Assert.True(geometry.Bars[i].Left >= geometry.Bars[i - 1].Right, "bars must not overlap");
    }

    [Fact]
    public void Compute_SingleDay_IsACappedCentredBar_NotAFullWidthSlab()
    {
        var geometry = Compute(7.0);

        var bar = Assert.Single(geometry.Bars);
        Assert.Equal(56f, bar.Width, 3);
        // Centred in the plot.
        Assert.Equal(Plot.Left + Plot.Width / 2f, bar.Left + bar.Width / 2f, 3);
        Assert.Equal(new[] { 0 }, geometry.LabelledDays);
    }

    [Fact]
    public void Compute_TicksSpanThePlotBottomToTop()
    {
        var geometry = Compute(2.0, 10.0);

        Assert.Equal(0.0, geometry.YTicks[0].Value);
        Assert.Equal(Plot.Bottom, geometry.YTicks[0].Y);
        Assert.Equal(geometry.AxisMax, geometry.YTicks[^1].Value, precision: 9);
        Assert.Equal(Plot.Top, geometry.YTicks[^1].Y);
    }

    [Fact]
    public void Compute_AllZeroCosts_StillLaysOutAnAxisWithoutBars()
    {
        var geometry = Compute(0.0, 0.0, 0.0);

        Assert.True(geometry.AxisMax > 0, "an all-zero series must not produce a zero-height axis");
        Assert.Equal(3, geometry.Bars.Count);
        Assert.All(geometry.Bars, b => Assert.Equal(0f, b.Height));
    }

    [Fact]
    public void Compute_NoDays_ReturnsEmptyGeometry()
    {
        var geometry = Compute();

        Assert.Empty(geometry.Bars);
        Assert.Empty(geometry.YTicks);
        Assert.Empty(geometry.LabelledDays);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-10, -10)]
    public void Compute_DegeneratePlot_ReturnsEmptyGeometry(int width, int height)
    {
        double[] costs = [1.0, 2.0];
        var geometry = CostChartLayout.Compute(
            costs, Ticks(costs), new Rectangle(0, 0, width, height), maxBarWidth: 56, maxLabels: 6);

        Assert.Empty(geometry.Bars);
        Assert.Empty(geometry.YTicks);
    }

    [Fact]
    public void Compute_NoAxis_ReturnsEmptyGeometry()
    {
        // A caller that hands over no ticks (or a zero-topped axis) gets nothing back
        // rather than a division by zero.
        Assert.Empty(CostChartLayout.Compute([1.0], [], Plot, maxBarWidth: 56, maxLabels: 6).Bars);
        Assert.Empty(CostChartLayout.Compute([1.0], [0.0], Plot, maxBarWidth: 56, maxLabels: 6).Bars);
    }

    [Fact]
    public void Compute_NonFiniteCost_DrawsNoBarRatherThanANaNRectangle()
    {
        double[] costs = [5.0, double.NaN, double.PositiveInfinity];
        var geometry = CostChartLayout.Compute(
            costs, CostChartLayout.NiceTicks(5.0, 5), Plot, maxBarWidth: 56, maxLabels: 6);

        Assert.Equal(0f, geometry.Bars[1].Height);
        Assert.Equal(0f, geometry.Bars[2].Height);
        Assert.All(geometry.Bars, b => Assert.False(float.IsNaN(b.Top) || float.IsNaN(b.Height)));
    }

    [Fact]
    public void Compute_UncappedBarWidth_FillsItsSlot()
    {
        var geometry = CostChartLayout.Compute(
            [1.0, 1.0], CostChartLayout.NiceTicks(1.0, 5), Plot, maxBarWidth: 0, maxLabels: 6);

        // No cap: each bar takes its 70% share of a 150px slot.
        Assert.Equal(105f, geometry.Bars[0].Width, 3);
    }

    [Fact]
    public void Compute_CrampedPlot_StillGivesEveryDayAVisibleBar()
    {
        // 30 days in 60px: the slot is 2px, so the 1px floor is what keeps bars drawable.
        var costs = Enumerable.Repeat(1.0, 30).ToArray();
        var geometry = CostChartLayout.Compute(
            costs, Ticks(costs), new Rectangle(0, 0, 60, 40), maxBarWidth: 56, maxLabels: 6);

        Assert.Equal(30, geometry.Bars.Count);
        Assert.All(geometry.Bars, b => Assert.True(b.Width >= 1f && b.Height >= 1f));
    }

    [Fact]
    public void Compute_TickRowsDescendEvenlyUpThePlot()
    {
        var geometry = Compute(37.5); // a $40 axis in $10 steps

        Assert.Equal(5, geometry.YTicks.Count);
        var step = geometry.YTicks[0].Y - geometry.YTicks[1].Y;
        for (var i = 1; i < geometry.YTicks.Count; i++)
        {
            Assert.Equal(step, geometry.YTicks[i - 1].Y - geometry.YTicks[i].Y);
            Assert.True(geometry.YTicks[i].Value > geometry.YTicks[i - 1].Value);
        }
    }
}
