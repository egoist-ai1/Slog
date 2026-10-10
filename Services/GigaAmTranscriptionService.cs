using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Egoist.Voice.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace Egoist.Voice.Services;

public sealed class GigaAmTranscriptionService : ITranscriptionEngine, ISampleTranscriptionService
{
    private const int SampleRate = RussianAsrProfile.SampleRate;
    internal static int BenchmarkSampleRate => SampleRate;
    internal static int BenchmarkDecodeThreads => RussianAsrProfile.GetDefaultThreads(Environment.ProcessorCount);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private readonly IModelManager _modelManager;
    private readonly bool _ownsModelManager;
    private readonly bool _enableContextualBias;
    private readonly int _inferenceThreads;
    private readonly IReadOnlyList<ModelDescriptor> _gigaDescriptors;
    private readonly string _engineName;
    private readonly bool _formattingEngine;
    private OfflineRecognizer? _recognizer;
    private volatile bool _disposed;

    internal bool ContextualBiasActive { get; private set; }
    internal int ContextualBiasPhraseCount { get; private set; }

    public GigaAmTranscriptionService(
        IModelManager? modelManager = null,
        bool enableContextualBias = false,
        int? inferenceThreads = null)
        : this(modelManager, enableContextualBias, inferenceThreads,
            ModelCatalog.CreateCompactModels(), "GigaAM", formattingEngine: false)
    {
    }

    private GigaAmTranscriptionService(
        IModelManager? modelManager,
        bool enableContextualBias,
        int? inferenceThreads,
        IReadOnlyList<ModelDescriptor> descriptors,
        string engineName,
        bool formattingEngine)
    {
        _modelManager = modelManager ?? new ModelManager(ModelCatalog.CreateRequiredModels());
        _ownsModelManager = modelManager is null;
        _enableContextualBias = enableContextualBias;
        _inferenceThreads = Math.Clamp(inferenceThreads ?? BenchmarkDecodeThreads, 1, 12);
        _gigaDescriptors = descriptors;
        _engineName = engineName;
        _formattingEngine = formattingEngine;
    }

    internal static GigaAmTranscriptionService CreateFormattingEngine(
        IModelManager modelManager, int? threads = null)
    {
        ArgumentNullException.ThrowIfNull(modelManager);
        return new GigaAmTranscriptionService(modelManager, enableContextualBias: false,
            threads ?? Math.Clamp(Environment.ProcessorCount, 1, 4),
            ModelCatalog.CreateFormattingModels(), "GigaAM v3 E2E RNNT", formattingEngine: true);
    }

    public string EngineName => _engineName;

    /// <summary>Только для стенда --asr-eval: подмена пути энкодера. В продакшене всегда null.</summary>
    internal string? EncoderPathOverride { get; set; }

    /// <summary>Только для стенда --asr-eval: подмена метода декодирования. В продакшене всегда null.</summary>
    internal string? DecodingMethodOverride { get; set; }

    internal OfflineRecognizerConfig CreateRecognizerConfiguration(IReadOnlyDictionary<string, string> paths)
    {
        var config = RussianAsrProfile.CreateRecognizerConfig(
            EncoderPathOverride ?? paths[_gigaDescriptors[0].Id], paths[_gigaDescriptors[1].Id],
            paths[_gigaDescriptors[2].Id], paths[_gigaDescriptors[3].Id], _inferenceThreads);
        if (DecodingMethodOverride is { Length: > 0 } method) config.DecodingMethod = method;
        return config;
    }

