namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class SparklineTests
{
    private static readonly Rectangle Rect = new(0, 0, 100, 50);

    [Fact]
    public void BuildPoints_ReturnsOnePointPerValue_SpanningWidth()
    {
        var points = Sparkline.BuildPoints(new double[] { 0, 50, 100 }, Rect);

        Assert.Equal(3, points.Length);
        Assert.Equal(Rect.Left, points[0].X, 3);
        Assert.Equal(Rect.Right, points[^1].X, 3);
        // Evenly spaced across the width.
        Assert.Equal(50f, points[1].X, 3);
    }

    [Fact]
    public void BuildPoints_MapsMinToBottom_AndMaxToTop()
    {
        var points = Sparkline.BuildPoints(new double[] { 0, 100 }, Rect);

        Assert.Equal(Rect.Bottom, points[0].Y, 3); // 0% → bottom edge
        Assert.Equal(Rect.Top, points[1].Y, 3);    // 100% → top edge
    }

    [Fact]
    public void BuildPoints_FlatSeries_ProducesHorizontalLine()
    {
        var points = Sparkline.BuildPoints(new double[] { 40, 40, 40, 40 }, Rect);

        Assert.Equal(4, points.Length);
        var y0 = points[0].Y;
        Assert.All(points, p => Assert.Equal(y0, p.Y, 3));
        // 40% sits 40% up from the bottom.
        Assert.Equal(Rect.Bottom - 0.40f * Rect.Height, y0, 3);
    }

    [Fact]
    public void BuildPoints_ClampsOutOfRangeValues()
    {
        var points = Sparkline.BuildPoints(new double[] { -20, 150 }, Rect);

        Assert.Equal(Rect.Bottom, points[0].Y, 3); // clamped to 0%
        Assert.Equal(Rect.Top, points[1].Y, 3);    // clamped to 100%
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BuildPoints_FewerThanTwoValues_ReturnsEmpty(int count)
    {
        var values = Enumerable.Repeat(50.0, count).ToArray();
        Assert.Empty(Sparkline.BuildPoints(values, Rect));
    }

    [Fact]
    public void BuildPoints_EmptyRect_ReturnsEmpty()
    {
        Assert.Empty(Sparkline.BuildPoints(new double[] { 1, 2 }, new Rectangle(0, 0, 0, 0)));
    }
}
