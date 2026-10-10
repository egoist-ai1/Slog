using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice;

/// <summary>
/// Жизненный цикл записей. Каждая запись (take) живёт в своём <see cref="TakeContext"/>: новое
/// нажатие разрешено, пока предыдущая фраза распознаётся; текст вставляется строго по порядку
/// отпускания, каждый — в своё окно-цель. Капсулой управляет самая новая активная запись, прежние
/// дорабатывают тихо.
/// </summary>
public partial class MainWindow
{
    private readonly List<TakeContext> _activeTakes = [];
    private readonly TakeSequencer _sequencer = new();
    private TakeContext? _latestTake;
    private long _takeCounter;
    private int _processingTakes;
    private bool _auxProcessing;
    private bool _isFinishingTail;
    private TakeContext? _modelAwaiter;

    /// <summary>Распознаётся хотя бы одна запись либо идёт служебная обработка (редактор текста).</summary>
    private bool _isProcessing
    {
        get => _auxProcessing || _processingTakes > 0;
        set => _auxProcessing = value;
    }

    internal nint CurrentTargetWindow => _latestTake?.TargetWindow ?? 0;
    internal int ActiveTakeCount => _activeTakes.Count;
    internal CapsuleVisualStateKind? CurrentVisualKind => _lastVisualStateKind;
    internal string? CurrentVisualLabel => _lastVisualLayout?.Label;

    private IEnumerable<Task?> PendingTakeTasks() =>
        _activeTakes.SelectMany(take => new[] { take.StartTask, take.StopTask });

    /// <summary>Капсулу показывает только самая новая активная запись.</summary>
    private bool OwnsCapsule(TakeContext take) =>
        !_disposed && _activeTakes.Count > 0 && ReferenceEquals(_activeTakes[^1], take);

    public async Task ToggleRecordingAsync()
    {
        if (_disposed || _isCancellingCapture || _isChangingCapture) return;
        if (_isRecording || _isStartingCapture) await EndRecordingAsync(Stopwatch.GetTimestamp());
        else if (CanStartRecording) await StartRecordingAsync(Stopwatch.GetTimestamp());
    }

    private Task StartRecordingAsync(long pressTimestamp)
    {
        // Окно-цель фиксируется один раз, до любых асинхронных операций с устройством.
        var take = new TakeContext(++_takeCounter, pressTimestamp,
            _interactionHooks.CaptureForegroundTarget(), _lifetimeCancellation.Token)
        {
            StartedUtc = DateTime.UtcNow
        };
        _activeTakes.Add(take);
        _latestTake = take;
        return take.StartTask = StartTakeAsync(take);
    }

