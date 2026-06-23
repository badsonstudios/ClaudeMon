namespace ClaudeMon.Tests;

using System.Drawing;
using System.Drawing.Imaging;
using ClaudeMon.Models;
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

    [Theory]
    [InlineData(TaskbarTextColor.White, 255, 255, 255)]
    [InlineData(TaskbarTextColor.Black, 0, 0, 0)]
    [InlineData(TaskbarTextColor.LightGray, 200, 200, 200)]
    [InlineData(TaskbarTextColor.DarkGray, 80, 80, 80)]
    public void GetTextColor_Preset_ReturnsFixedColor(TaskbarTextColor preset, int r, int g, int b)
    {
        // Percentage is irrelevant for fixed presets. Compare ARGB so named colours
        // (e.g. Color.White) match their RGB equivalents.
        var color = IconRenderer.GetTextColor(preset, 95);
        Assert.Equal(Color.FromArgb(r, g, b).ToArgb(), color.ToArgb());
    }

    [Theory]
    [InlineData(30)]   // green band
    [InlineData(85)]   // orange band
    [InlineData(95)]   // red band
    public void GetTextColor_Auto_MatchesThresholdColor(double percentage)
    {
        var color = IconRenderer.GetTextColor(TaskbarTextColor.Auto, percentage);
        Assert.Equal(IconRenderer.GetColorForPercentage(percentage), color);
    }

    [Fact]
    public void MeasureTaskbarUsageWidth_SingleNumber_IsAtLeastMinimum()
    {
        var width = IconRenderer.MeasureTaskbarUsageWidth(42, null, 40);
        Assert.True(width >= IconRenderer.MinTaskbarWidth, $"expected >= {IconRenderer.MinTaskbarWidth}, got {width}");
    }

    [Theory]
    [InlineData(42, 18)]
    [InlineData(100, 100)]   // widest case: two 3-digit numbers
    public void MeasureTaskbarUsageWidth_Dual_IsWiderThanSingle(double five, double seven)
    {
        var single = IconRenderer.MeasureTaskbarUsageWidth(five, null, 40);
        var dual = IconRenderer.MeasureTaskbarUsageWidth(five, seven, 40);
        Assert.True(dual > single, $"dual {dual} should exceed single {single}");
    }

    [Theory]
    [InlineData(42, 18)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void DrawTaskbarUsage_Dual_RendersWithoutError(double five, double seven)
    {
        var width = IconRenderer.MeasureTaskbarUsageWidth(five, seven, 40);
        using var bitmap = new Bitmap(width, 40);
        using var graphics = Graphics.FromImage(bitmap);

        // Mirrors the overlay: white label, each number coloured for its own level.
        IconRenderer.DrawTaskbarUsage(
            graphics, five, seven, new Rectangle(0, 0, width, 40),
            IconRenderer.GetTextColor(TaskbarTextColor.White, five),
            IconRenderer.GetTextColor(TaskbarTextColor.Auto, five),
            IconRenderer.GetTextColor(TaskbarTextColor.Auto, seven));

        Assert.Equal(width, bitmap.Width);
        Assert.Equal(40, bitmap.Height);
    }

    [Theory]
    [InlineData(30)]   // Win10 taskbar height
    [InlineData(40)]   // Win11 taskbar height
    public void MeasureTaskbarSignInExpiredWidth_IsAtLeastMinimum(int height)
    {
        var width = IconRenderer.MeasureTaskbarSignInExpiredWidth(height);
        Assert.True(width >= IconRenderer.MinTaskbarWidth, $"expected >= {IconRenderer.MinTaskbarWidth}, got {width}");
    }

    [Fact]
    public void DrawTaskbarSignInExpired_RendersVisibleMarker()
    {
        var width = IconRenderer.MeasureTaskbarSignInExpiredWidth(40);
        using var bitmap = new Bitmap(width, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            IconRenderer.DrawTaskbarSignInExpired(
                graphics, new Rectangle(0, 0, width, 40),
                IconRenderer.GetTextColor(TaskbarTextColor.White, 0));
        }

        // The label + marker should leave some non-transparent pixels.
        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) { painted = true; break; }

        Assert.True(painted, "expected the sign-in-expired marker to render visible pixels");
    }
}
