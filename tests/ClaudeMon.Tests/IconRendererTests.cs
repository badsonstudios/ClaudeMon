namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class IconRendererTests
{
    [Theory]
    [InlineData(0, 34, 139, 34)]     // Green
    [InlineData(30, 34, 139, 34)]    // Green
    [InlineData(59, 34, 139, 34)]    // Green
    [InlineData(60, 204, 163, 0)]    // Yellow
    [InlineData(79, 204, 163, 0)]    // Yellow
    [InlineData(80, 220, 120, 0)]    // Orange
    [InlineData(89, 220, 120, 0)]    // Orange
    [InlineData(90, 200, 30, 30)]    // Red
    [InlineData(95, 200, 30, 30)]    // Red
    [InlineData(100, 200, 30, 30)]   // Red
    public void GetColorForPercentage_ReturnsCorrectColor(double pct, int r, int g, int b)
    {
        var color = IconRenderer.GetColorForPercentage(pct);
        Assert.Equal(Color.FromArgb(r, g, b), color);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void RenderUsageIcon_CreatesValidIcon(double percentage)
    {
        using var icon = IconRenderer.RenderUsageIcon(percentage);
        Assert.NotNull(icon);
        Assert.Equal(16, icon.Width);
        Assert.Equal(16, icon.Height);
    }

    [Fact]
    public void RenderErrorIcon_CreatesValidIcon()
    {
        using var icon = IconRenderer.RenderErrorIcon();
        Assert.NotNull(icon);
        Assert.Equal(16, icon.Width);
        Assert.Equal(16, icon.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void RenderTaskbarImage_CreatesBitmapOfRequestedSize(double percentage)
    {
        using var image = IconRenderer.RenderTaskbarImage(percentage, 52, 40);
        Assert.NotNull(image);
        Assert.Equal(52, image.Width);
        Assert.Equal(40, image.Height);
    }
}
