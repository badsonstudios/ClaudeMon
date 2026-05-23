namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

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
