using Egoist.Voice.Services;

namespace Egoist.Voice.Core;

/// <summary>Стадии одной записи (take) от нажатия до конца обработки.</summary>
internal enum TakePhase
{
    /// <summary>Нажатие принято, захват ещё открывается.</summary>
    Starting,

    /// <summary>Захват идёт, можно говорить.</summary>
    Recording,

    /// <summary>Кнопка отпущена, дописывается хвост (StopAsync ещё не вернулся).</summary>
    Finishing,

    /// <summary>Звук получен: распознавание, оформление, ожидание очереди доставки.</summary>
    Recognizing,

    /// <summary>Запись завершена или отменена, ресурсы освобождены.</summary>
    Done
}

/// <summary>
/// Всё, что принадлежит одной записи: токен отмены, окно-цель вставки, время нажатия, трейс.
/// Раньше это были общие поля окна, поэтому новое нажатие во время распознавания предыдущей фразы
/// освобождало чужой токен и перезаписывало чужое окно-цель. Теперь у каждой записи свой набор.
/// </summary>
internal sealed class TakeContext
{
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal TakeContext(long id, long pressTimestamp, nint targetWindow, CancellationToken lifetime)
    {
        Id = id;
        PressTimestamp = pressTimestamp;
        TargetWindow = targetWindow;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        // Токен кешируется: после Dispose источника обращение к .Token бросает исключение.
        Token = _cancellation.Token;
    }

    internal long Id { get; }

    /// <summary>Stopwatch.GetTimestamp момента нажатия (нужен захвату для pre-roll).</summary>
    internal long PressTimestamp { get; }

    /// <summary>Окно, в которое попадёт текст этой записи; фиксируется один раз при нажатии.</summary>
    internal nint TargetWindow { get; }

    internal DateTime StartedUtc { get; set; }

    /// <summary>Момент отпускания; 0, пока кнопка не отпущена.</summary>
    internal long ReleaseTimestamp { get; set; }

    internal DictationTrace Trace { get; set; } = new();

    internal TakePhase Phase { get; set; } = TakePhase.Starting;

    internal Task? StartTask { get; set; }

    internal Task? StopTask { get; set; }

    /// <summary>Место этой записи в очереди доставки; задаётся при отпускании.</summary>
    internal TakeTurn? Turn { get; set; }

    /// <summary>Запись учтена в счётчике распознаваний окна (чтобы снять его ровно один раз).</summary>
    internal bool CountedAsProcessing { get; set; }

    /// <summary>
    /// Устройство захвата освобождено этой записью: StopAsync вернулся либо запись закрыта без него.
    /// Следующая запись не ставит Start в очередь раньше: иначе Start(B) оказался бы в очереди
    /// перед Stop(A), который появляется только после окончания старта A.
    /// </summary>
    internal TaskCompletionSource CaptureReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Пользователю показали «Подключаю»: он ждёт и ещё не говорит.</summary>
    internal bool ConnectingShown { get; set; }

    /// <summary>Кнопка записи сейчас удерживается: старт идёт или запись идёт, отпускания не было.</summary>
    internal bool IsHeld => Phase is TakePhase.Starting or TakePhase.Recording && ReleaseTimestamp == 0;

    internal CancellationToken Token { get; }

    internal bool IsCancellationRequested => Token.IsCancellationRequested;

    internal void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Нажатие короче порога «короткого тапа» (по метке отпускания).</summary>
    internal bool IsShortTapAt(long releaseTimestamp) =>
        releaseTimestamp != 0 &&
        PushToTalkTiming.ElapsedMs(PressTimestamp, releaseTimestamp) < PushToTalkTiming.ShortTapMs;

    internal void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _cancellation.Dispose();
    }
}

/// <summary>
/// Последовательная очередь доставки: запись получает «очередь» в порядке отпускания и вставляет
/// текст только после того, как завершились все более ранние записи — успешно, с ошибкой или отменой.
/// Распознавание при этом идёт параллельно.
/// </summary>
internal sealed class TakeSequencer
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    internal TakeTurn Enter()
    {
        lock (_gate)
        {
            var turn = new TakeTurn(_tail);
            _tail = turn.Completion;
            return turn;
        }
    }
}

internal sealed class TakeTurn
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _previous;

    internal TakeTurn(Task previous)
    {
        _previous = previous;
        // Очередь освобождается, только когда завершились и эта запись, и все более ранние: запись,
        // вышедшая без доставки (нет речи, ошибка), не должна пускать следующих вперёд предыдущих.
        Completion = Task.WhenAll(previous, _done.Task);
    }

    internal Task Completion { get; }

    /// <summary>Ждёт завершения всех более ранних записей. Отмена своей записи прерывает ожидание.</summary>
    internal Task WaitForTurnAsync(CancellationToken cancellationToken) => _previous.WaitAsync(cancellationToken);

    /// <summary>Освобождает очередь следующим записям. Безопасно вызывать повторно.</summary>
    internal void Complete() => _done.TrySetResult();
}

/// <summary>Когда показывать «Подключаю»: только если открытие захвата затянулось.</summary>
internal static class ConnectingIndicatorPolicy
{
    internal static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(150);

    internal static bool ShouldShow(TimeSpan startElapsed, bool startStillPending) =>
        startStillPending && startElapsed >= Threshold;
}
