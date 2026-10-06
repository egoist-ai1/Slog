using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Egoist.Voice.Controls;
using Egoist.Voice.Core;
using Egoist.Voice.Services;
using Microsoft.Win32;

namespace Egoist.Voice;

public partial class MainWindow : Window, IDisposable
{
    private static readonly SolidColorBrush ActiveDiscBrush = FrozenBrush("#A8FF00");
    private static readonly SolidColorBrush SuccessDiscBrush = FrozenBrush("#00000000");
    private static readonly SolidColorBrush CapsuleInkBrush = FrozenBrush("#000000");
    private static readonly SolidColorBrush CapsuleTextBrush = FrozenBrush("#FAFAFA");
    private SolidColorBrush IdleBorderBrush => CapsuleInkBrush;
    private System.Windows.Media.Brush ActiveBorderBrush => CapsuleInkBrush;
    private SolidColorBrush SurfaceBrush => CapsuleInkBrush;
    private SolidColorBrush PrimaryTextBrush => CapsuleTextBrush;
    private SolidColorBrush AccentBrush => ActiveDiscBrush;
    private SolidColorBrush ErrorBrush => ActiveDiscBrush;
    private System.Windows.Media.Brush ErrorBorderBrush => CapsuleInkBrush;
    private SolidColorBrush ProgressTrackBrush => ThemeBrush("AppMeterTrackBrush");

    private readonly IAudioCaptureService _audioCapture;
    private readonly CaptureOperationQueue _captureOperations;
    private volatile AudioCaptureState _captureState = new(null, "Микрофон", false, false, false);
    private volatile IReadOnlyList<MicrophoneDeviceInfo> _captureDevices = Array.Empty<MicrophoneDeviceInfo>();
    private Task _captureInitializationTask = Task.CompletedTask;
    private Task? _captureRefreshTask;
    private Task? _inventoryNotificationTask;
    private Task? _captureStartTask;
    private Task? _dictationTask;
    private Task? _captureCancelTask;
    private Task? _disposeTask;
    private bool _isStartingCapture;
    private int _captureChangeCount;
    private bool _isChangingCapture => _captureChangeCount > 0;
    private bool _isCancellingCapture;
    private readonly ITranscriptionService _transcription;
    private readonly DictationDeliveryService _delivery;
    private readonly IModelManager _modelManager;
    private readonly CapsulePositionService _positionService = new();
    private readonly DictationSettingsService _settingsService;
    private readonly AppThemeService _themeService;
    private readonly RecentRecordingHistoryService _recentRecordings;
    private readonly FeedbackSoundService _sounds;
    private readonly CancelKeyWatcher? _cancelKey;
    private readonly MainWindowInteractionHooks _interactionHooks;
    private TranscriptPostProcessor _postProcessor = new();
    private bool _mixedLanguageMode;
    private bool _saveRecentRecordings;

    // Голосовая команда «переведи …»: локальный переводчик EGOIST (HY-MT1.5).
    private readonly TranslatorClient _translator = new();
    private readonly ActivationSettingsService _activationSettings = new();
    private readonly DispatcherTimer _hideTimer = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task? _translationWarmupTask;
    private readonly PushToTalkCoordinator _pushToTalk = new();
    private Storyboard _exitStoryboard = null!;
    private GlobalHotkeyService? _hotkey;
    private KeyboardShortcut? _keyboardShortcut;
    private MousePushToTalkService? _mouseHotkey;
    private MouseSideButton? _mouseButton;
    private ActivationConfiguration _activationConfiguration = ActivationConfiguration.Default;
    private CancellationTokenSource? _operationCancellation;
    private nint _targetWindow;
    private DateTime _recordingStartedUtc;
    private double _wavePhase;
    private double _audioLevelCurrent;
    private volatile float _audioLevelTarget;
    private double _timbreBassCurrent;
    private volatile float _timbreBassTarget;
    private double _timbreTrebleCurrent;
    private volatile float _timbreTrebleTarget;
    private bool _positionInitialized;
    private bool _hideRequested;
    private bool _forceHideAfterCancellation;
    private bool _isRecording;
    private bool _isProcessing;
    private bool _announceModelDownloads;
    private bool _backgroundDownloadAnnounced;
    private bool _displayingBackgroundModelProgress;
    private ModelTransferProgress? _lastModelProgress;
    private bool _disposed;
    private bool _waveRendering;
    private bool _activationCaptureActive;
    private CapsuleVisualStateKind? _lastVisualStateKind;
    private string? _lastAnnouncement;
    private bool _timerVisible;
    private bool IsReducedMotion => _themeService.ReducedMotion;

    /// <summary>Window width up to and including 1.6.5, used to re-centre positions saved back then.</summary>
    private const double LegacyWindowWidth = 242;

    public MainWindow(
        IAudioCaptureService audioCapture,
        ITranscriptionService transcription,
        DictationDeliveryService delivery,
        IModelManager modelManager,
        DictationSettingsService settingsService,
        RecentRecordingHistoryService recentRecordings,
        AppThemeService themeService)
        : this(audioCapture, transcription, delivery, modelManager, settingsService,
            recentRecordings, themeService, interactionHooks: null)
    {
    }

