using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using Egoist.Voice.Core;
using Egoist.Voice.Controls;
using Egoist.Voice.Services;

namespace Egoist.Voice;

public partial class MainWindow
{
    private readonly object _spectrumFrameGate = new();
    private VoiceSpectrum _spectrumFrame;
    private readonly CapsuleAnimationCadence _waveCadence = new();
    private CapsuleAnimationSubscription? _waveSubscription;
    private bool _capsuleMotionVisible;
    private int _lastDisplayedRecordingSecond = -1;
    private CapsuleVisualState? _lastVisualLayout;
    private bool _processingIsIndeterminate;
    private ModelTransferStage? _lastTransferStage;
    private void BuildWaveform()
    {
        Waveform.HighContrast = EffectiveTheme == EffectiveAppTheme.HighContrast;
        if (_waveSubscription is not null) return;
        _waveSubscription = new CapsuleAnimationSubscription(
            () =>
            {
                _waveCadence.Reset();
                CompositionTarget.Rendering += OnWaveformRendering;
                _waveRendering = true;
            },
            () =>
            {
                CompositionTarget.Rendering -= OnWaveformRendering;
                _waveRendering = false;
                _waveCadence.Reset();
            });
        Loaded += OnWaveformLoadedOrUnloaded;
        Unloaded += OnWaveformLoadedOrUnloaded;
        IsVisibleChanged += OnWaveformVisibilityChanged;
        StateChanged += OnWaveformWindowStateChanged;
        Waveform.IsVisibleChanged += OnWaveformVisibilityChanged;
        Closed += (_, _) => _waveSubscription?.Stop();
    }

    private bool CanRenderWaveform => !_disposed && _isRecording && IsLoaded && IsVisible &&
        WindowState != WindowState.Minimized && Waveform.IsVisible;

    private bool CanRenderCapsuleMotion => !_disposed && IsLoaded && IsVisible &&
        WindowState != WindowState.Minimized;

    private void OnWaveformLoadedOrUnloaded(object sender, RoutedEventArgs args) =>
        RefreshCapsuleAnimationEligibility();

    private void OnWaveformVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        RefreshCapsuleAnimationEligibility();

    private void OnWaveformWindowStateChanged(object? sender, EventArgs args) =>
        RefreshCapsuleAnimationEligibility();

    private void RefreshCapsuleAnimationEligibility()
    {
        _waveSubscription?.Refresh(CanRenderWaveform);
        var visible = CanRenderCapsuleMotion;
        if (_capsuleMotionVisible == visible) return;
        _capsuleMotionVisible = visible;
        if (!visible)
        {
            StopStateAnimations();
            ((Storyboard)Resources["StateTransitionStoryboard"]).Stop(this);
            ((Storyboard)Resources["EnterStoryboard"]).Stop(this);
            CenterContent.Opacity = 1;
            StateContentTranslate.X = 0;
            // A pending finite exit still completes its existing hide contract.
            return;
        }
        switch (_lastVisualStateKind)
        {
            case CapsuleVisualStateKind.Recognizing when _processingIsIndeterminate:
                BeginStateStoryboard("ProcessingStoryboard");
                break;
            case CapsuleVisualStateKind.Downloading when _lastTransferStage == ModelTransferStage.Downloading:
                BeginStateStoryboard("DownloadStoryboard");
                break;
            case CapsuleVisualStateKind.Downloading when _lastTransferStage is ModelTransferStage.Verifying or ModelTransferStage.Loading:
                BeginStateStoryboard("SpinStoryboard");
                break;
            case CapsuleVisualStateKind.Success:
            case CapsuleVisualStateKind.Clipboard:
                BeginStateStoryboard("SuccessStoryboard");
                break;
            case CapsuleVisualStateKind.Error:
                BeginStateStoryboard("ErrorStoryboard");
                break;
        }
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        _audioLevelTarget = Math.Clamp(level, 0, 1);
    }

