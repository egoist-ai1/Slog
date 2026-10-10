namespace Egoist.Voice.Core;

/// <summary>
/// Статический подъём громкости только для очень тихих записей. GigaAM считает признаки с фиксированным
/// полом энергии, поэтому на тихой речи первыми пропадают слабые согласные и окончания. Нормальная речь
/// не затрагивается: один коэффициент на всю запись и только при пике ниже порога. Это не AGC и не
/// обрезка по тишине — оба варианта на корпусе владельца ухудшали результат.
/// </summary>
public static class QuietSpeechNormalizer
{
    /// <summary>Обычная речь владельца даёт пик около −14 dBFS; ниже −24 запись считается тихой.</summary>
    public const double QuietPeakThresholdDb = -24;
    public const double TargetPeakDb = -6;
    public const double MaximumGainDb = 30;
    /// <summary>Ниже этого пика в записи только шум: усиливать нечего.</summary>
    public const double NoisePeakDb = -70;

    /// <summary>Возвращает тот же массив, если подъём не нужен, иначе масштабированную копию.</summary>
    public static float[] Apply(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        if (peak <= 0f) return samples;
        var peakDb = 20 * Math.Log10(peak);
        if (peakDb >= QuietPeakThresholdDb || peakDb < NoisePeakDb) return samples;

        var gainDb = Math.Min(TargetPeakDb - peakDb, MaximumGainDb);
        var gain = (float)Math.Pow(10, gainDb / 20);
        var result = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++)
            result[index] = Math.Clamp(samples[index] * gain, -1f, 1f);
        return result;
    }
}
