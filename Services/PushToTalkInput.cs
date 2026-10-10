using System.Diagnostics;
using System.Text;

namespace Egoist.Voice.Services;

/// <summary>Пороги ввода push-to-talk: в одном месте, чтобы тесты и сервисы не расходились.</summary>
internal static class PushToTalkTiming
{
    /// <summary>
    /// Отпускание и следующее нажатие одной кнопки ближе этого интервала — дребезг контактов
    /// микропереключателя, а не намерение пользователя: оба события игнорируются.
    /// </summary>
    internal const int ReleaseDebounceMs = 70;

    /// <summary>Нажатие короче этого — «короткий тап»: не ошибка, капсулу скрываем тихо.</summary>
    internal const int ShortTapMs = 180;

    internal static double ElapsedMs(long fromTimestamp, long toTimestamp) =>
        (toTimestamp - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

    internal static long FromMs(double milliseconds) =>
        (long)(milliseconds * Stopwatch.Frequency / 1000.0);
}

/// <summary>
/// Данные события push-to-talk. Тип наследует <see cref="EventArgs"/>, поэтому прежние обработчики
/// вида <c>(object? sender, EventArgs e)</c> подписываются как раньше.
/// </summary>
public sealed class PushToTalkEventArgs : EventArgs
{
    internal PushToTalkEventArgs(long pressTimestamp, long releaseTimestamp)
    {
        PressTimestamp = pressTimestamp;
        ReleaseTimestamp = releaseTimestamp;
    }

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> в момент нажатия (взят в самом колбэке хука или в
    /// WM_HOTKEY, до постановки в очередь UI). Нужен захвату звука для pre-roll.
    /// </summary>
    public long PressTimestamp { get; }

    /// <summary>Момент отпускания в тех же единицах; 0 для события нажатия.</summary>
    public long ReleaseTimestamp { get; }

    /// <summary>Сколько кнопка была зажата, мс; 0 для события нажатия.</summary>
    public double HeldMilliseconds => ReleaseTimestamp == 0
        ? 0
        : PushToTalkTiming.ElapsedMs(PressTimestamp, ReleaseTimestamp);

    /// <summary>Отпускание короче 180 мс: потребитель скрывает капсулу тихо, без ошибки.</summary>
    public bool IsShortTap => ReleaseTimestamp != 0 && HeldMilliseconds < PushToTalkTiming.ShortTapMs;
}

internal enum DebounceDownResult
{
    /// <summary>Настоящее новое нажатие.</summary>
    Press,

    /// <summary>Автоповтор при удержании.</summary>
    Repeat,

    /// <summary>Нажатие сразу после отпускания: дребезг, удержание продолжается.</summary>
    Bounce
}

/// <summary>
/// Подавление дребезга одной кнопки. Отпускание не применяется сразу: оно «зреет»
/// <see cref="PushToTalkTiming.ReleaseDebounceMs"/>, и если за это время пришло нажатие, оба
/// события отбрасываются. Класс без потоков и таймеров — вызывающий сам опрашивает
/// <see cref="TryCommitRelease"/>. Не потокобезопасен: все вызовы с одного потока.
/// </summary>
internal sealed class ButtonDebounce
{
    private bool _held;
    private bool _releasePending;
    private long _pressTimestamp;
    private long _releaseTimestamp;

    internal bool IsHeld => _held;
    internal bool ReleasePending => _releasePending;
    internal long PressTimestamp => _pressTimestamp;

    /// <remarks>Перед вызовом нужно вызвать <see cref="TryCommitRelease"/> с тем же временем.</remarks>
    internal DebounceDownResult OnDown(long timestamp)
    {
        if (_held && _releasePending)
        {
            if (PushToTalkTiming.ElapsedMs(_releaseTimestamp, timestamp) < PushToTalkTiming.ReleaseDebounceMs)
            {
                _releasePending = false;
                return DebounceDownResult.Bounce;
            }

            // Защита от нарушенного контракта: отпускание уже созрело, значит это новое нажатие.
            _held = false;
            _releasePending = false;
        }

        if (_held)
        {
            return DebounceDownResult.Repeat;
        }

        _held = true;
        _pressTimestamp = timestamp;
        return DebounceDownResult.Press;
    }

    /// <summary>True, если отпускание принято в ожидание и его надо подтвердить позже.</summary>
    internal bool OnUp(long timestamp)
    {
        if (!_held || _releasePending)
        {
            return false;
        }

        _releasePending = true;
        _releaseTimestamp = timestamp;
        return true;
    }

    /// <summary>Подтверждает отпускание, если оно прожило без повторного нажатия весь интервал.</summary>
    internal bool TryCommitRelease(long now, out PushToTalkEventArgs args)
    {
        args = null!;
        if (!_held || !_releasePending ||
            PushToTalkTiming.ElapsedMs(_releaseTimestamp, now) < PushToTalkTiming.ReleaseDebounceMs)
        {
            return false;
        }

        _held = false;
        _releasePending = false;
        args = new PushToTalkEventArgs(_pressTimestamp, _releaseTimestamp);
        return true;
    }

    /// <summary>Принудительное отпускание (сторож, Dispose): без ожидания.</summary>
    internal bool ForceRelease(long now, out PushToTalkEventArgs args)
    {
        args = null!;
        if (!_held)
        {
            return false;
        }

        var releaseTimestamp = _releasePending ? _releaseTimestamp : now;
        _held = false;
        _releasePending = false;
        args = new PushToTalkEventArgs(_pressTimestamp, releaseTimestamp);
        return true;
    }

    internal void Reset()
    {
        _held = false;
        _releasePending = false;
    }
}

/// <summary>
/// Кольцевой журнал сырых событий кнопки (≤64) для диагностики «не сработало». Пишет один поток
/// (поток хука), читает он же по таймеру сторожа — блокировки не нужны. Только тип события и
/// время, без содержимого текста и звука.
/// </summary>
internal sealed class PushToTalkEventTrace
{
    internal const int Capacity = 64;

    private readonly (long Timestamp, char Kind)[] _ring = new (long, char)[Capacity];
    private int _next;
    private int _count;

    internal int Count => _count;

    /// <param name="kind">'D' нажатие, 'U' отпускание, 'B' подавленный дребезг, 'R' автоповтор.</param>
    internal void Add(long timestamp, char kind)
    {
        _ring[_next] = (timestamp, kind);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    /// <summary>События от старых к новым: «D+0 U+95 B+130», время в мс от первого события.</summary>
    internal string Format()
    {
        if (_count == 0)
        {
            return "(empty)";
        }

        var builder = new StringBuilder();
        var start = (_next - _count + Capacity) % Capacity;
        var origin = _ring[start].Timestamp;
        for (var i = 0; i < _count; i++)
        {
            var (timestamp, kind) = _ring[(start + i) % Capacity];
            if (i > 0)
            {
                builder.Append(' ');
            }
            builder.Append(kind).Append('+').Append((long)PushToTalkTiming.ElapsedMs(origin, timestamp));
        }
        return builder.ToString();
    }

    internal void Clear()
    {
        _next = 0;
        _count = 0;
    }
}
