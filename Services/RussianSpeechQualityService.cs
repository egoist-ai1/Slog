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
        // Both models load at once: they use separate native contexts and the formatter's own
        // failures are absorbed by EnsureFormattingAsync, so the primary result is never delayed
        // by, or lost to, the formatter.
        var formatterWarmup = FormatSpeechPunctuation
            ? EnsureFormattingAsync(progress, cancellationToken)
            : Task.CompletedTask;
        try
        {
            await _primary.WarmUpAsync(progress, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _primaryReady = true;
        }
        catch
        {
            await ObserveAsync(formatterWarmup).ConfigureAwait(false);
            throw;
        }
        await formatterWarmup.ConfigureAwait(false);
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
        var queueMs = elapsed.Elapsed.TotalMilliseconds;
        try
        {
            var warmStart = elapsed.Elapsed;
            await WarmUpCoreAsync(null, cancellationToken).ConfigureAwait(false);
            var warmMs = (elapsed.Elapsed - warmStart).TotalMilliseconds;
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Even a fully warm engine must not do long-audio chunk planning on the WPF dispatcher.
            var planStart = elapsed.Elapsed;
            var chunks = await Task.Run(() => GigaAmAudioChunker.Split(QuietSpeechNormalizer.Apply(samples), sampleRate), cancellationToken)
                .ConfigureAwait(false);
            var planMs = (elapsed.Elapsed - planStart).TotalMilliseconds;
            var decoded = new List<DecodedAudioChunk>(chunks.Count);
            var format = FormatSpeechPunctuation;
            // Диагностика медленного распознавания: только замеры, поведение не меняется.
            var timings = new DecodeTimings();
            var before = RecognitionDiagnostics.Capture();
            foreach (var chunk in chunks)
            {
                var text = await DecodeChunkAsync(GigaAmTranscriptionService.GetEngineSamples(chunk.Samples), format, cancellationToken, timings)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    decoded.Add(new DecodedAudioChunk(text, chunk.ParagraphBreakBefore));
            }
            var after = RecognitionDiagnostics.Capture();
            WriteBreakdown(samples.Length / (double)sampleRate, chunks.Count, queueMs, warmMs, planMs, timings, before, after);
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

    /// <summary>Накопитель времени основного движка и добавки форматтера по кускам одной записи.</summary>
    private sealed class DecodeTimings
    {
        internal double PrimaryMs;
        internal double FormatterExtraMs;
    }

    private static void WriteBreakdown(
        double audioSeconds, int chunks, double queueMs, double warmMs, double planMs, DecodeTimings timings,
        RecognitionDiagnostics.Snapshot before, RecognitionDiagnostics.Snapshot after)
    {
        try
        {
            var decodeMs = Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalMilliseconds;
            AppLog.Write(RecognitionDiagnostics.Format(new RecognitionBreakdown(
                audioSeconds, chunks, queueMs, warmMs, planMs, decodeMs, timings.PrimaryMs, timings.FormatterExtraMs,
                after.ProcessCpuMs - before.ProcessCpuMs,
                RecognitionDiagnostics.SystemBusyPercent(before, after),
                after.PageFaults - before.PageFaults,
                after.WorkingSetMb,
                after.GcPauseMs - before.GcPauseMs)));
        }
        catch
        {
            // Диагностика не должна ломать распознавание.
        }
    }

    private async Task<string> DecodeChunkAsync(
        float[] samples, bool format, CancellationToken cancellationToken, DecodeTimings? timings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chunkStart = Stopwatch.GetTimestamp();
        // The formatter decodes the same samples concurrently with the primary engine: each engine
        // serializes on its own decode lock, so only wall-clock time changes, not the result.
        var primaryTask = ((ISampleTranscriptionService)_primary)
            .TranscribeSamplesAsync(samples, 16_000, cancellationToken);
        var formatterTask = format && _formattingReady
            ? ((ISampleTranscriptionService)_formatter).TranscribeSamplesAsync(samples, 16_000, cancellationToken)
            : null;
        TranscriptionResult primary;
        try
        {
            primary = await primaryTask.ConfigureAwait(false);
            if (timings is not null) timings.PrimaryMs += Stopwatch.GetElapsedTime(chunkStart).TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        catch
        {
            if (formatterTask is not null) await ObserveAsync(formatterTask).ConfigureAwait(false);
            throw;
        }
        if (formatterTask is null) return primary.Text;
        if (string.IsNullOrWhiteSpace(primary.Text))
        {
            await ObserveAsync(formatterTask).ConfigureAwait(false);
            return primary.Text;
        }
        try
        {
            var formatterWaitStart = Stopwatch.GetTimestamp();
            var secondary = await formatterTask.ConfigureAwait(false);
            if (timings is not null) timings.FormatterExtraMs += Stopwatch.GetElapsedTime(formatterWaitStart).TotalMilliseconds;
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

    /// <summary>Waits for a result that will not be used so its exception is never left unobserved.</summary>
    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
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
