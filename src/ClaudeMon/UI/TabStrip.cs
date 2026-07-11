namespace ClaudeMon.UI;

using System.ComponentModel;
using System.Drawing;

/// <summary>
/// An owner-drawn horizontal row of tab headers for the Settings dialog — the active tab gets an
/// accent underline over a full-width hairline baseline. A custom control (in the vein of
/// <see cref="ToggleSwitch"/>) rather than a stock <see cref="TabControl"/>, which the app-wide
/// dark mode doesn't theme. Selection only — the parent shows/hides its own content per
/// <see cref="SelectedIndex"/>. Clickable, hoverable, and keyboard-accessible (Left/Right arrows
/// when focused). Geometry comes from <see cref="TabStripLayout"/>, scaled by the monitor DPI.
/// </summary>
public sealed class TabStrip : Control
{
    private readonly string[] _tabs;
    private int _selectedIndex;
    private int _hoverIndex = -1;

    // Logical (96-DPI) metrics, scaled by the current DeviceDpi in Sc.
    private const int LabelPadding = 10;   // horizontal padding inside each tab
    private const int TabGap = 6;          // gap between tabs
    private const int UnderlineHeight = 2; // the active tab's accent underline

    public event EventHandler? SelectedIndexChanged;

    public TabStrip(params string[] tabs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tabs.Length);
        _tabs = tabs;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    // Runtime-only control (no designer), so nothing serializes this.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, _tabs.Length);
            if (value == _selectedIndex)
                return;

            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            // Tell assistive tech the active tab changed (the accessible Value below).
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    // Screen readers get the active tab's name as the control's value; the parent supplies
    // AccessibleName for what the tab list is.
    protected override AccessibleObject CreateAccessibilityInstance() => new TabStripAccessibleObject(this);

    private sealed class TabStripAccessibleObject(TabStrip owner) : ControlAccessibleObject(owner)
    {
        public override string Value => owner._tabs[owner._selectedIndex];
    }

    private int Sc(int logical) => DpiScale.Scale(logical, DpiScale.FactorForDpi(DeviceDpi));

    // The tabs' physical-pixel rectangles (label widths measured with the current font).
    private IReadOnlyList<Rectangle> TabBounds()
    {
        // Measure with the same flags DrawText uses in OnPaint, so widths match exactly.
        var widths = new int[_tabs.Length];
        for (var i = 0; i < _tabs.Length; i++)
        {
            widths[i] = TextRenderer.MeasureText(
                _tabs[i], Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        return TabStripLayout.TabBounds(widths, Sc(LabelPadding), Sc(TabGap), Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);

        // Full-width hairline baseline the tabs sit on (stays 1px at every DPI, like the old
        // section dividers).
        using (var baseline = new Pen(theme.Divider))
            g.DrawLine(baseline, 0, Height - 1, Width - 1, Height - 1);

        var bounds = TabBounds();
        var underline = Sc(UnderlineHeight);
        for (var i = 0; i < bounds.Count; i++)
        {
            var rect = bounds[i];
            var active = i == _selectedIndex;

            // Active and hovered tabs read in the full text colour; the rest recede to hint grey.
            var color = active || i == _hoverIndex ? ForeColor : theme.HintText;
            TextRenderer.DrawText(
                g, _tabs[i], Font, rect, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            if (active)
            {
                using var accent = new SolidBrush(theme.HeaderAccent);
                g.FillRectangle(accent, rect.X, Height - underline, rect.Width, underline);

                // Focus ring only for keyboard users (Windows suppresses focus cues until the
                // keyboard is used), so a mouse click doesn't leave a dotted box behind.
                if (Focused && ShowFocusCues)
                {
                    var focus = rect;
                    focus.Inflate(-Sc(2), -Sc(4));
                    ControlPaint.DrawFocusRectangle(g, focus, color, BackColor);
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = TabStripLayout.HitTest(TabBounds(), e.Location);
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var hit = TabStripLayout.HitTest(TabBounds(), e.Location);
        if (hit >= 0)
            SelectedIndex = hit;
    }

    // Arrow keys normally navigate between controls; when the strip is focused they should move
    // the selection instead.
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left)
        {
            SelectedIndex = Math.Max(0, _selectedIndex - 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right)
        {
            SelectedIndex = Math.Min(_tabs.Length - 1, _selectedIndex + 1);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }
}
