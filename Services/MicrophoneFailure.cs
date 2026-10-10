using System.IO;
using System.Runtime.InteropServices;

namespace Egoist.Voice.Services;

/// <summary>Причина, по которой не удалось открыть микрофон, в терминах, понятных пользователю.</summary>
internal enum MicrophoneFailureKind
{
    /// <summary>Устройства нет в списке: отключено, не выбрано, драйвера нет.</summary>
    NotFound,

    /// <summary>Устройство есть, но занято другим приложением или не открывается.</summary>
    BusyOrUnavailable,

    /// <summary>Доступ к микрофону запрещён настройками Windows.</summary>
    AccessDenied,

    /// <summary>Аудио-служба Windows ещё не запущена (типично при автозапуске).</summary>
    AudioServiceNotReady,

    Other
}

internal static class MicrophoneFailure
{
    internal const int AudioServiceNotReadyHResult = unchecked((int)0x8007007F);
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private const int DeviceInvalidatedHResult = unchecked((int)0x88890004);
    private const int DeviceInUseHResult = unchecked((int)0x8889000A);
    private const int ExclusiveInUseHResult = unchecked((int)0x8889000F);

    internal static bool IsAudioServiceNotReady(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is COMException com && com.HResult == AudioServiceNotReadyHResult) return true;
            if (current.InnerException is null) break;
        }
        return false;
    }

    internal static MicrophoneFailureKind Classify(Exception exception)
    {
        if (IsAudioServiceNotReady(exception)) return MicrophoneFailureKind.AudioServiceNotReady;
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            switch (current.HResult)
            {
                case AccessDeniedHResult:
                    return MicrophoneFailureKind.AccessDenied;
                case ElementNotFoundHResult:
                case DeviceInvalidatedHResult:
                    return MicrophoneFailureKind.NotFound;
                case DeviceInUseHResult:
                case ExclusiveInUseHResult:
                    return MicrophoneFailureKind.BusyOrUnavailable;
            }
            if (current.InnerException is null) break;
        }

        var message = exception.Message;
        if (message.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Access", StringComparison.OrdinalIgnoreCase))
            return MicrophoneFailureKind.AccessDenied;

        // Сам сервис захвата бросает MicrophoneUnavailableException, когда нужного устройства нет в
        // перечне активных; открытие найденного устройства падает иначе (COM/IO).
        if (exception is MicrophoneUnavailableException) return MicrophoneFailureKind.NotFound;
        if (exception is COMException or IOException) return MicrophoneFailureKind.BusyOrUnavailable;
        return MicrophoneFailureKind.Other;
    }

    /// <summary>Короткий текст для капсулы; различает «не найден» и «занят/недоступен».</summary>
    internal static string Message(Exception exception) => Classify(exception) switch
    {
        MicrophoneFailureKind.NotFound => "Микрофон не найден",
        MicrophoneFailureKind.BusyOrUnavailable => "Микрофон занят или недоступен",
        MicrophoneFailureKind.AccessDenied => "Разрешите доступ к микрофону",
        MicrophoneFailureKind.AudioServiceNotReady => "Аудиослужба Windows не готова",
        _ => "Микрофон недоступен"
    };
}

/// <summary>
/// Повтор старта захвата, когда аудио-служба Windows ещё не поднялась после входа в систему
/// (COMException 0x8007007F). Раньше приложение при этом молча завершалось.
/// </summary>
internal static class MicrophoneStartupRetry
{
    internal const int MaximumRetries = 10;

    /// <summary>Пауза перед повтором №<paramref name="retry"/> (с 1): 0,5 → 1 → 2 → 4 → 5 с и дальше 5 с.</summary>
    internal static TimeSpan Delay(int retry) => TimeSpan.FromSeconds(retry switch
    {
        <= 1 => 0.5,
        2 => 1,
        3 => 2,
        4 => 4,
        _ => 5
    });

    /// <summary>
    /// Создаёт захват; при «служба не готова» ждёт и повторяет до <see cref="MaximumRetries"/> раз.
    /// <paramref name="onWaiting"/> вызывается перед каждой паузой (статус «ожидаю микрофон»).
    /// Любая другая ошибка и исчерпание повторов пробрасываются вызывающему.
    /// </summary>
    internal static async Task<T> CreateAsync<T>(
        Func<Task<T>> create,
        Action<int>? onWaiting,
        Func<TimeSpan, CancellationToken, Task>? wait,
        CancellationToken cancellationToken)
    {
        wait ??= static (delay, token) => Task.Delay(delay, token);
        for (var retry = 0; ; retry++)
        {
            try { return await create().ConfigureAwait(false); }
            catch (Exception exception) when (retry < MaximumRetries && IsRetryable(exception, cancellationToken))
            {
                var next = retry + 1;
                AppLog.Write($"Audio service not ready; microphone start retry {next}/{MaximumRetries}", exception);
                onWaiting?.Invoke(next);
                await wait(Delay(next), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && MicrophoneFailure.IsAudioServiceNotReady(exception);
}
