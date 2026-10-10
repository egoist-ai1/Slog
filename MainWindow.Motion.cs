using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Egoist.Voice.Controls;
using Egoist.Voice.Services;

namespace Egoist.Voice;

/// <summary>Частота кадров капсулы: min(refresh монитора, 144) Гц для волны и всех Storyboard; замер кадров по переменной окружения.</summary>
public partial class MainWindow
{
    private const int WmDisplayChange = 0x007E;
    private int _motionFrameRate = Motion.FallbackFrameRate;
    private int _displayRefreshHz;
    private FrameIntervalStats? _frameStats;
    private EventHandler? _frameStatsHandler;

    private void InitializeMotionRate()
    {
        _waveCadence.TargetFps = _motionFrameRate;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(MotionWindowHook);
            ApplyMotionFrameRate();
        };
        Closed += (_, _) => EndFrameStats();
    }

    private nint MotionWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Смена режима дисплея (в т.ч. частоты) приходит без смены DPI.
        if (message == WmDisplayChange) Dispatcher.BeginInvoke(ApplyMotionFrameRate);
        return 0;
    }

    /// <summary>
    /// Пересчитывает такт по монитору окна. Вызывается при показе, смене DPI и режима дисплея.
    /// Бегущие часы не меняются: новое значение действует с ближайшего запуска Storyboard.
    /// </summary>
    private void ApplyMotionFrameRate()
    {
        if (_disposed) return;
        var refresh = DisplayRefresh.Query(new WindowInteropHelper(this).Handle);
        var fps = Motion.ResolveFrameRate(refresh);
        if (refresh == _displayRefreshHz && fps == _motionFrameRate && _frameRateApplied) return;
        _displayRefreshHz = refresh;
        _motionFrameRate = fps;
        _frameRateApplied = true;
        _waveCadence.TargetFps = fps;
        foreach (var resource in Resources.Values)
        {
            if (resource is not Storyboard storyboard) continue;
            try { Timeline.SetDesiredFrameRate(storyboard, fps); }
            catch (InvalidOperationException) { /* замороженный Storyboard: оставляем как есть */ }
        }
        AppLog.Write($"Capsule motion rate: refresh={refresh}Hz -> {fps}fps");
    }

    private bool _frameRateApplied;

    private void BeginFrameStats()
    {
        if (!FrameIntervalStats.EnabledByEnvironment || _frameStats is not null) return;
        var stats = new FrameIntervalStats();
        _frameStats = stats;
        _frameStatsHandler = (_, args) =>
        {
            if (args is RenderingEventArgs rendering) stats.Record(rendering.RenderingTime);
        };
        CompositionTarget.Rendering += _frameStatsHandler;
    }

    /// <summary>Пишет в лог только числа: перцентили интервалов и число кадров дольше 1,5 целевого.</summary>
    private void EndFrameStats()
    {
        var stats = _frameStats;
        if (stats is null) return;
        CompositionTarget.Rendering -= _frameStatsHandler;
        _frameStats = null;
        _frameStatsHandler = null;
        var summary = stats.Summarize(_motionFrameRate);
        if (summary is not null)
            AppLog.Write($"Capsule frame stats: refresh={_displayRefreshHz}Hz {summary} waveSubscribed={_waveRendering}");
    }
}
