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

    /// <summary>
    /// Отпускание раньше этого после нажатия — «глитч» (у кнопки Up приходит через ~40 мс после Down):
    /// настоящий палец так быстро не отпускает.
    /// </summary>
    internal const int GlitchReleaseMs = 60;

    /// <summary>
    /// Окно созревания отпускания-глитча: если за это время кнопку нажали снова, оба события
    /// отбрасываются и запись продолжается.
    /// </summary>
    internal const int GlitchGraceMs = 250;

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
    private int _releaseWindowMs = PushToTalkTiming.ReleaseDebounceMs;

    /// <summary>Сколько мс отпускание должно прожить без повторного нажатия (зависит от момента Up).</summary>
    internal int ReleaseWindowMs => _releaseWindowMs;

    internal bool IsHeld => _held;
    internal bool ReleasePending => _releasePending;
    internal long PressTimestamp => _pressTimestamp;

    /// <remarks>Перед вызовом нужно вызвать <see cref="TryCommitRelease"/> с тем же временем.</remarks>
    internal DebounceDownResult OnDown(long timestamp)
    {
        if (_held && _releasePending)
        {
            if (PushToTalkTiming.ElapsedMs(_releaseTimestamp, timestamp) < _releaseWindowMs)
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
        _releaseWindowMs = PushToTalkTiming.ElapsedMs(_pressTimestamp, timestamp) < PushToTalkTiming.GlitchReleaseMs
            ? PushToTalkTiming.GlitchGraceMs
            : PushToTalkTiming.ReleaseDebounceMs;
        return true;
    }

    /// <summary>Подтверждает отпускание, если оно прожило без повторного нажатия весь интервал.</summary>
    internal bool TryCommitRelease(long now, out PushToTalkEventArgs args)
    {
        args = null!;
        if (!_held || !_releasePending ||
            PushToTalkTiming.ElapsedMs(_releaseTimestamp, now) < _releaseWindowMs)
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

    /// <param name="kind">
    /// 'D' нажатие, 'U' отпускание, 'B' подавленный дребезг, 'R' автоповтор; строчные 'd'/'u' —
    /// синтетическое событие (LLMHF_INJECTED), которое послало ПО, а не железо.
    /// </param>
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

/// <summary>Что сторож хука делает по итогам окна.</summary>
internal enum WatchdogVerdict
{
    /// <summary>Хук жив или курсор не двигался: ничего не делаем.</summary>
    Healthy,

    /// <summary>Курсор сдвинут программно (SetCursorPos и т. п.): хук тут ни при чём.</summary>
    Synthetic,

    /// <summary>Кнопка удерживается, подозрение первое: ждём подтверждения следующим окном.</summary>
    Wait,

    /// <summary>Хук подозрителен: переустановить.</summary>
    Reinstall,

    /// <summary>На переднем плане игра, кнопка не удерживается: Mouse 5 принадлежит игре, сторож молчит.</summary>
    GameForeground
}

/// <param name="Verdict">Итог окна.</param>
/// <param name="NewStreak">Новое значение счётчика подозрительных окон подряд.</param>
/// <param name="ReleaseHeld">Принудительно отпустить удерживаемую кнопку (хук реально снят системой).</param>
internal readonly record struct WatchdogDecision(WatchdogVerdict Verdict, int NewStreak, bool ReleaseHeld)
{
    internal bool Reinstall => Verdict == WatchdogVerdict.Reinstall;
}

/// <summary>Чистое решение сторожа хука: без WinAPI и состояния, чтобы его можно было проверить тестами.</summary>
internal static class HookWatchdog
{
    /// <summary>Win32-ошибка UnhookWindowsHookEx для уже снятого системой хука.</summary>
    internal const int ErrorInvalidHookHandle = 1404;

    /// <param name="streak">Подозрительные окна подряд, включая текущее.</param>
    /// <param name="oldHookInvalid">
    /// UnhookWindowsHookEx(старый хук) вернул 1404, то есть хук реально снят системой. До переустановки
    /// это неизвестно — передаётся false, и отпускание решается повторным вызовом после неё.
    /// </param>
    internal static WatchdogDecision Evaluate(
        bool cursorMoved, bool hookSeenInWindow, bool realInput, bool held, int streak, bool oldHookInvalid,
        bool foregroundIsGame = false)
    {
        if (!cursorMoved || hookSeenInWindow)
        {
            return new WatchdogDecision(WatchdogVerdict.Healthy, 0, false);
        }

        // В игре отсутствие событий хука — норма (игра сама владеет Mouse 5): не переустанавливаем.
        if (foregroundIsGame && !held)
        {
            return new WatchdogDecision(WatchdogVerdict.GameForeground, 0, false);
        }

        if (!realInput)
        {
            return new WatchdogDecision(WatchdogVerdict.Synthetic, 0, false);
        }

        // Удерживаемую кнопку без подтверждения вторым окном не трогаем (streak < 2).
        if (held && streak < 2)
        {
            return new WatchdogDecision(WatchdogVerdict.Wait, streak, false);
        }

        // Отпускаем, только если и окон было два, и система подтвердила, что старый хук снят.
        return new WatchdogDecision(WatchdogVerdict.Reinstall, 0, held && streak >= 2 && oldHookInvalid);
    }
}