    private void OnAudioTimbreChanged(object? sender, VoiceTimbreLevel timbre)
    {
        lock (_spectrumFrameGate) _spectrumFrame = timbre.Spectrum;
        _audioLevelTarget = Math.Clamp(timbre.Overall, 0, 1);
        _timbreBassTarget = Math.Clamp(timbre.Bass, 0, 1);
        _timbreTrebleTarget = Math.Clamp(timbre.Treble, 0, 1);
    }

    private void AnimateWaveformFrame(double deltaSeconds = 1d / 60d)
    {
        if (!_isRecording)
        {
            return;
        }

        _audioLevelCurrent = CapsuleWaveformProfile.SmoothLevel(_audioLevelCurrent, _audioLevelTarget, deltaSeconds);
        _timbreBassCurrent = CapsuleWaveformProfile.SmoothLevel(_timbreBassCurrent, _timbreBassTarget, deltaSeconds);
        _timbreTrebleCurrent = CapsuleWaveformProfile.SmoothLevel(_timbreTrebleCurrent, _timbreTrebleTarget, deltaSeconds);
        var frameFactor = Math.Clamp(deltaSeconds * 60, 0.25, 3);
        _wavePhase += (0.09 + (_audioLevelCurrent * 0.14) + (_timbreTrebleCurrent * 0.05)) * frameFactor;
        VoiceSpectrum spectrum;
        lock (_spectrumFrameGate) spectrum = _spectrumFrame;
        Waveform.Advance(_audioLevelCurrent, _wavePhase, deltaSeconds, IsReducedMotion,
            _timbreBassCurrent, 0, _timbreTrebleCurrent, spectrum);
        UpdateRecordingTimer();
    }

