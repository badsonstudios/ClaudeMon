namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// A <see cref="NumericUpDown"/> that themes its field and spin buttons from the palette in both
/// light and dark. We can't rely on the app-wide colour mode here: it leaves a dark field's spin
/// buttons light, and (worse) leaves the field itself dark in light mode — so we set the colours
/// explicitly and paint our own arrows.
/// </summary>
public sealed class ThemedNumericUpDown : NumericUpDown
{
    public ThemedNumericUpDown()
    {
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var theme = Theme.Current;
        BackColor = theme.FieldBack;
        ForeColor = theme.FieldText;

        // UpDownBase hosts an edit TextBox and an internal "UpDownButtons" spinner. Recolour both,
        // and find the spinner by type (not by child index) so a future WinForms internals change
        // degrades to "unthemed arrows" rather than theming the wrong child.
        Control? spinner = null;
        foreach (Control child in Controls)
        {
            child.BackColor = theme.FieldBack;
            child.ForeColor = theme.FieldText;
            if (child.GetType().Name == "UpDownButtons")
                spinner = child;
        }

        if (spinner is not null)
        {
            spinner.BackColor = theme.SpinButtonBack;
            // PaintSpinner is static, so the subscription doesn't root this control — the spinner
            // child shares our lifetime and is disposed with us, so no unsubscribe is needed.
            spinner.Paint += PaintSpinner;
        }
    }

    private static void PaintSpinner(object? sender, PaintEventArgs e)
    {
        var theme = Theme.Current;
        var c = (Control)sender!;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.SpinButtonBack);

        var cx = c.Width / 2f;
        var midY = c.Height / 2f;
        const float halfW = 3.5f;
        const float height = 3f;

        using var pen = new Pen(theme.SpinArrow, 1.4f);
        // Up arrow in the top half, down arrow in the bottom half.
        g.DrawLines(pen, [
            new PointF(cx - halfW, midY - 4),
            new PointF(cx, midY - 4 - height),
            new PointF(cx + halfW, midY - 4),
        ]);
        g.DrawLines(pen, [
            new PointF(cx - halfW, midY + 4),
            new PointF(cx, midY + 4 + height),
            new PointF(cx + halfW, midY + 4),
        ]);

        using var separator = new Pen(theme.SpinSeparator);
        g.DrawLine(separator, 0, 2, 0, c.Height - 2);
    }
}
