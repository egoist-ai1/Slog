namespace Egoist.Voice.Controls;

/// <summary>
/// Статистика интервалов кадров за сеанс показа капсулы: только числа, без текста речи.
/// Включается переменной EGOIST_VOICE_FRAME_STATS=1.
/// </summary>
internal sealed class FrameIntervalStats
{
    private readonly List<double> _intervalsMs = new(1024);
    private TimeSpan _last;
    private bool _hasLast;

    internal static bool EnabledByEnvironment =>
        Environment.GetEnvironmentVariable("EGOIST_VOICE_FRAME_STATS") == "1";

    internal int FrameCount => _hasLast ? _intervalsMs.Count + 1 : 0;

    internal void Reset()
    {
        _intervalsMs.Clear();
        _hasLast = false;
    }

    /// <summary>WPF может вызвать Rendering дважды с одним временем кадра: такие повторы пропускаем.</summary>
    internal void Record(TimeSpan renderingTime)
    {
        if (_hasLast)
        {
            if (renderingTime <= _last) return;
            _intervalsMs.Add((renderingTime - _last).TotalMilliseconds);
        }
        _last = renderingTime;
        _hasLast = true;
    }

    /// <summary>Строка для лога или null, если кадров меньше двух.</summary>
    internal string? Summarize(int targetFps)
    {
        if (_intervalsMs.Count == 0) return null;
        var sorted = _intervalsMs.OrderBy(value => value).ToArray();
        var budget = 1000d / Math.Max(1, targetFps);
        var slow = sorted.Count(value => value > budget * 1.5);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"frames={FrameCount} target={targetFps}Hz p50={Percentile(sorted, 0.50):0.00}ms p95={Percentile(sorted, 0.95):0.00}ms p99={Percentile(sorted, 0.99):0.00}ms slow(>1.5x)={slow}");
    }

    internal static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
