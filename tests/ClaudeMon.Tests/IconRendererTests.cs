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

    // Severity floor: the API's own severity judgment can only raise urgency, never lower it.
    // Base colours come from GetColorForPercentage (10→green, 70→yellow, 85→orange, 95→red).

    [Theory]
    [InlineData(10, LimitSeverity.Critical, 95)]  // green → red
    [InlineData(85, LimitSeverity.Critical, 95)]  // orange → red
    [InlineData(95, LimitSeverity.Critical, 95)]  // red stays red
    [InlineData(10, LimitSeverity.Warning, 85)]   // green → orange
    [InlineData(70, LimitSeverity.Warning, 85)]   // yellow → orange
    public void ApplySeverityFloor_EscalatesToSeverityColor(
        double basePct, LimitSeverity severity, double expectedPct)
    {
        var result = IconRenderer.ApplySeverityFloor(
            IconRenderer.GetColorForPercentage(basePct), severity);

        Assert.Equal(IconRenderer.GetColorForPercentage(expectedPct), result);
    }

    [Theory]
    [InlineData(LimitSeverity.Warning)]
    [InlineData(LimitSeverity.Normal)]
    [InlineData(LimitSeverity.Unknown)]
    public void ApplySeverityFloor_NeverDowngradesRed(LimitSeverity severity)
    {
        var red = IconRenderer.GetColorForPercentage(95);
        Assert.Equal(red, IconRenderer.ApplySeverityFloor(red, severity));
    }

    [Theory]
    [InlineData(LimitSeverity.Normal)]
    [InlineData(LimitSeverity.Unknown)]
    public void ApplySeverityFloor_NormalAndUnknown_PassThrough(LimitSeverity severity)
    {
        var green = IconRenderer.GetColorForPercentage(10);
        Assert.Equal(green, IconRenderer.ApplySeverityFloor(green, severity));
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

    [Theory]
    [InlineData(false, 255, 255, 255)] // dark taskbar → light text
    [InlineData(true, 0, 0, 0)]        // light taskbar → dark text
    public void GetTextColor_MatchTaskbar_ContrastsWithTaskbarTheme(bool lightTaskbar, int r, int g, int b)
    {
        // Percentage is irrelevant — MatchTaskbar follows the theme, not the usage level.
        var color = IconRenderer.GetTextColor(TaskbarTextColor.MatchTaskbar, 95, lightTaskbar);
        Assert.Equal(Color.FromArgb(r, g, b).ToArgb(), color.ToArgb());
    }

    [Theory]
    [InlineData(TaskbarTextColor.White)]
    [InlineData(TaskbarTextColor.Black)]
    [InlineData(TaskbarTextColor.LightGray)]
    [InlineData(TaskbarTextColor.DarkGray)]
    [InlineData(TaskbarTextColor.Auto)]
    public void GetTextColor_OtherPresets_IgnoreTaskbarTheme(TaskbarTextColor preset)
    {
        Assert.Equal(
            IconRenderer.GetTextColor(preset, 95, lightTaskbar: false).ToArgb(),
            IconRenderer.GetTextColor(preset, 95, lightTaskbar: true).ToArgb());
    }

    // Composes the dot-joined segment row the overlay builds for the given elements.
    private static IconRenderer.TaskbarSegment[] Segments(params (string Text, Color Color)[] elements) =>
        IconRenderer.JoinSegments(elements.Select(e => new IconRenderer.TaskbarSegment(e.Text, e.Color)).ToArray());

    [Theory]
    [InlineData(42.0, "42")]
    [InlineData(42.9, "42")]   // truncated, not rounded — matches the tray icon
    [InlineData(0.0, "0")]
    [InlineData(100.0, "100")]
    public void TaskbarSegmentPercent_Default_BareNumber(double pct, string expected)
    {
        Assert.Equal(expected, IconRenderer.TaskbarSegment.Percent(pct, Color.White).Text);
    }

    [Theory]
    [InlineData(42.0, "42%")]
    [InlineData(42.9, "42%")]  // truncation is preserved with the sign
    [InlineData(0.0, "0%")]
    [InlineData(100.0, "100%")]
    public void TaskbarSegmentPercent_WithSign_AppendsPercent(double pct, string expected)
    {
        Assert.Equal(expected, IconRenderer.TaskbarSegment.Percent(pct, Color.White, percentSign: true).Text);
    }

    [Fact]
    public void MeasureTaskbarSegmentsWidth_PercentSign_WidensTheRow()
    {
        // The measured width must track the extra % glyphs, or the readout would clip.
        var bare = IconRenderer.MeasureTaskbarSegmentsWidth(
            Segments(("100", Color.White), ("100", Color.White)), 40);
        var signed = IconRenderer.MeasureTaskbarSegmentsWidth(
            Segments(("100%", Color.White), ("100%", Color.White)), 40);
        Assert.True(signed > bare, $"signed {signed} should exceed bare {bare}");
    }

    [Fact]
    public void MeasureTaskbarSegmentsWidth_SingleNumber_IsAtLeastMinimum()
    {
        var width = IconRenderer.MeasureTaskbarSegmentsWidth(Segments(("42", Color.White)), 40);
        Assert.True(width >= IconRenderer.MinTaskbarWidth, $"expected >= {IconRenderer.MinTaskbarWidth}, got {width}");
    }

    [Theory]
    [InlineData("42", "18")]
    [InlineData("100", "100")]   // widest dual case: two 3-digit numbers
    public void MeasureTaskbarSegmentsWidth_MoreSegments_AreWider(string five, string seven)
    {
        var single = IconRenderer.MeasureTaskbarSegmentsWidth(Segments((five, Color.White)), 40);
        var dual = IconRenderer.MeasureTaskbarSegmentsWidth(
            Segments((five, Color.White), (seven, Color.White)), 40);
        var triple = IconRenderer.MeasureTaskbarSegmentsWidth(
            Segments((five, Color.White), (seven, Color.White), ("1h 23m", Color.White)), 40);
        Assert.True(dual > single, $"dual {dual} should exceed single {single}");
        Assert.True(triple > dual, $"triple {triple} should exceed dual {dual}");
    }

    [Theory]
    [InlineData(42, 18)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void DrawTaskbarSegments_DualWithCountdown_RendersWithoutError(double five, double seven)
    {
        // Mirrors the overlay: each number coloured for its own level, countdown neutral.
        var segments = Segments(
            (((int)five).ToString(), IconRenderer.GetTextColor(TaskbarTextColor.Auto, five)),
            (((int)seven).ToString(), IconRenderer.GetTextColor(TaskbarTextColor.Auto, seven)),
            ("1h 23m", IconRenderer.GetTextColor(TaskbarTextColor.White, five)));
        var width = IconRenderer.MeasureTaskbarSegmentsWidth(segments, 40);
        using var bitmap = new Bitmap(width, 40);
        using var graphics = Graphics.FromImage(bitmap);

        IconRenderer.DrawTaskbarSegments(
            graphics, new Rectangle(0, 0, width, 40),
            IconRenderer.GetTextColor(TaskbarTextColor.White, five), segments);

        Assert.Equal(width, bitmap.Width);
        Assert.Equal(40, bitmap.Height);
    }

    [Fact]
    public void JoinSegments_InterleavesSeparators()
    {
        var joined = Segments(("42", Color.White), ("18", Color.White), ("1h 5m", Color.White));
        Assert.Equal(5, joined.Length);
        Assert.Equal("42", joined[0].Text);
        Assert.Equal(IconRenderer.TaskbarSegment.Separator.Text, joined[1].Text);
        Assert.Equal("18", joined[2].Text);
        Assert.Equal(IconRenderer.TaskbarSegment.Separator.Text, joined[3].Text);
        Assert.Equal("1h 5m", joined[4].Text);
    }

    [Theory]
    [InlineData(83, "1h 23m")]     // hours + minutes
    [InlineData(120, "2h 0m")]     // exact hours keep the minutes for a stable shape
    [InlineData(45, "45m")]        // under an hour drops the hour part
    [InlineData(0.5, "1m")]        // sub-minute remainders round up, never "0m"
    [InlineData(59.5, "1h 0m")]    // ceiling can roll into the next hour
    [InlineData(0, "idle")]        // due: window over, no new one started yet
    [InlineData(-5, "idle")]       // past due: expired-and-idle, not a perpetual "now"
    public void FormatTaskbarCountdown_FormatsCompactly(double minutes, string expected)
    {
        Assert.Equal(expected, IconRenderer.FormatTaskbarCountdown(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void FormatTaskbarCountdown_UnknownReset_ShowsNeutralMarker()
    {
        Assert.Equal("—", IconRenderer.FormatTaskbarCountdown(null));
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
    public void MeasureTaskbarWaitingWidth_IsAtLeastMinimum(int height)
    {
        var width = IconRenderer.MeasureTaskbarWaitingWidth(height);
        Assert.True(width >= IconRenderer.MinTaskbarWidth, $"expected >= {IconRenderer.MinTaskbarWidth}, got {width}");
    }

    [Fact]
    public void DrawTaskbarWaiting_RendersVisibleMarker()
    {
        var width = IconRenderer.MeasureTaskbarWaitingWidth(40);
        using var bitmap = new Bitmap(width, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            IconRenderer.DrawTaskbarWaiting(
                graphics, new Rectangle(0, 0, width, 40),
                IconRenderer.GetTextColor(TaskbarTextColor.White, 0));
        }

        // The label + waiting marker should leave some non-transparent pixels.
        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) { painted = true; break; }

        Assert.True(painted, "expected the waiting marker to render visible pixels");
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
            var bars = new List<IconRenderer.TaskbarBarSpec> { IconRenderer.TaskbarBarSpec.FiveHour(62, 0.4) };
            if (dual)
                bars.Add(IconRenderer.TaskbarBarSpec.SevenDay(30, 0.5));
            IconRenderer.DrawTaskbarBar(
                graphics, new Rectangle(0, 0, width, 40), bars, UsageColorMode.Pace);
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
                new[] { IconRenderer.TaskbarBarSpec.FiveHour(62, 0.4) },
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
                new[] { IconRenderer.TaskbarBarSpec.FiveHour(0, null) },
                UsageColorMode.Pace);
        }

        var painted = false;
        for (var x = 0; x < bitmap.Width && !painted; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) { painted = true; break; }

        Assert.True(painted, "expected the empty bar track to still render");
    }
}
