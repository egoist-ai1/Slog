namespace Egoist.Voice.Controls;

/// <summary>Такт волны: min(частота монитора, 144) Гц; скорость движения не зависит от fps (шаг считается по реальному времени).</summary>
internal sealed class CapsuleAnimationCadence
{
    private int _targetFps = Motion.FallbackFrameRate;
    private bool _started;
    private bool _reducedMotion;
    private TimeSpan _lastFrame;
    private TimeSpan _nextFrame;

    /// <summary>Целевая частота обновлений; смена сохраняет фазу сетки кадров и действует со следующего кадра.</summary>
    internal int TargetFps
    {
        get => _targetFps;
        set
        {
            var fps = Math.Clamp(value, 30, Motion.MaxFrameRate);
            if (fps == _targetFps) return;
            _targetFps = fps;
            // Сетка кадров привязана к последнему принятому кадру: новый интервал считается от него.
            if (_started && !_reducedMotion) _nextFrame = _lastFrame + TimeSpan.FromSeconds(1d / fps);
        }
    }

    internal void Reset() => _started = false;

    internal bool TryAdvance(TimeSpan renderingTime, bool reducedMotion, out double deltaSeconds)
    {
        var interval = TimeSpan.FromSeconds(reducedMotion ? 0.1 : 1d / _targetFps);
        deltaSeconds = 0;
        if (!_started || _reducedMotion != reducedMotion || renderingTime < _lastFrame)
        {
            _started = true;
            _reducedMotion = reducedMotion;
            _lastFrame = renderingTime;
            _nextFrame = renderingTime + interval;
            deltaSeconds = interval.TotalSeconds;
            return true;
        }
        // Четверть интервала запаса: кадры монитора с джиттером в тики не должны выпадать через один.
        if (renderingTime + interval / 4 < _nextFrame) return false;

        deltaSeconds = Math.Clamp((renderingTime - _lastFrame).TotalSeconds, 1d / 240d,
            reducedMotion ? 0.2 : 0.05);
        _lastFrame = renderingTime;
        // Сетка кадров остаётся на месте: next = now + interval уронил бы 144 Гц до 48 fps.
        // После паузы пропущенные кадры отбрасываются одним шагом, без «догоняющей» серии.
        var skippedIntervals = (renderingTime - _nextFrame).Ticks / interval.Ticks + 1;
        _nextFrame += TimeSpan.FromTicks(skippedIntervals * interval.Ticks);
        return true;
    }
}

/// <summary>Owns one render-event subscription; hiding suspends a request, stopping cancels it.</summary>
internal sealed class CapsuleAnimationSubscription(Action attach, Action detach)
{
    internal bool IsRequested { get; private set; }
    internal bool IsAttached { get; private set; }

    internal void Start(bool eligible)
    {
        IsRequested = true;
        Refresh(eligible);
    }

    internal void Refresh(bool eligible)
    {
        var shouldAttach = IsRequested && eligible;
        if (shouldAttach == IsAttached) return;
        if (shouldAttach) attach();
        else detach();
        IsAttached = shouldAttach;
    }

    internal void Stop()
    {
        IsRequested = false;
        Refresh(eligible: false);
    }
}
