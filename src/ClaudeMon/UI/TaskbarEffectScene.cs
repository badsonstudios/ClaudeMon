namespace ClaudeMon.UI;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

/// <summary>
/// The particle systems behind the taskbar escalation effects (issue #208): looping light and
/// heavy smoke, looping fire, and the one-shot nuke. Pure simulation plus drawing into a caller-supplied
/// <see cref="Graphics"/> — no window, no timer — so <see cref="TaskbarEffectWindow"/> owns the
/// plumbing and this owns the look.
/// </summary>
/// <remarks>
/// Everything is laid out in the readout's logical space: the readout occupies
/// (0, 0)–(W, H) with y growing downward, so rising things go negative. Sizes and speeds are
/// in units of the readout height H, which keeps the effects proportioned to the taskbar on
/// every DPI and Size setting. Particles are drawn from a few white sprites pre-rendered once,
/// tinted and faded per draw with a colour matrix — one sprite per shape, any colour.
/// </remarks>
internal sealed class TaskbarEffectScene : IDisposable
{
    /// <summary>How long the nuke sequence runs, in seconds (flash → mushroom cloud → fade).</summary>
    internal const double NukeDurationSeconds = 3.4;

    private enum Kind
    {
        Smoke,
        Flame,
        Ember,
    }

    private struct Particle
    {
        public Kind Kind;
        public float X, Y, Vx, Vy;
        public float Age, Life;
        public float Size0, Size1;
        public float Phase;
        public float Shade;
        public float Alpha;
        public int Variant;
    }

    // A puff placed relative to the mushroom cloud's cap, generated once per nuke so the cloud
    // keeps its shape from frame to frame.
    private readonly record struct CapPuff(float Angle, float Radius, float Size, float Shade, int Variant);

    private const int PuffVariants = 3;
    private const int SpriteSize = 96;

    private readonly Random _rng;
    private readonly List<Particle> _particles = new(256);
    private readonly Bitmap[] _puffs;
    private readonly Bitmap _glow;
    private readonly Bitmap _flame;
    private readonly ImageAttributes _attributes = new();
    private readonly ColorMatrix _matrix = new();
    private readonly PointF[] _destination = new PointF[3];

    private double _smokeDue;
    private double _flameDue;
    private double _emberDue;

    private double? _nukeTime;
    private CapPuff[] _capPuffs = Array.Empty<CapPuff>();
    private float[] _stemJitter = Array.Empty<float>();
    private float[] _ringSpread = Array.Empty<float>();

    public TaskbarEffectScene(int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
        _puffs = Enumerable.Range(0, PuffVariants).Select(_ => RenderPuff()).ToArray();
        _glow = RenderGlow();
        _flame = RenderFlame();
    }

    /// <summary>The looping effect to emit. Switching to None lets live particles finish rather than vanish.</summary>
    public TaskbarLoopingEffect Looping { get; set; }

    /// <summary>Whether the nuke sequence is currently running.</summary>
    public bool NukeActive => _nukeTime is not null;

    /// <summary>
    /// Whether there is anything to animate: a loop, live particles, or the nuke. The window
    /// stops its frame timer the moment this goes false.
    /// </summary>
    public bool IsActive => Looping != TaskbarLoopingEffect.None || _particles.Count > 0 || NukeActive;

    /// <summary>Start (or restart) the one-shot nuke.</summary>
    public void StartNuke()
    {
        _nukeTime = 0;
        _capPuffs = Enumerable.Range(0, 26).Select(_ => new CapPuff(
            Angle: Rand(0, MathF.Tau),
            Radius: MathF.Sqrt(Rand(0.05f, 1f)),
            Size: Rand(0.65f, 1.05f),
            Shade: Rand(0.85f, 1.1f),
            Variant: _rng.Next(PuffVariants))).ToArray();
        _stemJitter = Enumerable.Range(0, 12).Select(_ => Rand(-1f, 1f)).ToArray();
        _ringSpread = Enumerable.Range(0, 14).Select(i => (i % 2 == 0 ? 1 : -1) * Rand(0.25f, 1f)).ToArray();
    }

