namespace ClaudeMon.UI;

using System.Media;
using ClaudeMon.Services;

/// <summary>
/// Synthesises the escalation effects' two sounds (issue #208) — a seamlessly looping fire
/// crackle and a one-shot explosion — as 16-bit mono WAV files in memory. Generated rather than
/// shipped as recordings: nothing to license, nothing to install, and a few hundred KB of code
/// output instead of assets. Deterministic (fixed seeds), so the sounds are the same every run.
/// </summary>
internal static class EffectSoundSynth
{
    internal const int SampleRate = 22050;

    /// <summary>Length of the fire loop, in seconds.</summary>
    internal const double FireLoopSeconds = 6;

    /// <summary>Length of the explosion, in seconds — roughly the nuke's on-screen run.</summary>
    internal const double ExplosionSeconds = 3.5;

    /// <summary>
    /// A crackling fire: a soft low roar and hiss under random crackles and the odd louder pop.
    /// The end is cross-faded into the start, so looping it has no seam to click on.
    /// </summary>
    public static byte[] FireLoop()
    {
        var rng = new Random(208);
        var length = (int)(FireLoopSeconds * SampleRate);
        var fade = SampleRate / 4;
        var raw = new float[length + fade];

        var roar = 0f;
        var hissLow = 0f;
        var swell = 0.8f;
        var swellTarget = 0.8f;
        var roarCoef = OnePole(400);
        var hissCoef = OnePole(3000);

        // Crackles: decaying bursts of bright noise, sometimes in quick clusters.
        var crackleLeft = 0;
        var crackleAmp = 0f;
        var crackleDecay = 0f;
        var nextCrackle = NextGap(rng, 18);
        var clusterLeft = 0;
        var crackleLow = 0f;

        for (var i = 0; i < raw.Length; i++)
        {
            // A slow random swell, so the fire breathes rather than drones.
            if (i % (SampleRate / 4) == 0)
                swellTarget = 0.6f + 0.4f * (float)rng.NextDouble();
            swell += (swellTarget - swell) * 0.0004f;

            var noise = (float)(rng.NextDouble() * 2 - 1);
            roar += roarCoef * (noise - roar);
            hissLow += hissCoef * (noise - hissLow);
            var sample = roar * 0.55f * swell + (noise - hissLow) * 0.025f * swell;

            if (--nextCrackle <= 0)
            {
                var big = rng.NextDouble() < 0.08;
                crackleAmp = big ? 0.9f : 0.2f + 0.45f * (float)rng.NextDouble();
                crackleLeft = (int)(SampleRate * (0.003 + 0.022 * rng.NextDouble()));
                var tau = SampleRate * (0.002f + 0.006f * (float)rng.NextDouble());
                crackleDecay = MathF.Exp(-1f / tau);

                if (clusterLeft > 0)
                {
                    clusterLeft--;
                    nextCrackle = (int)(SampleRate * (0.01 + 0.03 * rng.NextDouble()));
                }
                else
                {
                    clusterLeft = rng.NextDouble() < 0.3 ? rng.Next(1, 4) : 0;
                    nextCrackle = NextGap(rng, 18);
                }
            }

            if (crackleLeft > 0)
            {
                crackleLeft--;
                var bright = (float)(rng.NextDouble() * 2 - 1);
                crackleLow += 0.35f * (bright - crackleLow);
                sample += (bright - crackleLow) * crackleAmp;
                crackleAmp *= crackleDecay;
            }

            raw[i] = sample;
        }

        var loop = new float[length];
        for (var i = 0; i < length; i++)
        {
            if (i < fade)
            {
                var k = i / (float)fade;
                loop[i] = raw[i] * k + raw[length + i] * (1 - k);
            }
            else
            {
                loop[i] = raw[i];
            }
        }

        // Kept quiet on purpose: this loops for as long as a limit is red.
        Normalize(loop, 0.275f);
        return ToWav(loop);
    }