    private async Task StartTakeAsync(TakeContext take)
    {
        AppLog.Write("StartRecording requested");
        _recentRecordings.StopPlayback();
        _forceHideAfterCancellation = false;
        _hideTimer.Stop();
        _recordingStartedUtc = take.StartedUtc;
        _isRecording = true;
        _interactionHooks.ArmCancel();
        // Капсула появляется сразу, без текста; «Подключаю» — только если старт затянется.
        SetArmingState();
        ShowCapsule();
        MeasureFirstFrame(take.PressTimestamp);
        _ = ShowConnectingIfSlowAsync(take, Stopwatch.GetTimestamp());
        // Модель могла не дойти до готовности: прогреваем параллельно с речью, звук копится.
        if (_modelWarmupTask is not { IsCompleted: false } && !AreRecognitionModelsReady)
            BeginWarmUp(showProgress: false, announceModelDownloads: _announceModelDownloads);
        try
        {
            await _captureInitializationTask;
            await WaitForEarlierCaptureAsync(take);
            await RunCaptureOperationAsync(() => _audioCapture.Start(take.PressTimestamp), take.Token);
            take.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            take.Phase = TakePhase.Recording;
            AppLog.Write($"Audio capture started, target=0x{take.TargetWindow:X}");
            OnCaptureStarted(take);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_latestTake, take)) _isRecording = false;
            if (!_isRecording) _interactionHooks.DisarmCancel();
            FinalizeTake(take);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_latestTake, take)) _isRecording = false;
            _pushToTalk.Reset();
            if (!_isRecording) _interactionHooks.DisarmCancel();
            AppLog.Write("StartRecording failed", exception);
            if (!_disposed && OwnsCapsule(take)) ShowError(GetMicrophoneError(exception));
            FinalizeTake(take);
        }
    }

    /// <summary>
    /// Start новой записи ставится в очередь только после того, как прежние записи отдали устройство:
    /// у отпущенной записи Stop появляется в очереди лишь после окончания её старта.
    /// </summary>
    private async Task WaitForEarlierCaptureAsync(TakeContext take)
    {
        foreach (var earlier in _activeTakes.TakeWhile(other => !ReferenceEquals(other, take)).ToArray())
            await earlier.CaptureReleased.Task.WaitAsync(take.Token);
    }

    /// <summary>Сигнал «можно говорить»: анимация и звук строго после успешного старта захвата.</summary>
    private void OnCaptureStarted(TakeContext take)
    {
        if (!OwnsCapsule(take)) return;
        // Тап короче порога: без сигнала и анимации, капсулу скроет конвейер остановки.
        if (take.ReleaseTimestamp != 0 && take.IsShortTapAt(take.ReleaseTimestamp)) return;
        SetListeningState();
        PlayReadyToSpeakAnimation();
        RefreshCapsuleAnimationEligibility();
        // Звук старта — только если пользователь ждал («Подключаю») и ещё не говорит. При быстром
        // старте он говорит сразу, и сигнал попал бы в начало записи.
        if (take.ConnectingShown) PlayFeedback(FeedbackSound.RecordingStarted);
    }

    private async Task ShowConnectingIfSlowAsync(TakeContext take, long startRequested)
    {
        try
        {
            // Task.Delay может сработать на несколько мс раньше по Stopwatch — доживаем до порога.
            while (Stopwatch.GetElapsedTime(startRequested) < ConnectingIndicatorPolicy.Threshold)
                await Task.Delay(ConnectingIndicatorPolicy.Threshold - Stopwatch.GetElapsedTime(startRequested)
                    + TimeSpan.FromMilliseconds(1), take.Token);
        }
        catch (OperationCanceledException) { return; }
        var stillPending = take.Phase == TakePhase.Starting && !_disposed && OwnsCapsule(take);
        if (ConnectingIndicatorPolicy.ShouldShow(Stopwatch.GetElapsedTime(startRequested), stillPending))
        {
            take.ConnectingShown = true;
            SetProcessingState("Подключаю", null);
        }
    }

    /// <summary>Пишет в лог время от нажатия до первого кадра капсулы; только число, без текста речи.</summary>
    private void MeasureFirstFrame(long pressTimestamp)
    {
        if (pressTimestamp == 0) return;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            AppLog.Write($"Capsule first frame: {PushToTalkTiming.ElapsedMs(pressTimestamp, Stopwatch.GetTimestamp()):0}ms");
        };
        CompositionTarget.Rendering += handler;
    }

    private Task EndRecordingAsync(long releaseTimestamp)
    {
        var take = _latestTake;
        if (take is null || (!_isRecording && !_isStartingCapture)) return Task.CompletedTask;
        if (take.ReleaseTimestamp == 0) take.ReleaseTimestamp = releaseTimestamp;
        return take.StopTask ??= FinishPendingRecordingAsync(take);
    }

    private async Task FinishPendingRecordingAsync(TakeContext take)
    {
        if (take.StartTask is { } starting) await starting;
        if (!_disposed && take.Phase == TakePhase.Recording && !take.IsCancellationRequested)
            await StopAndTranscribeAsync(take, take.ReleaseTimestamp);
        else
            FinalizeTake(take); // отменена между концом старта и этой проверкой: иначе утечка токена и записи
    }

    private async Task StopAndTranscribeAsync(TakeContext take, long releaseTimestamp)
    {
        var heldMs = PushToTalkTiming.ElapsedMs(take.PressTimestamp, releaseTimestamp);
        var shortTap = take.IsShortTapAt(releaseTimestamp);
        AppLog.Write($"StopAndTranscribe requested, held={heldMs / 1000:0.00}s");
        take.Phase = TakePhase.Finishing;
        take.Trace = new DictationTrace();
        take.Turn = _sequencer.Enter();
        take.CountedAsProcessing = true;
        _processingTakes++;
        if (ReferenceEquals(_latestTake, take)) _isRecording = false;
        // Пока хвост ещё пишется, капсула показывает сжимающуюся дугу; тап — без неё.
        if (OwnsCapsule(take) && !shortTap) BeginFinishingVisual();
        var cancellationToken = take.Token;
        var trace = take.Trace;
        var textSettings = _currentTextSettings;
        trace.Mark(DictationStage.CaptureStarted);
        string? audioPath = null;
        AudioCaptureResult? completedCapture = null;
        var recordingStatus = RecentRecordingStatus.ProcessingFailed;

        try
        {
            AudioCaptureResult capture;
            try
            {
                capture = await RunCaptureOperationAsync(
                    () => _audioCapture.StopAsync(releaseTimestamp, cancellationToken), cancellationToken);
            }
            finally { take.CaptureReleased.TrySetResult(); }
            completedCapture = capture;
            take.Phase = TakePhase.Recognizing;
            trace.Mark(DictationStage.CaptureStopped);
            if (OwnsCapsule(take))
            {
                EndFinishingVisual();
                if (!shortTap) PlayFeedback(FeedbackSound.RecordingStopped);
            }
            audioPath = capture.Path;
            AppLog.Write(
                $"Audio capture stopped: samples={capture.Samples.Length}, " +
                $"duration={capture.Duration.TotalSeconds:0.00}s, speech={capture.DetectedSpeech.TotalSeconds:0.00}s, " +
                $"peak={capture.PeakDecibels:0.0}dBFS");
            trace.Mark(DictationStage.SpeechChecked);
            if (!capture.HasSpeech)
            {
                AppLog.Write($"No speech detected ({capture.RejectionMessage ?? "unspecified"}); delivery skipped");
                EndProcessing(take);
                if (OwnsCapsule(take))
                {
                    // Короткий тап без речи — не ошибка: капсула тихо уходит.
                    if (!shortTap && capture.RejectionMessage is { Length: > 0 } reason) ShowError(reason);
                    else HideOrShowRemaining(take);
                }
                return;
            }
            if (OwnsCapsule(take)) SetProcessingState("Распознаю", null);
            await EnsureRecognitionReadyAsync(take);
            var progress = new Progress<ModelProgress>(value =>
            {
                if (!cancellationToken.IsCancellationRequested && OwnsCapsule(take) &&
                    RecognitionProgressPolicy.ShouldRenderEngineProgress(value.Label))
                {
                    // Recognition already owns a continuous orbit state. Chunk
                    // counts and percentages are engine details; repainting the
                    // state for every long-form chunk also restarts its motion.
                    SetProcessingState(value.Label, value.Percentage);
                }
            });
            var result = _transcription is ISampleTranscriptionService sampleTranscription
                ? await sampleTranscription.TranscribeSamplesAsync(
                    capture.Samples, capture.SampleRate, cancellationToken)
                : audioPath is not null
                    ? await _transcription.TranscribeAsync(audioPath, progress, cancellationToken)
                    : throw new NotSupportedException("Движок не поддерживает распознавание из памяти.");
            trace.Mark(DictationStage.PrimaryDecoded);
            var entityProfile = EntityProfilePolicy.ResolveForWindow(
                take.TargetWindow,
                result.Text,
                _mixedLanguageMode);
            var text = _postProcessor.Process(result.Text, entityProfile);
            trace.Mark(DictationStage.TextFormatted);
            AppLog.Write($"Transcription complete: characters={text.Length}, elapsed={result.Elapsed.TotalSeconds:0.00}s");

            if (string.IsNullOrWhiteSpace(text))
            {
                if (OwnsCapsule(take)) ShowError("Не услышал");
                return;
            }
            recordingStatus = RecentRecordingStatus.Recognized;

            // Голосовая команда «переведи …» / «… переведи на немецкий» идёт
            // только через проверенный current-user Engine Host. При ошибке
            // ничего не вставляем: оригинал нельзя выдавать за успешный перевод.
            var directive = textSettings.PreserveSpokenWords ? null : TranslateCommandParser.TryParse(text);
            if (directive is not null)
            {
                AppLog.Write($"Команда перевода: → {directive.TargetLanguage}, {directive.Payload.Length} симв.");
                if (OwnsCapsule(take)) SetProcessingState("Перевожу", null);
                var translation = await _translator.TranslateAsync(
                    directive.Payload,
                    directive.TargetLanguage,
                    label => Dispatcher.Invoke(() => { if (OwnsCapsule(take)) SetProcessingState(label, null); }),
                    cancellationToken);

                if (translation.Succeeded)
                {
                    text = translation.Text!;
                    AppLog.Write($"Перевод готов: {text.Length} симв.");
                }
                else
                {
                    AppLog.Write($"Перевод не вставлен: {translation.Failure}");
                    if (OwnsCapsule(take)) ShowError(translation.UserMessage);
                    return;
                }
            }

            var audioFormattingUnavailable = result.AudioFormatting == AudioFormattingStatus.Unavailable;
            string? formattingMessage = null;
            if (directive is null && textSettings.FormatWithQwen && !textSettings.PreserveSpokenWords)
            {
                if (OwnsCapsule(take)) SetProcessingState("Оформляю", null);
                var budget = double.IsFinite(textSettings.FormatBudgetSeconds)
                    ? Math.Clamp(textSettings.FormatBudgetSeconds, 0.5, 5) : 2;
                var formatted = await FormatTextWithHostAsync(text, textSettings.TextModelEndpoint,
                    textSettings.TextModelId, TimeSpan.FromSeconds(budget), allowWordCorrection: false, cancellationToken);
                text = formatted.Text;
                formattingMessage = formatted.Message;
                AppLog.Write($"Text formatting status={formatted.Status}; elapsedMs={formatted.Elapsed.TotalMilliseconds:0}; characters={text.Length}");
                trace.Mark(DictationStage.TextEnhanced);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Доставка строго по порядку отпускания: ждём все более ранние записи.
            await take.Turn!.WaitForTurnAsync(cancellationToken);
            var deliveryResult = await _delivery.DeliverAsync(text, take.TargetWindow, cancellationToken);
            trace.Mark(DictationStage.Delivered);
            LastOperationSummary = $"От отпускания до результата: {trace.Total.TotalSeconds:0.00} с" +
                (textSettings.PreserveSpokenWords ? " · дословно" :
                    formattingMessage is null ? " · быстрое оформление" : " · " + formattingMessage);
            if (audioFormattingUnavailable)
                LastOperationSummary += " · оформление временно недоступно";
            AppLog.Write($"Dictation timing: {trace.Format()}");
            var owns = OwnsCapsule(take);
            switch (deliveryResult.Status)
            {
                case DictationDeliveryStatus.Inserted:
                    if (owns) ShowSuccess(audioFormattingUnavailable ? "Вставлено без оформления" : "Вставлено");
                    break;
                case DictationDeliveryStatus.ClipboardFallback:
                    if (owns) ShowClipboardFallback();
                    break;
                case DictationDeliveryStatus.ClipboardFailed:
                    if (owns) ShowError("Буфер занят");
                    break;

                // Без этой ветки капсула оставалась в состоянии «Распознаю» навсегда: ни один из
                // показов не вызывался, а значит не вызывался и ScheduleHide. Пользователь при
                // этом вообще не узнавал, почему текст не появился.
                case DictationDeliveryStatus.SuppressedForSensitiveTarget:
                    if (owns) ShowError("Не вставляю в пароли");
                    break;

                default:
                    AppLog.Write($"Unhandled delivery status: {deliveryResult.Status}");
                    if (owns) ShowError("Ошибка");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("Recording operation cancelled");
            if (!_disposed && OwnsCapsule(take)) { EndFinishingVisual(); SetReadyState(); ScheduleHide(); }
        }
        catch (Exception exception)
        {
            // Ошибка одной записи не ломает следующую: очередь освобождается в finally.
            AppLog.Write("StopAndTranscribe failed", exception);
            if (!_disposed && OwnsCapsule(take)) { EndFinishingVisual(); ShowError("Ошибка"); }
        }
        finally
        {
            take.Turn?.Complete();
            EndProcessing(take);
            if (!_isRecording && !_isStartingCapture) _interactionHooks.DisarmCancel();

            // Persistence is deliberately queued only after capture has completed and speech has
            // been accepted. Escape/pause cancellation never reaches this branch, and Media
            // Foundation work runs on the bounded history worker rather than the UI/WASAPI thread.
            if (RecentRecordingPersistencePolicy.ShouldQueue(
                    completedCapture,
                    cancellationToken.IsCancellationRequested,
                    _saveRecentRecordings) &&
                completedCapture is { } accepted)
            {
                _recentRecordings.TryQueue(
                    accepted.Samples,
                    accepted.SampleRate,
                    accepted.Duration,
                    recordingStatus);
            }

            // Diagnostic/corpus mode can still return an explicit temporary WAV. Нормальная диктовка
            // идёт из памяти; сброс захвата нужен, только если остановка не завершилась, и только
            // пока новая запись не заняла устройство.
            if (completedCapture is null && audioPath is null && ReferenceEquals(take, _latestTake) &&
                !_isRecording && !_isStartingCapture && _captureCancelTask is not { IsCompleted: false })
                audioPath = await TryResolveDiscardedRecordingAsync();
            if (audioPath is not null)
            {
                TryDelete(audioPath);
            }
            FinalizeTake(take);
        }
    }

    private void EndProcessing(TakeContext take)
    {
        if (!take.CountedAsProcessing) return;
        take.CountedAsProcessing = false;
        _processingTakes--;
    }

    /// <summary>Закрывает запись: снимает учёт, освобождает очередь доставки и токен.</summary>
    private void FinalizeTake(TakeContext take)
    {
        if (take.Phase == TakePhase.Done) return;
        take.Phase = TakePhase.Done;
        take.CaptureReleased.TrySetResult();
        EndProcessing(take);
        _activeTakes.Remove(take);
        take.Turn?.Complete();
        take.Dispose();
    }

    private void CancelTake(TakeContext take)
    {
        take.Cancel();
        if (take.StopTask is null && take.StartTask is { IsCompleted: true }) FinalizeTake(take);
    }

    /// <summary>
    /// Запись закончилась без результата: если другая фраза ещё распознаётся, капсула возвращается
    /// к ней, иначе тихо уходит.
    /// </summary>
    private void HideOrShowRemaining(TakeContext finished)
    {
        if (_activeTakes.Any(other => !ReferenceEquals(other, finished) &&
                                      other.Phase is TakePhase.Finishing or TakePhase.Recognizing))
            SetProcessingState("Распознаю", null);
        else
            HideCapsuleAnimated();
    }

    private async Task<string?> TryResolveDiscardedRecordingAsync()
    {
        try
        {
            return await RunCaptureOperationAsync(_audioCapture.CancelAsync, cleanup: true);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not resolve discarded recording path", exception);
            return null;
        }
    }

    private async void CloseButton_OnClick(object sender, RoutedEventArgs e) => await CancelRecordingOrAllAsync();

    private async void OnCancelKeyPressed(object? sender, EventArgs e)
    {
        AppLog.Write("Dictation cancelled with the cancel key");
        await CancelRecordingOrAllAsync();
    }

    /// <summary>Esc и крестик отменяют идущую запись; если её нет — все распознающиеся.</summary>
    private Task CancelRecordingOrAllAsync() => RequestCancel(all: !(_isRecording || _isStartingCapture));

    /// <summary>Отмена всех активных записей (смена устройства, пауза микрофона).</summary>
    private Task CancelDictationAsync() => RequestCancel(all: true);

    private Task RequestCancel(bool all) =>
        _captureCancelTask is { IsCompleted: false } ? _captureCancelTask :
            _captureCancelTask = CancelDictationCoreAsync(all);

    private async Task CancelDictationCoreAsync(bool all)
    {
        _interactionHooks.DisarmCancel();
        var recording = _isRecording || _isStartingCapture;
        var targets = all
            ? _activeTakes.ToArray()
            : _latestTake is { } latest && _activeTakes.Contains(latest) ? [latest] : Array.Empty<TakeContext>();
        foreach (var target in targets) target.Cancel();
        _pushToTalk.Reset();
        _isCancellingCapture = true;
        var hadCapture = recording || targets.Any(target => target.Phase == TakePhase.Finishing);
        _isRecording = false;
        _isFinishingTail = false;
        StopWaveformAnimation();
        if (!_disposed)
        {
            var othersRunning = _activeTakes.Any(other => !targets.Contains(other) &&
                                                          other.Phase is TakePhase.Finishing or TakePhase.Recognizing);
            if (othersRunning) SetProcessingState("Распознаю", null);
            else HideCapsuleAnimated(forceAfterCancellation: true);
        }
        try
        {
            // Start cannot be interrupted inside the driver's synchronous call. Once it returns,
            // this same owned queue clears its buffer before any later Start/device operation.
            foreach (var target in targets)
                if (target.StartTask is { } starting) await starting;
            if (hadCapture) TryDelete(await CancelCaptureAsync());
            foreach (var target in targets)
            {
                if (target.StopTask is { } stopping) await stopping;
                else FinalizeTake(target);
            }
        }
        finally { _isCancellingCapture = false; }
    }

    private async Task<string?> CancelCaptureAsync()
    {
        try
        {
            return await RunCaptureOperationAsync(_audioCapture.CancelAsync, cleanup: true);
        }
        catch (Exception exception)
        {
            AppLog.Write("Cancel of the active capture failed", exception);
            return null;
        }
    }
}
