namespace Egoist.Voice.Core;

internal enum CapsuleVisualStateKind
{
    Ready,
    Arming,
    Listening,
    Recognizing,
    Success,
    Clipboard,
    Downloading,
    Error
}

internal sealed record CapsuleVisualState(
    CapsuleVisualStateKind Kind,
    string? Label = null,
    double? Progress = null,
    bool CanCancel = false);
