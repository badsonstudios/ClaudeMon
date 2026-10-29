namespace ClaudeMon.UI;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClaudeMon.Services;

/// <summary>
/// Where a readout is right now, for the effect window that rides on it: its window handle
/// (the effect sits directly beneath it in z-order), its physical screen bounds, and its
/// content scale (monitor DPI × the Size setting).
/// </summary>
internal readonly record struct TaskbarEffectAnchor(IntPtr Handle, Rectangle Bounds, float Scale);

/// <summary>
/// The escalation effects' canvas (issue #208): one per taskbar readout, a click-through
/// per-pixel-alpha window that draws <see cref="TaskbarEffectScene"/> around and above its
/// <see cref="TaskbarOverlayWindow"/>. Kept separate from the readout so the readout's width
/// measuring, dirty-tracking, hit-testing and keep-alive are untouched by the animation.
/// </summary>
/// <remarks>
/// The ~30 fps frame timer runs only while the scene has something to animate, and stops the
/// moment it doesn't — an idle effect window costs nothing. While its readout is hidden
/// (feature off, fullscreen app, taskbar missing) the window hides too and the timer drops to a
/// slow poll, so a fire left burning under a fullscreen game isn't rendering unseen frames.
/// Frames are drawn straight into a reused premultiplied DIB section and pushed with
/// <c>UpdateLayeredWindow</c> — no per-frame bitmap or copy.
/// </remarks>
public sealed class TaskbarEffectWindow : Form
{
    private const int FrameIntervalMs = 33;
    private const int HiddenPollIntervalMs = 500;

    // Re-assert the z-order every few frames rather than every one: it's a cross-process
    // window-manager call, and the readout's own keep-alive only moves every 500 ms.
    private const int ZOrderEveryFrames = 8;

    private readonly TaskbarOverlayWindow _readout;
    private readonly Logger? _logger;
    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly TaskbarEffectScene _scene = new();
    private readonly Stopwatch _clock = new();

    private LayeredSurface? _surface;
    private long _lastFrameMs;
    private int _framesSinceZOrder = ZOrderEveryFrames;
    private bool _failureLogged;

    // The readout's logical size as last seen, so the simulation can keep advancing (the nuke
    // has a fixed duration) while the readout is hidden.
    private float _logicalWidth = 60;
    private float _logicalHeight = 40;

    internal TaskbarEffectWindow(TaskbarOverlayWindow readout, Logger? logger = null)
    {
        _readout = readout;
        _logger = logger;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;

        _frameTimer = new System.Windows.Forms.Timer { Interval = FrameIntervalMs };
        _frameTimer.Tick += (_, _) => OnFrame();
    }

    /// <summary>
    /// True while fire is actually on screen here — looping and its readout visible. Drives the
    /// fire crackle, so the sound follows what you can see: it stops behind a fullscreen app
    /// just as the flames do.
    /// </summary>
    internal bool ShowingFire { get; private set; }

    /// <summary>Raised when <see cref="ShowingFire"/> changes.</summary>
    internal event EventHandler? ShowingFireChanged;

    /// <summary>Whether this window's readout is on screen right now (so a nuke here would be seen).</summary>
    internal bool ReadoutVisible => !_readout.IsDisposed && _readout.EffectAnchor is not null;

    // Never steal focus from the foreground app.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Click-through (TRANSPARENT): the readout beneath stays the only click target.
            // Topmost from birth, so slotting it beneath the (topmost) readout keeps it in the
            // topmost band rather than dropping it behind the taskbar.
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Set the looping effect. None lets live particles burn out instead of vanishing.</summary>
    internal void SetEffect(TaskbarLoopingEffect effect)
    {
        _scene.Looping = effect;
        // Going off fire silences the crackle at once; coming on to it waits for the first frame
        // that actually shows it.
        if (effect != TaskbarLoopingEffect.Fire)
            SetShowingFire(false);
        EnsureRunning();
    }

    /// <summary>Play the one-shot nuke over whatever is looping.</summary>
    internal void PlayNuke()
    {
        _scene.StartNuke();
        EnsureRunning();
    }

    /// <summary>Hard stop: drop everything, hide, and stop the timer (the feature was switched off).</summary>
    internal void Stop()
    {
        _scene.Clear();
        _frameTimer.Stop();
        if (Visible) Hide();
        SetShowingFire(false);
    }

