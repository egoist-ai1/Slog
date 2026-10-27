using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Egoist.Voice.Services;

/// <summary>
/// Observes a configured side mouse button globally without consuming the click. The trigger is
/// disabled while a detected game owns the foreground window, so in-game bindings remain intact.
/// </summary>
/// <remarks>
/// The hook lives on its own message-pumping thread rather than on the UI thread. A low-level
/// mouse hook is delivered to the thread that installed it, so on the UI thread every system
/// mouse event had to queue behind WPF layout and native ASR decoding — and Windows silently
/// evicts a hook whose callback exceeds LowLevelHooksTimeout (300 ms by default).
/// </remarks>
public sealed class MousePushToTalkService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmQuit = 0x0012;
    private const int WmTimer = 0x0113;
    private const int WmUser = 0x0400;
    private const uint PmNoRemove = 0x0000;
    private const uint WatchdogIntervalMs = 3_000;
    // Зрелость отпускания проверяется чуть позже окна дребезга, чтобы таймер не сработал раньше.
    private const int ReleaseCommitMarginMs = 10;
    // Доставка события хука дольше этого — задержка в системе, а не в нашем колбэке.
    private const long DeliveryLagLogMs = 50;
    private const uint LlmhfInjected = 1;

    /// <summary>Windows drops a hook whose callback exceeds LowLevelHooksTimeout (300 ms by default).</summary>
    private const long CallbackBudgetMicroseconds = 1_000;

    private readonly MouseSideButton _button;
    private readonly Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private LowLevelMouseProc? _callback;
    private ForegroundGameMonitor? _foreground;
    // Предыдущее окно сторожа шло при игре на переднем плане (захват границы окна).
    private bool _windowStartedInGame;
    private bool _watchdogPausedLogged;
    private Exception? _startupError;
    private nint _hook;
    private uint _threadId;
    private nuint _watchdogTimerId;
    private nuint _releaseTimerId;
    private readonly ButtonDebounce _debounce = new();
    private readonly PushToTalkEventTrace _trace = new();
    private bool _traceRequested;
    private int _suspectStreak;
    private int _reinstallStreak;
    private long _syntheticMoves;
    private long _lastHookTick;
    private long _watchdogWindowStart;
    private long _worstCallbackMicroseconds;
    private long _worstDeliveryLagMs;
    private Point _lastWatchdogCursor;
    // True from a swallowed button-down until its matching button-up, so the pair is never split.
    private bool _swallowUp;
    private volatile bool _ignoredForGame;
    private volatile bool _disposed;

    public MousePushToTalkService(MouseSideButton button)
    {
        _button = button;
        _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        _thread = new Thread(RunHookLoop)
        {
            IsBackground = true,
            Name = "EgoistVoice.InputHook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Installation stays synchronous so the activation-binding switch can still roll back
        // atomically when Windows refuses the hook.
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            // Never report success on a timeout: the caller relies on the exception to restore
            // the previous binding, and a half-started thread is worse than no hook at all.
            Dispose();
            throw new TimeoutException($"Поток ввода не запустился за 5 секунд ({ButtonName}).");
        }

        if (_startupError is not null)
        {
            Dispose();
            throw _startupError;
        }

        AppLog.Write($"{ButtonName} push-to-talk hook installed on dedicated input thread");
    }

    // Тип аргументов наследует EventArgs: подписчики с сигнатурой (object?, EventArgs) работают как раньше.
    public event EventHandler<PushToTalkEventArgs>? Pressed;
    public event EventHandler<PushToTalkEventArgs>? Released;

    private void RunHookLoop()
    {
        try
        {
            // Force the message queue into existence before anyone can post to it: a thread that
            // has never called a message function has no queue, and PostThreadMessage against it
            // fails silently — leaving this thread parked in GetMessage forever.
            PeekMessage(out _, 0, WmUser, WmUser, PmNoRemove);
            Volatile.Write(ref _threadId, GetCurrentThreadId());

            _callback = HookCallback;
            _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не удалось подключить {ButtonName}.");
            }

            _foreground = new ForegroundGameMonitor();
            _foreground.Start();
            LogHookEnvironment();
            _lastHookTick = Environment.TickCount64;
            _watchdogWindowStart = _lastHookTick;
            GetCursorPos(out _lastWatchdogCursor);

            // SetTimer with a null window ignores the requested id and allocates its own, which
            // is what arrives in WM_TIMER.wParam. Comparing against a hard-coded constant here
            // meant the watchdog never ran.
            _watchdogTimerId = SetTimer(0, 1, WatchdogIntervalMs, 0);
            if (_watchdogTimerId == 0)
            {
                AppLog.Write($"{ButtonName} watchdog timer unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        catch (Exception exception)
        {
            _startupError = exception;
            SignalStarted();
            return;
        }

        SignalStarted();

        while (!_disposed)
        {
            var result = GetMessage(out var message, 0, 0, 0);
            if (result == 0)
            {
                break;
            }
            if (result < 0)
            {
                AppLog.Write($"{ButtonName} message loop failed", new Win32Exception(Marshal.GetLastWin32Error()));
                break;
            }

            if (message.Message == WmTimer && _watchdogTimerId != 0 && message.WParam == _watchdogTimerId)
            {
                CheckHookHealth();
                continue;
            }
            if (message.Message == WmTimer && _releaseTimerId != 0 && message.WParam == _releaseTimerId)
            {
                OnReleaseTimer();
                continue;
            }
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_watchdogTimerId != 0)
        {
            KillTimer(0, _watchdogTimerId);
            _watchdogTimerId = 0;
        }
        CancelReleaseTimer();
        _foreground?.Dispose();
        _foreground = null;
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    /// <summary>
    /// The constructor abandons the wait after five seconds, so by the time this runs the event
    /// may already be disposed. Losing the signal is harmless; an escaping exception on a
    /// background thread would terminate the process.
    /// </summary>
    private void SignalStarted()
    {
        try
        {
            _started.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Если курсор двигался за окно, хук обязан был получить события. Когда не получил — Windows
    /// мог молча его снять, и хук нужно переустановить.
    /// </summary>
    /// <remarks>
    /// Сам по себе сдвиг курсора без событий хука ничего не доказывает: программные перемещения
    /// (SetCursorPos из других приложений, игры, удалённые рабочие столы) не проходят через
    /// низкоуровневый хук. В логах это давало десятки «hook stopped» без единого реально
    /// удерживаемого нажатия. Поэтому сдвиг засчитывается, только если за окно было настоящее
    /// пользовательское действие (GetLastInputInfo), а удерживаемая кнопка отпускается только
    /// при подтверждении в двух окнах подряд. Состояние кнопки у системы не спрашиваем: хук
    /// глушит события XBUTTON, поэтому система их не видела и GetAsyncKeyState всегда ответил бы
    /// «не нажата» — такая проверка ничего бы не защищала.
    /// </remarks>
    private void CheckHookHealth()
    {
        if (_ignoredForGame)
        {
            _ignoredForGame = false;
            AppLog.Write($"{ButtonName} ignored for foreground game: {_foreground?.ForegroundProcessName}");
        }

        var worst = Interlocked.Exchange(ref _worstCallbackMicroseconds, 0);
        var worstLag = Interlocked.Exchange(ref _worstDeliveryLagMs, 0);
        if (worst > CallbackBudgetMicroseconds)
        {
            AppLog.Write($"{ButtonName} hook callback peaked at {worst / 1000d:0.00} ms in the last window " +
                         $"(worst delivery lag {worstLag} ms)");
        }
        if (worstLag > DeliveryLagLogMs)
        {
            AppLog.Write($"{ButtonName} hook event delivery lag peaked at {worstLag} ms in the last window");
        }

        FlushTrace("watchdog");

        var windowStart = _watchdogWindowStart;
        var now = Environment.TickCount64;
        _watchdogWindowStart = now;
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var previous = _lastWatchdogCursor;
        _lastWatchdogCursor = cursor;
        var game = _foreground?.ForegroundIsGame == true;
        var gameWindow = game || _windowStartedInGame;
        _windowStartedInGame = game;
        if (!gameWindow)
        {
            _watchdogPausedLogged = false;
        }
        var cursorMoved = cursor.X != previous.X || cursor.Y != previous.Y;
        var hookSeen = Volatile.Read(ref _lastHookTick) >= windowStart;
        // Опрос последнего ввода нужен только для подозрительного окна.
        var realInput = cursorMoved && !hookSeen && HadRealInputSince(windowStart, now);
        var held = _debounce.IsHeld;
        var streak = _suspectStreak + 1;
        var decision = HookWatchdog.Evaluate(cursorMoved, hookSeen, realInput, held, streak, false, gameWindow);
        switch (decision.Verdict)
        {
            case WatchdogVerdict.Healthy:
                _suspectStreak = 0;
                _reinstallStreak = 0;
                return;
            case WatchdogVerdict.Synthetic:
                // Курсор сдвинут программно: хук тут ни при чём.
                _syntheticMoves++;
                _suspectStreak = 0;
                return;
            case WatchdogVerdict.GameForeground:
                // В игре Mouse 5 принадлежит игре: хук не трогаем, в лог пишем один раз за вход в игру.
                _suspectStreak = 0;
                _reinstallStreak = 0;
                if (!_watchdogPausedLogged)
                {
                    _watchdogPausedLogged = true;
                    AppLog.Write($"{ButtonName} watchdog paused for foreground game: {_foreground?.ForegroundProcessName}");
                }
                return;
            case WatchdogVerdict.Wait:
                // Держим: ждём подтверждения следующим окном (_suspectStreak < 2), не рискуем ложным отпусканием.
                _suspectStreak = decision.NewStreak;
                return;
        }

        _suspectStreak = 0;

        // Новый хук ставится до снятия старого, чтобы не было окна без хука.
        var replacement = SetWindowsHookEx(WhMouseLl, _callback!, GetModuleHandle(null), 0);
        if (replacement == 0)
        {
            AppLog.Write($"{ButtonName} hook reinstall failed", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        var old = _hook;
        _hook = replacement;
        var unhookResult = "no old hook";
        var oldHookInvalid = false;
        if (old != 0)
        {
            var unhooked = UnhookWindowsHookEx(old);
            var error = Marshal.GetLastWin32Error();
            oldHookInvalid = !unhooked && error == HookWatchdog.ErrorInvalidHookHandle;
            unhookResult = unhooked ? "ok (old hook was alive)" : $"failed, Win32 error {error}";
        }

        _reinstallStreak++;
        if (_reinstallStreak == 1 || _reinstallStreak % 10 == 0)
        {
            AppLog.Write($"{ButtonName} hook stopped receiving events (input without hook events, " +
                         $"synthetic cursor moves ignored so far: {_syntheticMoves}); reinstalled, " +
                         $"foreground={_foreground?.ForegroundProcessName}, unhook(old)={unhookResult}, " +
                         $"reinstalls in a row={_reinstallStreak}");
            FlushTrace("hook-suspect");
        }

        // Потерянное отпускание: кнопку отпустили, пока хук был снят. Отпускаем, только если система
        // подтвердила, что старый хук снят (1404), и подозрительных окон подряд было два; живой хук —
        // только лог.
        if (!held)
        {
            return;
        }

        if (HookWatchdog.Evaluate(cursorMoved, hookSeen, realInput, held, streak, oldHookInvalid, gameWindow).ReleaseHeld)
        {
            AppLog.Write($"{ButtonName} was still held when the hook died; releasing");
            CancelReleaseTimer();
            if (_debounce.ForceRelease(Stopwatch.GetTimestamp(), out var args))
            {
                _swallowUp = false;
                FinishRelease(args);
            }
        }
        else
        {
            AppLog.Write($"{ButtonName} is held and the old hook was not invalidated; keeping the press");
        }
    }

    private void LogHookEnvironment()
    {
        try
        {
            var timeout = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop", "LowLevelHooksTimeout", null);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AppLog.Write($"{ButtonName} hook environment: LowLevelHooksTimeout={timeout?.ToString() ?? "(not set)"}, " +
                         $"elevated={elevated}");
        }
        catch (Exception exception)
        {
            AppLog.Write($"{ButtonName} hook environment unavailable", exception);
        }
    }

    /// <summary>Было ли за окно настоящее действие пользователя (мышь или клавиатура).</summary>
    private static bool HadRealInputSince(long windowStartTick, long nowTick)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return true; // не знаем — прежнее поведение: считаем подозрительным
        }

        var ageMs = unchecked((uint)Environment.TickCount - info.Time);
        return nowTick - ageMs >= windowStartTick;
    }

    private void FlushTrace(string reason)
    {
        if (!_traceRequested && reason == "watchdog")
        {
            return;
        }

        _traceRequested = false;
        AppLog.Write($"{ButtonName} xbutton trace [{reason}]: {_trace.Format()}");
    }

    private void OnReleaseTimer()
    {
        CancelReleaseTimer();
        var now = Stopwatch.GetTimestamp();
        if (_debounce.TryCommitRelease(now, out var args))
        {
            FinishRelease(args);
        }
        else if (_debounce.ReleasePending)
        {
            ArmReleaseTimer(); // таймер сработал раньше порога
        }
    }

    private void ArmReleaseTimer()
    {
        if (_releaseTimerId == 0)
        {
            _releaseTimerId = SetTimer(0, 0, (uint)(_debounce.ReleaseWindowMs + ReleaseCommitMarginMs), 0);
        }
    }

    private void CancelReleaseTimer()
    {
        if (_releaseTimerId != 0)
        {
            KillTimer(0, _releaseTimerId);
            _releaseTimerId = 0;
        }
    }

    private void FinishRelease(PushToTalkEventArgs args)
    {
        if (args.IsShortTap)
        {
            _traceRequested = true;
        }
        Raise(Released, args);
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return HookCallbackCore(code, message, data);
        }
        finally
        {
            RecordCallbackCost(Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Keeps the worst callback cost seen since the last watchdog tick. The acceptance criterion
    /// for this service is a sub-millisecond callback, and a criterion nobody measures is a wish.
    /// </summary>
    private void RecordCallbackCost(TimeSpan elapsed) =>
        RecordMax(ref _worstCallbackMicroseconds, (long)(elapsed.TotalMilliseconds * 1000));

    private static void RecordMax(ref long field, long value)
    {
        var previous = Volatile.Read(ref field);
        while (value > previous)
        {
            var exchanged = Interlocked.CompareExchange(ref field, value, previous);
            if (exchanged == previous)
            {
                return;
            }
            previous = exchanged;
        }
    }

    private nint HookCallbackCore(int code, nint message, nint data)
    {
        // Written for every event, including mouse moves: this is what the watchdog observes.
        Volatile.Write(ref _lastHookTick, Environment.TickCount64);

        var swallow = false;
        if (code >= 0 && !_disposed && (message == WmXButtonDown || message == WmXButtonUp))
        {
            try
            {
                var input = Marshal.PtrToStructure<MsllHookStruct>(data);
                if (MatchesButton(input.MouseData, _button))
                {
                    // Отметка берётся первым делом: потребителю нужен момент нажатия, а не момент,
                    // когда UI дошёл до обработчика.
                    var timestamp = Stopwatch.GetTimestamp();
                    // Задержка доставки: сколько событие ждало в очереди системы до нашего колбэка.
                    RecordMax(ref _worstDeliveryLagMs, unchecked((uint)Environment.TickCount - input.Time));
                    var injected = (input.Flags & LlmhfInjected) != 0;
                    swallow = message == WmXButtonDown
                        ? HandleDown(timestamp, injected)
                        : HandleUp(timestamp, injected);
                }
            }
            catch
            {
                // Swallowed deliberately: logging from inside a low-level hook costs a global
                // lock and a file append. Any repeated failure surfaces through the watchdog.
            }
        }

        // The configured dictation button is consumed so other applications (browser Back/Forward,
        // Claude, editors) never see it. Foreground games are the exception and get their click.
        if (swallow)
        {
            return 1;
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private bool HandleDown(long timestamp, bool injected)
    {
        // Созревшее, но не подтверждённое таймером отпускание фиксируем до разбора нового нажатия.
        if (_debounce.TryCommitRelease(timestamp, out var committed))
        {
            CancelReleaseTimer();
            FinishRelease(committed);
        }

        if (!_debounce.IsHeld && _foreground?.ForegroundIsGame == true)
        {
            // Logging is file I/O under a global lock. It must not happen here:
            // this callback is budgeted against LowLevelHooksTimeout. Record the
            // fact and let the watchdog tick write it out.
            _ignoredForGame = true;
            return false;
        }

        switch (_debounce.OnDown(timestamp))
        {
            case DebounceDownResult.Press:
                _trace.Add(timestamp, injected ? 'd' : 'D');
                _swallowUp = true;
                Raise(Pressed, new PushToTalkEventArgs(timestamp, 0));
                return true;
            case DebounceDownResult.Bounce:
                // Дребезг: отпускание не состоялось, запись продолжается как удерживаемая.
                CancelReleaseTimer();
                _trace.Add(timestamp, 'B');
                _traceRequested = true;
                _swallowUp = true;
                return true;
            default:
                _trace.Add(timestamp, 'R');
                return _swallowUp; // auto-repeat while held
        }
    }

    private bool HandleUp(long timestamp, bool injected)
    {
        if (_debounce.OnUp(timestamp))
        {
            _trace.Add(timestamp, injected ? 'u' : 'U');
            ArmReleaseTimer();
        }

        if (_swallowUp)
        {
            _swallowUp = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Handlers touch WPF, so they are posted to the UI dispatcher. The post is asynchronous on
    /// purpose — the callback must return long before LowLevelHooksTimeout.
    /// </summary>
    private void Raise(EventHandler<PushToTalkEventArgs>? handler, PushToTalkEventArgs args)
    {
        if (handler is null)
        {
            return;
        }

        if (_dispatcher is null)
        {
            handler(this, args);
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Send, () => handler(this, args));
    }

    internal static ushort HighWord(uint value) => (ushort)(value >> 16);
    internal static bool MatchesButton(uint mouseData, MouseSideButton button) =>
        HighWord(mouseData) == (ushort)button;

    private string ButtonName => _button == MouseSideButton.Mouse5 ? "Mouse 5" : "Mouse 4";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var stopped = RequestLoopExit();
        if (!stopped)
        {
            AppLog.Write($"{ButtonName} input thread did not stop; hook left to process teardown");
        }

        // Disposed only once the thread is known to be gone: SignalStarted still races with this
        // otherwise, and an ObjectDisposedException there would take the process down.
        if (stopped)
        {
            _started.Dispose();
        }
        AppLog.Write($"{ButtonName} push-to-talk hook removed");
    }

    /// <summary>
    /// Retries the quit post: the thread may not have created its message queue yet when a
    /// binding switch disposes a service moments after constructing it.
    /// </summary>
    private bool RequestLoopExit()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0 && PostThreadMessage(threadId, WmQuit, 0, 0))
            {
                return _thread.Join(TimeSpan.FromSeconds(2));
            }

            if (!_thread.IsAlive)
            {
                return true;
            }
            Thread.Sleep(25);
        }

        return !_thread.IsAlive;
    }


    private delegate nint LowLevelMouseProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint filterMin,
        uint filterMax,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, nuint timerId, uint interval, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint timerId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);


    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