    public async Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_recognizer is not null)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is not null)
            {
                return;
            }

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var descriptor in _gigaDescriptors)
            {
                var transferProgress = progress is null
                    ? null
                    : new Progress<ModelTransferProgress>(value =>
                        progress.Report(new ModelProgress(GetProgressLabel(value), value.OverallPercentage)));
                paths[descriptor.Id] = await _modelManager.EnsureModelAsync(
                    descriptor,
                    transferProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (_enableContextualBias)
            {
                // E2E SentencePiece resources do not match the 34 character IDs in plain RNNT.
                // Keep legacy callers functional while truthfully reporting an unbiased decoder.
                AppLog.Write("GigaAM contextual bias disabled: reason=plain-rnnt-character-vocabulary");
            }

            progress?.Report(new ModelProgress(_formattingEngine ? "Запускаю оформление…" : "Запускаю GigaAM…", 100));
            var initialized = await Task.Run(
                () => new OfflineRecognizer(CreateRecognizerConfiguration(paths)),
                cancellationToken).ConfigureAwait(false);

            // The first two ONNX Runtime invocations pay for graph optimization and
            // arena setup and run several times slower than steady state. Prime them
            // here so the first real dictation does not carry that cost.
            try
            {
                await Task.Run(() => PrimeRecognizer(initialized), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
            catch
            {
                initialized.Dispose();
                throw;
            }
            ContextualBiasActive = false;
            ContextualBiasPhraseCount = 0;
            _recognizer = initialized;
            AppLog.Write(_formattingEngine
                ? "Russian formatter ready: engine=GigaAM v3 E2E RNNT"
                : "Russian ASR ready: engine=GigaAM v3 RNNT");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await WarmUpAsync(progress, cancellationToken).ConfigureAwait(false);
        await _decodeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Native decoding blocks its thread. Without this hop the loop can run on
            // the WPF dispatcher, freezing the capsule and starving the low-level mouse
            // hook until Windows evicts it on LowLevelHooksTimeout.
            var text = await Task.Run(
                () => DecodeFile(audioPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new TranscriptionResult(text, stopwatch.Elapsed);
        }
        finally
        {
            _decodeLock.Release();
        }
    }

    /// <summary>
    /// Decodes audio already in memory. The file path exists because the recording is written to
    /// disk anyway; a live phrase has no file and should not acquire one just to be read back.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeSamplesAsync(
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (sampleRate != SampleRate)
        {
            throw new ArgumentException($"Ожидается {SampleRate} Гц, получено {sampleRate}.", nameof(sampleRate));
        }

        var stopwatch = Stopwatch.StartNew();
        await WarmUpAsync(null, cancellationToken).ConfigureAwait(false);
        await _decodeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = await Task.Run(
                () => DecodeChunks(samples, null, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new TranscriptionResult(text, stopwatch.Elapsed);
        }
        finally
        {
            _decodeLock.Release();
        }
    }

    /// <summary>Above this, batching pays for the extra streams held in memory at once.</summary>
    private const int BatchDecodeThreshold = 2;
    internal static int BenchmarkBatchDecodeThreshold => BatchDecodeThreshold;

    private string DecodeFile(string path, IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        var decoded = new List<DecodedAudioChunk>();
        var batch = new List<GigaAmAudioChunk>(MaxBatchSize);
        foreach (var chunk in AudioSampleReader.ReadChunks(path, cancellationToken))
        {
            batch.Add(chunk);
            if (batch.Count < MaxBatchSize) continue;
            DecodePending();
        }
        if (batch.Count > 0) DecodePending();
        return TranscriptChunkJoiner.Join(decoded);

        void DecodePending()
        {
            for (var offset = 0; offset < batch.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ModelProgress("Распознаю", null));
                var size = GetBatchSize(batch, offset);
                DecodeBatch(batch, offset, size, decoded, cancellationToken);
                offset += size;
            }
            batch.Clear();
        }
    }

    private string DecodeChunks(
        float[] samples,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = GigaAmAudioChunker.Split(samples, SampleRate);
        return chunks.Count >= BatchDecodeThreshold
            ? DecodeBatched(chunks, progress, cancellationToken)
            : DecodeSequentially(chunks, progress, cancellationToken);
    }

    private string DecodeSequentially(
        IReadOnlyList<GigaAmAudioChunk> chunks,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var decoded = new List<DecodedAudioChunk>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModelProgress("Распознаю", index * 100d / chunks.Count));
            using var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(SampleRate, GetEngineSamples(chunks[index].Samples));
            _recognizer.Decode(stream);
            // Reading Result marshals token/timing arrays as well as text. Retrieve it once.
            var text = stream.Result.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                decoded.Add(new DecodedAudioChunk(text.Trim(), chunks[index].ParagraphBreakBefore));
            }
        }

        return TranscriptChunkJoiner.Join(decoded);
    }

    /// <summary>
    /// Number of chunks handed to the engine at once.
    /// </summary>
    /// <remarks>
    /// The batch used to be unbounded, which meant a thirty-minute recording built eighty-odd
    /// streams and pushed them through one encoder pass. sherpa pads a batch to its longest member,
    /// so activation memory grows linearly with batch size — that was the one place in the product
    /// where a long dictation could exhaust memory outright. Six keeps the encoder usefully busy
    /// while bounding the peak.
    /// </remarks>
    private const int MaxBatchSize = 6;
    internal static int BenchmarkMaxBatchSize => MaxBatchSize;

    /// <summary>
    /// Hands chunks of a long recording to the engine in bounded groups. The chunks are independent
    /// by construction — each gets a fresh stream — so the strict loop that decoded them one after
    /// another was serializing work the encoder can batch.
    /// </summary>
    private string DecodeBatched(
        IReadOnlyList<GigaAmAudioChunk> chunks,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var decoded = new List<DecodedAudioChunk>(chunks.Count);

        for (var offset = 0; offset < chunks.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = GetBatchSize(chunks, offset);
            progress?.Report(new ModelProgress("Распознаю", offset * 100d / chunks.Count));
            DecodeBatch(chunks, offset, size, decoded, cancellationToken);
            offset += size;
        }

        return TranscriptChunkJoiner.Join(decoded);
    }

    internal static int GetBatchSize(IReadOnlyList<GigaAmAudioChunk> chunks, int offset)
    {
        var size = Math.Min(MaxBatchSize, chunks.Count - offset);
        var longest = 0;
        for (var index = offset; index < offset + size; index++)
            longest = Math.Max(longest, chunks[index].Samples.Length);

        // Sherpa pads every batch member to the longest waveform. A short final tail
        // would pay for almost another full chunk; let the next bounded batch decode it.
        if (size > 1 && chunks[offset + size - 1].Samples.Length * 2 < longest)
            size--;
        return size;
    }

    private void DecodeBatch(
        IReadOnlyList<GigaAmAudioChunk> chunks,
        int offset,
        int size,
        List<DecodedAudioChunk> decoded,
        CancellationToken cancellationToken)
    {
        var streams = new OfflineStream[size];
        try
        {
            for (var index = 0; index < size; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                streams[index] = _recognizer!.CreateStream();
                streams[index].AcceptWaveform(SampleRate, GetEngineSamples(chunks[offset + index].Samples));
            }

            if (size == 1)
                _recognizer!.Decode(streams[0]);
            else
                _recognizer!.Decode(streams);

            for (var index = 0; index < size; index++)
            {
                var text = streams[index].Result.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    decoded.Add(new DecodedAudioChunk(text.Trim(), chunks[offset + index].ParagraphBreakBefore));
                }
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream?.Dispose();
            }
        }
    }

    private static void PrimeRecognizer(OfflineRecognizer recognizer)
    {
        // Verify two native encoder/decoder passes before reporting readiness. Silence is enough
        // to warm graph/arena setup without capturing or logging user audio.
        for (var pass = 0; pass < 2; pass++)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, new float[SampleRate / 10]);
            recognizer.Decode(stream);
            _ = stream.Result.Text;
        }
    }

    internal static float[] GetEngineSamples(ReadOnlyMemory<float> samples)
    {
        if (MemoryMarshal.TryGetArray(samples, out var segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length)
        {
            return segment.Array;
        }

        // Sherpa's managed API currently accepts only float[]. Keep chunk
        // planning zero-copy and materialize one bounded segment at decode time
        // instead of cloning every long-form chunk up front.
        return samples.ToArray();
    }

    private static string GetProgressLabel(ModelTransferProgress value) => value.Stage switch
    {
        ModelTransferStage.Verifying => "Проверяю GigaAM…",
        ModelTransferStage.Ready => "GigaAM готова",
        _ => "Загружаю GigaAM…"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Releasing the native session underneath an in-flight Decode() crashes the process, and
        // Decode() is not cancellable mid-chunk. If the slot cannot be taken, the recognizer is
        // deliberately leaked to process teardown — a leak on exit is strictly better than a
        // native crash, and waiting longer would only move the crash later.
        if (!_decodeLock.Wait(TimeSpan.FromSeconds(2)))
        {
            AppLog.Write("GigaAM decode still running at shutdown; native recognizer left to process teardown");
            return;
        }

        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _decodeLock.Release();
        }

        _decodeLock.Dispose();

        // The initialization lock is only released once warm-up leaves its finally block. If a
        // model download is still in flight, disposing it here would throw inside an unobserved
        // task; leave it to the GC instead.
        if (_initializationLock.CurrentCount == 1)
        {
            _initializationLock.Dispose();
        }

        if (_ownsModelManager)
        {
            _modelManager.Dispose();
        }
    }
}