    /// <summary>Drop everything at once — the hard stop for the feature being switched off.</summary>
    public void Clear()
    {
        Looping = TaskbarLoopingEffect.None;
        _particles.Clear();
        _nukeTime = null;
        _smokeDue = _flameDue = _emberDue = 0;
    }

    /// <summary>
    /// The area (in readout space) this frame can draw into: the window is sized to it. Wider
    /// and taller while the nuke runs; otherwise just room for smoke to rise and drift.
    /// </summary>
    public RectangleF Bounds(float w, float h)
    {
        // Room for the widest drift and the tallest rise: heavy smoke — the biggest — spawns
        // well past either end of the readout, climbs over 4 H, and its puffs grow to nearly
        // 3 H across. Fixed rather than per effect, so particles left over from a bigger effect
        // never clip when the loop steps down.
        var loop = RectangleF.FromLTRB(-0.3f * w - 2.1f * h, -5.6f * h, 1.3f * w + 2.1f * h, h);
        if (!NukeActive)
            return loop;

        var cx = w / 2;
        return RectangleF.Union(loop, RectangleF.FromLTRB(cx - 3.3f * h, -4.9f * h, cx + 3.3f * h, h));
    }

    /// <summary>
    /// Advance the simulation by <paramref name="dt"/> seconds for a W×H readout. With
    /// <paramref name="emit"/> false the loop spawns nothing — live particles and the nuke's
    /// clock still advance — for while the readout is hidden and nothing is being drawn.
    /// </summary>
    public void Step(double dt, float w, float h, bool emit = true)
    {
        if (dt <= 0) return;
        var t = (float)dt;

        if (_nukeTime is { } nuke)
            _nukeTime = nuke + dt >= NukeDurationSeconds ? null : nuke + dt;

        if (emit)
            Emit(dt, w, h);

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += t;
            if (p.Age >= p.Life)
            {
                _particles.RemoveAt(i);
                continue;
            }

            // Smoke and embers meander; flames just lick upward.
            var wobble = p.Kind == Kind.Flame ? 0 : MathF.Sin(p.Age * 2.2f + p.Phase) * 0.12f * h;
            p.X += (p.Vx + wobble) * t;
            p.Y += p.Vy * t;
            _particles[i] = p;
        }
    }

    private void Emit(double dt, float w, float h)
    {
        switch (Looping)
        {
            case TaskbarLoopingEffect.LightSmoke:
                for (_smokeDue += dt * 7; _smokeDue >= 1; _smokeDue--)
                    _particles.Add(Smoke(w, h, shade: Rand(95, 140), y: 0.35f * h, alpha: 1));
                break;

            case TaskbarLoopingEffect.HeavySmoke:
                // Thick, near-black and wide: bigger puffs, spread further past the readout, more
                // of them and more opaque, climbing a touch higher than the light smoke.
                for (_smokeDue += dt * 12; _smokeDue >= 1; _smokeDue--)
                    _particles.Add(Smoke(w, h, shade: Rand(18, 38), y: 0.35f * h, alpha: 1.7f,
                        scale: 1.6f, rise: 1.35f, spread: 0.3f));
                break;

            case TaskbarLoopingEffect.Fire:
                // A wider readout gets a proportionally wider fire, not a sparser one.
                var flameRate = Math.Max(40, 21 * w / h);
                for (_flameDue += dt * flameRate; _flameDue >= 1; _flameDue--)
                    _particles.Add(Flame(w, h));
                for (_emberDue += dt * 4; _emberDue >= 1; _emberDue--)
                    _particles.Add(Ember(w, h));
                // A little dark smoke off the top of the flames.
                for (_smokeDue += dt * 2; _smokeDue >= 1; _smokeDue--)
                    _particles.Add(Smoke(w, h, shade: Rand(45, 70), y: -0.4f * h, alpha: 0.6f));
                break;

            default:
                _smokeDue = _flameDue = _emberDue = 0;
                break;
        }
    }

    // scale grows the puffs; rise grows, more gently, how high and long they climb; spread is
    // how far past each end of the readout (as a fraction of its width) puffs can start.
    private Particle Smoke(float w, float h, float shade, float y, float alpha,
        float scale = 1, float rise = 1, float spread = 0.15f) => new()
    {
        Kind = Kind.Smoke,
        X = Rand(-spread, 1 + spread) * w,
        Y = y,
        Vx = Rand(-0.12f, 0.12f) * h,
        Vy = -Rand(0.6f, 0.9f) * h * (1 + (rise - 1) * 0.45f),
        Life = Rand(3.0f, 3.8f) * (1 + (rise - 1) * 0.3f),
        Size0 = 0.45f * h * scale,
        Size1 = Rand(1.4f, 1.8f) * h * scale,
        Phase = Rand(0, MathF.Tau),
        Shade = shade,
        Alpha = alpha,
        Variant = _rng.Next(PuffVariants),
    };

    private Particle Flame(float w, float h) => new()
    {
        Kind = Kind.Flame,
        X = Rand(-0.15f, 1.15f) * w,
        Y = Rand(0.75f, 1.0f) * h,
        Vx = Rand(-0.2f, 0.2f) * h,
        Vy = -Rand(1.4f, 2.4f) * h,
        Life = Rand(0.55f, 0.95f),
        Size0 = Rand(0.6f, 0.95f) * h,
        Size1 = 0.16f * h,
        Phase = Rand(0, MathF.Tau),
    };

    private Particle Ember(float w, float h) => new()
    {
        Kind = Kind.Ember,
        X = Rand(-0.1f, 1.1f) * w,
        Y = 0.6f * h,
        Vx = Rand(-0.25f, 0.25f) * h,
        Vy = -Rand(1.1f, 1.8f) * h,
        Life = Rand(1.4f, 2.0f),
        Size0 = 0.1f * h,
        Size1 = 0.05f * h,
        Phase = Rand(0, MathF.Tau),
    };

    /// <summary>
    /// Draw the current frame. <paramref name="g"/> is already transformed so readout-space
    /// coordinates land in the right place; the caller has cleared it.
    /// </summary>
    public void Draw(Graphics g, float w, float h)
    {
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        // Smoke behind, flames over it, embers on top.
        foreach (var p in _particles)
        {
            if (p.Kind != Kind.Smoke) continue;
            var f = p.Age / p.Life;
            var alpha = MathF.Min(1, p.Age / 0.4f) * MathF.Pow(1 - f, 1.2f) * 0.55f * p.Alpha;
            var size = Lerp(p.Size0, p.Size1, f);
            var shade = p.Shade / 255f;
            DrawSprite(g, _puffs[p.Variant], p.X, p.Y, size, size, shade, shade, shade, alpha);
        }

        foreach (var p in _particles)
        {
            if (p.Kind != Kind.Flame) continue;
            var f = p.Age / p.Life;
            var (r, gr, b) = FlameColour(f);
            var alpha = MathF.Min(1, p.Age / 0.06f) * MathF.Pow(1 - f, 0.8f) * 0.9f;
            var size = Lerp(p.Size0, p.Size1, f) * (1 + 0.12f * MathF.Sin(p.Age * 25 + p.Phase));
            DrawSprite(g, _flame, p.X, p.Y, size * 0.75f, size, r, gr, b, alpha);
        }

        foreach (var p in _particles)
        {
            if (p.Kind != Kind.Ember) continue;
            var f = p.Age / p.Life;
            var flicker = 0.6f + 0.4f * MathF.Sin(p.Age * 30 + p.Phase);
            var size = Lerp(p.Size0, p.Size1, f);
            DrawSprite(g, _glow, p.X, p.Y, size, size, 1f, 0.8f, 0.35f, (1 - f) * flicker);
        }

        if (_nukeTime is { } t)
            DrawNuke(g, (float)t, w, h);
    }

    private void DrawNuke(Graphics g, float t, float w, float h)
    {
        var cx = w / 2;
        var ground = 0.85f * h;

        var fade = MathF.Min(1, t / 0.15f) * (t < 2.3f ? 1 : MathF.Max(0, 1 - (t - 2.3f) / 1.1f));
        var (hr, hg, hb) = NukeHeat(t);

        // Base surge: dust rolling outward along the taskbar.
        var surge = EaseOut(MathF.Min(1, t / 1.8f));
        foreach (var spread in _ringSpread)
        {
            var x = cx + spread * h * (0.3f + 2.4f * surge);
            var d = 0.6f * h * (0.7f + 0.5f * surge);
            DrawSprite(g, _puffs[0], x, ground - 0.15f * h, d, d * 0.8f,
                0.6f * hr + 0.25f, 0.5f * hg + 0.2f, 0.45f * hb + 0.15f, 0.5f * fade * (1 - 0.6f * surge));
        }

        var rise = EaseOut(Math.Clamp((t - 0.1f) / 1.6f, 0, 1));
        var capY = ground - h * (0.6f + 3.6f * rise);
        var capR = h * (0.5f + 1.1f * rise);

        // Stem: a column of puffs from the ground up to the cap, narrowing as it climbs.
        for (var i = 0; i < _stemJitter.Length; i++)
        {
            var k = i / (float)(_stemJitter.Length - 1);
            var y = Lerp(ground - 0.2f * h, capY + 0.3f * capR, k);
            var d = h * (0.75f - 0.3f * k) * (0.6f + 0.4f * rise);
            DrawSprite(g, _puffs[i % PuffVariants], cx + _stemJitter[i] * 0.12f * h, y, d, d,
                hr * 0.8f, hg * 0.75f, hb * 0.75f, 0.85f * fade);
        }

        // Cap: puffs churning around a flattened ring, the underside a touch darker.
        foreach (var puff in _capPuffs)
        {
            var angle = puff.Angle + t * 0.9f;
            var x = cx + MathF.Cos(angle) * puff.Radius * capR;
            var y = capY + MathF.Sin(angle) * puff.Radius * capR * 0.55f;
            var under = MathF.Sin(angle) > 0 ? 0.8f : 1f;
            var d = capR * puff.Size;
            var shade = puff.Shade * under;
            DrawSprite(g, _puffs[puff.Variant], x, y, d, d,
                MathF.Min(1, hr * shade), MathF.Min(1, hg * shade), MathF.Min(1, hb * shade), 0.9f * fade);
        }

        // Inner fireball glow, cooling as the cloud rises.
        var glow = MathF.Max(0, 1 - t / 1.6f);
        if (glow > 0)
            DrawSprite(g, _glow, cx, capY, capR * 1.4f, capR, 1f, 0.75f, 0.3f, glow * 0.8f * fade);

        // The flash: a white-hot bloom over everything for the first instant.
        if (t < 0.35f)
        {
            var k = t / 0.35f;
            var d = h * (1 + 6 * EaseOut(k));
            DrawSprite(g, _glow, cx, ground - 0.3f * h, d, d, 1f, 0.98f, 0.86f, MathF.Pow(1 - k, 1.5f));
        }
    }

    // White-yellow at birth, through orange and red, to a dark ember-red as it dies.
    private static (float R, float G, float B) FlameColour(float f) => f switch
    {
        < 0.2f => LerpColour((1f, 0.94f, 0.67f), (1f, 0.67f, 0.16f), f / 0.2f),
        < 0.55f => LerpColour((1f, 0.67f, 0.16f), (0.9f, 0.27f, 0.08f), (f - 0.2f) / 0.35f),
        _ => LerpColour((0.9f, 0.27f, 0.08f), (0.47f, 0.12f, 0.04f), (f - 0.55f) / 0.45f),
    };

    // The cloud's colour over time: incandescent, then orange, then cooling to smoke grey.
    private static (float R, float G, float B) NukeHeat(float t) => t switch
    {
        < 0.5f => (1f, 0.9f, 0.6f),
        < 1.2f => LerpColour((1f, 0.9f, 0.6f), (0.98f, 0.55f, 0.16f), (t - 0.5f) / 0.7f),
        < 2.0f => LerpColour((0.98f, 0.55f, 0.16f), (0.67f, 0.32f, 0.16f), (t - 1.2f) / 0.8f),
        _ => LerpColour((0.67f, 0.32f, 0.16f), (0.4f, 0.36f, 0.34f), MathF.Min(1, (t - 2.0f) / 0.8f)),
    };

    private void DrawSprite(Graphics g, Bitmap sprite, float x, float y, float w, float h,
        float r, float gr, float b, float a)
    {
        if (a <= 0.004f || w <= 0 || h <= 0) return;

        // The sprites are white with an alpha falloff, so scaling the channels tints them and
        // scaling alpha fades them — one reused matrix, no per-draw allocation.
        _matrix.Matrix00 = r;
        _matrix.Matrix11 = gr;
        _matrix.Matrix22 = b;
        _matrix.Matrix33 = MathF.Min(1, a);
        _attributes.SetColorMatrix(_matrix);

        // Float destination (the parallelogram overload), not a Rectangle: coordinates here are
        // logical, so integer rounding would jitter particles by whole DPI-scaled pixels.
        _destination[0] = new PointF(x - w / 2, y - h / 2);
        _destination[1] = new PointF(x + w / 2, y - h / 2);
        _destination[2] = new PointF(x - w / 2, y + h / 2);
        g.DrawImage(sprite, _destination, new RectangleF(0, 0, sprite.Width, sprite.Height),
            GraphicsUnit.Pixel, _attributes);
    }

    // A soft round puff, made lumpy by a few overlapping blobs so smoke doesn't look like
    // a stack of identical discs.
    private Bitmap RenderPuff()
    {
        var bitmap = new Bitmap(SpriteSize, SpriteSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawRadial(g, new RectangleF(SpriteSize * 0.12f, SpriteSize * 0.12f, SpriteSize * 0.76f, SpriteSize * 0.76f), 150);
        for (var i = 0; i < 4; i++)
        {
            var d = SpriteSize * Rand(0.35f, 0.5f);
            var angle = Rand(0, MathF.Tau);
            var cx = SpriteSize / 2f + MathF.Cos(angle) * SpriteSize * 0.17f;
            var cy = SpriteSize / 2f + MathF.Sin(angle) * SpriteSize * 0.17f;
            DrawRadial(g, new RectangleF(cx - d / 2, cy - d / 2, d, d), 110);
        }
        return bitmap;
    }

    private static Bitmap RenderGlow()
    {
        var bitmap = new Bitmap(SpriteSize, SpriteSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawRadial(g, new RectangleF(1, 1, SpriteSize - 2, SpriteSize - 2), 255);
        return bitmap;
    }

    // A teardrop: wide, bright base tapering to a point, so flames read as licks rather than dots.
    private static Bitmap RenderFlame()
    {
        var bitmap = new Bitmap(SpriteSize, SpriteSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var s = (float)SpriteSize;
        using var path = new GraphicsPath();
        path.AddBezier(s * 0.5f, s * 0.04f, s * 0.62f, s * 0.38f, s * 0.92f, s * 0.55f, s * 0.86f, s * 0.78f);
        path.AddBezier(s * 0.86f, s * 0.78f, s * 0.8f, s * 0.98f, s * 0.2f, s * 0.98f, s * 0.14f, s * 0.78f);
        path.AddBezier(s * 0.14f, s * 0.78f, s * 0.08f, s * 0.55f, s * 0.38f, s * 0.38f, s * 0.5f, s * 0.04f);
        path.CloseFigure();

        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(s * 0.5f, s * 0.72f),
            CenterColor = Color.FromArgb(255, 255, 255, 255),
            SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
        };
        g.FillPath(brush, path);
        return bitmap;
    }

    private static void DrawRadial(Graphics g, RectangleF rect, int centreAlpha)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(rect);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(centreAlpha, 255, 255, 255),
            SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
        };
        // Bias the falloff so the puff has a dense core and a long soft edge.
        brush.SetSigmaBellShape(0.55f);
        g.FillPath(brush, path);
    }

    private float Rand(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float EaseOut(float t) => 1 - MathF.Pow(1 - t, 3);

    private static (float R, float G, float B) LerpColour(
        (float R, float G, float B) a, (float R, float G, float B) b, float t) =>
        (Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    public void Dispose()
    {
        foreach (var puff in _puffs)
            puff.Dispose();
        _glow.Dispose();
        _flame.Dispose();
        _attributes.Dispose();
    }
}
