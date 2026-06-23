namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClaudeMon.Models;
using Microsoft.Win32;

/// <summary>
/// A borderless, click-through, always-on-top window that paints the current Claude
/// usage percentage directly over the right end of the primary Windows taskbar,
/// just left of the system tray / clock.
/// </summary>
/// <remarks>
/// This is the lightweight alternative to a deskband COM shell extension: it needs no
/// COM registration and works on Windows 11 (where deskbands are unsupported). The
/// content is drawn with per-pixel alpha via <c>UpdateLayeredWindow</c> so anti-aliased
/// text shows cleanly over the taskbar (a colour-keyed <c>TransparencyKey</c> can't —
/// it fringes anti-aliased glyphs). A low-frequency timer re-asserts the topmost
/// z-order and position so the overlay survives taskbar clicks and layout changes.
/// Positioning is best-effort for the primary, horizontal taskbar.
/// </remarks>
public sealed class TaskbarOverlayWindow : Form
{
    private readonly System.Windows.Forms.Timer _keepAliveTimer;

    private double? _percentage;
    private double? _sevenDayPercentage;
    private bool _showSevenDay;
    private bool _signInExpired;
    private TaskbarTextColor _labelColor = TaskbarTextColor.White;
    private TaskbarTextColor _numberColor = TaskbarTextColor.Auto;
    private int _x;
    private int _y;
    private int _width = IconRenderer.MinTaskbarWidth;
    private int _height = 40;

    // Inputs the cached _width was last measured for, so the 500 ms keep-alive tick
    // doesn't re-measure (allocating fonts + a bitmap) when nothing changed.
    private double _widthPct = double.NaN;
    private double? _widthSeven;
    private int _widthHeight;
    private bool _widthSignInExpired;

    public TaskbarOverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(_width, _height);

        // Re-glue to the taskbar and re-assert topmost ordering periodically. Clicking
        // the taskbar can push us below it; this brings us back promptly.
        _keepAliveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _keepAliveTimer.Tick += (_, _) => { if (Visible) Reposition(); };