internal static class AudioSampleReader
{
    internal static IEnumerable<GigaAmAudioChunk> ReadChunks(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        if (reader.WaveFormat.Channels > 2)
            throw new InvalidOperationException("Для многоканальной записи сначала выберите дорожку или экспортируйте моно/стерео WAV.");
        ISampleProvider provider = reader;
        if (reader.WaveFormat.Channels > 1) provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != 16_000) provider = new WdlResamplingSampleProvider(provider, 16_000);
        foreach (var chunk in ReadChunks(provider, cancellationToken)) yield return chunk;
    }

    internal static IEnumerable<GigaAmAudioChunk> ReadChunks(ISampleProvider provider,
        CancellationToken cancellationToken = default)
    {
        var sampleRate = provider.WaveFormat.SampleRate;
        var capacity = checked(GigaAmAudioChunker.DefaultMaxSeconds * sampleRate + 1);
        var buffer = new float[capacity];
        var filled = 0;
        var paragraphBefore = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (filled < capacity)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = provider.Read(buffer, filled, capacity - filled);
                if (read == 0) break;
                filled += read;
            }
            if (filled == 0) yield break;
            if (filled < capacity)
            {
                // Keep short recordings byte-for-byte identical to the memory path.
                yield return new GigaAmAudioChunk(buffer.AsMemory(0, filled), paragraphBefore);
                yield break;
            }

            // One sample of lookahead distinguishes the final window from a boundary.
            // Reuse the same silence search and overlap as in-memory transcription;
            // the adaptive level is estimated from this bounded window.
            var split = GigaAmAudioChunker.Split(buffer, sampleRate);
            var first = split[0];
            yield return first with { ParagraphBreakBefore = paragraphBefore };
            var nextStart = first.Samples.Length - sampleRate * GigaAmAudioChunker.OverlapMilliseconds / 1000;
            var remaining = filled - nextStart;
            var next = new float[capacity];
            buffer.AsSpan(nextStart, remaining).CopyTo(next);
            buffer = next;
            filled = remaining;
            paragraphBefore = split[1].ParagraphBreakBefore;
        }
    }

    internal static float[] ReadMono16Khz(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (reader.WaveFormat.Channels > 2)
            throw new InvalidOperationException("Для многоканальной записи сначала выберите дорожку или экспортируйте моно/стерео WAV.");
        if (reader.WaveFormat.Channels > 1)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        if (provider.WaveFormat.SampleRate != 16_000)
        {
            provider = new WdlResamplingSampleProvider(provider, 16_000);
        }

        // Compatibility callers explicitly need one contiguous waveform. Allow any
        // duration that fits memory, reserving space for buffer growth and the final copy.
        // Production file transcription uses ReadChunks and never takes this path.
        var maximumSamples = (int)Math.Min(Array.MaxLength,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (3L * sizeof(float)));
        var expected = (int)Math.Min(16_000 * 35, Math.Ceiling(reader.TotalTime.TotalSeconds * 16_000) + 1024);
        var writer = new ArrayBufferWriter<float>(Math.Max(1024, expected));
        var buffer = ArrayPool<float>.Shared.Rent(16_000);
        try
        {
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((long)writer.WrittenCount + read > maximumSamples)
                    throw new InvalidOperationException("Недостаточно памяти для загрузки всей записи. Используйте потоковое распознавание файла.");
                buffer.AsSpan(0, read).CopyTo(writer.GetSpan(read));
                writer.Advance(read);
            }
            return writer.WrittenSpan.ToArray();
        }
        catch (OutOfMemoryException exception)
        {
            throw new InvalidOperationException("Недостаточно памяти для загрузки всей записи. Используйте потоковое распознавание файла.", exception);
        }
        finally
        {
            writer.Clear();
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal sealed record GigaAmAudioChunk(ReadOnlyMemory<float> Samples, bool ParagraphBreakBefore);
internal sealed record DecodedAudioChunk(string Text, bool ParagraphBreakBefore);

internal static class GigaAmAudioChunker
{
    internal const int DefaultMaxSeconds = 35;
    private const int SearchSeconds = 4;
    private const int MinimumSilenceMilliseconds = 240;
    private const int ParagraphSilenceMilliseconds = 1050;
    internal const int OverlapMilliseconds = 240;

    /// <summary>Default pause threshold, further limited by the recording's active level.</summary>
    internal const double DefaultSilenceRms = 0.009;

    /// <summary>
    /// Upper bound (~-30.5 dBFS) for the adaptive threshold. Without it a loud recording would
    /// classify ordinary speech as silence and the splitter would cut mid-word everywhere.
    /// </summary>
    internal const double MaximumSilenceRms = 0.03;

    private const double NoiseFloorPercentile = 0.15;
    private const double NoiseFloorHeadroom = 1.8;

    internal static IReadOnlyList<GigaAmAudioChunk> Split(
        float[] samples,
        int sampleRate,
        int maxSeconds = DefaultMaxSeconds)
    {
        var maxSamples = maxSeconds * sampleRate;
        if (samples.Length <= maxSamples)
        {
            return [new GigaAmAudioChunk(samples.AsMemory(), false)];
        }

        var window = Math.Max(1, sampleRate * 20 / 1000);
        var silenceRms = EstimateSilenceThreshold(samples, window);
        var overlap = sampleRate * OverlapMilliseconds / 1000;
        var result = new List<GigaAmAudioChunk>();
        var start = 0;
        var paragraphBefore = false;
        while (start < samples.Length)
        {
            var hardEnd = Math.Min(samples.Length, start + maxSamples);
            var silence = hardEnd == samples.Length
                ? null
                : FindSilenceBoundary(samples, start, hardEnd, sampleRate, window, silenceRms);
            var end = silence?.Boundary ?? hardEnd;
            if (end <= start)
            {
                end = hardEnd;
            }
            result.Add(new GigaAmAudioChunk(samples.AsMemory(start, end - start), paragraphBefore));
            if (end == samples.Length)
            {
                break;
            }
            paragraphBefore = silence?.DurationMilliseconds >= ParagraphSilenceMilliseconds;

            // Overlap applies to every boundary, not only to the hard-cut fallback: a
            // detected pause still sits inside a breath, and the engine loses the word
            // straddling it when the next chunk starts exactly where the previous ended.
            start = Math.Max(start + 1, end - overlap);
        }
        return result;
    }

    /// <summary>
    /// Derives the silence threshold from the recording itself instead of a fixed constant.
    /// A pause must also be quieter than the recording's active level. With no energy contrast,
    /// prefer the bounded overlapping hard cut over inventing a pause in continuous quiet speech.
    /// </summary>
    internal static double EstimateSilenceThreshold(float[] samples, int window)
    {
        var windowCount = samples.Length / window;
        if (windowCount < 8)
        {
            return 0;
        }

        var energies = new double[windowCount];
        for (var index = 0; index < windowCount; index++)
        {
            energies[index] = WindowRms(samples, index * window, window);
        }

        Array.Sort(energies);
        var floor = energies[(int)(windowCount * NoiseFloorPercentile)];
        var activeLevel = energies[(int)(windowCount * (1 - NoiseFloorPercentile))];
        var noiseThreshold = Math.Clamp(floor * NoiseFloorHeadroom, DefaultSilenceRms, MaximumSilenceRms);
        return Math.Min(noiseThreshold, activeLevel / NoiseFloorHeadroom);
    }

    private static double WindowRms(float[] samples, int offset, int window)
    {
        var sum = 0d;
        for (var index = offset; index < offset + window; index++)
        {
            sum += samples[index] * samples[index];
        }
        return Math.Sqrt(sum / window);
    }

    private static SilenceBoundary? FindSilenceBoundary(
        float[] samples,
        int start,
        int hardEnd,
        int sampleRate,
        int window,
        double silenceRms)
    {
        var searchStart = Math.Max(start + sampleRate, hardEnd - (SearchSeconds * sampleRate));
        var runStart = -1;
        SilenceBoundary? best = null;
        for (var candidate = searchStart; candidate <= hardEnd - window; candidate += window)
        {
            if (WindowRms(samples, candidate, window) <= silenceRms)
            {
                runStart = runStart < 0 ? candidate : runStart;
                continue;
            }
            best = SelectBoundary(runStart, candidate, sampleRate, best);
            runStart = -1;
        }
        return SelectBoundary(runStart, hardEnd, sampleRate, best);
    }

    private static SilenceBoundary? SelectBoundary(int runStart, int runEnd, int sampleRate, SilenceBoundary? current)
    {
        if (runStart < 0)
        {
            return current;
        }
        var durationMs = (runEnd - runStart) * 1000d / sampleRate;
        if (durationMs < MinimumSilenceMilliseconds)
        {
            return current;
        }
        var candidate = new SilenceBoundary(runStart + ((runEnd - runStart) / 2), durationMs);

        // Prefer the longest confirmed pause, not the one closest to the hard limit:
        // a longer pause is a more reliable sentence boundary for the decoder.
        return current is null || candidate.DurationMilliseconds > current.DurationMilliseconds
            ? candidate
            : current;
    }

    private sealed record SilenceBoundary(int Boundary, double DurationMilliseconds);
}

internal static class TranscriptChunkJoiner
{
    private const int MaxOverlapWords = 12;

    internal static string Join(IReadOnlyList<DecodedAudioChunk> chunks)
    {
        var result = new List<string>();
        int? previousChunkWordCount = null;
        foreach (var chunk in chunks)
        {
            var words = chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var overlap = FindBoundaryOverlap(result, previousChunkWordCount, words);
            if (chunk.ParagraphBreakBefore && result.Count > 0)
            {
                result[^1] += Environment.NewLine + Environment.NewLine;
            }
            var appended = words.Length - overlap;
            result.AddRange(words.Skip(overlap));
            previousChunkWordCount = appended;
        }
        return string.Join(" ", result).Replace(Environment.NewLine + Environment.NewLine + " ", Environment.NewLine + Environment.NewLine);
    }

    /// <summary>
    /// Compares the tail of the previous chunk with the head of the next one. Comparison runs on
    /// normalized tokens because GigaAM v3 e2e emits punctuation, which made the previous exact
    /// match fail on every real boundary ("три," != "три"). The window is limited to the previous
    /// chunk so a phrase repeated later in the dictation cannot be swallowed.
    /// </summary>
    private static int FindBoundaryOverlap(
        IReadOnlyList<string> existing,
        int? previousChunkWordCount,
        IReadOnlyList<string> next)
    {
        // null means "no previous chunk yet"; 0 means the previous chunk contributed nothing, in
        // which case there is no boundary to deduplicate against and the window must stay closed
        // rather than silently widening to the whole transcript.
        var available = previousChunkWordCount is { } contributed
            ? Math.Min(contributed, existing.Count)
            : existing.Count;
        var max = Math.Min(MaxOverlapWords, Math.Min(available, next.Count));
        for (var count = max; count > 0; count--)
        {
            var matches = true;
            for (var index = 0; index < count; index++)
            {
                if (!TokensMatch(existing[existing.Count - count + index], next[index]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return count;
        }
        return 0;
    }

    private static bool TokensMatch(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        return normalizedLeft.Length > 0 &&
            string.Equals(normalizedLeft, Normalize(right), StringComparison.Ordinal);
    }

    private static string Normalize(string token)
    {
        Span<char> buffer = token.Length <= 64 ? stackalloc char[token.Length] : new char[token.Length];
        var length = 0;
        foreach (var character in token)
        {
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }
            var lowered = char.ToLowerInvariant(character);
            buffer[length++] = lowered is 'ё' ? 'е' : lowered;
        }
        return new string(buffer[..length]);
    }
}
