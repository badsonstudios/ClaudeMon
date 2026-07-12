namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public static class IconRenderer
{
    private static readonly Color GreenColor = Color.FromArgb(34, 139, 34);
    private static readonly Color YellowColor = Color.FromArgb(204, 163, 0);
    private static readonly Color OrangeColor = Color.FromArgb(220, 120, 0);
    private static readonly Color RedColor = Color.FromArgb(200, 30, 30);
    private static readonly Color GrayColor = Color.FromArgb(100, 100, 100);

    // Muted grey for the 5-hour/7-day separator dot — softer than white on a dark taskbar.
    private static readonly Color SeparatorDotColor = Color.FromArgb(160, 160, 160);

    public static Color GetColorForPercentage(double percentage) => percentage switch
    {
        < 60 => GreenColor,
        < 80 => YellowColor,
        < 90 => OrangeColor,
        _ => RedColor,
    };

    /// <summary>
    /// Resolves the colour for a usage percentage. In <see cref="UsageColorMode.Pace"/> (when a
    /// window-elapsed fraction is known) it colours by pace — usage relative to time elapsed — so
    /// the same percentage reads differently early vs late in the window. Otherwise it colours by
    /// the absolute level (<see cref="GetColorForPercentage"/>).
    /// </summary>
    public static Color GetUsageColor(double percentage, double? windowFraction, UsageColorMode mode)
    {
        if (mode != UsageColorMode.Pace || windowFraction is null)
            return GetColorForPercentage(percentage);

        // ratio r ⇒ you'd hit 100% at 1/r of the window: ≤1 on/behind pace, >2 exhaust before halfway.
        return Pace.Ratio(percentage, windowFraction.Value) switch
        {
            <= 1.0 => GreenColor,
            <= 1.33 => YellowColor,
            <= 2.0 => OrangeColor,
            _ => RedColor,
        };
    }

    public static Icon RenderUsageIcon(double percentage)
        => RenderUsageIcon(percentage, GetColorForPercentage(percentage));

    public static Icon RenderUsageIcon(double percentage, Color bgColor)
    {
        var bitmap = new Bitmap(16, 16);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            using var bgBrush = new SolidBrush(bgColor);
            graphics.FillRectangle(bgBrush, 0, 0, 16, 16);

            var text = ((int)percentage).ToString();
            using var textBrush = new SolidBrush(Color.White);

            // Use smaller font for 3-digit numbers
            var fontSize = text.Length >= 3 ? 5.5f : 7f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);

            var size = graphics.MeasureString(text, font);
            var x = (16 - size.Width) / 2;
            var y = (16 - size.Height) / 2;
            graphics.DrawString(text, font, textBrush, x, y);

            var hIcon = bitmap.GetHicon();
            var icon = Icon.FromHandle(hIcon);
            // Clone so we can destroy the handle
            var cloned = (Icon)icon.Clone();
            DestroyIcon(hIcon);
            return cloned;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static readonly Color LightGrayColor = Color.FromArgb(200, 200, 200);
    private static readonly Color DarkGrayColor = Color.FromArgb(80, 80, 80);

    /// <summary>
    /// Resolves a <see cref="TaskbarTextColor"/> preset to a concrete colour.
    /// <see cref="TaskbarTextColor.Auto"/> maps to the usage-level threshold colour.
    /// </summary>
    public static Color GetTextColor(TaskbarTextColor preset, double percentage) => preset switch
    {
        TaskbarTextColor.White => Color.White,
        TaskbarTextColor.Black => Color.Black,
        TaskbarTextColor.LightGray => LightGrayColor,
        TaskbarTextColor.DarkGray => DarkGrayColor,
        _ => GetColorForPercentage(percentage),
    };

    private const string TaskbarLabel = "Claude";

    // Monospaced number font so digits line up and the readout width stays stable. Consolas
    // ships on all supported Windows versions.
    private const string NumberFontFamily = "Consolas";

    // Middle dot (U+00B7), no surrounding spaces — the compact 5-hour · 7-day separator.
    private const string NumberSeparatorText = "·";

    /// <summary>
    /// Neutral "no reading" marker shown on the overlay when sign-in has expired.
    /// This is an em dash (U+2014), not a hyphen-minus — keep it as-is.
    /// </summary>
    private const string SignInExpiredMarker = "—";

    /// <summary>
    /// Countdown placeholder when the reset time is unknown. Same em dash as the sign-in
    /// marker today, but a separate name so retuning one doesn't silently change the other.
    /// </summary>
    private const string UnknownCountdownMarker = "—";

    /// <summary>Minimum overlay width — keeps single-number mode pixel-identical to before.</summary>
    public const int MinTaskbarWidth = 52;
    private const int TaskbarWidthPadding = 6;

    // Truncate (not round) so the taskbar number matches the tray icon exactly.
    private static string FormatPct(double percentage) => ((int)percentage).ToString();

    // Scale the fonts to the available height (Win11 taskbar ≈ 40px, Win10 ≈ 30px).
    // Shared by the draw and measure paths so they always agree.
    private static (float Label, float Number) TaskbarFontSizes(int height) =>
        (Math.Clamp(height * 0.18f, 6f, 8f), Math.Clamp(height * 0.30f, 9f, 13f));

    /// <summary>
    /// A number-row element of the taskbar readout. <see cref="Separator"/> renders the tight
    /// dot; everything else renders as text in the given colour. Callers compose the enabled
    /// elements (session %, weekly %, countdown) and interleave separators.
    /// </summary>
    internal readonly record struct TaskbarSegment(string Text, Color Color)
    {
        public static TaskbarSegment Separator => new(NumberSeparatorText, default);

        public static TaskbarSegment Percent(double pct, Color color) => new(FormatPct(pct), color);
    }

    /// <summary>
    /// Interleaves the separator dot between the given elements — the "a · b · c" row shape.
    /// </summary>
    internal static TaskbarSegment[] JoinSegments(IReadOnlyList<TaskbarSegment> elements)
    {
        if (elements.Count <= 1)
            return elements.ToArray();

        var joined = new List<TaskbarSegment>(elements.Count * 2 - 1);
        foreach (var element in elements)
        {
            if (joined.Count > 0)
                joined.Add(TaskbarSegment.Separator);
            joined.Add(element);
        }

        return joined.ToArray();
    }

    /// <summary>
    /// Compact countdown for the time-left-to-reset element: <c>1h 23m</c>, <c>45m</c> under an
    /// hour, <c>now</c> once the reset is due, and the neutral <c>—</c> when the reset time is
    /// unknown (null). Minute-granular so the overlay repaints at most once a minute.
    /// </summary>
    internal static string FormatTaskbarCountdown(TimeSpan? remaining)
    {
        if (remaining is null)
            return UnknownCountdownMarker;

        var r = remaining.Value;
        if (r <= TimeSpan.Zero)
            return "now";

        // Ceiling on minutes so the display doesn't read "0m"/"1h 0m" for most of a minute and
        // agrees with intuition ("59m 30s left" reads as 1h).
        var minutes = (int)Math.Ceiling(r.TotalMinutes);
        var (h, m) = (minutes / 60, minutes % 60);
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    // The separator is drawn as a tight manual dot, not a full monospace cell (which wastes
    // most of its width as whitespace). One source of truth for its size/spacing.
    private static (float Dot, float Gap) SeparatorMetrics(float numberSize)
        => (Math.Max(2f, numberSize * 0.22f), Math.Max(1f, numberSize * 0.14f));

    // Advance contributed by a number-row segment: the tight dot footprint for the separator,
    // the monospace cell width for digits. Keeps the draw and measure paths in agreement.
    private static float SegmentAdvance(Graphics graphics, string text, Font numberFont, float numberSize)
    {
        if (text == NumberSeparatorText)
        {
            var (dot, gap) = SeparatorMetrics(numberSize);
            return gap + dot + gap;
        }

        return graphics.MeasureString(text, numberFont).Width;
    }

    /// <summary>
    /// Draws the taskbar usage readout — a small "Claude" label on top and a larger
    /// percentage number below it. The caller supplies the resolved colours
    /// (see <see cref="GetTextColor"/>). No background is filled; the host window
    /// supplies transparency.
    /// </summary>
    public static void DrawTaskbarUsage(
        Graphics graphics, double percentage, Rectangle bounds, Color labelColor, Color numberColor)
        => DrawTaskbarSegments(graphics, bounds, labelColor,
            new[] { TaskbarSegment.Percent(percentage, numberColor) });

    /// <summary>
    /// Renders the "Claude" label row and a number row composed of the given segments
    /// (see <see cref="JoinSegments"/> for the "a · b · c" composition), both horizontally
    /// centred and vertically centred as a block within <paramref name="bounds"/>.
    /// </summary>
    internal static void DrawTaskbarSegments(
        Graphics graphics, Rectangle bounds, Color labelColor,
        TaskbarSegment[] numberSegments)
    {
        // Grayscale AA (not ClearType) so the glyph alpha is correct on a transparent
        // bitmap — ClearType needs a known opaque background and fringes otherwise.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (labelSize, numberSize) = TaskbarFontSizes(bounds.Height);
        using var labelFont = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Point);
        using var numberFont = new Font(NumberFontFamily, numberSize, FontStyle.Bold, GraphicsUnit.Point);
        using var labelBrush = new SolidBrush(labelColor);

        var labelMeasure = graphics.MeasureString(TaskbarLabel, labelFont);

        float numberWidth = 0f, numberHeight = 0f;
        foreach (var seg in numberSegments)
        {
            numberWidth += SegmentAdvance(graphics, seg.Text, numberFont, numberSize);
            numberHeight = Math.Max(numberHeight, graphics.MeasureString(seg.Text, numberFont).Height);
        }

        // Stack the two rows and centre the block vertically within the bounds.
        var totalHeight = labelMeasure.Height + numberHeight;
        var top = bounds.Y + Math.Max(0, (bounds.Height - totalHeight) / 2);

        var labelX = bounds.X + (bounds.Width - labelMeasure.Width) / 2;
        graphics.DrawString(TaskbarLabel, labelFont, labelBrush, labelX, top);

        var numberX = bounds.X + (bounds.Width - numberWidth) / 2f;
        var numberY = top + labelMeasure.Height;
        foreach (var seg in numberSegments)
        {
            using var brush = new SolidBrush(seg.Color);
            if (seg.Text == NumberSeparatorText)
            {
                // Tight, vertically-centred dot in a muted grey (white is too harsh on a dark taskbar).
                var (dot, gap) = SeparatorMetrics(numberSize);
                using var dotBrush = new SolidBrush(SeparatorDotColor);
                graphics.FillEllipse(dotBrush, numberX + gap, numberY + (numberHeight - dot) / 2f, dot, dot);
                numberX += gap + dot + gap;
            }
            else
            {
                graphics.DrawString(seg.Text, numberFont, brush, numberX, numberY);
                numberX += graphics.MeasureString(seg.Text, numberFont).Width;
            }
        }
    }

    /// <summary>
    /// Measures the overlay width needed to show the given number-row segments at the given
    /// taskbar height without clipping: the wider of the "Claude" label and the summed
    /// segments, padded, never below <see cref="MinTaskbarWidth"/>. Sums per-segment widths to
    /// match how <see cref="DrawTaskbarSegments"/> lays them out; colours are irrelevant.
    /// </summary>
    internal static int MeasureTaskbarSegmentsWidth(TaskbarSegment[] numberSegments, int height)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (labelSize, numberSize) = TaskbarFontSizes(height);
        using var labelFont = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Point);
        using var numberFont = new Font(NumberFontFamily, numberSize, FontStyle.Bold, GraphicsUnit.Point);

        var labelWidth = graphics.MeasureString(TaskbarLabel, labelFont).Width;

        float numberWidth = 0f;
        foreach (var seg in numberSegments)
            numberWidth += SegmentAdvance(graphics, seg.Text, numberFont, numberSize);

        var content = Math.Max(labelWidth, numberWidth);
        return Math.Max(MinTaskbarWidth, (int)Math.Ceiling(content) + TaskbarWidthPadding);
    }

    /// <summary>
    /// Draws the sign-in-expired marker on the taskbar overlay — the "Claude" label with a
    /// neutral "—" where the percentage would be, so an expired session never leaves a stale
    /// number on the taskbar. The tray icon and tooltip carry the actionable detail.
    /// </summary>
    public static void DrawTaskbarSignInExpired(Graphics graphics, Rectangle bounds, Color labelColor)
        => DrawTaskbarSegments(graphics, bounds, labelColor,
            new[] { new TaskbarSegment(SignInExpiredMarker, labelColor) });

    /// <summary>Overlay width for the sign-in-expired marker at the given taskbar height.</summary>
    public static int MeasureTaskbarSignInExpiredWidth(int height)
        => MeasureTaskbarSegmentsWidth(new[] { new TaskbarSegment(SignInExpiredMarker, Color.White) }, height);

    // --- Bar style ---

    // Track colour for the empty part of a bar — a subtle dark pill so the bar reads as a
    // container even at 0% (matches the flyout's bar background tone).
    private static readonly Color BarTrackColor = Color.FromArgb(60, 60, 60);

    // Faint hour/day dividers; semi-transparent so they read on both the coloured fill and the
    // dark track (same treatment as the flyout bars).
    private static readonly Color BarDividerColor = Color.FromArgb(90, 0, 0, 0);

    // The time-in-window tick ("now") is drawn as a core line over a contrasting casing (a halo)
    // so it reads on any taskbar: on a dark taskbar a light core with a dark halo; on a light
    // taskbar a dark core with a light halo (white washes out on a light taskbar — see the
    // SystemTheme check in the overlay).
    private static readonly Color BarTickCoreDark = Color.FromArgb(245, 245, 245);   // for dark taskbars
    private static readonly Color BarTickCasingDark = Color.FromArgb(25, 25, 25);
    private static readonly Color BarTickCoreLight = Color.FromArgb(30, 30, 30);     // for light taskbars
    private static readonly Color BarTickCasingLight = Color.FromArgb(245, 245, 245);

    // Equal divisions per bar: hours on the 5-hour bar, days on the 7-day bar.
    private const int FiveHourSegments = 5;
    private const int SevenDaySegments = 7;

    // The drawable bar track width: scaled to the taskbar height (DPI) by a per-width-setting
    // factor and clamped to a sane range. Wider settings give the dividers/tick more room. The
    // overlay pads around it (see MeasureTaskbarBarWidth).
    private static int BarTrackWidth(int height, TaskbarBarWidth width)
    {
        var factor = width switch
        {
            TaskbarBarWidth.Compact => 1.2f,
            TaskbarBarWidth.Wide => 2.6f,
            TaskbarBarWidth.ExtraWide => 3.6f,
            _ => 1.8f, // Standard
        };
        return Math.Clamp((int)Math.Round(height * factor), 40, 220);
    }

    /// <summary>
    /// Overlay width for the bar style at the given taskbar height and width setting. Unlike the
    /// number style the bar's width is fixed by the setting (the 5-hour and 7-day bars stack
    /// vertically rather than widening with the value), so this depends only on height + width.
    /// Never below <see cref="MinTaskbarWidth"/>.
    /// </summary>
    public static int MeasureTaskbarBarWidth(int height, TaskbarBarWidth width)
        => Math.Max(MinTaskbarWidth, BarTrackWidth(height, width) + TaskbarWidthPadding);

    /// <summary>
    /// One bar of the bar-style readout: its usage, window-elapsed fraction (drives the tick),
    /// and division count (hours on the session bar, days on the weekly one).
    /// </summary>
    internal readonly record struct TaskbarBarSpec(double Pct, double? Fraction, int Segments)
    {
        public static TaskbarBarSpec FiveHour(double pct, double? fraction) =>
            new(pct, fraction, FiveHourSegments);

        public static TaskbarBarSpec SevenDay(double pct, double? fraction) =>
            new(pct, fraction, SevenDaySegments);
    }

    /// <summary>
    /// Draws the bar-style readout: compact horizontal usage bars with faint hour/day dividers
    /// and a bright time-in-window tick, pace-coloured via <see cref="GetUsageColor"/>. One bar
    /// draws as a single, taller centred bar; two draw as thinner bars stacked as a
    /// vertically-centred block (session over weekly). No background is filled — the host
    /// window supplies transparency.
    /// </summary>
    internal static void DrawTaskbarBar(
        Graphics graphics, Rectangle bounds, IReadOnlyList<TaskbarBarSpec> bars,
        UsageColorMode colorMode, bool lightTaskbar = false)
    {
        if (bars.Count == 0)
            return;

        // Rounded fill/track corners and a crisp tick need anti-aliasing.
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var inset = TaskbarWidthPadding / 2;
        var trackWidth = Math.Max(1, bounds.Width - TaskbarWidthPadding);
        var x = bounds.X + inset;

        if (bars.Count == 1)
        {
            var barHeight = Math.Clamp((int)Math.Round(bounds.Height * 0.26f), 6, 14);
            var y = bounds.Y + (bounds.Height - barHeight) / 2;
            DrawSingleBar(graphics, x, y, trackWidth, barHeight,
                bars[0].Pct, bars[0].Fraction, bars[0].Segments, colorMode, lightTaskbar);
            return;
        }

        // Stacked: thinner bars as a vertically-centred block.
        var thinHeight = Math.Clamp((int)Math.Round(bounds.Height * 0.18f), 4, 10);
        var gap = Math.Clamp((int)Math.Round(bounds.Height * 0.12f), 3, 8);
        var blockHeight = thinHeight * bars.Count + gap * (bars.Count - 1);
        var top = bounds.Y + (bounds.Height - blockHeight) / 2;

        for (var i = 0; i < bars.Count; i++)
        {
            DrawSingleBar(graphics, x, top + i * (thinHeight + gap), trackWidth, thinHeight,
                bars[i].Pct, bars[i].Fraction, bars[i].Segments, colorMode, lightTaskbar);
        }
    }

    private static void DrawSingleBar(
        Graphics graphics, int x, int y, int trackWidth, int barHeight,
        double pct, double? windowFraction, int segments, UsageColorMode colorMode, bool lightTaskbar)
    {
        var geo = TaskbarBarLayout.Compute(trackWidth, pct, windowFraction, segments);
        var radius = barHeight / 2;

        // Empty track pill.
        var trackRect = new Rectangle(x, y, trackWidth, barHeight);
        using (var trackBrush = new SolidBrush(BarTrackColor))
        using (var trackPath = CreateRoundedRect(trackRect, radius))
            graphics.FillPath(trackBrush, trackPath);

        // Coloured fill up to the usage level.
        if (geo.FillWidth > 0)
        {
            var fillRect = new Rectangle(x, y, geo.FillWidth, barHeight);
            using var fillBrush = new SolidBrush(GetUsageColor(pct, windowFraction, colorMode));
            using var fillPath = CreateRoundedRect(fillRect, radius);
            graphics.FillPath(fillBrush, fillPath);
        }

        // Hour/day dividers turn the bar into a time ruler.
        using (var dividerPen = new Pen(BarDividerColor))
            foreach (var dx in geo.DividerXs)
                graphics.DrawLine(dividerPen, x + dx, y, x + dx, y + barHeight);

        // Time-in-window tick: where "now" sits. Fill past it ⇒ burning faster than the clock.
        // Drawn as a core line over a wider contrasting casing (a halo) so it reads on both dark
        // and light taskbars — a plain white tick washes out on a light taskbar.
        if (geo.TickX is { } tickX)
        {
            var overhang = Math.Max(2, barHeight / 3);
            var top = y - overhang;
            var bottom = y + barHeight + overhang;
            var tx = x + tickX;
            var coreWidth = Math.Max(1.5f, barHeight / 6f);

            var core = lightTaskbar ? BarTickCoreLight : BarTickCoreDark;
            var casing = lightTaskbar ? BarTickCasingLight : BarTickCasingDark;

            using (var casingPen = new Pen(casing, coreWidth + 2f))
                graphics.DrawLine(casingPen, tx, top, tx, bottom);
            using (var corePen = new Pen(core, coreWidth))
                graphics.DrawLine(corePen, tx, top, tx, bottom);
        }
    }

    /// <summary>Builds a rounded-rectangle path, falling back to a plain rect when too small to round.</summary>
    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        if (radius <= 0 || bounds.Width < diameter || bounds.Height < diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Estimates the horizontal space the Windows clock occupies at the right end of a
    /// secondary-monitor taskbar, so the usage readout can sit just to its left. Windows 11
    /// draws that clock inside a windowless XAML surface, so its bounds can't be queried;
    /// instead we measure worst-case short date/time strings (the same patterns the taskbar
    /// shows) at the taskbar's font size and add padding for the clock's own margins. The
    /// samples are fixed (not the live time) so the reserved width doesn't jitter each
    /// minute, and we err slightly wide — a small gap is fine, overlap is not.
    /// </summary>
    public static int MeasureTaskbarClockReserve(int height)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (_, numberSize) = TaskbarFontSizes(height);
        using var font = new Font("Segoe UI", numberSize, FontStyle.Regular, GraphicsUnit.Point);

        // A representative two-line clock measured with the current culture's short date and
        // short time patterns (the same the taskbar shows). A fixed wide-ish sample, not the
        // live time, so the reserve is stable; width still varies by culture, which is fine.
        var culture = CultureInfo.CurrentCulture;
        var sample = new DateTime(2000, 12, 28, 22, 38, 0);
        var date = sample.ToString("d", culture);
        var time = sample.ToString("t", culture);
        var widest = Math.Max(
            graphics.MeasureString(date, font).Width,
            graphics.MeasureString(time, font).Width);

        // Padding covers the clock's left/right margins within the tray and keeps a small
        // visible gap between the readout and the clock. Scales with the taskbar height (DPI).
        // The worst-case sample above already over-estimates the real text a little, so this
        // stays modest; the user can fine-tune with the horizontal-offset setting.
        var padding = (int)Math.Round(height * 0.4);
        return (int)Math.Ceiling(widest) + padding;
    }

    /// <summary>
    /// Renders the taskbar usage readout onto a transparent bitmap of the given size,
    /// using the default colours (white label, usage-level number). Primarily a testable
    /// wrapper around <see cref="DrawTaskbarUsage"/>.
    /// </summary>
    public static Bitmap RenderTaskbarImage(double percentage, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        // Mirrors the default presets: White label + Auto (usage-level) number.
        DrawTaskbarUsage(
            graphics, percentage, new Rectangle(0, 0, width, height),
            GetTextColor(TaskbarTextColor.White, percentage),
            GetTextColor(TaskbarTextColor.Auto, percentage));
        return bitmap;
    }

    public static Icon RenderErrorIcon()
    {
        var bitmap = new Bitmap(16, 16);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            using var bgBrush = new SolidBrush(GrayColor);
            graphics.FillRectangle(bgBrush, 0, 0, 16, 16);

            using var textBrush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);

            var text = "?";
            var size = graphics.MeasureString(text, font);
            var x = (16 - size.Width) / 2;
            var y = (16 - size.Height) / 2;
            graphics.DrawString(text, font, textBrush, x, y);

            var hIcon = bitmap.GetHicon();
            var icon = Icon.FromHandle(hIcon);
            var cloned = (Icon)icon.Clone();
            DestroyIcon(hIcon);
            return cloned;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