    internal MainWindow(
        IAudioCaptureService audioCapture,
        ITranscriptionService transcription,
        DictationDeliveryService delivery,
        IModelManager modelManager,
        DictationSettingsService settingsService,
        RecentRecordingHistoryService recentRecordings,
        AppThemeService themeService,
        MainWindowInteractionHooks? interactionHooks)
    {
        _cancelKey = interactionHooks is null ? new CancelKeyWatcher() : null;
        _interactionHooks = interactionHooks ?? new(
            NativeMethods.GetForegroundWindow, _cancelKey!.Arm, _cancelKey!.Disarm,
            () => System.Windows.MessageBox.Show(
                "Текущая диктовка будет отменена, а её аудиобуфер очищен. Сменить микрофон?",
                "Смена микрофона", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);
        InitializeComponent();
        _audioCapture = audioCapture;
        _captureOperations = new(audioCapture);
        _transcription = transcription;
        _delivery = delivery;
        _modelManager = modelManager;
        _settingsService = settingsService;
        _recentRecordings = recentRecordings;
        _themeService = themeService;
        _sounds = new FeedbackSoundService(_audioCapture.SuppressFeedbackAudio);
        _exitStoryboard = (Storyboard)Resources["ExitStoryboard"];
        _themeService.ThemeChanged += OnCapsuleThemeChanged;
        ApplyDictationSettings();

        BuildWaveform();
        _audioCapture.LevelChanged += OnAudioLevelChanged;
        _audioCapture.TimbreChanged += OnAudioTimbreChanged;
        _audioCapture.StateChanged += OnAudioCaptureStateChanged;
        var capturePreference = _settingsService.Load();
        _captureState = new(capturePreference.CaptureDeviceId, "Микрофон",
            capturePreference.IsCapturePaused, false, false);
        _pushToTalk.SetPaused(capturePreference.IsCapturePaused);
        _captureInitializationTask = InitializeCaptureSnapshotAsync();
        _modelManager.ProgressChanged += OnModelProgressChanged;
        _exitStoryboard.Completed += (_, _) =>
        {
            if (CapsuleHidePolicy.CanComplete(
                    _hideRequested,
                    _isRecording,
                    _isProcessing,
                    _forceHideAfterCancellation))
            {
                _hideRequested = false;
                _forceHideAfterCancellation = false;
                _displayingBackgroundModelProgress = false;
                Hide();
            }
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_isRecording && !_isProcessing)
            {
                HideCapsuleAnimated();
            }
        };

        SourceInitialized += (_, _) =>
        {
            AppLog.Write("Capsule SourceInitialized");
            NativeMethods.MakeWindowNonActivating(new WindowInteropHelper(this).Handle);
            InitializeCapsulePosition();
        };

        // Without this the capsule stays on a monitor that no longer exists — the previous
        // clamp only ran on the next show, which may never come if the capsule is off-screen.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        if (_cancelKey is not null) _cancelKey.Cancelled += OnCancelKeyPressed;
        Loaded += (_, _) =>
        {
            AppLog.Write("Capsule Loaded");
        };
    }

    public event EventHandler? ActivationBindingChanged;
    public event EventHandler<AudioCaptureStateChangedEventArgs>? AudioCaptureStateChanged;

    public event EventHandler<TranslationEngineHealthChangedEventArgs> TranslationEngineHealthChanged
    {
        add => _translator.HealthChanged += value;
        remove => _translator.HealthChanged -= value;
    }

    public event EventHandler<AppThemeChangedEventArgs> ThemeChanged
    {
        add => _themeService.ThemeChanged += value;
        remove => _themeService.ThemeChanged -= value;
    }

    public ActivationBinding CurrentActivationBinding => _activationConfiguration.Binding;

    public KeyboardShortcut? CurrentCustomShortcut => _activationConfiguration.CustomShortcut;

    public string CurrentActivationDisplayName => ActivationBindingInfo.DisplayName(_activationConfiguration);

    public AudioCaptureState CurrentAudioCaptureState => _captureState;

    public bool IsCaptureOperationPending => _isStartingCapture || _isChangingCapture ||
        _isCancellingCapture || !_captureInitializationTask.IsCompleted || _disposed;

    public bool CanStartRecording => !_disposed && !CurrentAudioCaptureState.IsUserPaused &&
        !_isStartingCapture && !_isChangingCapture && !_isCancellingCapture && !_isRecording &&
        !_isProcessing && _dictationTask is not { IsCompleted: false };

    public IReadOnlyList<MicrophoneDeviceInfo> CaptureDevices => _captureDevices;

    public float CurrentAudioLevel => _audioLevelTarget;

    public bool IsRecording => _isRecording;

    public bool IsProcessing => _isProcessing;

    public bool AreRecognitionModelsReady => _transcription is RussianSpeechQualityService quality &&
        quality.PrimaryAvailable && !quality.FormatSpeechPunctuation || _modelManager.AreAllModelsReady;
    public bool IsSpeechFormattingUnavailable => _transcription is RussianSpeechQualityService quality &&
        quality.PrimaryAvailable && quality.FormatSpeechPunctuation && !quality.FormattingAvailable;

    public ModelTransferProgress? RecognitionModelProgress => _modelManager.CurrentProgress;

    public TranslationEngineHealth CurrentTranslationEngineHealth => _translator.CurrentHealth;

    public RecentRecordingHistoryService RecentRecordings => _recentRecordings;

    public AppTheme ThemePreference => _themeService.Preference;

    public EffectiveAppTheme EffectiveTheme => _themeService.EffectiveTheme;

    public bool ReducedMotion => _themeService.ReducedMotion;

    public Task<TranslationEngineHealth> RefreshTranslationEngineAsync() =>
        _translator.EnsureReadyAsync(_lifetimeCancellation.Token);

    public void BeginTranslationEngineWarmup()
    {
        if (VoiceRuntimeProfile.IsPortable) return;
        _translationWarmupTask ??= WarmTranslationEngineAsync();
    }

    public void PreviewFeedbackSound() => PlayFeedback(FeedbackSound.RecordingStarted, preview: true);

    private async Task WarmTranslationEngineAsync()
    {
        try
        {
            _ = await _translator.EnsureReadyAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal application shutdown owns this cancellation.
        }
    }

    private void RefreshCaptureSnapshot()
    {
        // This method is called exclusively by the native-operation worker.
        _captureState = _audioCapture.GetState();
        _captureDevices = _audioCapture.GetCaptureDevices().ToArray();
    }

    private async Task InitializeCaptureSnapshotAsync()
    {
        try
        {
            await _captureOperations.RunAsync(RefreshCaptureSnapshot, _lifetimeCancellation.Token);
            if (!_disposed) _pushToTalk.SetPaused(_isChangingCapture || CurrentAudioCaptureState.IsUserPaused);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) { AppLog.Write("Microphone inventory initialization failed", exception); }
    }

