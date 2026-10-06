using System.Windows.Media;
using Egoist.Voice.Services;

namespace Egoist.Voice.Controls;

internal static class CapsuleWaveformProfile
{
    internal const int BarCount = 15;
    internal const double BarWidth = 4;
    internal const double BarMargin = 2;
    internal const double BarHeight = 34;
    internal const double MinimumScale = 0.06;
    internal const double AmplitudeGamma = 0.55;
    internal static double TotalWidth => BarCount * (BarWidth + BarMargin * 2);
    internal static double PreferredWidth => TotalWidth;

    internal static double SmoothLevel(double current, double target, double deltaSeconds)
    {
        var timeConstant = target > current ? 0.010 : 0.115;
        var alpha = 1 - Math.Exp(-Math.Clamp(deltaSeconds, 1d / 240d, 0.05) / timeConstant);
        return current + (target - current) * alpha;
    }

    internal static double EdgeEnvelope(int index, int count)
    {
        var distance = count <= 1 ? 0 : Math.Abs(index - (count - 1) / 2d) / ((count - 1) / 2d);
        return 0.04 + 0.96 * Math.Pow(Math.Max(0, Math.Cos(distance * Math.PI / 2)), 1.15);
    }

    internal static double TargetScale(int index, int count, double level, double phase, bool reducedMotion,
        double bass = 0, double mid = 0, double treble = 0, VoiceSpectrum spectrum = default)
    {
        var envelope = EdgeEnvelope(index, count);
        var activity = Math.Pow(Math.Clamp(level, 0, 1), AmplitudeGamma);
        double detail;
        if (spectrum.IsMeasured)
        {
            // Eight independent bands fan out from the centre into fifteen mirrored bars.
            var band = count <= 1 ? 0 : (int)Math.Round(Math.Abs(index - (count - 1) / 2d) * 14 / (count - 1));
            detail = 0.18 + Math.Pow(Math.Clamp(spectrum[Math.Clamp(band, 0, 7)], 0, 1), 0.65) * 1.15;
        }
        else
        {
            // Level-only sources and diagnostics retain a bounded non-flat speech silhouette.
            detail = reducedMotion ? 0.8 : 0.25 + 0.75 * Math.Abs(Math.Sin(phase * 1.3 + index * 1.12));
            detail += Math.Clamp(bass, 0, 1) * envelope * 0.25;
        }
        return Math.Clamp(MinimumScale + activity * envelope * detail, MinimumScale, 1);
    }

    internal static double OpacityForLevel(double level) =>
        0.62 + Math.Min(1, Math.Pow(Math.Clamp(level, 0, 1), AmplitudeGamma) + 0.08) * 0.38;

    internal static SolidColorBrush CreateBarBrush(int index, int count)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(168, 255, 0));
        brush.Freeze();
        return brush;
    }
}