        // Also react immediately to display layout changes (resolution, DPI, monitors).
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Don't steal focus from the taskbar / foreground app when shown.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Layered (per-pixel alpha), click-through, and out of alt-tab / focus.
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Show or hide the overlay live, without restarting the app.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!Visible) Show();
            Reposition();
            _keepAliveTimer.Start();
        }
        else
        {
            _keepAliveTimer.Stop();
            if (Visible) Hide();
        }
    }

    /// <summary>
    /// Update the displayed values and repaint. <paramref name="sevenDayPercentage"/> is
    /// only shown when the "also show 7-day" option is on (see <see cref="SetShowSevenDay"/>);
    /// pass null when the API didn't return a 7-day window.
    /// </summary>
    public void UpdateUsage(double percentage, double? sevenDayPercentage)
    {
        // A fresh reading clears any sign-in-expired marker, so normal display returns
        // automatically once credentials are refreshed.
        _signInExpired = false;
        _percentage = percentage;
        _sevenDayPercentage = sevenDayPercentage;
        if (Visible) Reposition();
    }

    /// <summary>
    /// Replace the usage readout with a neutral sign-in-expired marker (see
    /// <see cref="IconRenderer.DrawTaskbarSignInExpired"/>) so the overlay never shows a
    /// stale percentage after the Claude Code token expires. Cleared by the next
    /// <see cref="UpdateUsage"/>. Honoured the next time the overlay is shown if currently
    /// disabled.
    /// </summary>
    public void ShowSignInExpired()
    {
        _signInExpired = true;
        if (Visible) Reposition();
    }

    /// <summary>Set the text colour presets and repaint live (no restart).</summary>
    public void SetColors(TaskbarTextColor labelColor, TaskbarTextColor numberColor)
    {
        _labelColor = labelColor;
        _numberColor = numberColor;
        if (Visible) Redraw();
    }

    /// <summary>
    /// Toggle whether the 7-day usage is shown alongside the 5-hour one. Re-measures the
    /// overlay width and repaints live (no restart).
    /// </summary>
    public void SetShowSevenDay(bool showSevenDay)
    {
        _showSevenDay = showSevenDay;
        if (Visible) Reposition();
    }

    /// <summary>The 7-day value to display, or null when the option is off.</summary>
    private double? SevenDayForDisplay => _showSevenDay ? _sevenDayPercentage : null;

    /// <summary>
    /// Recomputes <see cref="_width"/> from the current readout, but only when the
    /// percentage, 7-day value, or taskbar height changed — measuring allocates fonts
    /// and a bitmap, and this runs on the 500 ms keep-alive tick.
    /// </summary>
    private void UpdateMeasuredWidth()
    {
        if (_signInExpired)
        {
            if (_widthSignInExpired && _widthHeight == _height) return;

            _width = IconRenderer.MeasureTaskbarSignInExpiredWidth(_height);
            _widthSignInExpired = true;
            _widthHeight = _height;
            return;
        }

        var pct = _percentage ?? 0;
        var seven = SevenDayForDisplay;
        if (!_widthSignInExpired && _widthPct.Equals(pct) && _widthSeven.Equals(seven) && _widthHeight == _height)
            return;

        _width = IconRenderer.MeasureTaskbarUsageWidth(pct, seven, _height);
        _widthPct = pct;
        _widthSeven = seven;
        _widthHeight = _height;
        _widthSignInExpired = false;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Visible) Reposition();
    }

    /// <summary>
    /// Positions the overlay against the right edge of the primary taskbar, immediately
    /// to the left of the notification area, re-asserts topmost ordering, and repaints.
    /// </summary>
    private void Reposition()
    {
        if (!IsHandleCreated) return;

        var trayHwnd = FindWindow("Shell_TrayWnd", null);
        if (trayHwnd == IntPtr.Zero || !GetWindowRect(trayHwnd, out var taskbar))
            return;

        // Left edge of the notification area (clock/tray); fall back to the taskbar's
        // right edge if it can't be found.
        var left = taskbar.Right;
        var notifyHwnd = FindWindowEx(trayHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notifyHwnd != IntPtr.Zero && GetWindowRect(notifyHwnd, out var notify))
            left = notify.Left;

        var taskbarHeight = taskbar.Bottom - taskbar.Top;
        _height = taskbarHeight > 0 ? taskbarHeight : _height;

        // Size the overlay to its content so the dual "5hr / 7day" readout never clips.
        // The window is right-anchored, so a wider overlay extends leftward and the
        // clock/tray stay put. Re-measure only when the inputs actually change.
        UpdateMeasuredWidth();
        _x = left - _width;
        _y = taskbar.Top;

        SetWindowPos(
            Handle, HWND_TOPMOST, _x, _y, _width, _height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        Redraw();
    }

    /// <summary>Renders the readout to a 32bpp ARGB bitmap and pushes it via UpdateLayeredWindow.</summary>
    private void Redraw()
    {
        // Sign-in-expired draws without a percentage; otherwise there's nothing to paint yet.
        if (!IsHandleCreated || (!_signInExpired && _percentage is null)) return;

        using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            var bounds = new Rectangle(0, 0, _width, _height);

            if (_signInExpired)
            {
                // Resolve at 0% so the neutral marker isn't usage-coloured under the Auto preset.
                var labelColor = IconRenderer.GetTextColor(_labelColor, 0);
                IconRenderer.DrawTaskbarSignInExpired(graphics, bounds, labelColor);
            }
            else
            {
                var pct = _percentage!.Value;
                var labelColor = IconRenderer.GetTextColor(_labelColor, pct);
                var sevenDay = SevenDayForDisplay;

                if (sevenDay is null)
                {
                    var numberColor = IconRenderer.GetTextColor(_numberColor, pct);
                    IconRenderer.DrawTaskbarUsage(graphics, pct, bounds, labelColor, numberColor);
                }
                else
                {
                    // Each number is coloured for its own usage level (under the Auto preset).
                    var fiveColor = IconRenderer.GetTextColor(_numberColor, pct);
                    var sevenColor = IconRenderer.GetTextColor(_numberColor, sevenDay.Value);
                    IconRenderer.DrawTaskbarUsage(
                        graphics, pct, sevenDay.Value, bounds, labelColor, fiveColor, sevenColor);
                }
            }
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = _width, cy = _height };
            var source = new POINT { x = 0, y = 0 };
            var dest = new POINT { x = _x, y = _y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(
                Handle, screenDc, ref dest, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _keepAliveTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // --- Win32 interop ---

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
