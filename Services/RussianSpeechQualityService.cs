using System.Diagnostics;
using Egoist.Voice.Core;

namespace Egoist.Voice.Services;

/// <summary>Russian lexical recognition followed by audio-derived punctuation, without replacing words.</summary>
public sealed class RussianSpeechQualityService : ITranscriptionService, ISampleTranscriptionService
{
    private readonly ITranscriptionEngine _primary;
    private readonly ITranscriptionEngine _formatter;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Func<long> _ticks;
    private long _retryFormattingAfter;
    private volatile bool _formattingReady;
    private volatile bool _primaryReady;
    private volatile bool _disposed;
    private const long FormattingRetryDelayMs = 30_000;

    public bool FormatSpeechPunctuation { get; set; } = true;
    public bool FormattingAvailable => !_disposed && _formattingReady;
    public bool PrimaryAvailable => !_disposed && _primaryReady;

    public RussianSpeechQualityService(IModelManager manager)
        : this(new GigaAmTranscriptionService(manager, inferenceThreads: 4),
            GigaAmTranscriptionService.CreateFormattingEngine(manager, 4))
    {
    }

    internal RussianSpeechQualityService(
        ITranscriptionEngine primary, ITranscriptionEngine formatter, Func<long>? ticks = null)
    {
        if (primary is not ISampleTranscriptionService || formatter is not ISampleTranscriptionService)
            throw new ArgumentException("Both Russian engines must support memory transcription.");
        _primary = primary;
        _formatter = formatter;
        _ticks = ticks ?? (() => Environment.TickCount64);
    }

    public async Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WarmUpCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task WarmUpCoreAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _primary.WarmUpAsync(progress, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _primaryReady = true;
        if (FormatSpeechPunctuation)
            await EnsureFormattingAsync(progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task EnsureFormattingAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        if (_formattingReady || _ticks() < Volatile.Read(ref _retryFormattingAfter))
            return;
        try
        {
            progress?.Report(new ModelProgress("Готовлю оформление речи", null));
            await _formatter.WarmUpAsync(null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _formattingReady = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !_disposed)
        {
            _formattingReady = false;
            Volatile.Write(ref _retryFormattingAfter, _ticks() + FormattingRetryDelayMs);
            AppLog.Write("Audio punctuation unavailable; preserving the primary transcript", exception);
        }
    }

    public async Task<TranscriptionResult> TranscribeSamplesAsync(
        float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        if (sampleRate != 16_000)
            throw new ArgumentException("Ожидается 16000 Гц.", nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(samples);
        var elapsed = Stopwatch.StartNew();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WarmUpCoreAsync(null, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Even a fully warm engine must not do long-audio chunk planning on the WPF dispatcher.
            var chunks = await Task.Run(() => GigaAmAudioChunker.Split(QuietSpeechNormalizer.Apply(samples), sampleRate), cancellationToken)
                .ConfigureAwait(false);
            var decoded = new List<DecodedAudioChunk>(chunks.Count);
            var format = FormatSpeechPunctuation;
            foreach (var chunk in chunks)
            {
                var text = await DecodeChunkAsync(GigaAmTranscriptionService.GetEngineSamples(chunk.Samples), format, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    decoded.Add(new DecodedAudioChunk(text, chunk.ParagraphBreakBefore));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CreateResult(TranscriptChunkJoiner.Join(decoded), elapsed.Elapsed, format);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath, IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WarmUpCoreAsync(progress, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var format = FormatSpeechPunctuation;
            var text = await Task.Run(async () =>
            {
                var decoded = new List<DecodedAudioChunk>();
                foreach (var chunk in AudioSampleReader.ReadChunks(audioPath, cancellationToken))
                {
                    progress?.Report(new ModelProgress("Распознаю и оформляю", null));
                    var value = await DecodeChunkAsync(QuietSpeechNormalizer.Apply(GigaAmTranscriptionService.GetEngineSamples(chunk.Samples)), format, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(value))
                        decoded.Add(new DecodedAudioChunk(value, chunk.ParagraphBreakBefore));
                }
                return TranscriptChunkJoiner.Join(decoded);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CreateResult(text, elapsed.Elapsed, format);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<string> DecodeChunkAsync(float[] samples, bool format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var primary = await ((ISampleTranscriptionService)_primary)
            .TranscribeSamplesAsync(samples, 16_000, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!format || !_formattingReady || string.IsNullOrWhiteSpace(primary.Text))
            return primary.Text;
        try
        {
            var secondary = await ((ISampleTranscriptionService)_formatter)
                .TranscribeSamplesAsync(samples, 16_000, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var composed = AudioTranscriptComposer.Compose(primary.Text, secondary.Text);
            return AudioConfirmedNameFormatter.Apply(composed, secondary.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !_disposed)
        {
            _formattingReady = false;
            Volatile.Write(ref _retryFormattingAfter, _ticks() + FormattingRetryDelayMs);
            AppLog.Write("Audio punctuation failed; preserving the primary transcript", exception);
            return primary.Text;
        }
    }

    private TranscriptionResult CreateResult(string text, TimeSpan elapsed, bool format) =>
        new(text, elapsed)
        {
            AudioFormatting = !format ? AudioFormattingStatus.NotRequested :
                _formattingReady ? AudioFormattingStatus.Completed : AudioFormattingStatus.Unavailable
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _formattingReady = false;
        _primaryReady = false;
        // Each engine guards its own native context against an in-flight decode and warm-up.
        _primary.Dispose();
        _formatter.Dispose();
        // Waiters may still release this semaphore after cancellation during native decoding.
        // It has no native wait handle, so leave it alive until the service is collected.
    }
}
