using System.Diagnostics;
using System.Text.RegularExpressions;
using Egoist.Voice.Core;

namespace Egoist.Voice.Services;

public sealed class HybridTranscriptionService :
    ITranscriptionService,
    ISampleTranscriptionService
{
    /// <summary>
    /// Full memory-only dictation path. It preserves the same conditional Whisper policy as the
    /// file compatibility path; memory capture must not silently lose mixed-language accuracy.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeSamplesAsync(
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var pipeline = Stopwatch.StartNew();
        await WarmUpAsync(null, cancellationToken).ConfigureAwait(false);
        var giga = await CaptureSampleCandidateAsync(_gigaAm, samples, sampleRate, cancellationToken)
            .ConfigureAwait(false);
        var decision = giga.Result is null
            ? new MixedSpeechDecision(MixedSpeechTrigger.Requested, "primary engine failed")
            : _mixedSpeech.Inspect(giga.Result.Text, MixedLanguageMode);

        if (!decision.NeedsFallback || (FastModeNoWhisperRefinement && !string.IsNullOrWhiteSpace(giga.Result?.Text) && !MixedLanguageMode))
        {
            return giga.Result! with { Elapsed = pipeline.Elapsed };
        }

        AppLog.Write($"Mixed speech suspected trigger={decision.Trigger}; refining with Whisper");
        var warmUp = EnsureWhisperWarmUpStarted(force: true);
        if (!_whisperReady && _modelManager.AreAllModelsReady)
        {
            try
            {
                await warmUp.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("Whisper warm-up failed; keeping GigaAM result", exception);
            }
        }

        if (!_whisperReady)
        {
            return (giga.Result ?? throw giga.Error ?? new InvalidOperationException("Распознавание не дало результата."))
                with { Elapsed = pipeline.Elapsed };
        }

        Volatile.Write(ref _lastWhisperUseTicks, Environment.TickCount64);
        var whisper = await CaptureSampleCandidateAsync(_whisper, samples, sampleRate, cancellationToken)
            .ConfigureAwait(false);
        if (giga.Result is null && whisper.Result is null)
        {
            throw new AggregateException("Оба движка распознавания завершились ошибкой.", giga.Error!, whisper.Error!);
        }
        if (giga.Result is null)
        {
            AppLog.Write("Hybrid ASR fell back to Whisper after GigaAM failure", giga.Error);
            return whisper.Result! with { Elapsed = pipeline.Elapsed };
        }
        if (whisper.Result is null)
        {
            AppLog.Write("Hybrid ASR kept GigaAM after Whisper failure", whisper.Error);
            return giga.Result with { Elapsed = pipeline.Elapsed };
        }

        var selected = _selector.Select(giga.Result.Text, whisper.Result.Text);
        AppLog.Write(
            $"Hybrid ASR selected {selected.Engine}: gigaChars={giga.Result.Text.Length}, whisperChars={whisper.Result.Text.Length}");
        return new TranscriptionResult(
            selected.Text,
            pipeline.Elapsed);
    }

    private readonly ITranscriptionEngine _gigaAm;
    private readonly ITranscriptionEngine _whisper;
    private readonly ITranscriptCandidateSelector _selector;
    private readonly IModelManager _modelManager;
    /// <summary>Progress label that puts the capsule into its "refining terms" state.</summary>
    public const string RefiningLabel = "Уточняю термины";

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _warmUpGate = new();
    private MixedSpeechDetector _mixedSpeech = new();

    /// <summary>
    /// Forces the fallback for every dictation. Set by the tray toggle and, later, by per-application
    /// profiles for editors and terminals where English terms are the norm rather than the exception.
    /// </summary>
    public bool MixedLanguageMode { get; set; }

    /// <summary>
    /// When true and MixedLanguageMode is false, skips the secondary Whisper pass if GigaAM succeeded.
    /// Qwen handles term transliteration and English brand casing without paying Whisper's decode latency.
    /// </summary>
    public bool FastModeNoWhisperRefinement { get; set; }

    /// <summary>Extends the suspicion map with russifications derived from the user dictionary.</summary>
    public void UpdateVocabulary(IEnumerable<string> spokenForms) =>
        _mixedSpeech = new MixedSpeechDetector(MixedSpeechDetector.DeriveRussifiedForms(spokenForms));

    private Task? _whisperWarmUp;
    private System.Threading.Timer? _idleTimer;
    private long _lastWhisperUseTicks = Environment.TickCount64;
    private volatile bool _whisperReady;
    private volatile bool _whisperUnloaded;
    private volatile bool _disposed;

    /// <summary>Two minutes: keeps fallback warm during active sessions, frees ~600 MB quickly when idle.</summary>
    private const long WhisperIdleUnloadMs = 2 * 60 * 1000;

    public HybridTranscriptionService(
        IModelManager modelManager,
        bool enableContextualBias = false)
        : this(
            modelManager,
            new GigaAmTranscriptionService(modelManager, enableContextualBias),
            new WhisperTranscriptionService(modelManager),
            new MixedLanguageTranscriptSelector())
    {
    }

    internal HybridTranscriptionService(
        IModelManager modelManager,
        ITranscriptionEngine gigaAm,
        ITranscriptionEngine whisper,
        ITranscriptCandidateSelector selector)
    {
        _modelManager = modelManager;
        _gigaAm = gigaAm;
        _whisper = whisper;
        _selector = selector;
        if (whisper is IUnloadableEngine)
        {
            var period = TimeSpan.FromMinutes(1);
            _idleTimer = new System.Threading.Timer(OnIdleCheck, null, period, period);
        }
    }

    public async Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
    {
        await _gigaAm.WarmUpAsync(progress, cancellationToken).ConfigureAwait(false);

        // Fast Russian dictation does not use a successful primary result's fallback. Avoid
        // loading/priming it until a primary failure or an explicit mixed-language request.
        if (!FastModeNoWhisperRefinement || MixedLanguageMode)
        {
            _ = EnsureWhisperWarmUpStarted(force: false);
        }
    }

    /// <summary>
    /// WarmUpAsync is reachable both from start-up and from every dictation. A plain <c>??=</c>
    /// can start two warm-ups and overwrite the field, after which Dispose waits on the wrong task.
    /// </summary>
    /// <param name="force">
    /// False on the routine path, so a model released by the idle timer is not immediately reloaded
    /// by the next pure-Russian dictation. True only when the fallback is actually about to run.
    /// </param>
    private Task EnsureWhisperWarmUpStarted(bool force)
    {
        var existing = Volatile.Read(ref _whisperWarmUp);
        if (existing is not null)
        {
            return existing;
        }

        lock (_warmUpGate)
        {
            if (_disposed || (_whisperUnloaded && !force))
            {
                return Task.CompletedTask;
            }

            _whisperUnloaded = false;

            // Task.Run, not a bare call: an async method runs synchronously up to its first
            // genuinely incomplete await, and Whisper's prefix opens directories and stats model
            // files. On the calling path that prefix executed on the UI thread.
            return _whisperWarmUp ??= Task.Run(WarmUpWhisperInBackgroundAsync);
        }
    }

    /// <summary>
    /// Releases the fallback model after an idle stretch. Checked on a timer rather than at the
    /// end of a dictation so the memory comes back even when the user simply walks away.
    /// </summary>
    private void OnIdleCheck(object? state)
    {
        if (_disposed || !_whisperReady || _whisper is not IUnloadableEngine unloadable)
        {
            return;
        }

        lock (_warmUpGate)
        {
            // Re-read inside the lock. Checking the timestamp first and then taking the lock is a
            // time-of-check race: a dictation that starts in the same tick refreshes the stamp
            // after the check has already passed.
            if (_disposed ||
                !_whisperReady ||
                Environment.TickCount64 - Volatile.Read(ref _lastWhisperUseTicks) < WhisperIdleUnloadMs ||
                !unloadable.TryUnload())
            {
                return;
            }

            _whisperReady = false;
            _whisperUnloaded = true;
            _whisperWarmUp = null;
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken) =>
        (await TranscribeObservedAsync(audioPath, progress, cancellationToken).ConfigureAwait(false)).Result;

    internal Task<HybridTranscriptionObservation> TranscribeObservedAsync(
        string audioPath,
        CancellationToken cancellationToken) =>
        TranscribeObservedAsync(audioPath, progress: null, cancellationToken);

    private async Task<HybridTranscriptionObservation> TranscribeObservedAsync(
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pipeline = Stopwatch.StartNew();
        await WarmUpAsync(progress, cancellationToken).ConfigureAwait(false);

        // Deliberately no longer waiting for the Whisper warm-up here. That wait made the first
        // dictation after start-up hostage to a 574 MB model load, even for pure Russian speech
        // that would never touch the fallback.
        var giga = await CaptureCandidateAsync(_gigaAm, audioPath, progress, cancellationToken)
            .ConfigureAwait(false);

        // The fallback is now conditional. It used to run in parallel on every dictation and
        // dominated p95 while its output was discarded almost every time; the price of that
        // symmetry was roughly 0.28 s out of 0.4 s on a GPU machine and seconds on CPU-only.
        var decision = giga.Result is null
            ? new MixedSpeechDecision(MixedSpeechTrigger.Requested, "primary engine failed")
            : _mixedSpeech.Inspect(giga.Result.Text, MixedLanguageMode);

        if (!decision.NeedsFallback || (FastModeNoWhisperRefinement && !string.IsNullOrWhiteSpace(giga.Result?.Text) && !MixedLanguageMode))
        {
            return CompleteObservation(
                giga.Result!, giga, fallback: null, decision, "GigaAM",
                fallbackRan: false, fallbackUnavailable: false,
                selectionElapsed: TimeSpan.Zero, pipeline);
        }

        AppLog.Write($"Mixed speech suspected trigger={decision.Trigger}; refining with Whisper");
        progress?.Report(new ModelProgress(RefiningLabel, null));

        // Only now is it worth waiting for the fallback to be loadable — and only when the models
        // are actually on disk, otherwise a first run with no network would hang here.
        var warmUp = EnsureWhisperWarmUpStarted(force: true);
        if (!_whisperReady && _modelManager.AreAllModelsReady)
        {
            try
            {
                await warmUp.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("Whisper warm-up failed; keeping GigaAM result", exception);
            }
        }

        if (!_whisperReady)
        {
            var availableResult = giga.Result
                ?? throw giga.Error
                ?? new InvalidOperationException("Распознавание не дало результата.");
            return CompleteObservation(
                availableResult, giga, fallback: null, decision, "GigaAM",
                fallbackRan: false, fallbackUnavailable: true,
                selectionElapsed: TimeSpan.Zero, pipeline);
        }

        Volatile.Write(ref _lastWhisperUseTicks, Environment.TickCount64);
        var whisper = await CaptureCandidateAsync(_whisper, audioPath, null, cancellationToken)
            .ConfigureAwait(false);
        if (giga.Result is null && whisper.Result is null)
        {
            throw new AggregateException("Оба движка распознавания завершились ошибкой.", giga.Error!, whisper.Error!);
        }
        if (giga.Result is null)
        {
            AppLog.Write("Hybrid ASR fell back to Whisper after GigaAM failure", giga.Error);
            return CompleteObservation(
                whisper.Result!, giga, whisper, decision, "Whisper",
                fallbackRan: true, fallbackUnavailable: false,
                selectionElapsed: TimeSpan.Zero, pipeline);
        }
        if (whisper.Result is null)
        {
            AppLog.Write("Hybrid ASR kept GigaAM after Whisper failure", whisper.Error);
            return CompleteObservation(
                giga.Result, giga, whisper, decision, "GigaAM",
                fallbackRan: true, fallbackUnavailable: false,
                selectionElapsed: TimeSpan.Zero, pipeline);
        }

        var selectionStarted = Stopwatch.GetTimestamp();
        var selected = _selector.Select(giga.Result.Text, whisper.Result.Text);
        var selectionElapsed = Stopwatch.GetElapsedTime(selectionStarted);
        AppLog.Write(
            $"Hybrid ASR selected {selected.Engine}: gigaChars={giga.Result.Text.Length}, whisperChars={whisper.Result.Text.Length}");
        var selectedResult = new TranscriptionResult(
            selected.Text,
            giga.Result.Elapsed > whisper.Result.Elapsed ? giga.Result.Elapsed : whisper.Result.Elapsed);
        return CompleteObservation(
            selectedResult, giga, whisper, decision, selected.Engine,
            fallbackRan: true, fallbackUnavailable: false,
            selectionElapsed, pipeline);
    }

    private static HybridTranscriptionObservation CompleteObservation(
        TranscriptionResult result,
        EngineAttempt primary,
        EngineAttempt? fallback,
        MixedSpeechDecision decision,
        string selectedEngine,
        bool fallbackRan,
        bool fallbackUnavailable,
        TimeSpan selectionElapsed,
        Stopwatch pipeline)
    {
        pipeline.Stop();
        return new HybridTranscriptionObservation(
            result,
            primary.Result,
            fallback?.Result,
            decision.Trigger,
            selectedEngine,
            primary.Error is not null,
            fallback?.Error is not null,
            fallbackRan,
            fallbackUnavailable,
            selectionElapsed,
            pipeline.Elapsed);
    }

    private static async Task<EngineAttempt> CaptureCandidateAsync(
        ITranscriptionEngine engine,
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine
                .TranscribeAsync(audioPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return new EngineAttempt(result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"{engine.EngineName} transcription failed", exception);
            return new EngineAttempt(null, exception);
        }
    }

    private static async Task<EngineAttempt> CaptureSampleCandidateAsync(
        ITranscriptionEngine engine,
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (engine is not ISampleTranscriptionService sampleEngine)
        {
            return new EngineAttempt(null, new NotSupportedException($"{engine.EngineName} не поддерживает memory audio."));
        }

        try
        {
            var result = await sampleEngine
                .TranscribeSamplesAsync(samples, sampleRate, cancellationToken)
                .ConfigureAwait(false);
            return new EngineAttempt(result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"{engine.EngineName} memory transcription failed", exception);
            return new EngineAttempt(null, exception);
        }
    }

    private async Task WarmUpWhisperInBackgroundAsync()
    {
        try
        {
            await _whisper.WarmUpAsync(null, _lifetime.Token).ConfigureAwait(false);
            _whisperReady = true;
            AppLog.Write("Whisper mixed-language fallback ready");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            AppLog.Write("Whisper mixed-language fallback unavailable; GigaAM remains active", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _idleTimer?.Dispose();
        _idleTimer = null;
        _lifetime.Cancel();
        Task? warmUp;
        lock (_warmUpGate)
        {
            warmUp = _whisperWarmUp;
        }

        // Bounded, not two seconds: a cooperative warm-up observes cancellation almost
        // immediately, and anything slower is handed to the deferred path below rather
        // than stalling the UI thread that is closing the window.
        var warmUpFinished = warmUp is null;
        try
        {
            warmUpFinished = warmUp?.Wait(TimeSpan.FromMilliseconds(250)) != false;
        }
        catch (AggregateException)
        {
            // The background task already logged the model failure.
            warmUpFinished = true;
        }
        _gigaAm.Dispose();
        if (warmUpFinished)
        {
            _whisper.Dispose();
        }
        else
        {
            // A native warm-up may take a moment to observe cancellation. Never
            // release its runtime underneath the active call; finish cleanup as
            // soon as that task actually leaves the engine.
            AppLog.Write("Whisper warm-up is still stopping; native dispose deferred");
            _ = warmUp!.ContinueWith(
                _ => _whisper.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _lifetime.Dispose();
    }
}

internal sealed record EngineAttempt(TranscriptionResult? Result, Exception? Error);

internal sealed record HybridTranscriptionObservation(
    TranscriptionResult Result,
    TranscriptionResult? Primary,
    TranscriptionResult? Fallback,
    MixedSpeechTrigger FallbackTrigger,
    string SelectedEngine,
    bool PrimaryFailed,
    bool FallbackFailed,
    bool FallbackRan,
    bool FallbackUnavailable,
    TimeSpan SelectionElapsed,
    TimeSpan PipelineElapsed);

internal sealed record TranscriptSelection(string Text, string Engine);

internal sealed partial class MixedLanguageTranscriptSelector : ITranscriptCandidateSelector
{
    public TranscriptSelection Select(string giga, string whisper)
    {
        if (string.IsNullOrWhiteSpace(giga))
        {
            return new TranscriptSelection(whisper, "Whisper");
        }
        if (string.IsNullOrWhiteSpace(whisper))
        {
            return new TranscriptSelection(giga, "GigaAM");
        }

        // A Whisper run that loops on itself is a hallucination, not a better transcript.
        // This check comes first because every branch below is otherwise willing to take it.
        if (HasRepeatedNgram(whisper))
        {
            return new TranscriptSelection(giga, "GigaAM");
        }

        var gigaMetrics = Analyze(giga);
        var whisperMetrics = Analyze(whisper);
        var comparableLength = whisper.Length >= giga.Length * ComparableLengthLowerBound &&
            whisper.Length <= giga.Length * ComparableLengthUpperBound;
        var preservesMoreEnglish =
            whisperMetrics.LatinWords >= Math.Max(2, gigaMetrics.LatinWords + 2) ||
            whisperMetrics.TechnicalTerms > gigaMetrics.TechnicalTerms;
        var predominantlyEnglish = whisperMetrics.LatinRatio >= 0.45 &&
            whisperMetrics.LatinRatio >= gigaMetrics.LatinRatio + 0.15;

        // English speech legitimately produces a longer transcript than GigaAM's phonetic
        // Russian rendering, so this branch keeps a wider window than the mixed-speech one —
        // but it is no longer unbounded: a run several times longer is runaway decoding.
        var credibleEnglish = predominantlyEnglish &&
            whisperMetrics.LatinWords >= 3 &&
            whisper.Length <= giga.Length * EnglishLengthUpperBound;

        return (comparableLength && preservesMoreEnglish) || credibleEnglish
            ? new TranscriptSelection(whisper, "Whisper")
            : new TranscriptSelection(giga, "GigaAM");
    }

    private const double ComparableLengthLowerBound = 0.55;
    private const double ComparableLengthUpperBound = 1.5;
    private const double EnglishLengthUpperBound = 3.0;
    private const int RepeatNgramSize = 3;
    private const int RepeatNgramLimit = 3;

    /// <summary>
    /// Detects the classic runaway-decoding signature: the same word sequence emitted back to
    /// back several times. Consecutive repetition only, so genuine refrains stay intact.
    /// </summary>
    internal static bool HasRepeatedNgram(string text)
    {
        var words = WordRegex().Matches(text).Select(match => match.Value.ToLowerInvariant()).ToArray();
        if (words.Length < RepeatNgramSize * RepeatNgramLimit)
        {
            return false;
        }

        var repeats = 1;
        for (var index = RepeatNgramSize; index + RepeatNgramSize <= words.Length; index += RepeatNgramSize)
        {
            var identical = true;
            for (var offset = 0; offset < RepeatNgramSize; offset++)
            {
                if (!string.Equals(
                        words[index - RepeatNgramSize + offset],
                        words[index + offset],
                        StringComparison.Ordinal))
                {
                    identical = false;
                    break;
                }
            }

            repeats = identical ? repeats + 1 : 1;
            if (repeats >= RepeatNgramLimit)
            {
                return true;
            }
        }

        return false;
    }

    private static TranscriptMetrics Analyze(string text)
    {
        var words = WordRegex().Matches(text).Select(match => match.Value).ToArray();
        var latinWords = words.Count(word => word.Any(IsLatin));
        var technicalTerms = TechnicalTermCatalog.Terms.Count(term => ContainsCompleteTerm(text, term));
        var letters = text.Count(char.IsLetter);
        var latinLetters = text.Count(IsLatin);
        return new TranscriptMetrics(latinWords, technicalTerms, letters == 0 ? 0 : latinLetters / (double)letters);
    }

    private static bool IsLatin(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool ContainsCompleteTerm(string text, string term)
    {
        var start = 0;
        while ((start = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var end = start + term.Length;
            var after = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }
            start++;
        }
        return false;
    }

    [GeneratedRegex(@"[\p{L}\p{N}+#._-]+")]
    private static partial Regex WordRegex();

    private sealed record TranscriptMetrics(int LatinWords, int TechnicalTerms, double LatinRatio);
}