    private void SetShowingFire(bool showing)
    {
        if (ShowingFire == showing) return;
        ShowingFire = showing;
        ShowingFireChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureRunning()
    {
        if (!_scene.IsActive || _frameTimer.Enabled) return;

        _clock.Restart();
        _lastFrameMs = 0;
        _frameTimer.Interval = FrameIntervalMs;
        _frameTimer.Start();
    }

    private void OnFrame()
    {
        try
        {
            var now = _clock.ElapsedMilliseconds;
            var dt = (now - _lastFrameMs) / 1000.0;
            _lastFrameMs = now;

            if (_readout.IsDisposed || _readout.EffectAnchor is not { } anchor)
            {
                // Keep time moving (so a nuke can't stall forever behind a fullscreen app) but
                // draw nothing, at a poll rate rather than a frame rate — and emit nothing, so a
                // loop left running unseen isn't churning particles nobody will see.
                if (Visible) Hide();
                _frameTimer.Interval = HiddenPollIntervalMs;
                _scene.Step(Math.Min(dt, 1.0), _logicalWidth, _logicalHeight, emit: false);
                SetShowingFire(false);
            }
            else
            {
                _frameTimer.Interval = FrameIntervalMs;
                _logicalWidth = Math.Max(1, anchor.Bounds.Width / anchor.Scale);
                _logicalHeight = Math.Max(1, anchor.Bounds.Height / anchor.Scale);
                // Clamped so a stall (a modal loop, a debugger) is a skipped beat, not a jump.
                _scene.Step(Math.Min(dt, 0.1), _logicalWidth, _logicalHeight);
                if (_scene.IsActive)
                    Render(anchor);
                SetShowingFire(_scene.Looping == TaskbarLoopingEffect.Fire);
            }

            if (!_scene.IsActive)
            {
                _frameTimer.Stop();
                if (Visible) Hide();
                SetShowingFire(false);
            }
        }
        catch (Exception ex)
        {
            // A transient GDI/Win32 failure must never escape into the message loop; the next
            // frame retries. Logged once per window so a persistent failure isn't 30 lines a second.
            if (!_failureLogged)
            {
                _failureLogged = true;
                _logger?.Warn($"Taskbar effect frame failed: {ex.Message}");
            }
        }
    }

    private void Render(TaskbarEffectAnchor anchor)
    {
        var scale = anchor.Scale;
        var area = _scene.Bounds(_logicalWidth, _logicalHeight);

        // The canvas in physical pixels, positioned so readout-space (0,0) lands on the
        // readout's top-left corner.
        var left = anchor.Bounds.Left + (int)MathF.Floor(area.Left * scale);
        var top = anchor.Bounds.Top + (int)MathF.Floor(area.Top * scale);
        var width = Math.Max(1, (int)MathF.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)MathF.Ceiling(area.Height * scale));

        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            _surface?.Dispose();
            _surface = new LayeredSurface(width, height);
        }

        using (var g = Graphics.FromImage(_surface.Bitmap))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-area.Left, -area.Top);
            _scene.Draw(g, _logicalWidth, _logicalHeight);
        }

        // Push before showing: a layered window keeps its last frame, so showing first would
        // flash the stale one (at the old position) after a fullscreen app or a DPI change.
        _surface.Push(Handle, left, top);

        if (!Visible)
        {
            Show();
            _framesSinceZOrder = ZOrderEveryFrames;
        }

        if (++_framesSinceZOrder >= ZOrderEveryFrames)
        {
            _framesSinceZOrder = 0;
            // Same Rude Window Manager treatment as the readout (see TaskbarOverlayWindow).
            // Cleared whenever the window hides, so re-set on every assert.
            SetProp(Handle, "NonRudeHWND", new IntPtr(1));
            // Directly beneath the readout: the readout's text stays on top of the flames, and
            // two windows both claiming the top of the topmost band would trade places (flicker)
            // every time either re-asserted.
            SetWindowPos(Handle, anchor.Handle, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Dispose();
            _scene.Dispose();
            _surface?.Dispose();
            _surface = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// A top-down 32bpp premultiplied DIB section selected into a memory DC, with a GDI+
    /// <see cref="System.Drawing.Bitmap"/> over the same pixels — draw with GDI+, push with
    /// <c>UpdateLayeredWindow</c>, no per-frame bitmap and no copy.
    /// </summary>
    private sealed class LayeredSurface : IDisposable
    {
        private readonly IntPtr _memDc;
        private readonly IntPtr _dib;
        private readonly IntPtr _oldBitmap;

        public LayeredSurface(int width, int height)
        {
            Width = width;
            Height = height;

            var info = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(),
                biWidth = width,
                biHeight = -height, // negative = top-down, matching GDI+'s scan order
                biPlanes = 1,
                biBitCount = 32,
            };

            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                _memDc = CreateCompatibleDC(screenDc);
                _dib = CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero)
                {
                    ReleaseNative();
                    throw new InvalidOperationException($"Layered surface allocation failed for {width}x{height}.");
                }

                _oldBitmap = SelectObject(_memDc, _dib);
                try
                {
                    Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
                }
                catch
                {
                    ReleaseNative();
                    throw;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public int Width { get; }
        public int Height { get; }
        public Bitmap Bitmap { get; }

        public void Push(IntPtr hwnd, int x, int y)
        {
            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE { cx = Width, cy = Height };
                var source = new POINT { x = 0, y = 0 };
                var dest = new POINT { x = x, y = y };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(hwnd, screenDc, ref dest, ref size, _memDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            ReleaseNative();
        }

        private void ReleaseNative()
        {
            if (_oldBitmap != IntPtr.Zero)
                SelectObject(_memDc, _oldBitmap);
            if (_dib != IntPtr.Zero)
                DeleteObject(_dib);
            if (_memDc != IntPtr.Zero)
                DeleteDC(_memDc);
        }
    }

    // --- Win32 interop ---

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int bmiColors;
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

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
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
