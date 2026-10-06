using System.Diagnostics;
using Egoist.Voice.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Egoist.Voice.Services;

/// <summary>
/// Single-engine Russian recognizer: Whisper large-v3-turbo (q5_0) forced to Russian, greedy, with
/// a sentence-style vocabulary prompt. The model is resident only while it is being used: after
/// <see cref="IdleUnloadDelay"/> without a decode the native context is released (RAM and VRAM go
/// back to the system) and the next recording reloads it, ideally while the user is still speaking.
/// </summary>
public sealed class WhisperRussianService : ITranscriptionEngine, ISampleTranscriptionService, IUnloadableEngine
{
    public static readonly TimeSpan IdleUnloadDelay = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IModelManager _modelManager;
    private readonly Func<long> _ticks;
    private readonly System.Threading.Timer _idleTimer;
    private WhisperFactory? _factory;
    private long _lastUsedTick;
    private volatile bool _disposed;

    static WhisperRussianService()
    {
        // Vulkan runs on any GPU driver without a CUDA toolkit; CPU is the safety net.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
    }

    public WhisperRussianService(IModelManager modelManager, Func<long>? ticks = null)
    {
        _modelManager = modelManager;
        _ticks = ticks ?? (() => Environment.TickCount64);
        _lastUsedTick = _ticks();
        _idleTimer = new System.Threading.Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public string EngineName => "Whisper";

    public bool IsLoaded => _factory is not null;

    public async Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            Touch();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath, IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        var samples = await Task.Run(() => AudioSampleReader.ReadMono16Khz(audioPath), cancellationToken)
            .ConfigureAwait(false);
        return await DecodeAsync(samples, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<TranscriptionResult> TranscribeSamplesAsync(
        float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        if (sampleRate != 16_000)
            throw new ArgumentException($"Ожидается 16000 Гц, получено {sampleRate}.", nameof(sampleRate));
        return DecodeAsync(samples, null, cancellationToken);
    }

    private async Task<TranscriptionResult> DecodeAsync(
        float[] samples, IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stopwatch = Stopwatch.StartNew();
        // Loading and decoding share one lock, so the idle unloader can never free the native
        // context underneath a running decode (that is an access violation, not an exception).
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var factory = await EnsureLoadedLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            var segments = new List<TranscriptSegment>();
            using (var processor = CreateProcessor(factory))
            {
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                    segments.Add(new TranscriptSegment(segment.Text, segment.Start, segment.End));
            }
            Touch();
            var text = WhisperOutputGuard.Clean(TranscriptFormatter.Format(segments));
            return new TranscriptionResult(text, stopwatch.Elapsed);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WhisperFactory> EnsureLoadedLockedAsync(
        IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        if (_factory is { } existing) return existing;

        var transfer = progress is null
            ? null
            : new Progress<ModelTransferProgress>(value => progress.Report(new ModelProgress(
                value.Stage switch
                {
                    ModelTransferStage.Verifying => "Проверяю модель…",
                    ModelTransferStage.Ready => "Модель готова",
                    _ => "Загружаю модель…"
                }, value.Percentage)));
        var path = await _modelManager.EnsureModelAsync(ModelCatalog.Whisper, transfer, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new ModelProgress("Запускаю модель…", 100));
        var clock = Stopwatch.StartNew();
        // Flash attention cuts the decode by about a third and lowers committed memory, with identical text
        // on the owner's corpus; fall back to the plain graph if a driver refuses it.
        var factory = await Task.Run(() =>
        {
            try { return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseFlashAttention = true }); }
            catch (Exception exception)
            {
                AppLog.Write("Whisper flash attention unavailable; using the standard graph", exception);
                return WhisperFactory.FromPath(path);
            }
        }, cancellationToken).ConfigureAwait(false);
        // A short pass compiles the GPU pipelines; without it the first real dictation pays seconds.
        await Task.Run(async () =>
        {
            using var processor = CreateProcessor(factory);
            await foreach (var _ in processor.ProcessAsync(new float[8_000], cancellationToken).ConfigureAwait(false)) { }
        }, cancellationToken).ConfigureAwait(false);
        _factory = factory;
        AppLog.Write($"Whisper loaded: runtime={RuntimeOptions.LoadedLibrary}, ms={clock.ElapsedMilliseconds}");
        return factory;
    }

    private static WhisperProcessor CreateProcessor(WhisperFactory factory) =>
        factory.CreateBuilder()
            .WithLanguage("ru")
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .WithPrompt(TechnicalTermCatalog.WhisperPrompt)
            .WithNoContext()
            .WithGreedySamplingStrategy()
            .Build();

    private void Touch() => Volatile.Write(ref _lastUsedTick, _ticks());

    private void CheckIdle()
    {
        if (_disposed || _factory is null) return;
        if (_ticks() - Volatile.Read(ref _lastUsedTick) < IdleUnloadDelay.TotalMilliseconds) return;
        TryUnload();
    }

    /// <summary>Releases the native model; false when it is already gone or currently in use.</summary>
    public bool TryUnload()
    {
        if (_disposed || _factory is null) return false;
        if (!_lock.Wait(0)) return false;
        try
        {
            if (_factory is null) return false;
            _factory.Dispose();
            _factory = null;
            AppLog.Write("Whisper unloaded after idle period");
            TrimProcessMemory();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint process);

    /// <summary>Returns the pages the model touched to the system instead of waiting for pressure.</summary>
    private static void TrimProcessMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch (Exception)
        {
            // Purely an optimization; never let it affect dictation.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        if (!_lock.Wait(TimeSpan.FromSeconds(2)))
        {
            AppLog.Write("Whisper decode still running at shutdown; native factory left to process teardown");
            return;
        }
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _lock.Release();
        }
        _modelManager.Dispose();
    }
}

/// <summary>
/// Whisper invents text on silence and noise ("Субтитры сделал DimaTorzok" and similar). The speech
/// gate normally keeps silence away from the decoder; this is the second line of defence.
/// </summary>
internal static class WhisperOutputGuard
{
    private static readonly string[] KnownHallucinations =
    [
        "субтитры сделал", "субтитры создавал", "субтитры подогнал", "субтитры добавил",
        "dimatorzok", "редактор субтитров", "корректор а.", "а.семкин", "а.егорова",
        "продолжение следует", "спасибо за просмотр", "спасибо за субтитры", "amara.org",
        "подписывайтесь на канал", "ставьте лайки"
    ];

    internal static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant().Replace('ё', 'е');
        foreach (var phrase in KnownHallucinations)
            if (lower.Contains(phrase, StringComparison.Ordinal)) return string.Empty;
        return CollapseRepetition(text.Trim());
    }

    /// <summary>Cuts a runaway loop ("да да да да ...") down to at most three repeats.</summary>
    private static string CollapseRepetition(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 8) return text;
        for (var size = 1; size <= 4; size++)
        {
            var run = 1;
            for (var i = size; i + size <= words.Length; i += size)
            {
                var same = true;
                for (var k = 0; k < size; k++)
                    if (!string.Equals(words[i + k], words[i - size + k], StringComparison.OrdinalIgnoreCase)) { same = false; break; }
                run = same ? run + 1 : 1;
                if (run >= 5)
                {
                    var keep = i - size * (run - 1) + size * 3;
                    return string.Join(' ', words.Take(keep));
                }
            }
        }
        return text;
    }
}
