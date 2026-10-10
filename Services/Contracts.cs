namespace Egoist.Voice.Services;

public readonly record struct VoiceTimbreLevel(
    float Overall,
    float Bass,
    float Mid,
    float Treble)
{
    public VoiceSpectrum Spectrum { get; init; }
}

/// <summary>Eight measured frequency-band levels, normalized for the capsule display.</summary>
public readonly record struct VoiceSpectrum(float Band0, float Band1, float Band2, float Band3,
    float Band4, float Band5, float Band6, float Band7, bool IsMeasured = true)
{
    public float this[int index] => index switch
    {
        0 => Band0, 1 => Band1, 2 => Band2, 3 => Band3,
        4 => Band4, 5 => Band5, 6 => Band6, 7 => Band7,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<float>? LevelChanged;
    event EventHandler<VoiceTimbreLevel>? TimbreChanged;
    event EventHandler<AudioCaptureStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Completed 16 kHz mono session normalized to −1…1. Normal dictation consumes the same
    /// in-memory array directly; this event exists for optional observers and never causes a WAV.
    /// </summary>
    event EventHandler<float[]>? SamplesAvailable;

    AudioCaptureState GetState();
    IReadOnlyList<MicrophoneDeviceInfo> GetCaptureDevices();
    void SelectCaptureDevice(string? deviceId);
    void PauseMonitoring();
    void ResumeMonitoring();
    void SuppressFeedbackAudio(TimeSpan duration);

    void Start();
    Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// То же, но начало записи привязано к моменту нажатия (Stopwatch.GetTimestamp). Реализации без
    /// таймстемпов по умолчанию игнорируют момент и работают как Start().
    /// </summary>
    void Start(long pressTimestamp) => Start();

    /// <summary>То же, но хвост отсчитывается от момента отпускания (Stopwatch.GetTimestamp).</summary>
    Task<AudioCaptureResult> StopAsync(long releaseTimestamp, CancellationToken cancellationToken) =>
        StopAsync(cancellationToken);
    Task<string?> CancelAsync();
}

public sealed record MicrophoneDeviceInfo(
    string Id,
    string Name,
    bool IsDefault);

public sealed record AudioCaptureState(
    string? SelectedDeviceId,
    string DeviceName,
    bool IsPaused,
    bool IsMonitoring,
    bool IsAvailable,
    string? ErrorCode = null)
{
    /// <summary>Operational pause after device loss while automatic recovery remains intended.</summary>
    public bool IsTransientlyUnavailable { get; init; }

    /// <summary>A deliberate pause, rather than a temporary missing or failing endpoint.</summary>
    public bool IsUserPaused => IsPaused && !IsTransientlyUnavailable;
}

public enum AudioCaptureChangeKind
{
    InventoryChanged,
    DeviceChanged,
    DefaultDeviceChanged,
    Paused,
    Resumed,
    DeviceUnavailable
}

public sealed record AudioCaptureStateChangedEventArgs(
    AudioCaptureState State,
    AudioCaptureChangeKind Kind,
    bool ActiveTakeCancelled,
    string? UserMessage = null);

public sealed class MicrophoneUnavailableException : InvalidOperationException
{
    public MicrophoneUnavailableException(string message)
        : base(message)
    {
    }

    public MicrophoneUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record AudioCaptureResult(
    string? Path,
    float[] Samples,
    int SampleRate,
    bool HasSpeech,
    TimeSpan Duration,
    TimeSpan DetectedSpeech,
    double PeakDecibels,
    string? RejectionMessage = null);

public interface ITranscriptionService : IDisposable
{
    Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken);
    Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Benchmark-only observation of the existing hybrid decision. The raw candidates stay in memory;
/// callers may score them, but must never serialize their text.
/// </summary>
internal interface IBenchmarkTranscriptionService : ITranscriptionService
{
    Task<HybridTranscriptionObservation> TranscribeObservedAsync(
        string audioPath,
        CancellationToken cancellationToken);
}

internal interface ITranscriptionEngine : ITranscriptionService
{
    string EngineName { get; }
}

/// <summary>
/// An engine that can decode audio already in memory.
/// </summary>
/// <remarks>
/// Live transcription needs this: a phrase lasts a second or two, and writing it to disk and
/// reading it back would cost a meaningful share of the latency the feature exists to remove. A
/// separate interface rather than a member on <see cref="ITranscriptionService"/> so callers that
/// only transcribe files are not forced to care.
/// </remarks>
public interface ISampleTranscriptionService
{
    Task<TranscriptionResult> TranscribeSamplesAsync(
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken);
}

/// <summary>
/// An engine whose native model can be released without discarding the service. Implemented by the
/// mixed-language fallback, which is idle most of the time and expensive to keep resident.
/// </summary>
internal interface IUnloadableEngine
{
    /// <summary>Returns false when the model is already gone or currently in use.</summary>
    bool TryUnload();
}

internal interface ITranscriptCandidateSelector
{
    TranscriptSelection Select(string gigaAm, string whisper);
}

public interface ITextInsertionService
{
    Task InsertAsync(string text, nint targetWindow, CancellationToken cancellationToken);
}

public interface IClipboardService
{
    Task CopyAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// A clipboard that can hand back what it overwrote. Separate from <see cref="IClipboardService"/>
/// so a caller that only needs to copy is not forced to reason about restore semantics.
/// </summary>
public interface IRestorableClipboardService : IClipboardService
{
    Task<ClipboardSnapshot> CopyAsync(string text, bool captureSnapshot, CancellationToken cancellationToken);
    Task<bool> TryRestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IModelManager : IDisposable
{
    event EventHandler<ModelTransferProgress>? ProgressChanged;
    IReadOnlyList<ModelDescriptor> RequiredModels { get; }
    bool AreAllModelsReady { get; }
    ModelTransferProgress? CurrentProgress { get; }
    Task<string> EnsureModelAsync(
        ModelDescriptor descriptor,
        IProgress<ModelTransferProgress>? progress,
        CancellationToken cancellationToken);
    Task DownloadRequiredModelsAsync(CancellationToken cancellationToken);
}

public enum AudioFormattingStatus
{
    NotRequested,
    Completed,
    Unavailable
}

public sealed record TranscriptionResult(string Text, TimeSpan Elapsed)
{
    public AudioFormattingStatus AudioFormatting { get; init; }
}

/// <summary>
/// A percentage of <c>null</c> means indeterminate — a stage that has no meaningful fraction, such
/// as refining an already-decoded transcript. Rendering it as 0 % would read as "stuck".
/// </summary>
public sealed record ModelProgress(string Label, double? Percentage);

public enum ModelKind
{
    Speech
}

public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    ModelKind Kind,
    Uri DownloadUri,
    string FileName,
    long SizeBytes,
    string Sha256,
    bool Optional = false);

public enum ModelTransferStage
{
    Waiting,
    Downloading,
    Verifying,
    Loading,
    Ready,
    Failed
}

public sealed record ModelTransferProgress(
    string ModelName,
    int ModelIndex,
    int ModelCount,
    ModelTransferStage Stage,
    long BytesReceived,
    long TotalBytes,
    double Percentage,
    double OverallPercentage,
    double BytesPerSecond = 0,
    TimeSpan? EstimatedRemaining = null,
    string? Error = null);