    /// <summary>
    /// An explosion: a sharp crack, a deep boom whose brightness sweeps down as it rolls, a
    /// sub-bass thump, then a rumbling tail with falling debris, fading to silence.
    /// </summary>
    public static byte[] Explosion()
    {
        var rng = new Random(100);
        var length = (int)(ExplosionSeconds * SampleRate);
        var samples = new float[length];

        float lp1 = 0, lp2 = 0, rumble = 0, debrisLow = 0;
        var rumbleCoef = OnePole(150);
        double phase = 0;

        var debrisLeft = 0;
        var debrisAmp = 0f;
        var debrisDecay = 0f;
        var nextDebris = (int)(SampleRate * 0.3);

        for (var i = 0; i < length; i++)
        {
            var t = i / (float)SampleRate;
            var noise = (float)(rng.NextDouble() * 2 - 1);

            // The crack: a very short burst of full-band noise.
            var crack = noise * MathF.Exp(-t / 0.015f);

            // The boom: noise through a two-stage low-pass whose cutoff sweeps 2 kHz → 80 Hz.
            var cutoff = 80 + 1920 * MathF.Exp(-t / 0.35f);
            var coef = OnePole(cutoff);
            lp1 += coef * (noise - lp1);
            lp2 += coef * (lp1 - lp2);
            var boomEnv = MathF.Min(1, t / 0.008f) * MathF.Exp(-t / 0.9f);
            var boom = lp2 * 2.6f * boomEnv;

            // The thump: a sine falling 55 → 28 Hz.
            var freq = 28 + 27 * MathF.Exp(-t / 0.5f);
            phase += 2 * Math.PI * freq / SampleRate;
            var sub = (float)Math.Sin(phase) * 0.7f * MathF.Exp(-t / 0.6f);

            // The tail: a low rumble swelling in after the blast and dying away.
            rumble += rumbleCoef * (noise - rumble);
            var tailEnv = MathF.Min(1, t / 0.3f) * MathF.Exp(-MathF.Max(0, t - 0.3f) / 1.2f);
            var tail = rumble * 1.4f * tailEnv;

            // Debris: sparse crackles thinning out over the first couple of seconds.
            var debris = 0f;
            if (t > 0.3f && t < 2.6f && --nextDebris <= 0)
            {
                debrisAmp = (0.25f + 0.3f * (float)rng.NextDouble()) * MathF.Max(0, 1 - t / 2.6f);
                debrisLeft = (int)(SampleRate * (0.004 + 0.015 * rng.NextDouble()));
                debrisDecay = MathF.Exp(-1f / (SampleRate * 0.004f));
                nextDebris = NextGap(rng, 10);
            }
            if (debrisLeft > 0)
            {
                debrisLeft--;
                debrisLow += 0.3f * (noise - debrisLow);
                debris = (noise - debrisLow) * debrisAmp;
                debrisAmp *= debrisDecay;
            }

            // Fade the last 300 ms so it ends in silence, not a cut.
            var end = MathF.Min(1, ((float)ExplosionSeconds - t) / 0.3f);
            samples[i] = (crack * 0.9f + boom + sub + tail + debris) * MathF.Max(0, end);
        }

        // Soft-clip the peak of the blast, then level it.
        for (var i = 0; i < length; i++)
            samples[i] = MathF.Tanh(samples[i] * 1.4f);
        Normalize(samples, 0.45f);
        return ToWav(samples);
    }

    // Smoothing coefficient of a one-pole low-pass at the given cutoff.
    private static float OnePole(float cutoffHz) =>
        1 - MathF.Exp(-2 * MathF.PI * cutoffHz / SampleRate);

    // Samples until the next event of a Poisson process at ratePerSecond.
    private static int NextGap(Random rng, double ratePerSecond) =>
        Math.Max(1, (int)(-Math.Log(1 - rng.NextDouble()) / ratePerSecond * SampleRate));

    private static void Normalize(float[] samples, float peak)
    {
        var max = samples.Max(MathF.Abs);
        if (max <= 0) return;
        var gain = peak / max;
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }

    private static byte[] ToWav(float[] samples)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                 // PCM chunk size
        writer.Write((short)1);           // PCM
        writer.Write((short)1);           // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);     // byte rate
        writer.Write((short)2);           // block align
        writer.Write((short)16);          // bits per sample
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>
/// Plays the escalation effects' sounds: the fire crackle when a readout catches fire, and the
/// explosion with the nuke. One per app, not per readout — two monitors on fire are still one
/// fire to listen to.
/// </summary>
/// <remarks>
/// The crackle is an alert, not a soundtrack: it plays for <see cref="FireCrackleMs"/> when the
/// fire starts and then goes quiet while the flames keep burning. It re-arms only after the fire
/// has been out for <see cref="FireRearmMs"/>, so the brief gaps from opening the flyout or
/// alt-tabbing through a fullscreen app don't replay it on every glance.
///
/// Built on <see cref="SoundPlayer"/>, which plays one sound per process at a time: starting the
/// explosion cuts the fire loop, so the loop is resumed once the explosion has finished (if the
/// crackle's time isn't up). The clips are synthesised on first use. A machine with no audio
/// device just stays quiet — the first failure is logged and sound is given up for the session.
/// </remarks>
internal sealed class TaskbarEffectSoundPlayer : IDisposable
{
    /// <summary>How long the crackle plays once fire starts.</summary>
    internal const int FireCrackleMs = 15_000;

