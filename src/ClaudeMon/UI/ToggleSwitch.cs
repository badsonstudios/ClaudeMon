namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// A modern sliding on/off switch with full <see cref="CheckBox"/> semantics (<c>Checked</c>,
/// <c>CheckedChanged</c>, click-to-toggle), painted for the dark theme. Used in place of the
/// stock checkbox for boolean settings. No text — the row's label is drawn separately.
/// </summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor,
            true);
        AutoSize = false;
        Size = new Size(40, 20);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The parent (the themed form) supplies the backdrop the rounded track sits on.
        g.Clear(Parent?.BackColor ?? (theme.IsDark ? Color.FromArgb(32, 32, 32) : Color.White));

        // Scale every inset from the 40x20 design size so the switch keeps its proportions at any
        // DPI — the parent sizes this control per-monitor, but the paint geometry was fixed pixels
        // (so the knob crept larger relative to the track as DPI rose). At Height 20, P() is a no-op.
        var s = Height / 20f;
        int P(int designPx) => Math.Max(1, (int)Math.Round(designPx * s));

        var track = new Rectangle(0, P(1), Width - P(1), Height - P(3));
        var trackColor = !Enabled ? theme.ToggleTrackDisabled
            : Checked ? theme.ToggleTrackOn : theme.ToggleTrackOff;
        using (var b = new SolidBrush(trackColor))
        using (var path = Rounded(track, track.Height / 2))
            g.FillPath(b, path);

        var d = track.Height - P(4);
        var knobX = Checked ? track.Right - d - P(2) : track.Left + P(2);
        var knobY = track.Top + P(2);
        var knobColor = Enabled ? theme.ToggleKnob : theme.ToggleKnobDisabled;
        using (var knob = new SolidBrush(knobColor))
            g.FillEllipse(knob, knobX, knobY, d, d);

        // A faint ring keeps the knob defined (a white knob on a light off-track, especially).
        using var ring = new Pen(Color.FromArgb(40, 0, 0, 0));
        g.DrawEllipse(ring, knobX, knobY, d, d);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0 || r.Width < d)
        {
            p.AddRectangle(r);
            return p;
        }

        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}
