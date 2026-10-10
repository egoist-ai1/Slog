using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice;

public partial class MainWindow
{
    private Task? _modelWarmupTask;
    private const string ModelPreparingLabel = "Готовлю модель";

    /// <summary>
    /// Если модель ещё прогревается, ждёт её готовности (звук к этому моменту уже записан), показывая
    /// «Готовлю модель» с прогрессом. Ошибки прогрева не бросает: распознавание само догрузит модель.
    /// </summary>
    private async Task EnsureRecognitionReadyAsync(TakeContext take)
    {
        if (_modelWarmupTask is not { IsCompleted: false } warmup) return;
        _modelAwaiter = take;
        try
        {
            if (OwnsCapsule(take)) SetProcessingState(ModelPreparingLabel, null);
            await warmup.WaitAsync(take.Token);
        }
        catch (OperationCanceledException) when (!take.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("Model warm-up ended with an error before recognition", exception);
        }
        finally
        {
            if (ReferenceEquals(_modelAwaiter, take)) _modelAwaiter = null;
        }
        if (OwnsCapsule(take)) SetProcessingState("Распознаю", null);
    }
    public void BeginWarmUp(bool showProgress = true, bool announceModelDownloads = false)
    {
        _announceModelDownloads = announceModelDownloads;
        if (_disposed || _modelWarmupTask is { IsCompleted: false }) return;
        _modelWarmupTask = WarmUpAsync(showProgress);
    }

    private async Task WarmUpAsync(bool showProgress)
    {
        var progress = new Progress<ModelProgress>(value =>
        {
            if (_disposed) return;
            // Запись уже ждёт модель: прогресс прогрева показывает «Готовлю модель».
            if (_modelAwaiter is { } awaiter)
            {
                if (OwnsCapsule(awaiter)) SetProcessingState(ModelPreparingLabel, value.Percentage);
                return;
            }
            if (showProgress && !_isRecording && !_isProcessing)
            {
                SetProcessingState(value.Label, value.Percentage);
            }
        });

        try
        {
            await _transcription.WarmUpAsync(progress, _lifetimeCancellation.Token);
            if (!_disposed && showProgress && !_isRecording && !_isProcessing)
            {
                SetReadyState();
                ShowCapsule();
                ScheduleHide();
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            AppLog.Write("Model warm-up failed", exception);
            if (!_disposed && showProgress && !_isRecording && !_isProcessing)
            {
                ShowError("Модель не готова");
            }
            return;
        }
    }

    private void OnModelProgressChanged(object? sender, ModelTransferProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => HandleModelProgress(progress));
            return;
        }
        HandleModelProgress(progress);
    }

    private void HandleModelProgress(ModelTransferProgress progress)
    {
        if (_disposed) return;
        _lastModelProgress = progress;
        if (_isRecording || _isProcessing)
        {
            return;
        }

        if (_announceModelDownloads && !_backgroundDownloadAnnounced &&
            progress.Stage is ModelTransferStage.Downloading or ModelTransferStage.Verifying)
        {
            _backgroundDownloadAnnounced = true;
            _displayingBackgroundModelProgress = true;
            SetModelTransferState(progress);
            ShowCapsule();
            ScheduleHide(TimeSpan.FromSeconds(4));
            return;
        }

        if (_displayingBackgroundModelProgress && IsVisible)
        {
            SetModelTransferState(progress);
        }

        if (progress.Stage == ModelTransferStage.Ready && progress.ModelIndex == progress.ModelCount)
        {
            ShowModelsReady();
        }
        else if (progress.Stage == ModelTransferStage.Failed &&
                 progress.ModelName.StartsWith("Whisper", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write("Whisper fallback download failed; GigaAM remains available");
            SetReadyState();
            ShowCapsule();
            ScheduleHide();
        }
        else if (progress.Stage == ModelTransferStage.Failed)
        {
            ShowError("Модель не загружена");
        }
    }

    public void ShowModelDownloadStatus()
    {
        _displayingBackgroundModelProgress = true;
        if (_lastModelProgress is not null)
        {
            SetModelTransferState(_lastModelProgress);
        }
        else if (_modelManager.AreAllModelsReady)
        {
            ShowModelsReady();
            return;
        }
        else
        {
            SetProcessingState("Готовлю загрузку", null);
        }
        ShowCapsule();
        ScheduleHide(TimeSpan.FromSeconds(7));
    }

    public void RetryModelDownloads()
    {
        _displayingBackgroundModelProgress = true;
        ShowModelDownloadStatus();
        if (!_disposed && _modelWarmupTask is not { IsCompleted: false })
            _modelWarmupTask = WarmUpAsync(showProgress: true);
    }
}
