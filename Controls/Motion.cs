using System.Windows;
using System.Windows.Media.Animation;

namespace Egoist.Voice.Controls;

/// <summary>Единые длительности и кривые движения; зеркало MotionTokens.xaml.</summary>
internal static class Motion
{
    internal const int EnterMs = 90;
    internal const int ExitMs = 140;
    internal const int ContentMs = 160;
    internal const int DiscMs = 180;
    internal const int RingMs = 280;
    internal const int ReducedMs = 80;

    /// <summary>Потолок такта анимаций: выше 144 Гц волна не выигрывает, а CPU растёт.</summary>
    internal const int MaxFrameRate = 144;

    /// <summary>Запасная частота, если Windows не отдала значение (0 и 1 означают «по умолчанию»).</summary>
    internal const int FallbackFrameRate = 60;

    internal static Duration Enter { get; } = new(TimeSpan.FromMilliseconds(EnterMs));
    internal static Duration Exit { get; } = new(TimeSpan.FromMilliseconds(ExitMs));
    internal static Duration Content { get; } = new(TimeSpan.FromMilliseconds(ContentMs));
    internal static Duration Disc { get; } = new(TimeSpan.FromMilliseconds(DiscMs));
    internal static Duration Ring { get; } = new(TimeSpan.FromMilliseconds(RingMs));
    internal static Duration Reduced { get; } = new(TimeSpan.FromMilliseconds(ReducedMs));

    /// <summary>Частота кадров анимации для монитора: min(refresh, 144), при неизвестной - 60.</summary>
    internal static int ResolveFrameRate(int refreshHz) =>
        refreshHz <= 1 ? FallbackFrameRate : Math.Clamp(refreshHz, 30, MaxFrameRate);

    internal static IEasingFunction EaseOut() => Frozen(EasingMode.EaseOut);

    internal static IEasingFunction EaseInOut() => Frozen(EasingMode.EaseInOut);

    private static CubicEase Frozen(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }
}