    public Task RefreshCaptureDevicesAsync()
    {
        if (_disposed) return Task.CompletedTask;
        return _captureRefreshTask is { IsCompleted: false } ? _captureRefreshTask :
            _captureRefreshTask = InitializeCaptureSnapshotAsync();
    }

    private Task RunCaptureOperationAsync(Action operation, CancellationToken cancellationToken = default) =>
        _captureOperations.RunAsync(() =>
        {
            try { operation(); }
            finally { RefreshCaptureSnapshot(); }
        }, cancellationToken);

    private Task<T> RunCaptureOperationAsync<T>(Func<Task<T>> operation,
        CancellationToken cancellationToken = default, bool cleanup = false) =>
        _captureOperations.RunAsync(async () =>
        {
            try { return await operation().ConfigureAwait(false); }
            finally { RefreshCaptureSnapshot(); }
        }, cancellationToken, cleanup);

    public async Task<bool> SelectMicrophoneAsync(string? deviceId)
    {
        if (_disposed) return false;
        if ((_isRecording || _isStartingCapture) && !_interactionHooks.ConfirmDeviceSwitch()) return false;
        _pushToTalk.SetPaused(true);
        _captureChangeCount++;
        try
        {
            if (_isRecording || _isStartingCapture || _isProcessing) await CancelDictationAsync();
            await RunCaptureOperationAsync(() => _audioCapture.SelectCaptureDevice(deviceId), _lifetimeCancellation.Token);
            if (_disposed) return false;
            PersistAudioSettings();
            _pushToTalk.SetPaused(CurrentAudioCaptureState.IsUserPaused);
            return true;
        }
        finally
        {
            _captureChangeCount--;
            if (!_disposed) _pushToTalk.SetPaused(_isChangingCapture || CurrentAudioCaptureState.IsUserPaused);
        }
    }

    public async Task SetMicrophonePausedAsync(bool paused)
    {
        if (_disposed) return;
        _recentRecordings.StopPlayback();
        // Disable new intent immediately, rather than after a slow driver join/open.
        _pushToTalk.SetPaused(true);
        _captureChangeCount++;
        try
        {
            if (paused && (_isRecording || _isStartingCapture || _isProcessing)) await CancelDictationAsync();
            await RunCaptureOperationAsync(paused ? _audioCapture.PauseMonitoring : _audioCapture.ResumeMonitoring,
                _lifetimeCancellation.Token);
            if (!_disposed) PersistAudioSettings();
        }
        finally
        {
            _captureChangeCount--;
            if (!_disposed) _pushToTalk.SetPaused(_isChangingCapture || CurrentAudioCaptureState.IsUserPaused);
        }
    }

    public void SetActivationCaptureActive(bool active)
    {
        _activationCaptureActive = active;
        if (active)
        {
            _pushToTalk.Reset();
        }
    }

    public void InitializeHotkey()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        AppLog.Write($"InitializeHotkey handle=0x{handle:X}");
        NativeMethods.MakeWindowNonActivating(handle);

        var requested = _activationSettings.Load();
        if (TryApplyActivationBinding(requested, persist: false, out var error))
        {
            return;
        }

        AppLog.Write($"Requested activation binding unavailable: {error}");
        foreach (var fallback in new[] { ActivationBinding.Mouse5, ActivationBinding.Keyboard })
        {
            if (TryApplyActivationBinding(requested.WithBinding(fallback), persist: false, out _))
            {
                return;
            }
        }

