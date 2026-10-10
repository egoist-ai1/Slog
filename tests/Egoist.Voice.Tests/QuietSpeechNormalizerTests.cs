using Egoist.Voice.Core;
using Xunit;

namespace Egoist.Voice.Tests;

public sealed class QuietSpeechNormalizerTests
{
    private static float[] Tone(double peakDb, int length = 1600)
    {
        var amplitude = (float)Math.Pow(10, peakDb / 20);
        var samples = new float[length];
        for (var index = 0; index < length; index++)
            samples[index] = amplitude * (float)Math.Sin(index * 0.3);
        samples[1] = amplitude;
        return samples;
    }

    private static double PeakDb(float[] samples) => 20 * Math.Log10(samples.Max(Math.Abs));

    [Fact]
    public void NormalSpeechIsReturnedUntouched()
    {
        var samples = Tone(-14);
        Assert.Same(samples, QuietSpeechNormalizer.Apply(samples));
    }

    [Fact]
    public void QuietSpeechIsRaisedToTargetPeak()
    {
        var result = QuietSpeechNormalizer.Apply(Tone(-30));
        Assert.InRange(PeakDb(result), -6.5, -5.5);
    }

    [Fact]
    public void GainIsCapped()
    {
        var result = QuietSpeechNormalizer.Apply(Tone(-60));
        Assert.InRange(PeakDb(result), -30.5, -29.5);
    }

    [Fact]
    public void PureNoiseAndSilenceAreNotAmplified()
    {
        var noise = Tone(-80);
        Assert.Same(noise, QuietSpeechNormalizer.Apply(noise));
        var silence = new float[800];
        Assert.Same(silence, QuietSpeechNormalizer.Apply(silence));
    }

    [Fact]
    public void InputIsNeverMutated()
    {
        var samples = Tone(-30);
        var before = (float[])samples.Clone();
        QuietSpeechNormalizer.Apply(samples);
        Assert.Equal(before, samples);
    }
}