    /// <summary>How long the fire must be out before the next fire crackles again.</summary>
    internal const int FireRearmMs = 30_000;

    private readonly Logger? _logger;
    private readonly System.Windows.Forms.Timer _resumeTimer;
    private readonly System.Windows.Forms.Timer _crackleTimer;

    private SoundPlayer? _fire;
    private SoundPlayer? _explosion;
    private bool _fireWanted;
    private bool _fireLooping;
    private bool _explosionPlaying;
    private bool _failed;

    // The crackle's allowance for the current fire is used up (or still running down).
    private bool _crackleSpent;
    private long _fireOutSinceTicks;

    public TaskbarEffectSoundPlayer(Logger? logger = null)
    {
        _logger = logger;
        _resumeTimer = new System.Windows.Forms.Timer { Interval = (int)(EffectSoundSynth.ExplosionSeconds * 1000) };
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            _explosionPlaying = false;
            Apply();
        };

        _crackleTimer = new System.Windows.Forms.Timer { Interval = FireCrackleMs };
        _crackleTimer.Tick += (_, _) =>
        {
            _crackleTimer.Stop();
            Apply();
        };
    }

    // The crackle should be sounding: fire is showing and its allowance isn't used up.
    private bool CrackleWanted => _fireWanted && _crackleTimer.Enabled;

    /// <summary>Whether some readout is showing fire (the crackle decides for itself whether to sound).</summary>
    public void SetFire(bool on)
    {
        if (_fireWanted == on) return;
        _fireWanted = on;

        if (on)
        {
            // A new fire gets a fresh allowance; a flicker back on after a brief gap doesn't.
            if (!_crackleSpent || Environment.TickCount64 - _fireOutSinceTicks >= FireRearmMs)
            {
                _crackleSpent = true;
                _crackleTimer.Stop();
                _crackleTimer.Start();
            }
        }
        else
        {
            _fireOutSinceTicks = Environment.TickCount64;
        }

        Apply();
    }

    /// <summary>Play the explosion now, cutting the fire loop until it's done.</summary>
    public void PlayExplosion()
    {
        if (_failed) return;
        Try(() =>
        {
            _explosion ??= Load(EffectSoundSynth.Explosion());
            _explosion.Play();
            _fireLooping = false;
            _explosionPlaying = true;
            _resumeTimer.Stop();
            _resumeTimer.Start();
        });
    }

    /// <summary>Silence everything (the effects or their sound were switched off).</summary>
    public void StopAll()
    {
        _fireWanted = false;
        _explosionPlaying = false;
        _resumeTimer.Stop();
        // Switched off, not merely out: the next fire after switching back on crackles afresh.
        _crackleTimer.Stop();
        _crackleSpent = false;
        // SoundPlayer.Stop silences whatever this process is playing, so either player will do.
        if ((_fire ?? _explosion) is { } player)
            Try(player.Stop);
        _fireLooping = false;
    }

    private void Apply()
    {
        if (_explosionPlaying) return;

        // Stopping comes first and isn't gated on _failed: a failure while the crackle was looping
        // must not leave it looping for the rest of the session.
        if (!CrackleWanted && _fireLooping)
        {
            Try(() => _fire?.Stop());
            _fireLooping = false;
            return;
        }

        if (_failed) return;

        if (CrackleWanted && !_fireLooping)
        {
            Try(() =>
            {
                _fire ??= Load(EffectSoundSynth.FireLoop());
                _fire.PlayLooping();
                _fireLooping = true;
            });
        }
    }

    private static SoundPlayer Load(byte[] wav)
    {
        // The MemoryStream holds no unmanaged resources; it lives as long as the player.
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _failed = true;
            _logger?.Warn($"Taskbar effect sound unavailable — continuing silently: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _resumeTimer.Dispose();
        _crackleTimer.Dispose();
        try
        {
            _fire?.Stop();
        }
        catch
        {
            // Shutting down; nothing useful to do with a failure to stop a sound.
        }
        _fire?.Dispose();
        _explosion?.Dispose();
    }
}
