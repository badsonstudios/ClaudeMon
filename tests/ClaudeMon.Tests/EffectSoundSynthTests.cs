namespace ClaudeMon.Tests;

using ClaudeMon.UI;

/// <summary>
/// The sounds themselves are judged by ear; these pin what playback relies on — a well-formed
/// PCM WAV of the right length that never clips, and a fire loop with no click at the seam.
/// </summary>
public class EffectSoundSynthTests
{
    private const int HeaderBytes = 44;

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - HeaderBytes) / 2];
        Buffer.BlockCopy(wav, HeaderBytes, samples, 0, samples.Length * 2);
        return samples;
    }

    [Fact]
    public void BothSoundsAreMono16BitPcmWav()
    {
        foreach (var wav in new[] { EffectSoundSynth.FireLoop(), EffectSoundSynth.Explosion() })
        {
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(1, BitConverter.ToInt16(wav, 20));   // PCM
            Assert.Equal(1, BitConverter.ToInt16(wav, 22));   // mono
            Assert.Equal(EffectSoundSynth.SampleRate, BitConverter.ToInt32(wav, 24));
            Assert.Equal(16, BitConverter.ToInt16(wav, 34));
            Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
            Assert.Equal(wav.Length - HeaderBytes, BitConverter.ToInt32(wav, 40));
        }
    }

    [Fact]
    public void LengthsMatchTheirDurations()
    {
        Assert.Equal((int)(EffectSoundSynth.FireLoopSeconds * EffectSoundSynth.SampleRate),
            Samples(EffectSoundSynth.FireLoop()).Length);
        Assert.Equal((int)(EffectSoundSynth.ExplosionSeconds * EffectSoundSynth.SampleRate),
            Samples(EffectSoundSynth.Explosion()).Length);
    }

    [Fact]
    public void BothSoundsSitAtHalfVolumeOrBelowButAreAudible()
    {
        foreach (var wav in new[] { EffectSoundSynth.FireLoop(), EffectSoundSynth.Explosion() })
        {
            var peak = Samples(wav).Max(s => Math.Abs((int)s));
            Assert.InRange(peak, short.MaxValue / 5, short.MaxValue / 2);
        }
    }

    [Fact]
    public void TheExplosionEndsInSilence()
    {
        var samples = Samples(EffectSoundSynth.Explosion());
        Assert.True(Math.Abs((int)samples[^1]) < 50);
    }

    [Fact]
    public void TheFireLoopHasNoClickAtTheSeam()
    {
        // The jump from the last sample back to the first must look like any other step in the
        // signal, not stand out as a discontinuity.
        var samples = Samples(EffectSoundSynth.FireLoop());
        var seam = Math.Abs(samples[0] - samples[^1]);
        var typical = Enumerable.Range(1, samples.Length - 1)
            .Select(i => Math.Abs(samples[i] - samples[i - 1]))
            .OrderBy(d => d)
            .ElementAt(samples.Length * 99 / 100);

        Assert.True(seam <= typical, $"seam step {seam} exceeds the 99th-percentile step {typical}");
    }

    [Fact]
    public void TheSoundsAreTheSameEveryRun()
    {
        Assert.Equal(EffectSoundSynth.FireLoop(), EffectSoundSynth.FireLoop());
        Assert.Equal(EffectSoundSynth.Explosion(), EffectSoundSynth.Explosion());
    }
}
