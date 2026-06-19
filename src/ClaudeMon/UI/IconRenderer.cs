namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeMon.Models;

public static class IconRenderer
{
    private static readonly Color GreenColor = Color.FromArgb(34, 139, 34);
    private static readonly Color YellowColor = Color.FromArgb(204, 163, 0);
    private static readonly Color OrangeColor = Color.FromArgb(220, 120, 0);
    private static readonly Color RedColor = Color.FromArgb(200, 30, 30);
    private static readonly Color GrayColor = Color.FromArgb(100, 100, 100);

    public static Color GetColorForPercentage(double percentage) => percentage switch
    {
        < 60 => GreenColor,
        < 80 => YellowColor,
        < 90 => OrangeColor,
        _ => RedColor,
    };

    public static Icon RenderUsageIcon(double percentage)
    {
        var bitmap = new Bitmap(16, 16);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            var bgColor = GetColorForPercentage(percentage);
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

    /// <summary>Minimum overlay width — keeps single-number mode pixel-identical to before.</summary>
    public const int MinTaskbarWidth = 52;
    private const int TaskbarWidthPadding = 6;

    // Truncate (not round) so the taskbar number matches the tray icon exactly.
    private static string FormatPct(double percentage) => ((int)percentage).ToString();

    // Scale the fonts to the available height (Win11 taskbar ≈ 40px, Win10 ≈ 30px).
    // Shared by the draw and measure paths so they always agree.
    private static (float Label, float Number) TaskbarFontSizes(int height) =>
        (Math.Clamp(height * 0.18f, 6f, 8f), Math.Clamp(height * 0.30f, 9f, 13f));

    private static (string Text, Color Color)[] NumberSegments(
        double fiveHourPct, double? sevenDayPct, Color fiveHourColor, Color sevenDayColor, Color separatorColor)
        => sevenDayPct is null
            ? new[] { (FormatPct(fiveHourPct), fiveHourColor) }
            : new[]
            {
                (FormatPct(fiveHourPct), fiveHourColor),
                (" / ", separatorColor),
                (FormatPct(sevenDayPct.Value), sevenDayColor),
            };

    /// <summary>
    /// Draws the taskbar usage readout — a small "Claude" label on top and a larger
    /// percentage number below it. The caller supplies the resolved colours
    /// (see <see cref="GetTextColor"/>). No background is filled; the host window
    /// supplies transparency.
    /// </summary>
    public static void DrawTaskbarUsage(
        Graphics graphics, double percentage, Rectangle bounds, Color labelColor, Color numberColor)
        => DrawTaskbarRows(graphics, bounds, labelColor,
            NumberSegments(percentage, null, numberColor, numberColor, labelColor));

    /// <summary>
    /// Draws the dual taskbar readout — the 5-hour and 7-day numbers separated by a
    /// slash (<c>5hr / 7day</c>). Each number gets its own resolved colour; the
    /// separator uses the label colour as a neutral element.
    /// </summary>
    public static void DrawTaskbarUsage(
        Graphics graphics, double fiveHourPct, double sevenDayPct, Rectangle bounds,
        Color labelColor, Color fiveHourColor, Color sevenDayColor)
        => DrawTaskbarRows(graphics, bounds, labelColor,
            NumberSegments(fiveHourPct, sevenDayPct, fiveHourColor, sevenDayColor, labelColor));

    /// <summary>
    /// Renders the "Claude" label row and a number row composed of coloured segments,
    /// both horizontally centred and vertically centred as a block within
    /// <paramref name="bounds"/>.
    /// </summary>
    private static void DrawTaskbarRows(
        Graphics graphics, Rectangle bounds, Color labelColor,
        (string Text, Color Color)[] numberSegments)
    {
        // Grayscale AA (not ClearType) so the glyph alpha is correct on a transparent
        // bitmap — ClearType needs a known opaque background and fringes otherwise.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (labelSize, numberSize) = TaskbarFontSizes(bounds.Height);
        using var labelFont = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Point);
        using var numberFont = new Font("Segoe UI", numberSize, FontStyle.Bold, GraphicsUnit.Point);
        using var labelBrush = new SolidBrush(labelColor);

        var labelMeasure = graphics.MeasureString(TaskbarLabel, labelFont);

        float numberWidth = 0f, numberHeight = 0f;
        foreach (var seg in numberSegments)
        {
            var m = graphics.MeasureString(seg.Text, numberFont);
            numberWidth += m.Width;
            numberHeight = Math.Max(numberHeight, m.Height);
        }

        // Stack the two rows and centre the block vertically within the bounds.
        var totalHeight = labelMeasure.Height + numberHeight;
        var top = bounds.Y + Math.Max(0, (bounds.Height - totalHeight) / 2);

        var labelX = bounds.X + (bounds.Width - labelMeasure.Width) / 2;
        graphics.DrawString(TaskbarLabel, labelFont, labelBrush, labelX, top);

        var numberX = bounds.X + (bounds.Width - numberWidth) / 2;
        var numberY = top + labelMeasure.Height;
        foreach (var seg in numberSegments)
        {
            using var brush = new SolidBrush(seg.Color);
            graphics.DrawString(seg.Text, numberFont, brush, numberX, numberY);
            numberX += graphics.MeasureString(seg.Text, numberFont).Width;
        }
    }

    /// <summary>
    /// Measures the overlay width needed to show the readout (single or dual) at the
    /// given taskbar height without clipping, never below <see cref="MinTaskbarWidth"/>.
    /// Sums per-segment widths to match how <see cref="DrawTaskbarRows"/> lays them out.
    /// </summary>
    public static int MeasureTaskbarUsageWidth(double fiveHourPct, double? sevenDayPct, int height)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (labelSize, numberSize) = TaskbarFontSizes(height);
        using var labelFont = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Point);
        using var numberFont = new Font("Segoe UI", numberSize, FontStyle.Bold, GraphicsUnit.Point);

        var labelWidth = graphics.MeasureString(TaskbarLabel, labelFont).Width;

        var dummy = Color.White;
        float numberWidth = 0f;
        foreach (var seg in NumberSegments(fiveHourPct, sevenDayPct, dummy, dummy, dummy))
            numberWidth += graphics.MeasureString(seg.Text, numberFont).Width;

        var content = Math.Max(labelWidth, numberWidth);
        return Math.Max(MinTaskbarWidth, (int)Math.Ceiling(content) + TaskbarWidthPadding);
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