        throw new InvalidOperationException("Не удалось подключить ни Mouse 5, ни Ctrl + Alt + Space.");
    }

    public bool TrySetActivationBinding(ActivationBinding binding, out string? error) =>
        TryApplyActivationBinding(_activationConfiguration.WithBinding(binding), persist: true, out error);

    public bool TrySetCustomShortcut(KeyboardShortcut shortcut, out string? error) =>
        TryApplyActivationBinding(
            _activationConfiguration with
            {
                Binding = ActivationBinding.CustomKeyboard,
                CustomShortcut = shortcut
            },
            persist: true,
            out error);

    private bool TryApplyActivationBinding(ActivationConfiguration configuration, bool persist, out string? error)
    {
        var previousConfiguration = _activationConfiguration;
        var settingsChanged = false;
        var desiredKeyboard = ActivationBindingInfo.Keyboard(configuration);
        var desiredMouse = ActivationBindingInfo.MouseButton(configuration.Binding);
        GlobalHotkeyService? createdKeyboard = null;
        MousePushToTalkService? createdMouse = null;
        try
        {
            if (configuration.Binding == ActivationBinding.CustomKeyboard && desiredKeyboard is not { IsValid: true })
            {
                throw new InvalidDataException("Сначала задайте пользовательскую горячую клавишу.");
            }

            if (desiredKeyboard is not null && (_hotkey is null || _keyboardShortcut != desiredKeyboard))
            {
                var handle = new WindowInteropHelper(this).EnsureHandle();
                createdKeyboard = new GlobalHotkeyService(handle, desiredKeyboard.Value);
                createdKeyboard.Pressed += OnHotkeyPressed;
                createdKeyboard.Released += OnHotkeyReleased;
            }

            if (desiredMouse is not null && (_mouseHotkey is null || _mouseButton != desiredMouse))
            {
                createdMouse = new MousePushToTalkService(desiredMouse.Value);
                createdMouse.Pressed += OnMouseHotkeyPressed;
                createdMouse.Released += OnMouseHotkeyReleased;
            }

            // Persist before swapping live hooks. If the atomic settings write fails,
            // the existing working binding remains untouched.
            if (persist)
            {
                _activationSettings.Save(configuration);
                settingsChanged = true;
            }

            var oldKeyboard = _hotkey;
            var oldMouse = _mouseHotkey;
            if (createdKeyboard is not null)
            {
                _hotkey = createdKeyboard;
                _keyboardShortcut = desiredKeyboard;
                createdKeyboard = null;
            }
            if (createdMouse is not null)
            {
                _mouseHotkey = createdMouse;
                _mouseButton = desiredMouse;
                createdMouse = null;
            }

            if (desiredKeyboard is null && oldKeyboard is not null)
            {
                oldKeyboard.Pressed -= OnHotkeyPressed;
                oldKeyboard.Released -= OnHotkeyReleased;
                oldKeyboard.Dispose();
                _hotkey = null;
                _keyboardShortcut = null;
            }
            else if (desiredKeyboard is not null && oldKeyboard is not null && !ReferenceEquals(oldKeyboard, _hotkey))
            {
                oldKeyboard.Pressed -= OnHotkeyPressed;
                oldKeyboard.Released -= OnHotkeyReleased;
                oldKeyboard.Dispose();
            }
            if (desiredMouse is null && oldMouse is not null)
            {
                oldMouse.Pressed -= OnMouseHotkeyPressed;
                oldMouse.Released -= OnMouseHotkeyReleased;
                oldMouse.Dispose();
                _mouseHotkey = null;
                _mouseButton = null;
            }
            else if (desiredMouse is not null && oldMouse is not null && !ReferenceEquals(oldMouse, _mouseHotkey))
            {
                oldMouse.Pressed -= OnMouseHotkeyPressed;
                oldMouse.Released -= OnMouseHotkeyReleased;
                oldMouse.Dispose();
            }

            _pushToTalk.Reset();
            _activationConfiguration = configuration;
            ActivationBindingChanged?.Invoke(this, EventArgs.Empty);
            AppLog.Write($"Activation binding changed: {ActivationBindingInfo.DisplayName(configuration)}");
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            if (settingsChanged)
            {
                try
                {
                    _activationSettings.Save(previousConfiguration);
                }
                catch (Exception rollbackException)
                {
                    AppLog.Write("Activation settings rollback failed", rollbackException);
                }
            }
            if (createdKeyboard is not null)
            {
                createdKeyboard.Pressed -= OnHotkeyPressed;
                createdKeyboard.Released -= OnHotkeyReleased;
                createdKeyboard.Dispose();
            }
            if (createdMouse is not null)
            {
                createdMouse.Pressed -= OnMouseHotkeyPressed;
                createdMouse.Released -= OnMouseHotkeyReleased;
                createdMouse.Dispose();
            }
            error = exception.Message;
            AppLog.Write($"Activation binding rejected: {ActivationBindingInfo.DisplayName(configuration)}", exception);
            return false;
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs args)
    {
        if (_activationCaptureActive)
        {
            return;
        }
        BeginPushToTalk(PushToTalkSource.Keyboard);
    }

    private async void OnHotkeyReleased(object? sender, EventArgs args)
    {
        if (_activationCaptureActive)
        {
            return;
        }
        await EndPushToTalkAsync(PushToTalkSource.Keyboard);
    }

    private void OnMouseHotkeyPressed(object? sender, EventArgs args)
    {
        if (_activationCaptureActive)
        {
            return;
        }
        BeginPushToTalk(PushToTalkSource.Mouse);
    }

    private async void OnMouseHotkeyReleased(object? sender, EventArgs args)
    {
        if (_activationCaptureActive)
        {
            return;
        }
        await EndPushToTalkAsync(PushToTalkSource.Mouse);
    }

    private void BeginPushToTalk(PushToTalkSource source)
    {
        if (_disposed || _isChangingCapture || _isCancellingCapture || _isProcessing || CurrentAudioCaptureState.IsUserPaused) return;
        if (_pushToTalk.Press(source) && CanStartRecording && !_isRecording)
        {
            _captureStartTask = StartRecordingAsync();
        }
    }

    private void OnAudioCaptureStateChanged(object? sender, AudioCaptureStateChangedEventArgs change)
    {
        if (_disposed) return;
        _captureState = change.State;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => HandleAudioCaptureStateChanged(change));
            return;
        }
        HandleAudioCaptureStateChanged(change);
    }

    private void HandleAudioCaptureStateChanged(AudioCaptureStateChangedEventArgs change)
    {
        if (_disposed) return;
        // A newer worker snapshot must not be overwritten by an older queued notification.
        change = change with { State = CurrentAudioCaptureState };
        _pushToTalk.SetPaused(_isChangingCapture || change.State.IsUserPaused);
        if (change.ActiveTakeCancelled)
        {
            _operationCancellation?.Cancel();
            _isRecording = false;
            _isProcessing = false;
            _interactionHooks.DisarmCancel();
            ShowError("Запись отменена");
        }
        if (change.Kind == AudioCaptureChangeKind.DeviceUnavailable)
        {
            PersistAudioSettings();
        }
        AudioCaptureStateChanged?.Invoke(this, change);
        if ((change.Kind is AudioCaptureChangeKind.InventoryChanged or AudioCaptureChangeKind.DefaultDeviceChanged) &&
            _inventoryNotificationTask is not { IsCompleted: false })
            _inventoryNotificationTask = RefreshCaptureDevicesAndNotifyAsync(change);
    }

    private async Task RefreshCaptureDevicesAndNotifyAsync(AudioCaptureStateChangedEventArgs change)
    {
        try
        {
            await RefreshCaptureDevicesAsync();
            if (!_disposed) AudioCaptureStateChanged?.Invoke(this, change with { State = CurrentAudioCaptureState });
        }
        catch (Exception exception) { AppLog.Write("Microphone inventory refresh failed", exception); }
    }

    private void PersistAudioSettings()
    {
        var state = CurrentAudioCaptureState;
        _settingsService.Save(_settingsService.Load() with
        {
            CaptureDeviceId = state.SelectedDeviceId,
            IsCapturePaused = state.IsUserPaused
        });
    }

    private async Task EndPushToTalkAsync(PushToTalkSource source)
    {
        if (_pushToTalk.Release(source) && _isRecording && !_isProcessing)
        {
            await EndRecordingAsync();
        }
    }

    public void ShowReadyBriefly()
    {
        SetReadyState();
        ShowCapsule();
        ScheduleHide();
    }

    public void ShowListeningPreview()
    {
        _isRecording = true;
        SetListeningState();

        // Backdate the start so the preview renders the timer instead of an empty slot. Without
        // this the visual regression never exercised the digits at all, which is precisely the
        // element most likely to collide with the waveform when either one is resized.
        _recordingStartedUtc = DateTime.UtcNow - PreviewElapsedTime;
        _audioLevelTarget = 0.72f;
        _timbreBassTarget = 0.55f;
        _timbreTrebleTarget = 0.45f;
        lock (_spectrumFrameGate) _spectrumFrame = new VoiceSpectrum(0.9f, 0.56f, 0.93f, 0.36f, 0.72f, 0.42f, 0.5f, 0.2f);
        for (var frame = 0; frame < 18; frame++)
        {
            AnimateWaveformFrame();
        }

        UpdateRecordingTimer();
        ShowCapsule();
    }

    public void ShowStatePreview(string state)
    {
        switch (state.ToLowerInvariant())
        {
            case "ready":
                SetReadyState();
                ShowCapsule();
                break;
            case "listening":
                ShowListeningPreview();
                break;
            case "processing":
            case "recognizing":
                _isProcessing = true;
                SetProcessingState("Распознаю", null);
                break;
            case "success-unformatted":
                ShowSuccess("Вставлено без оформления");
                break;
            case "success":
                ShowSuccess();
                break;
            case "clipboard":
                ShowClipboardFallback();
                break;
            case "error":
                ShowError("Не услышал");
                break;
            case "download":
                SetModelTransferState(new ModelTransferProgress(
                    "GigaAM v3 · ядро", 1, 5, ModelTransferStage.Downloading,
                    133_978_319, 318_995_997, 42, 14, 38_000_000, TimeSpan.FromSeconds(5)));
                break;
            default:
                ShowListeningPreview();
                break;
        }
    }

    public void RenderPreview(string outputPath)
    {
        // RenderTargetBitmap can sample separate animated WPF layers between
        // composition ticks. Freeze diagnostic previews on one coherent frame;
        // this path is used only by visual QA and never changes runtime motion.
        StopStateAnimations();
        ((Storyboard)Resources["EnterStoryboard"]).Stop(this);
        _exitStoryboard.Stop(this);
        BeginAnimation(WidthProperty, null);
        CapsuleShell.Opacity = 1;
        ShadowSurface.Opacity = 1;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        ShellTranslate.Y = 0;
        CenterContent.Opacity = 1;
        StateContentTranslate.X = 0;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    public async Task ToggleRecordingAsync()
    {
        if (_disposed || _isCancellingCapture || _isChangingCapture || _isProcessing) return;
        if (_isRecording || _isStartingCapture) await EndRecordingAsync();
        else if (CanStartRecording) await (_captureStartTask = StartRecordingAsync());
    }

    private async Task StartRecordingAsync()
    {
        AppLog.Write("StartRecording requested");
        // The recognizer unloads itself when idle; reload it while the user is still speaking.
        if (_transcription is WhisperRussianService { IsLoaded: false }) BeginWarmUp(showProgress: false);
        _recentRecordings.StopPlayback();
        _forceHideAfterCancellation = false;
        _hideTimer.Stop();
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellationToken = _operationCancellation.Token;
        // Foreground intent is captured once, before any asynchronous device operation.
        _targetWindow = _interactionHooks.CaptureForegroundTarget();
        _recordingStartedUtc = DateTime.UtcNow;
        _isStartingCapture = true;
        _isRecording = true;
        _interactionHooks.ArmCancel();
        SetProcessingState("Подключаю микрофон", null);
        ShowCapsule();
        try
        {
            await _captureInitializationTask;
            await RunCaptureOperationAsync(_audioCapture.Start, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return;
            SetListeningState();
            RefreshCapsuleAnimationEligibility();
            PlayFeedback(FeedbackSound.RecordingStarted);
            AppLog.Write($"Audio capture started, target=0x{_targetWindow:X}");
        }
        catch (OperationCanceledException)
        {
            _isRecording = false;
            _interactionHooks.DisarmCancel();
        }
        catch (Exception exception)
        {
            _isRecording = false;
            _pushToTalk.Reset();
            _interactionHooks.DisarmCancel();
            AppLog.Write("StartRecording failed", exception);
            if (!_disposed) ShowError(GetMicrophoneError(exception));
        }
        finally { _isStartingCapture = false; }
    }

    private Task EndRecordingAsync() =>
        _dictationTask is { IsCompleted: false } ? _dictationTask :
            _dictationTask = FinishPendingRecordingAsync();

    private async Task FinishPendingRecordingAsync()
    {
        if (_captureStartTask is { } starting) await starting;
        if (!_disposed && _isRecording && _operationCancellation is { IsCancellationRequested: false })
            await StopAndTranscribeAsync();
    }

    private async Task StopAndTranscribeAsync()
    {
        AppLog.Write($"StopAndTranscribe requested, held={(DateTime.UtcNow - _recordingStartedUtc).TotalSeconds:0.00}s");
        _isRecording = false;
        _isProcessing = true;
        SetProcessingState("Распознаю", null);
        var cancellationToken = _operationCancellation?.Token ?? CancellationToken.None;
        var trace = new DictationTrace();
        var textSettings = _currentTextSettings;
        trace.Mark(DictationStage.CaptureStarted);
        string? audioPath = null;
        AudioCaptureResult? completedCapture = null;
        var recordingStatus = RecentRecordingStatus.ProcessingFailed;

        try
        {
            var capture = await RunCaptureOperationAsync(() => _audioCapture.StopAsync(cancellationToken), cancellationToken);
            completedCapture = capture;
            trace.Mark(DictationStage.CaptureStopped);
            PlayFeedback(FeedbackSound.RecordingStopped);
            audioPath = capture.Path;
            AppLog.Write(
                $"Audio capture stopped: samples={capture.Samples.Length}, " +
                $"duration={capture.Duration.TotalSeconds:0.00}s, speech={capture.DetectedSpeech.TotalSeconds:0.00}s, " +
                $"peak={capture.PeakDecibels:0.0}dBFS");
            trace.Mark(DictationStage.SpeechChecked);
            if (!capture.HasSpeech)
            {
                AppLog.Write($"No speech detected ({capture.RejectionMessage ?? "unspecified"}); delivery skipped");
                _isProcessing = false;

                // Silence used to be indistinguishable from a broken microphone: the capsule simply
                // disappeared. Say which one it was.
                if (capture.RejectionMessage is { Length: > 0 } reason)
                {
                    ShowError(reason);
                }
                else
                {
                    HideCapsuleAnimated();
                }
                return;
            }
            var progress = new Progress<ModelProgress>(value =>
            {
                if (!cancellationToken.IsCancellationRequested &&
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
                _targetWindow,
                result.Text,
                _mixedLanguageMode);
            var text = _postProcessor.Process(result.Text, entityProfile);
            trace.Mark(DictationStage.TextFormatted);
            AppLog.Write($"Transcription complete: characters={text.Length}, elapsed={result.Elapsed.TotalSeconds:0.00}s");

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowError("Не услышал");
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
                SetProcessingState("Перевожу", null);
                var translation = await _translator.TranslateAsync(
                    directive.Payload,
                    directive.TargetLanguage,
                    label => Dispatcher.Invoke(() => SetProcessingState(label, null)),
                    cancellationToken);

                if (translation.Succeeded)
                {
                    text = translation.Text!;
                    AppLog.Write($"Перевод готов: {text.Length} симв.");
                }
                else
                {
                    AppLog.Write($"Перевод не вставлен: {translation.Failure}");
                    ShowError(translation.UserMessage);
                    return;
                }
            }

            var audioFormattingUnavailable = result.AudioFormatting == AudioFormattingStatus.Unavailable;
            string? formattingMessage = null;
            if (directive is null && textSettings.FormatWithQwen && !textSettings.PreserveSpokenWords)
            {
                SetProcessingState("Оформляю", null);
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
            var deliveryResult = await _delivery.DeliverAsync(text, _targetWindow, cancellationToken);
            trace.Mark(DictationStage.Delivered);
            LastOperationSummary = $"От отпускания до результата: {trace.Total.TotalSeconds:0.00} с" +
                (textSettings.PreserveSpokenWords ? " · дословно" :
                    formattingMessage is null ? " · быстрое оформление" : " · " + formattingMessage);
            if (audioFormattingUnavailable)
                LastOperationSummary += " · оформление временно недоступно";
            AppLog.Write($"Dictation timing: {trace.Format()}");
            switch (deliveryResult.Status)
            {
                case DictationDeliveryStatus.Inserted:
                    ShowSuccess(audioFormattingUnavailable ? "Вставлено без оформления" : "Вставлено");
                    break;
                case DictationDeliveryStatus.ClipboardFallback:
                    ShowClipboardFallback();
                    break;
                case DictationDeliveryStatus.ClipboardFailed:
                    ShowError("Буфер занят");
                    break;

                // Без этой ветки капсула оставалась в состоянии «Распознаю» навсегда: ни один из
                // показов не вызывался, а значит не вызывался и ScheduleHide. Пользователь при
                // этом вообще не узнавал, почему текст не появился.
                case DictationDeliveryStatus.SuppressedForSensitiveTarget:
                    ShowError("Не вставляю в пароли");
                    break;

                default:
                    AppLog.Write($"Unhandled delivery status: {deliveryResult.Status}");
                    ShowError("Ошибка");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("Recording operation cancelled");
            if (!_disposed) { SetReadyState(); ScheduleHide(); }
        }
        catch (Exception exception)
        {
            AppLog.Write("StopAndTranscribe failed", exception);
            if (!_disposed) ShowError("Ошибка");
        }
        finally
        {
            _isProcessing = false;
            _interactionHooks.DisarmCancel();

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

            // Diagnostic/corpus mode can still return an explicit temporary WAV. Normal dictation
            // is memory-only, so cancellation normally has no path to resolve or delete.
            if (audioPath is null && _captureCancelTask is not { IsCompleted: false })
                audioPath = await TryResolveDiscardedRecordingAsync();
            if (audioPath is not null)
            {
                TryDelete(audioPath);
            }

        }
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

    private async void CloseButton_OnClick(object sender, RoutedEventArgs e) => await CancelDictationAsync();

    private async void OnCancelKeyPressed(object? sender, EventArgs e)
    {
        AppLog.Write("Dictation cancelled with the cancel key");
        await CancelDictationAsync();
    }

    private Task CancelDictationAsync() =>
        _captureCancelTask is { IsCompleted: false } ? _captureCancelTask :
            _captureCancelTask = CancelDictationCoreAsync();

    private async Task CancelDictationCoreAsync()
    {
        _interactionHooks.DisarmCancel();
        _operationCancellation?.Cancel();
        _pushToTalk.Reset();
        _isCancellingCapture = true;
        var hadCapture = _isRecording || _isStartingCapture || _isProcessing;
        _isRecording = false;
        StopWaveformAnimation();
        if (!_disposed) HideCapsuleAnimated(forceAfterCancellation: true);
        try
        {
            // Start cannot be interrupted inside the driver's synchronous call. Once it returns,
            // this same owned queue clears its buffer before any later Start/device operation.
            if (_captureStartTask is { } starting) await starting;
            if (hadCapture) TryDelete(await CancelCaptureAsync());
            if (_dictationTask is { } dictation) await dictation;
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

    public Action? RequestOpenSettings { get; set; }
    public Action? RequestOpenHistory { get; set; }
    public Action? RequestExit { get; set; }

    private void RootBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
            return;
        }

        e.Handled = true;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.BeginWindowDrag(handle);
        KeepCapsuleOnScreen();
        _positionService.Save(Left, Top, Width);
        AppLog.Write($"Capsule moved: left={Left:0}, top={Top:0}");
    }

    private void CapsuleContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        MenuDirectFastMode.IsChecked = settings.DirectGigaamFastMode;
        MenuDirectFastMode.Visibility = VoiceRuntimeProfile.IsPortable ? Visibility.Collapsed : Visibility.Visible;
        MenuFormatWithQwen.Visibility = VoiceRuntimeProfile.IsPortable ? Visibility.Collapsed : Visibility.Visible;
        MenuFormatWithQwen.IsChecked = settings.FormatWithQwen && !settings.PreserveSpokenWords;
        MenuSoundFeedback.IsChecked = _sounds.Enabled;
    }

    private void MenuSettings_OnClick(object sender, RoutedEventArgs e) => RequestOpenSettings?.Invoke();

    private void MenuHistory_OnClick(object sender, RoutedEventArgs e) => RequestOpenHistory?.Invoke();

    private void MenuDirectFastMode_OnClick(object sender, RoutedEventArgs e)
    {
        var current = _settingsService.Load();
        _settingsService.Save(current with { DirectGigaamFastMode = MenuDirectFastMode.IsChecked });
        ApplyDictationSettings();
    }

    private void MenuFormatWithQwen_OnClick(object sender, RoutedEventArgs e)
    {
        if (VoiceRuntimeProfile.IsPortable) return;
        var current = _settingsService.Load();
        _settingsService.Save(current with { FormatWithQwen = MenuFormatWithQwen.IsChecked,
            PreserveSpokenWords = !MenuFormatWithQwen.IsChecked });
        ApplyDictationSettings();
    }

    private void MenuSoundFeedback_OnClick(object sender, RoutedEventArgs e)
    {
        var current = _settingsService.Load();
        _settingsService.Save(current with { SoundFeedback = MenuSoundFeedback.IsChecked });
        ApplyDictationSettings();
    }

    private void MenuHide_OnClick(object sender, RoutedEventArgs e) => HideCapsuleAnimated();

    private void MenuExit_OnClick(object sender, RoutedEventArgs e) => RequestExit?.Invoke();

    private void ShowCapsule()
    {
        if (_forceHideAfterCancellation)
        {
            return;
        }

        var foregroundBefore = NativeMethods.GetForegroundWindow();
        KeepCapsuleOnScreen();
        _hideRequested = false;
        _forceHideAfterCancellation = false;
        _exitStoryboard.Stop(this);
        var wasVisible = IsVisible;
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsVisible)
        {
            Show();
            NativeMethods.ShowWithoutActivation(handle);

            // Re-apply position after Show(): a WM_DPICHANGED raised while the window becomes
            // visible on a differently scaled monitor overwrites the placement set above.
            NativeMethods.TryClampToMonitorWorkArea(handle);
        }

        NativeMethods.ReassertTopmost(handle);
        if (!wasVisible)
        {
            if (IsReducedMotion)
            {
                CapsuleShell.Opacity = 1;
                ShadowSurface.Opacity = 1;
                ShellScale.ScaleX = 1;
                ShellScale.ScaleY = 1;
                ShellTranslate.Y = 0;
            }
            else
            {
                ((Storyboard)Resources["EnterStoryboard"]).Begin(this, true);
            }
        }
        AppLog.Write($"Capsule shown: foregroundBefore=0x{foregroundBefore:X}, foregroundAfter=0x{NativeMethods.GetForegroundWindow():X}");
    }

    private void HideCapsuleAnimated(bool forceAfterCancellation = false)
    {
        if (!IsVisible || _hideRequested)
        {
            return;
        }

        _hideRequested = true;
        _forceHideAfterCancellation = forceAfterCancellation;
        StopWaveformAnimation();
        if (IsReducedMotion)
        {
            _hideRequested = false;
            _forceHideAfterCancellation = false;
            _displayingBackgroundModelProgress = false;
            Hide();
        }
        else
        {
            _exitStoryboard.Begin(this, true);
        }
    }

    private void InitializeCapsulePosition()
    {
        if (_positionInitialized)
        {
            return;
        }

        var saved = _positionService.Load();
        if (saved is null)
        {
            var workArea = SystemParameters.WorkArea;
            saved = new CapsulePosition(
                workArea.Left + ((workArea.Width - Width) / 2),
                workArea.Bottom - Height - 20,
                Width);
        }
        else
        {
            saved = CapsulePositionService.Recentre(saved, Width, LegacyWindowWidth);
        }

        var clamped = ClampToVirtualScreen(saved);
        Left = clamped.Left;
        Top = clamped.Top;
        _positionInitialized = true;
    }

    private void KeepCapsuleOnScreen()
    {
        InitializeCapsulePosition();
        if (NativeMethods.TryClampToMonitorWorkArea(new WindowInteropHelper(this).Handle))
        {
            // SetWindowPos already moved the window; WPF picks Left/Top up from the position
            // change, so no second, competing assignment is needed here.
            return;
        }

        var clamped = ClampToVirtualScreen(new CapsulePosition(Left, Top));
        Left = clamped.Left;
        Top = clamped.Top;
    }

    private CapsulePosition ClampToVirtualScreen(CapsulePosition position) => CapsulePositionService.Clamp(
        position,
        Width,
        Height,
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Rebuilds the post-processing pipeline from disk. Called at start-up and whenever the user
    /// edits the dictionary, so a new term takes effect without restarting the application.
    /// </summary>
    public void ApplyDictationSettings()
    {
        var settings = _settingsService.Load();
        _currentTextSettings = settings;
        ApplyLocalQwenPreference(settings);
        var dictionary = _settingsService.LoadDictionary();
        _postProcessor = new TranscriptPostProcessor(dictionary, settings.ToPostProcessingOptions());
        _mixedLanguageMode = settings.MixedLanguageMode && !settings.PreserveSpokenWords && !VoiceRuntimeProfile.IsPortable;
        _saveRecentRecordings = settings.SaveRecentRecordings;
        _delivery.RestoreClipboard = settings.RestoreClipboard;
        _sounds.Enabled = settings.SoundFeedback;
        _sounds.Volume = settings.SoundVolume;
        _sounds.Invalidate();
        _themeService.Apply(settings.Theme);

        if (!settings.SaveRecentRecordings)
        {
            _recentRecordings.CancelPending();
        }

        if (_transcription is RussianSpeechQualityService russianQuality)
        {
            russianQuality.FormatSpeechPunctuation = settings.FormatSpeechPunctuation;
        }

        if (_transcription is HybridTranscriptionService hybrid)
        {
            hybrid.MixedLanguageMode = settings.MixedLanguageMode && !settings.PreserveSpokenWords;
            hybrid.FastModeNoWhisperRefinement = settings.PreserveSpokenWords ||
                (settings.DirectGigaamFastMode && !settings.MixedLanguageMode);

            // Every dictionary term also becomes a suspicion for the mixed-speech detector, so a
            // user-added word starts pulling in the fallback without a second list to maintain.
            // The built-in entries are included: they are the terms most likely to appear.
            hybrid.UpdateVocabulary(dictionary.SpokenForms);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_positionInitialized)
            {
                return;
            }
            KeepCapsuleOnScreen();
            _positionService.Save(Left, Top, Width);
        });
    }

    /// <summary>
    /// PerMonitorV2 means the capsule really does change scale when it crosses monitors, so the
    /// supersampled chrome has to be re-rasterized and the placement re-checked.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_disposed)
        {
            return;
        }

        AppLog.Write($"Capsule DPI changed: {oldDpi.DpiScaleX:0.##} -> {newDpi.DpiScaleX:0.##}");
        InvalidateCapsuleChrome();

        // Deferred: Windows sends WM_DPICHANGED with a suggested rectangle and expects WPF to
        // apply it. Calling SetWindowPos from inside the same handler fights that placement and
        // can flip the window back and forth between two monitors.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_disposed)
            {
                KeepCapsuleOnScreen();
            }
        });
    }

    /// <summary>
    /// Forces the supersampled chrome to re-rasterize. Only <see cref="RootBorder"/> needs it now:
    /// the shadow is a blurred effect rather than a stack of hairline borders, and the effect
    /// scales with the visual on its own.
    /// </summary>
    private void InvalidateCapsuleChrome() => RootBorder.InvalidateVisual();

    private void ScheduleHide(TimeSpan? delay = null)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = delay ?? TimeSpan.FromSeconds(2.2);
        _hideTimer.Start();
    }

    private void StopStateAnimations()
    {
        ((Storyboard)Resources["SpinStoryboard"]).Stop(this);
        ((Storyboard)Resources["ProcessingStoryboard"]).Stop(this);
        ((Storyboard)Resources["ListenPulseStoryboard"]).Stop(this);
        ((Storyboard)Resources["SuccessStoryboard"]).Stop(this);
        ((Storyboard)Resources["ErrorStoryboard"]).Stop(this);
        ((Storyboard)Resources["DownloadStoryboard"]).Stop(this);
    }

    private void SetWaveform(double scaleY)
    {
        BuildWaveform();
        Waveform.SetUniformScale(scaleY);
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// Turns a capture failure into something the user can act on.
    /// </summary>
    /// <remarks>
    /// This existed but was never called: every failure showed "Нет микрофона", including a denied
    /// microphone permission and a denied write to %LOCALAPPDATA%. Somebody whose disk permissions
    /// are wrong was being told to check their microphone.
    /// </remarks>
    private static string GetMicrophoneError(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "Нет доступа к папке приложения";
        }

        if (exception is InvalidOperationException && exception.Message.Contains("уже запущена", StringComparison.Ordinal))
        {
            return "Запись уже идёт";
        }

        var message = exception.Message;
        return message.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Access", StringComparison.OrdinalIgnoreCase)
            ? "Разрешите доступ к микрофону"
            : "Нет микрофона";
    }

    private static SolidColorBrush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private SolidColorBrush ThemeBrush(string key) => (SolidColorBrush)FindResource(key);

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary audio is also cleared on the next startup.
        }
    }

    // IDisposable starts the same tracked shutdown. App awaits ShutdownAsync before exiting;
    // native driver joins and model disposal are never executed synchronously on the dispatcher.
    public void Dispose() => _ = ShutdownAsync();

    public Task ShutdownAsync()
    {
        Dispatcher.VerifyAccess();
        return _disposeTask ??= DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_cancelKey is not null) _cancelKey.Cancelled -= OnCancelKeyPressed;
        _hideTimer.Stop();
        StopWaveformAnimation();
        if (_positionInitialized) _positionService.Save(Left, Top, Width);
        _operationCancellation?.Cancel();
        _pushToTalk.Reset();
        _lifetimeCancellation.Cancel();
        _hotkey?.Dispose();
        _mouseHotkey?.Dispose();
        _modelManager.ProgressChanged -= OnModelProgressChanged;
        _audioCapture.LevelChanged -= OnAudioLevelChanged;
        _audioCapture.TimbreChanged -= OnAudioTimbreChanged;
        _audioCapture.StateChanged -= OnAudioCaptureStateChanged;
        _themeService.ThemeChanged -= OnCapsuleThemeChanged;
        try
        {
            await Task.WhenAll(new[] { _captureStartTask, _dictationTask, _captureCancelTask,
                _captureInitializationTask, _captureRefreshTask, _inventoryNotificationTask, _translationWarmupTask, _modelWarmupTask }
                .Where(task => task is not null).Cast<Task>());
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLog.Write("Pending operation ended during shutdown", exception); }
        try { await _captureOperations.ShutdownAsync(); }
        finally
        {
            await Task.Run(() =>
            {
                DisposeOwned(_cancelKey); DisposeOwned(_sounds); DisposeOwned(_recentRecordings);
                DisposeOwned(_transcription); DisposeOwned(_translator); DisposeOwned(_textFormatter);
                DisposeOwned(_localQwen); DisposeOwned(_modelManager);
            });
            _operationCancellation?.Dispose();
            _lifetimeCancellation.Dispose();
        }
        _isRecording = false;
        _isProcessing = false;
    }

    private static void DisposeOwned(IDisposable? owned)
    {
        try { owned?.Dispose(); }
        catch (Exception exception) { AppLog.Write("Owned service disposal failed", exception); }
    }
}
