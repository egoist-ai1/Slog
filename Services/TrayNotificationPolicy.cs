namespace Egoist.Voice.Services;

internal enum TrayNotificationKind
{
    MicrophoneUnavailable,
    HistoryFailure,
    ModelFailure,
    RecoveryRequired,
    DictationSucceeded,
    ActivationFallback
}

internal static class TrayNotificationPolicy
{
    internal static readonly TimeSpan DuplicateCooldown = TimeSpan.FromSeconds(30);

    internal static bool ShouldNotify(TrayNotificationKind kind, bool enabled) =>
        enabled && kind is not TrayNotificationKind.DictationSucceeded;

    internal static bool IsActionableHistoryMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("Не удалось", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("Недостаточно", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("Кодек", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("отсутств", StringComparison.OrdinalIgnoreCase));

    internal static bool IsDuplicate(
        TrayNotificationKind kind,
        string message,
        TrayNotificationKind previousKind,
        string previousMessage,
        TimeSpan elapsed) =>
        kind is not TrayNotificationKind.RecoveryRequired &&
        kind == previousKind &&
        string.Equals(message, previousMessage, StringComparison.Ordinal) &&
        elapsed < DuplicateCooldown;
}
