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

    [Theory]
    [InlineData(30)]   // Win10 taskbar height
    [InlineData(40)]   // Win11 taskbar height
    [InlineData(72)]   // ~150% DPI
    public void MeasureTaskbarClockReserve_ReservesPositiveWidth(int height)
    {
        // A zero/negative reserve would let the readout draw on top of the secondary-taskbar
        // clock — the exact overlap this measurement exists to prevent.
        var reserve = IconRenderer.MeasureTaskbarClockReserve(height);
        Assert.True(reserve > 0, $"expected a positive clock reserve, got {reserve}");
    }

    [Fact]
    public void MeasureTaskbarClockReserve_DoesNotShrinkWithTallerTaskbar()
    {
        // Both the padding and the font scale with height, so a taller (higher-DPI) taskbar
        // must reserve at least as much clock space as a shorter one.
        Assert.True(
            IconRenderer.MeasureTaskbarClockReserve(48) >= IconRenderer.MeasureTaskbarClockReserve(30),
            "a taller taskbar should not reserve less clock space than a shorter one");
    }

    [Theory]
    [InlineData(30, TaskbarBarWidth.Compact)]
    [InlineData(40, TaskbarBarWidth.Standard)]
    [InlineData(40, TaskbarBarWidth.Wide)]
    [InlineData(72, TaskbarBarWidth.ExtraWide)]
    public void MeasureTaskbarBarWidth_IsAtLeastMinimum(int height, TaskbarBarWidth width)
    {
        var w = IconRenderer.MeasureTaskbarBarWidth(height, width);
        Assert.True(w >= IconRenderer.MinTaskbarWidth, $"expected >= {IconRenderer.MinTaskbarWidth}, got {w}");
    }

    [Fact]
    public void MeasureTaskbarBarWidth_GrowsWithWiderSetting()
    {
        // Each step up the width setting must give at least as much room (strictly more once past
        // the MinTaskbarWidth floor) so "Wide"/"Extra wide" actually widen the bar.
        var compact = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Compact);
        var standard = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Standard);
        var wide = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Wide);
        var extra = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.ExtraWide);

        Assert.True(standard >= compact);
        Assert.True(wide > standard, $"wide {wide} should exceed standard {standard}");
        Assert.True(extra > wide, $"extra-wide {extra} should exceed wide {wide}");
    }

    [Theory]
    [InlineData(false)]  // single 5-hour bar
    [InlineData(true)]   // stacked 5-hour + 7-day bars
    public void DrawTaskbarBar_RendersVisiblePixels(bool dual)
    {
        var width = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Standard);
        using var bitmap = new Bitmap(width, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            IconRenderer.DrawTaskbarBar(
                graphics, new Rectangle(0, 0, width, 40),
                fiveHourPct: 62, fiveHourFraction: 0.4,
                sevenDayPct: dual ? 30 : null, sevenDayFraction: dual ? 0.5 : null,
                UsageColorMode.Pace);
        }

        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) { painted = true; break; }

        Assert.True(painted, "expected the bar to render visible pixels");
    }

    [Fact]
    public void DrawTaskbarBar_LightTaskbar_ChangesTickRendering()
    {
        // The time tick's halo flips colour by taskbar theme (dark core on dark taskbars, dark
        // core/light halo on light ones) so it reads on both — so the two renders must differ.
        Bitmap Render(bool lightTaskbar)
        {
            var width = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Standard);
            var bmp = new Bitmap(width, 40, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            IconRenderer.DrawTaskbarBar(
                g, new Rectangle(0, 0, width, 40),
                fiveHourPct: 62, fiveHourFraction: 0.4,
                sevenDayPct: null, sevenDayFraction: null,
                UsageColorMode.Pace, lightTaskbar);
            return bmp;
        }

        using var dark = Render(false);
        using var light = Render(true);

        var differs = false;
        for (var x = 0; x < dark.Width && !differs; x++)
            for (var y = 0; y < dark.Height; y++)
                if (dark.GetPixel(x, y) != light.GetPixel(x, y)) { differs = true; break; }

        Assert.True(differs, "expected the tick to render differently on a light taskbar");
    }

    [Fact]
    public void DrawTaskbarBar_RendersAtZeroUsageWithUnknownWindow()
    {
        // The empty track must still draw (a visible container) even with no usage and no reset
        // time — the bar should never vanish to nothing.
        var width = IconRenderer.MeasureTaskbarBarWidth(40, TaskbarBarWidth.Standard);
        using var bitmap = new Bitmap(width, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            IconRenderer.DrawTaskbarBar(
                graphics, new Rectangle(0, 0, width, 40),
                fiveHourPct: 0, fiveHourFraction: null,
                sevenDayPct: null, sevenDayFraction: null,
                UsageColorMode.Pace);
        }

        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) { painted = true; break; }

        Assert.True(painted, "expected the empty bar track to still render");
    }
}