    private void SetReadyState()
    {
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Ready, "Готово"));
        StopWaveformAnimation();
        SetStateDisc(System.Windows.Media.Brushes.Transparent);
        SetStateBorder(IdleBorderBrush);
        StateHalo.Opacity = 0;
        StopStateAnimations();
    }

    private void SetListeningState()
    {
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Listening, CanCancel: true));
        SetStateDisc(ActiveDiscBrush);
        SetStateBorder(ActiveBorderBrush);
        SetWaveform(CapsuleWaveformProfile.MinimumScale);
        StopStateAnimations();
        StateHalo.Opacity = 0.12;
        StateHaloScale.ScaleX = StateHaloScale.ScaleY = 0.94;
        _audioLevelCurrent = 0;
        _audioLevelTarget = 0;
        _timbreBassCurrent = 0;
        _timbreBassTarget = 0;
        _timbreTrebleCurrent = 0;
        _timbreTrebleTarget = 0;
        lock (_spectrumFrameGate) _spectrumFrame = default;
        _lastDisplayedRecordingSecond = -1;
        StartWaveformAnimation();
    }

    private void SetProcessingState(string label, double? percentage)
    {
        var alreadyProcessing = _lastVisualStateKind == CapsuleVisualStateKind.Recognizing;
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Recognizing, label, percentage, CanCancel: true));
        var indeterminate = percentage is null;
        if (!alreadyProcessing)
        {
            StopWaveformAnimation();
            SetStateDisc(ActiveDiscBrush);
            SetStateBorder(ActiveBorderBrush);
            StateHalo.Opacity = 0;
            StopStateAnimations();
            ShowCapsule();
        }
        // Progress updates keep the existing clock. Only a change of progress mode needs motion.
        if (!alreadyProcessing || _processingIsIndeterminate != indeterminate)
        {
            ((Storyboard)Resources["ProcessingStoryboard"]).Stop(this);
            if (indeterminate) BeginStateStoryboard("ProcessingStoryboard");
        }
        _processingIsIndeterminate = indeterminate;
    }

    private void ShowSuccess(string label = "Вставлено")
    {
        PlayFeedback(FeedbackSound.TextInserted);
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Success, label));
        StopWaveformAnimation();
        SetStateDisc(SuccessDiscBrush);
        SetStateBorder(IdleBorderBrush);
        StateHalo.Opacity = 0;
        StopStateAnimations();
        BeginStateStoryboard("SuccessStoryboard");
        ShowCapsule();
        ScheduleHide();
    }

    private void ShowClipboardFallback()
    {
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Clipboard, "Ctrl+V"));
        StopWaveformAnimation();
        SetStateDisc(SuccessDiscBrush);
        SetStateBorder(IdleBorderBrush);
        StateHalo.Opacity = 0;
        StopStateAnimations();
        BeginStateStoryboard("SuccessStoryboard");
        ShowCapsule();
        ScheduleHide(TimeSpan.FromSeconds(4));
    }

    private void SetModelTransferState(ModelTransferProgress progress)
    {
        var stageChanged = _lastVisualStateKind != CapsuleVisualStateKind.Downloading || _lastTransferStage != progress.Stage;
        _lastTransferStage = progress.Stage;
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Downloading, ModelProgressFormatter.Capsule(progress), progress.Percentage));
        StopWaveformAnimation();
        CheckIcon.Visibility = progress.Stage == ModelTransferStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        var downloading = progress.Stage == ModelTransferStage.Downloading;
        SpinnerIcon.Visibility = progress.Stage is ModelTransferStage.Verifying or ModelTransferStage.Loading
            ? Visibility.Visible
            : Visibility.Collapsed;
        DownloadIcon.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = progress.Stage == ModelTransferStage.Failed ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Visibility = progress.Stage is ModelTransferStage.Downloading or ModelTransferStage.Verifying or ModelTransferStage.Loading
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!stageChanged) return;
        ApplyThemeToCapsule();
        SetStateDisc(progress.Stage == ModelTransferStage.Ready ? SuccessDiscBrush : System.Windows.Media.Brushes.Transparent);
        SetStateBorder(progress.Stage == ModelTransferStage.Failed ? ErrorBrush : IdleBorderBrush);
        StateHalo.Opacity = 0;
        StopStateAnimations();
        if (downloading)
        {
            BeginStateStoryboard("DownloadStoryboard");
        }
        else if (progress.Stage is ModelTransferStage.Verifying or ModelTransferStage.Loading)
        {
            BeginStateStoryboard("SpinStoryboard");
        }
    }

    private void ShowModelsReady()
    {
        ShowSuccess("Модель готова");
        ScheduleHide(TimeSpan.FromSeconds(3));
    }

    private void ShowError(string title)
    {
        PlayFeedback(FeedbackSound.Error);
        ApplyVisualStateLayout(new CapsuleVisualState(CapsuleVisualStateKind.Error, title));
        StopWaveformAnimation();
        _isRecording = false;
        _isProcessing = false;
        SetStateDisc(SuccessDiscBrush);

        // Recording uses a filled microphone disc; failures use a scarlet outline and error icon.
        SetStateBorder(ErrorBrush);
        StateHalo.Opacity = 0;
        StopStateAnimations();
        BeginStateStoryboard("ErrorStoryboard");
        ShowCapsule();
        ScheduleHide(TimeSpan.FromSeconds(5));
    }

    private void SetCancelActionVisible(bool visible)
    {
        CloseButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Opacity = visible ? 1 : 0;
        ActionColumn.Width = visible ? new GridLength(24) : new GridLength(0);
        ActionDividerColumn.Width = visible ? new GridLength(3) : new GridLength(0);
    }

    /// <summary>Updates a fixed-width clock without taking space away from the speech meter.</summary>
    private void UpdateRecordingTimer()
    {
        // The start time is only meaningful once a real capture has begun. Without this guard the
        // preview and diagnostic paths, which never set it, formatted the span since year zero and
        // rendered a nine-digit minute count that shoved the waveform out of the capsule.
        if (!_isRecording || _recordingStartedUtc == default)
        {
            SetRecordingTimerVisible(false);
            return;
        }

        var elapsed = DateTime.UtcNow - _recordingStartedUtc;

        // A hard ceiling on how long one dictation may run. Nothing legitimate reaches it; what
        // does is a trigger stuck in the "pressed" state, and without a stop the recording grows
        // until memory or disk runs out. Stopping normally means the audio is still transcribed.
        if (elapsed > MaximumRecordingDuration)
        {
            AppLog.Write($"Recording exceeded {MaximumRecordingDuration.TotalMinutes:0} minutes; stopping");
            _pushToTalk.Reset();
            _ = StopAndTranscribeAsync();
            return;
        }

        if (elapsed < TimerAppearsAfter || elapsed > MaximumDisplayedDuration)
        {
            SetRecordingTimerVisible(false);
            return;
        }

        var wholeSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        if (wholeSeconds != _lastDisplayedRecordingSecond)
        {
            _lastDisplayedRecordingSecond = wholeSeconds;
            RecordingTimer.Text = $"{wholeSeconds / 60:0}:{wholeSeconds % 60:00}";
        }
        SetRecordingTimerVisible(true);
    }

    private void SetRecordingTimerVisible(bool visible)
    {
        if (visible == _timerVisible)
        {
            return;
        }

        _timerVisible = visible;
        RecordingTimer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimerColumn.Width = new GridLength(visible ? 33 : 0);
    }

    private static readonly TimeSpan TimerAppearsAfter = TimeSpan.Zero;

    /// <summary>
    /// Elapsed time the listening preview pretends to be at. Past <see cref="TimerAppearsAfter"/>
    /// so the timer is actually drawn, and two digits wide, which is the common case.
    /// </summary>
    private static readonly TimeSpan PreviewElapsedTime = TimeSpan.FromSeconds(27);

    /// <summary>Beyond this the clock is wrong, not the dictation long.</summary>
    private static readonly TimeSpan MaximumDisplayedDuration = TimeSpan.FromHours(2);

    /// Thirty minutes accommodates long uninterrupted dictations without overflowing memory.
    private static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromMinutes(30);

    private void BeginStateStoryboard(string resourceKey)
    {
        if (!IsReducedMotion && CanRenderCapsuleMotion)
        {
            ((Storyboard)Resources[resourceKey]).Begin(this, true);
        }
    }

    private void PlayFeedback(FeedbackSound sound, bool preview = false)
    {
        // An acoustic cue during capture suppresses the beginning of the recorded PCM.
        if (sound == FeedbackSound.RecordingStarted && !preview && _isRecording) return;
        if (preview)
        {
            _sounds.Preview(sound);
        }
        else
        {
            _sounds.Play(sound);
        }
    }

    /// <summary>
    /// Publishes the capsule's state to assistive technology.
    /// </summary>
    /// <remarks>
    /// The window is <c>WS_EX_NOACTIVATE</c> and non-focusable by design — it must never steal
    /// focus from what the user is dictating into — which also means a screen reader will never
    /// land on it by navigation. A polite live region is therefore the only channel: the state is
    /// announced where the user already is, without moving them.
    /// </remarks>
    private void AnnounceState(CapsuleVisualState state)
    {
        var announcement = state.Kind switch
        {
            CapsuleVisualStateKind.Listening => "Запись идёт",
            CapsuleVisualStateKind.Recognizing => state.Progress is null
                ? state.Label ?? "Распознавание"
                : $"{state.Label} {state.Progress:0} процентов",
            CapsuleVisualStateKind.Success => state.Label is null or "Вставлено" ? "Текст вставлен" : state.Label,
            CapsuleVisualStateKind.Clipboard => "Текст скопирован, нажмите Ctrl+V",
            CapsuleVisualStateKind.Error => $"Ошибка: {state.Label}",
            CapsuleVisualStateKind.Downloading => state.Label ?? "Загрузка модели",
            _ => "Готово"
        };

        if (_lastAnnouncement == announcement)
        {
            return;
        }

        _lastAnnouncement = announcement;
        System.Windows.Automation.AutomationProperties.SetName(CapsuleShell, announcement);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(CapsuleShell)
            ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(CapsuleShell);
        peer?.RaiseAutomationEvent(
            System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void ApplyVisualStateLayout(CapsuleVisualState state)
    {
        if (_lastVisualLayout == state) return;
        _lastVisualLayout = state;
        var stateChanged = _lastVisualStateKind != state.Kind;
        _lastVisualStateKind = state.Kind;
        MicIcon.Visibility = state.Kind is CapsuleVisualStateKind.Ready or CapsuleVisualStateKind.Listening or CapsuleVisualStateKind.Recognizing
            ? Visibility.Visible : Visibility.Collapsed;
        CheckIcon.Visibility = state.Kind == CapsuleVisualStateKind.Success
            ? Visibility.Visible : Visibility.Collapsed;
        ClipboardIcon.Visibility = state.Kind == CapsuleVisualStateKind.Clipboard
            ? Visibility.Visible : Visibility.Collapsed;
        SpinnerIcon.Visibility = Visibility.Collapsed;
        DownloadIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = state.Kind == CapsuleVisualStateKind.Error
            ? Visibility.Visible : Visibility.Collapsed;
        ProcessingDots.Visibility = state.Kind == CapsuleVisualStateKind.Recognizing && state.Progress is null
            ? Visibility.Visible : Visibility.Collapsed;
        Waveform.Visibility = state.Kind == CapsuleVisualStateKind.Listening
            ? Visibility.Visible : Visibility.Collapsed;
        var showProgress = state.Progress is not null && state.Kind is CapsuleVisualStateKind.Recognizing or CapsuleVisualStateKind.Downloading;
        DownloadProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        if (state.Progress is not null)
        {
            DownloadProgress.Value = state.Progress.Value;
        }
        ProcessingLabel.Text = state.Label ?? string.Empty;
        DetailText.Text = state.Kind == CapsuleVisualStateKind.Recognizing && state.Progress is not null
            ? $"{state.Label} {state.Progress:0}%"
            : state.Label ?? string.Empty;
        DetailText.Visibility = state.Kind == CapsuleVisualStateKind.Listening ||
                                state.Kind == CapsuleVisualStateKind.Recognizing && state.Progress is null
            ? Visibility.Collapsed : Visibility.Visible;
        SetCancelActionVisible(state.CanCancel);
        AnnounceState(state);

        if (state.Kind != CapsuleVisualStateKind.Listening)
            SetRecordingTimerVisible(false);
        AnimateCapsuleWidth(256);

        if (stateChanged) ApplyThemeToCapsule();

        if (stateChanged && CanRenderCapsuleMotion && !IsReducedMotion)
        {
            ((Storyboard)Resources["StateTransitionStoryboard"]).Begin(this, true);
        }
    }

    private void OnCapsuleThemeChanged(object? sender, AppThemeChangedEventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnCapsuleThemeChanged(sender, args));
            return;
        }

        Waveform.HighContrast = args.EffectiveTheme == EffectiveAppTheme.HighContrast;
        _waveCadence.Reset();
        if (args.ReducedMotion)
        {
            ApplyReducedMotionImmediately();
        }
        else if (_lastVisualStateKind == CapsuleVisualStateKind.Recognizing && _processingIsIndeterminate)
        {
            BeginStateStoryboard("ProcessingStoryboard");
        }
        else if (_lastVisualStateKind == CapsuleVisualStateKind.Downloading)
        {
            if (_lastTransferStage == ModelTransferStage.Downloading) BeginStateStoryboard("DownloadStoryboard");
            else if (_lastTransferStage is ModelTransferStage.Verifying or ModelTransferStage.Loading) BeginStateStoryboard("SpinStoryboard");
        }
        ApplyThemeToCapsule();
    }

    private void ApplyReducedMotionImmediately()
    {
        StopStateAnimations();
        ((Storyboard)Resources["StateTransitionStoryboard"]).Stop(this);
        ((Storyboard)Resources["EnterStoryboard"]).Stop(this);
        _exitStoryboard.Stop(this);

        CapsuleShell.Opacity = 1;
        ShadowSurface.Opacity = 1;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        ShellTranslate.X = 0;
        ShellTranslate.Y = 0;
        StateHalo.Opacity = 0;
        StateHaloScale.ScaleX = 1;
        StateHaloScale.ScaleY = 1;
        StateDiscScale.ScaleX = 1;
        StateDiscScale.ScaleY = 1;
        StateContentTranslate.X = 0;
        CenterContent.Opacity = 1;
        StateDisc.Opacity = 1;
        SpinnerOrbitRotate.Angle = 0;
        DownloadArrowOffset.Y = 0;
        DownloadIcon.Opacity = 1;
        CheckScale.ScaleX = 1;
        CheckScale.ScaleY = 1;
        ProcessingDot1.Opacity = 0.4;
        ProcessingDot2.Opacity = 0.4;
        ProcessingDot3.Opacity = 0.4;
        ProcessingDot1Scale.ScaleX = ProcessingDot1Scale.ScaleY = 1;
        ProcessingDot2Scale.ScaleX = ProcessingDot2Scale.ScaleY = 1;
        ProcessingDot3Scale.ScaleX = ProcessingDot3Scale.ScaleY = 1;

        CapsuleBody.BeginAnimation(FrameworkElement.WidthProperty, null);
        CapsuleBody.Width = 256;

        if (_hideRequested)
        {
            _hideRequested = false;
            _forceHideAfterCancellation = false;
            _displayingBackgroundModelProgress = false;
            Hide();
        }
    }

    private void ApplyThemeToCapsule()
    {
        BuildWaveform();
        SetStateDisc(_lastVisualStateKind switch
        {
            CapsuleVisualStateKind.Listening or CapsuleVisualStateKind.Recognizing => ActiveDiscBrush,
            CapsuleVisualStateKind.Success or CapsuleVisualStateKind.Clipboard or CapsuleVisualStateKind.Error => SuccessDiscBrush,
            CapsuleVisualStateKind.Downloading when _lastTransferStage == ModelTransferStage.Ready => SuccessDiscBrush,
            _ => System.Windows.Media.Brushes.Transparent
        });
        if (EffectiveTheme == EffectiveAppTheme.HighContrast)
        {
            RootBorder.PhysicalStroke = 1.6;
            RootBorder.Background = System.Windows.SystemColors.WindowBrush;
            RecordingTimer.Foreground = System.Windows.SystemColors.WindowTextBrush;
            CloseButton.Foreground = System.Windows.SystemColors.WindowTextBrush;
            RootBorder.BorderBrush = System.Windows.SystemColors.WindowTextBrush;
            DetailText.Foreground = System.Windows.SystemColors.WindowTextBrush;
            ProcessingLabel.Foreground = System.Windows.SystemColors.WindowTextBrush;
            SetMicStroke(_lastVisualStateKind is CapsuleVisualStateKind.Listening or CapsuleVisualStateKind.Recognizing
                ? System.Windows.SystemColors.HighlightTextBrush : System.Windows.SystemColors.WindowTextBrush);
            SetStroke(CheckIcon, System.Windows.SystemColors.WindowTextBrush);
            ClipboardIcon.Foreground = System.Windows.SystemColors.WindowTextBrush;
            SpinnerIcon.Opacity = 1;
            DownloadIcon.Foreground = System.Windows.SystemColors.HighlightBrush;
            SetStroke(ErrorIcon, System.Windows.SystemColors.HighlightBrush);
            DownloadProgress.Foreground = System.Windows.SystemColors.HighlightBrush;
            DownloadProgress.Background = System.Windows.SystemColors.ControlBrush;
            SurfaceGradient.Visibility = Visibility.Collapsed;
            InnerSpecularBorder.Visibility = Visibility.Collapsed;
            HoverSurface.Visibility = Visibility.Collapsed;
            SuccessFlash.Visibility = Visibility.Collapsed;
            ShadowSurface.Visibility = Visibility.Collapsed;
        }
        else
        {
            RootBorder.Background = SurfaceBrush;
            RootBorder.BorderBrush = _lastVisualStateKind switch
            {
                CapsuleVisualStateKind.Listening or CapsuleVisualStateKind.Recognizing => ActiveBorderBrush,
                CapsuleVisualStateKind.Error => ErrorBrush,
                CapsuleVisualStateKind.Downloading when _lastTransferStage == ModelTransferStage.Failed => ErrorBrush,
                _ => IdleBorderBrush
            };
            DetailText.Foreground = PrimaryTextBrush;
            ProcessingLabel.Foreground = PrimaryTextBrush;
            SetMicStroke(AccentBrush);
            RecordingTimer.Foreground = CapsuleMutedBrush;
            CloseButton.Foreground = CapsuleMutedBrush;
            SetStroke(CheckIcon, AccentBrush);
            ClipboardIcon.Foreground = PrimaryTextBrush;
            DownloadIcon.Foreground = AccentBrush;
            SetStroke(ErrorIcon, ErrorBrush);
            DownloadProgress.Foreground = AccentBrush;
            DownloadProgress.Background = ProgressTrackBrush;
            RootBorder.PhysicalStroke = _lastVisualStateKind == CapsuleVisualStateKind.Error ||
                _lastVisualStateKind == CapsuleVisualStateKind.Downloading && _lastTransferStage == ModelTransferStage.Failed ? 1.25 : 0;
            SurfaceGradient.Visibility = Visibility.Collapsed;
            InnerSpecularBorder.Visibility = Visibility.Collapsed;
            HoverSurface.Visibility = Visibility.Collapsed;
            SuccessFlash.Visibility = Visibility.Collapsed;
            ShadowSurface.Visibility = Visibility.Collapsed;

        }
    }

    /// <summary>
    /// Recolours the vector microphone. It is a stroked path rather than a glyph now, so the colour
    /// lives on <c>Stroke</c> and has to be pushed down to the children.
    /// </summary>
    private void SetMicStroke(System.Windows.Media.Brush brush) => SetStroke(MicIcon, brush);

    private static void SetStroke(System.Windows.Controls.Canvas icon, System.Windows.Media.Brush brush)
    {
        foreach (var path in icon.Children.OfType<System.Windows.Shapes.Path>())
        {
            path.Stroke = brush;
        }
    }

    private void SetStateBorder(System.Windows.Media.Brush brush) =>
        RootBorder.BorderBrush = EffectiveTheme == EffectiveAppTheme.HighContrast ? System.Windows.SystemColors.WindowTextBrush : brush;

    private void SetStateDisc(System.Windows.Media.Brush brush) =>
        StateDisc.Background = EffectiveTheme == EffectiveAppTheme.HighContrast && brush is SolidColorBrush { Color.A: > 0 }
            ? System.Windows.SystemColors.HighlightBrush
            : brush;

    /// <summary>
    /// Morphs the capsule between state widths.
    /// </summary>
    /// <remarks>
    /// The animation now runs on an element inside a fixed-size window. Animating
    /// <c>Window.Width</c> meant a HWND resize plus a separate, non-atomic <c>SetWindowPos</c> to
    /// re-centre it on every frame — at 144 Hz that is 144 resizes a second, each dragging a full
    /// layout pass and a re-rasterization of the supersampled chrome behind it. The window is
    /// simply large enough for the widest state now, and the body is centred inside it.
    /// </remarks>
    private void AnimateCapsuleWidth(double targetWidth)
    {
        var currentWidth = CapsuleBody.ActualWidth > 0 ? CapsuleBody.ActualWidth : CapsuleBody.Width;
        if (Math.Abs(currentWidth - targetWidth) < 0.1)
        {
            return;
        }

        if (IsReducedMotion || !IsVisible)
        {
            CapsuleBody.BeginAnimation(FrameworkElement.WidthProperty, null);
            CapsuleBody.Width = targetWidth;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentWidth,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        CapsuleBody.BeginAnimation(
            FrameworkElement.WidthProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StartWaveformAnimation()
    {
        BuildWaveform();
        _waveSubscription!.Start(CanRenderWaveform);
    }

    private void StopWaveformAnimation()
    {
        _waveSubscription?.Stop();
        _audioLevelTarget = 0;
        _audioLevelCurrent = 0;
    }

    private void OnWaveformRendering(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs rendering)
        {
            return;
        }

        if (!CanRenderWaveform)
        {
            // A defensive detach also covers visibility changes during event dispatch.
            _waveSubscription?.Refresh(eligible: false);
            return;
        }
        if (_waveCadence.TryAdvance(rendering.RenderingTime, IsReducedMotion, out var deltaSeconds))
        {
            AnimateWaveformFrame(deltaSeconds);
        }
    }

    /// <summary>Isolated CLI fixture; exercises only visual state, never capture or transcription.</summary>
    public async Task RenderWaveformLifecycleDiagnosticsAsync(string outputPath)
    {
        if (!_audioCapture.GetState().IsPaused)
            throw new InvalidOperationException("Visual diagnostics require paused capture.");
        var foreground = NativeMethods.GetForegroundWindow();
        var originalTheme = EffectiveTheme;
        var originalReducedMotion = IsReducedMotion;
        var observations = new List<object>();
        bool ProcessingMotionActive()
        {
            try
            {
                return ((Storyboard)Resources["ProcessingStoryboard"]).GetCurrentState(this) == ClockState.Active;
            }
            catch (InvalidOperationException) { return false; }
        }
        void Capture(string state) => observations.Add(new
        {
            state,
            requested = _waveSubscription?.IsRequested ?? false,
            subscribed = _waveRendering,
            visible = IsVisible,
            loaded = IsLoaded,
            minimized = WindowState == WindowState.Minimized,
            waveformVisible = Waveform.IsVisible,
            processingAnimationActive = ProcessingMotionActive(),
            reducedMotion = IsReducedMotion,
            foregroundUnchanged = NativeMethods.GetForegroundWindow() == foreground
        });
        Capture("constructed");
        try
        {
            foreach (var theme in new[] { EffectiveAppTheme.Dark, EffectiveAppTheme.HighContrast })
            {
                var label = theme == EffectiveAppTheme.Dark ? "dark" : "contrast";
                _themeService.ApplyDiagnostic(theme, reducedMotion: theme == EffectiveAppTheme.HighContrast);
                ShowListeningPreview();
                await Task.Delay(80);
                Capture(label + "-listening");
                Waveform.Visibility = Visibility.Collapsed;
                await Task.Delay(80);
                Capture(label + "-waveform-collapsed");
                Waveform.Visibility = Visibility.Visible;
                await Task.Delay(80);
                Capture(label + "-waveform-restored");
                Hide();
                await Task.Delay(80);
                Capture(label + "-hidden");
                Show();
                await Task.Delay(80);
                Capture(label + "-restored");
                WindowState = WindowState.Minimized;
                await Task.Delay(80);
                Capture(label + "-minimized");
                WindowState = WindowState.Normal;
                await Task.Delay(80);
                Capture(label + "-unminimized");
                _isRecording = false;
                ShowStatePreview("processing");
                await Task.Delay(80);
                Capture(label + "-processing");
                Hide();
                await Task.Delay(80);
                Capture(label + "-processing-hidden");
                Show();
                await Task.Delay(80);
                Capture(label + "-processing-restored");
                _isProcessing = false;
                SetReadyState();
                Hide();
                Show();
                await Task.Delay(80);
                Capture(label + "-stopped-restored");
            }
        }
        finally
        {
            _isRecording = false;
            _isProcessing = false;
            StopWaveformAnimation();
            _themeService.ApplyDiagnostic(originalTheme, originalReducedMotion);
        }
        Dispose();
        Capture("disposed");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
        await System.IO.File.WriteAllTextAsync(outputPath, System.Text.Json.JsonSerializer.Serialize(
            new { schema = "egoist.voice.capsule-animation-lifecycle/v1", observations,
                noCaptureOrTranscription = true, diagnosticDelayMs = 80 },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
