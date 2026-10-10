using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice;

public partial class SettingsWindow : Window
{
    private System.Windows.Media.Brush MutedBrush => (System.Windows.Media.Brush)FindResource("AppTextMutedBrush");
    private System.Windows.Media.Brush NormalBrush => (System.Windows.Media.Brush)FindResource("AppAccentBrush");
    private System.Windows.Media.Brush LoudBrush => (System.Windows.Media.Brush)FindResource("AppWarningBrush");
    private System.Windows.Media.Brush SuccessBrush => (System.Windows.Media.Brush)FindResource("AppSuccessBrush");
    private System.Windows.Media.Brush PrimaryBrush => (System.Windows.Media.Brush)FindResource("AppTextPrimaryBrush");
    private readonly MainWindow _mainWindow;
    private readonly DictationSettingsService _settingsService;
    private readonly RecentRecordingHistoryService _recentRecordings;
    private readonly Action? _quit;
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _loading;
    private bool _allowClose;
    private bool _closed;
    private Task? _microphoneRefreshTask;
    private bool _recognitionModelPreviewActive;
    private FrameworkElement? _pageTransitionElement;
    private TranslateTransform? _pageTransitionOffset;
    public bool DiagnosticPreview { get; set; }

    public SettingsWindow(MainWindow mainWindow, DictationSettingsService settingsService, Action? quit = null)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        _mainWindow = mainWindow;
        _settingsService = settingsService;
        _recentRecordings = mainWindow.RecentRecordings;
        _quit = quit;
        ThemeCombo.ItemsSource = ThemeChoices;
        ThemeCombo.DisplayMemberPath = nameof(ThemeChoice.Label);
        ActivationCombo.ItemsSource = ActivationChoices;
        ActivationCombo.DisplayMemberPath = nameof(ActivationChoice.Label);
        ExitButton.Visibility = quit is null ? Visibility.Collapsed : Visibility.Visible;
        _mainWindow.AudioCaptureStateChanged += OnAudioCaptureStateChanged;
        _mainWindow.ThemeChanged += OnThemeChanged;
        _mainWindow.ActivationBindingChanged += OnActivationBindingChanged;
        _mainWindow.TranslationEngineHealthChanged += OnTranslationEngineHealthChanged;
        _recentRecordings.Changed += OnRecentRecordingsChanged;
        _recentRecordings.PlaybackChanged += OnRecentRecordingPlaybackChanged;
        _levelTimer.Tick += (_, _) =>
        {
            UpdateLevelMeter();
        };
        _stateTimer.Tick += (_, _) => UpdateControlCenterState();
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            SaveGeneralSettings();
        };
        Loaded += (_, _) =>
        {
            LoadGeneralSettings();
            RefreshMicrophones();
            RefreshHistory();
            UpdateControlCenterState();
            UpdateTranslationEngineState(_mainWindow.CurrentTranslationEngineHealth);
            UpdateActivityTimers();
            RequestMicrophoneInventoryRefresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                FlushPendingVolumeSettings();
                LoadGeneralSettings();
                RefreshMicrophones();
                RefreshHistory();
                UpdateControlCenterState();
                UpdateTranslationEngineState(_mainWindow.CurrentTranslationEngineHealth);
                UpdateActivityTimers();
                RequestMicrophoneInventoryRefresh();
            }
            else
            {
                FlushPendingVolumeSettings();
                UpdateActivityTimers();
                StopPageTransition();
                _textOperation?.Cancel();
                _recentRecordings.StopPlayback();
            }
        };
        StateChanged += (_, _) =>
        {
            UpdateActivityTimers();
            if (WindowState == WindowState.Minimized)
                StopPageTransition();
        };
        Closing += (_, args) =>
        {
            FlushPendingVolumeSettings();
            if (!_allowClose)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _levelTimer.Stop();
            _stateTimer.Stop();
            _textOperation?.Cancel();
            StopPageTransition();
            _volumeSaveTimer.Stop();
            _mainWindow.AudioCaptureStateChanged -= OnAudioCaptureStateChanged;
            _mainWindow.ThemeChanged -= OnThemeChanged;
            _mainWindow.ActivationBindingChanged -= OnActivationBindingChanged;
            _mainWindow.TranslationEngineHealthChanged -= OnTranslationEngineHealthChanged;
            _recentRecordings.Changed -= OnRecentRecordingsChanged;
            _recentRecordings.PlaybackChanged -= OnRecentRecordingPlaybackChanged;
        };
    }

    private void ContentViewport_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the complete page reachable on small screens and high display scales.
        // At normal sizes the Viewbox uses 1:1 rendering and the editor grows with the window.
        SettingsContent.Width = Math.Max(960, e.NewSize.Width) - 48;
        SettingsContent.Height = Math.Max(688, e.NewSize.Height) - 40;
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        if (!DiagnosticPreview)
        {
            Activate();
            Focus();
        }
        UpdateControlCenterState();
        UpdateTranslationEngineState(_mainWindow.CurrentTranslationEngineHealth);
    }

    public void ShowHistoryAndActivate()
    {
        ShowAndActivate();
        SettingsTabs.SelectedItem = HistoryTab;
    }

    public void ShowAppearanceAndActivate()
    {
        ShowAndActivate();
        SettingsTabs.SelectedItem = AppearanceTab;
        UpdateAppearanceStatus();
    }

    public void ShowFeedbackAndActivate()
    {
        ShowAndActivate();
        SettingsTabs.SelectedItem = AudioTab;
        UpdateLayout();
    }

    public void ShowGeneralAndActivate()
    {
        ShowAndActivate();
        SettingsTabs.SelectedItem = GeneralTab;
    }

    public void ShowRecognitionAndActivate()
    {
        ShowAndActivate();
        SettingsTabs.SelectedItem = RecognitionTab;
    }

    public void ShowTranslationEngineRepairPreview()
    {
        ShowRecognitionAndActivate();
        UpdateTranslationEngineState(new TranslationEngineHealth(
            TranslationEngineHealthState.RepairRequired,
            "Нужно восстановить движок",
            "Runtime-пакет повреждён или неполон. Переустановите Слог и повторите проверку.",
            CanRetry: true));
    }

    public void ShowRecognitionModelFailurePreview()
    {
        ShowModelsAndActivate();
        _recognitionModelPreviewActive = true;
        UpdateRecognitionModelState(
            allModelsReady: false,
            new ModelTransferProgress(
                "GigaAM v3 · ядро",
                1,
                5,
                ModelTransferStage.Failed,
                0,
                318_995_997,
                0,
                0));
    }

    // Static render fixture only; never changes the real engine or saved settings.
    public void ShowRecognitionFormattingUnavailablePreview()
    {
        ShowModelsAndActivate();
        _recognitionModelPreviewActive = true;
        UpdateRecognitionModelState(allModelsReady: true, progress: null);
        RecognitionModelStatusText.Text = "Речь готова · оформление недоступно";
    }
    public void ShowFilledHistoryPreview()
    {
        ShowHistoryAndActivate();
        var now = DateTime.Now;
        HistoryItemsControl.ItemsSource = new[]
        {
            new HistoryItemPresentation("preview-1", $"Сегодня, {now:HH:mm}", "0:08 · Распознано", "Остановить", "Остановить воспроизведение записи"),
            new HistoryItemPresentation("preview-2", $"Сегодня, {now.AddMinutes(-4):HH:mm}", "0:15 · Распознано", "Слушать", "Воспроизвести запись"),
            new HistoryItemPresentation("preview-3", "Вчера, 22:41", "0:04 · Обработка не завершена", "Слушать", "Воспроизвести запись")
        };
        HistoryEmptyState.Visibility = Visibility.Collapsed;
        HistoryItemsControl.Visibility = Visibility.Visible;
        ClearHistoryButton.IsEnabled = true;
        HistorySummaryText.Text = "3 из 3 · 0:27 аудио";
        HistoryStateMessage.Visibility = Visibility.Collapsed;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public void RenderPreview(string outputPath)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        if (DiagnosticPreview && SettingsTabs.SelectedItem is TabItem { Content: FrameworkElement page })
        {
            var viewport = page.TransformToAncestor(this).TransformBounds(new Rect(page.RenderSize));
            var controls = new List<object>();
            var overflow = new List<string>();
            void Inspect(DependencyObject node)
            {
                if (node is FrameworkElement element && element.IsVisible && !string.IsNullOrEmpty(element.Name) &&
                    (element is System.Windows.Controls.Button or System.Windows.Controls.CheckBox or System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox or TextBlock))
                {
                    var bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
                    var fits = bounds.Top >= viewport.Top - 2 && bounds.Bottom <= viewport.Bottom + 2 &&
                        bounds.Left >= viewport.Left - 2 && bounds.Right <= viewport.Right + 2;
                    controls.Add(new { element.Name, fits, bounds.X, bounds.Y, bounds.Width, bounds.Height });
                    if (!fits) overflow.Add(element.Name);
                }
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Inspect(VisualTreeHelper.GetChild(node, index));
            }
            Inspect(page);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".layout.json"), System.Text.Json.JsonSerializer.Serialize(
                new { fits = overflow.Count == 0, page = ((TabItem)SettingsTabs.SelectedItem).Name, width = ActualWidth, height = ActualHeight, overflow, controls },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void OpenMicrophoneDropdownForPreview() => MicrophoneCombo.IsDropDownOpen = true;

    public void RenderScreenPreview(string outputPath)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new System.Windows.Point(0, 0));
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        using var bitmap = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                0,
                0,
                new System.Drawing.Size(width, height),
                System.Drawing.CopyPixelOperation.SourceCopy);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void LoadGeneralSettings()
    {
        _loading = true;
        try
        {
            var settings = _settingsService.Load();
            LoadTextSettings(settings);
            SpeechPunctuationCheck.IsChecked = settings.FormatSpeechPunctuation;
            SpeechPunctuationCheck.Visibility = Visibility.Collapsed;
            DirectFastModeCheck.IsChecked = settings.DirectGigaamFastMode;
            DirectFastModeCheck.Visibility = VoiceRuntimeProfile.IsPortable ? Visibility.Collapsed : Visibility.Visible;
            DirectFastModeHint.Text = VoiceRuntimeProfile.IsPortable
                ? "Русская речь распознаётся локально моделью GigaAM v3 со словарём: пунктуация и регистр определяются по голосу. Модель всегда готова в памяти, ожидания перед записью нет."
                : "GigaAM распознаёт русскую речь. Дополнительная сверка Whisper может помочь смешанной речи, но увеличивает ожидание.";
            MixedLanguageCheck.IsChecked = settings.MixedLanguageMode && !VoiceRuntimeProfile.IsPortable;
            MixedLanguageCheck.IsEnabled = !VoiceRuntimeProfile.IsPortable && !settings.PreserveSpokenWords;
            DirectFastModeCheck.IsEnabled = NumbersCheck.IsEnabled = VoiceCommandsCheck.IsEnabled =
                !settings.PreserveSpokenWords;
            MixedLanguageCheck.Visibility = VoiceRuntimeProfile.IsPortable ? Visibility.Collapsed : Visibility.Visible;
            NumbersCheck.IsChecked = settings.ApplyNumberNormalization;
            VoiceCommandsCheck.IsChecked = settings.ApplyVoiceCommands;
            AutostartCheck.IsChecked = AutostartService.IsEnabled();
            RestoreClipboardCheck.IsChecked = settings.RestoreClipboard;
            SoundCheck.IsChecked = settings.SoundFeedback;
            SoundVolumeSlider.Value = settings.SoundVolume;
            SoundVolumeLabel.Text = FormatVolume(settings.SoundVolume);
            NotificationsCheck.IsChecked = settings.DesktopNotifications;
            ThemeCombo.SelectedItem = ThemeChoices.First(choice => choice.Theme == settings.Theme);
            HistorySaveCheck.IsChecked = settings.SaveRecentRecordings;
            RefreshActivationState();
            UpdateAppearanceStatus();
            UpdateControlCenterState();
            UpdateTranslationEngineState(_mainWindow.CurrentTranslationEngineHealth);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RequestMicrophoneInventoryRefresh()
    {
        if (_closed || _microphoneRefreshTask is { IsCompleted: false }) return;
        _microphoneRefreshTask = RefreshMicrophoneInventoryAsync();
    }

    private async Task RefreshMicrophoneInventoryAsync()
    {
        try
        {
            await _mainWindow.RefreshCaptureDevicesAsync();
            if (_closed || !IsVisible) return;
            RefreshMicrophones();
            UpdateControlCenterState();
        }
        catch (Exception exception)
        {
            if (!_closed) AppLog.Write("Settings microphone inventory refresh failed", exception);
        }
    }

    private void RefreshMicrophones()
    {
        _loading = true;
        try
        {
            var state = _mainWindow.CurrentAudioCaptureState;
            var devices = _mainWindow.CaptureDevices;
            var defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
            var choices = new List<MicrophoneChoice>
            {
                new(null, defaultDevice is null
                    ? "Системный микрофон · недоступен"
                    : $"Системный · {defaultDevice.Name}")
            };
            choices.AddRange(devices.Select(device => new MicrophoneChoice(
                device.Id,
                device.IsDefault ? $"{device.Name} · по умолчанию" : device.Name)));
            if (state.SelectedDeviceId is not null
                && choices.All(choice => !string.Equals(choice.Id, state.SelectedDeviceId, StringComparison.Ordinal)))
            {
                choices.Add(new MicrophoneChoice(state.SelectedDeviceId, "Выбранный микрофон · недоступен"));
            }

            MicrophoneCombo.ItemsSource = choices;
            MicrophoneCombo.DisplayMemberPath = nameof(MicrophoneChoice.Label);
            MicrophoneCombo.SelectedItem = choices.First(choice => string.Equals(
                choice.Id,
                state.SelectedDeviceId,
                StringComparison.Ordinal));
            UpdateAudioState(state);
        }
        catch (Exception exception)
        {
            MicrophoneStatus.Text = "Не удалось получить список микрофонов.";
            MicrophoneStatus.Foreground = LoudBrush;
            AppLog.Write("Settings microphone refresh failed", exception);
        }
        finally
        {
            _loading = false;
        }
    }

    private async void MicrophoneCombo_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || MicrophoneCombo.SelectedItem is not MicrophoneChoice choice)
        {
            return;
        }
        try
        {
            MicrophoneCombo.IsEnabled = false;
            await _mainWindow.SelectMicrophoneAsync(choice.Id);
            RefreshMicrophones();
        }
        catch (Exception exception)
        {
            ShowAudioError(exception.Message);
            RefreshMicrophones();
        }
        finally
        {
            MicrophoneCombo.IsEnabled = true;
        }
    }

    private async void PauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PauseButton.IsEnabled = false;
            GeneralPauseButton.IsEnabled = false;
            var state = _mainWindow.CurrentAudioCaptureState;
            await _mainWindow.SetMicrophonePausedAsync(!state.IsUserPaused);
            RefreshMicrophones();
        }
        catch (Exception exception)
        {
            ShowAudioError(exception.Message);
        }
        finally
        {
            PauseButton.IsEnabled = !_mainWindow.IsCaptureOperationPending;
            GeneralPauseButton.IsEnabled = !_mainWindow.IsCaptureOperationPending;
            UpdateControlCenterState();
        }
    }

    private async void StartStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StartStopButton.IsEnabled = false;
            await _mainWindow.ToggleRecordingAsync();
        }
        catch (Exception exception)
        {
            VoiceStateTitle.Text = "Не удалось изменить состояние диктовки";
            VoiceStateDetail.Text = exception.Message;
            VoiceStatusDot.Fill = LoudBrush;
            AppLog.Write("Control center start/stop failed", exception);
        }
        finally
        {
            UpdateControlCenterState();
        }
    }

    private void ActivationCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ActivationCombo.SelectedItem is not ActivationChoice choice)
        {
            return;
        }

        if (_mainWindow.TrySetActivationBinding(choice.Binding, out var error))
        {
            RefreshActivationState();
            return;
        }

        ActivationStatus.Text = error ?? "Не удалось сменить кнопку запуска.";
        ActivationStatus.Foreground = LoudBrush;
        RefreshActivationState();
    }

    private void CustomShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.SetActivationCaptureActive(true);
        CustomShortcutDialog dialog;
        bool accepted;
        try
        {
            dialog = new CustomShortcutDialog(_mainWindow.CurrentCustomShortcut) { Owner = this };
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            _mainWindow.SetActivationCaptureActive(false);
        }

        if (!accepted)
        {
            return;
        }

        if (_mainWindow.TrySetCustomShortcut(dialog.SelectedShortcut, out var error))
        {
            RefreshActivationState();
            return;
        }

        ActivationStatus.Text = error ?? "Не удалось назначить сочетание.";
        ActivationStatus.Foreground = LoudBrush;
    }

    private void OpenDictionaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.EnsureDictionaryTemplate();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settingsService.DictionaryPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            VoiceStateTitle.Text = "Не удалось открыть словарь";
            VoiceStateDetail.Text = "Проверьте доступ к локальной папке настроек.";
            AppLog.Write("Could not open the user dictionary from settings", exception);
        }
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _quit?.Invoke();
    }

    private async void TranslationEngineRefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        TranslationEngineRefreshButton.IsEnabled = false;
        try
        {
            UpdateTranslationEngineState(TranslationEngineHealth.Initial);
            var health = await _mainWindow.RefreshTranslationEngineAsync();
            UpdateTranslationEngineState(health);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown owns the cancellation.
        }
        finally
        {
            TranslationEngineRefreshButton.IsEnabled = true;
        }
    }

    private void ShowModelDownloadsButton_OnClick(object sender, RoutedEventArgs e) =>
        _mainWindow.ShowModelDownloadStatus();

    private void RetryModelDownloadsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.RetryModelDownloads();
        UpdateControlCenterState();
    }

    private void GeneralSetting_OnChanged(object sender, RoutedEventArgs e) => SaveGeneralSettings();

    private void Autostart_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var wanted = AutostartCheck.IsChecked == true;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !AutostartService.SetEnabled(wanted, executable))
        {
            // Registry refused the change: show the real state instead of a switch that lies.
            _loading = true;
            try { AutostartCheck.IsChecked = AutostartService.IsEnabled(); }
            finally { _loading = false; }
        }
    }

    private void SoundVolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
        SoundVolumeLabel.Text = FormatVolume(e.NewValue);
    }

    private void SaveGeneralSettings()
    {
        if (_loading)
        {
            return;
        }
        var current = _settingsService.Load();
        _settingsService.Save(current with
        {
            FormatSpeechPunctuation = SpeechPunctuationCheck.IsChecked == true,
            DirectGigaamFastMode = DirectFastModeCheck.IsChecked == true,
            MixedLanguageMode = MixedLanguageCheck.IsChecked == true,
            ApplyNumberNormalization = NumbersCheck.IsChecked == true,
            ApplyVoiceCommands = VoiceCommandsCheck.IsChecked == true,
            RestoreClipboard = RestoreClipboardCheck.IsChecked == true,
            SoundFeedback = SoundCheck.IsChecked == true,
            SoundVolume = Math.Clamp(SoundVolumeSlider.Value, 0, 1),
            DesktopNotifications = NotificationsCheck.IsChecked == true
        });
        _mainWindow.ApplyDictationSettings();
    }

    private void ThemeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ThemeChoice choice)
        {
            return;
        }

        var current = _settingsService.Load();
        _settingsService.Save(current with { Theme = choice.Theme });
        _mainWindow.ApplyDictationSettings();
        UpdateAppearanceStatus();
    }

    private void SoundPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveGeneralSettings();
        _mainWindow.PreviewFeedbackSound();
    }

    private void UpdateActivityTimers()
    {
        var visible = IsLoaded && IsVisible && WindowState != WindowState.Minimized;
        if (visible && AudioTab.IsSelected) _levelTimer.Start();
        else _levelTimer.Stop();
        if (visible) _stateTimer.Start();
        else _stateTimer.Stop();
    }

    private void FlushPendingVolumeSettings()
    {
        if (!_volumeSaveTimer.IsEnabled) return;
        _volumeSaveTimer.Stop();
        SaveGeneralSettings();
    }

    private void StopPageTransition()
    {
        if (_pageTransitionElement is not { } content) return;
        content.BeginAnimation(OpacityProperty, null);
        _pageTransitionOffset?.BeginAnimation(TranslateTransform.YProperty, null);
        content.Opacity = 1;
        content.RenderTransform = Transform.Identity;
        _pageTransitionElement = null;
        _pageTransitionOffset = null;
    }

    private void SettingsTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsTabs)) return;
        UpdateActivityTimers();
        StopPageTransition();
        if (!IsLoaded || SettingsTabs.SelectedItem is not TabItem { Content: FrameworkElement content })
            return;

        content.Opacity = 1;
        content.RenderTransform = Transform.Identity;
        if (_mainWindow.ReducedMotion || !IsVisible || WindowState == WindowState.Minimized)
            return;

        var offset = new TranslateTransform(0, 6);
        _pageTransitionElement = content;
        _pageTransitionOffset = offset;
        content.RenderTransform = offset;
        content.Opacity = 0;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = easing
        });
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = easing
        });
    }

    private void UpdateAppearanceStatus()
    {
        ThemeStatus.Text = _mainWindow.EffectiveTheme switch
        {
            EffectiveAppTheme.Light => "Сейчас используется светлая палитра.",
            EffectiveAppTheme.HighContrast => "Сейчас используется высокая контрастность Windows.",
            _ => "Сейчас используется тёмная палитра."
        };
        MotionStatus.Text = _mainWindow.ReducedMotion
            ? "Упрощённые переходы включены настройками Windows."
            : "Плавные переходы включены. Непрерывная декоративная анимация не используется.";
    }

    private static string FormatVolume(double volume) => $"{Math.Round(Math.Clamp(volume, 0, 1) * 100):0}%";

    private async void HistorySave_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = HistorySaveCheck.IsChecked == true;
        if (!enabled)
        {
            var answer = System.Windows.MessageBox.Show(
                this,
                "Отключить сохранение новых записей. Удалить и три уже сохранённые записи?",
                "Локальная история",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel)
            {
                _loading = true;
                HistorySaveCheck.IsChecked = true;
                _loading = false;
                return;
            }

            SaveHistorySetting(false);
            _recentRecordings.StopPlayback();
            _recentRecordings.CancelPending();
            if (answer == MessageBoxResult.Yes)
            {
                await _recentRecordings.ClearAsync();
            }
        }
        else
        {
            SaveHistorySetting(true);
        }

        RefreshHistory();
    }

    private void SaveHistorySetting(bool enabled)
    {
        var current = _settingsService.Load();
        _settingsService.Save(current with { SaveRecentRecordings = enabled });
        _mainWindow.ApplyDictationSettings();
    }

    private async void HistoryPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string id })
        {
            return;
        }

        await _recentRecordings.PlayAsync(id);
        RefreshHistory();
    }

    private async void HistoryDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string id })
        {
            return;
        }

        await _recentRecordings.DeleteAsync(id);
        RefreshHistory();
    }

    private async void ClearHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recentRecordings.GetItems().Count == 0)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            "Удалить все локальные записи без возможности восстановления?",
            "Очистить историю",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _recentRecordings.ClearAsync();
        RefreshHistory();
    }

    private void OnRecentRecordingsChanged(object? sender, EventArgs e) => RefreshHistory();

    private void OnRecentRecordingPlaybackChanged(
        object? sender,
        RecentRecordingPlaybackChangedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshHistory);
            return;
        }

        var items = _recentRecordings.GetItems();
        var playing = _recentRecordings.PlayingRecordingId;
        HistoryItemsControl.ItemsSource = items.Select(item => HistoryItemPresentation.From(item, playing)).ToArray();
        HistoryEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryItemsControl.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.IsEnabled = items.Count > 0;
        var seconds = Math.Max(0, (int)Math.Round(items.Sum(item => item.Duration.TotalSeconds)));
        HistorySummaryText.Text = $"{items.Count} из {RecentRecordingHistoryService.Capacity} · {seconds / 60}:{seconds % 60:00} аудио";
        var message = _recentRecordings.StateMessage;
        HistoryStateMessage.Text = message ?? string.Empty;
        HistoryStateMessage.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnAudioCaptureStateChanged(object? sender, AudioCaptureStateChangedEventArgs change)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnAudioCaptureStateChanged(sender, change));
            return;
        }
        RefreshMicrophones();
        UpdateControlCenterState();
    }

    private void OnActivationBindingChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnActivationBindingChanged(sender, e));
            return;
        }
        RefreshActivationState();
    }

    private void OnTranslationEngineHealthChanged(object? sender, TranslationEngineHealthChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnTranslationEngineHealthChanged(sender, e));
            return;
        }
        UpdateTranslationEngineState(e.Health);
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs change)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnThemeChanged(sender, change));
            return;
        }
        if (_mainWindow.ReducedMotion) StopPageTransition();
        UpdateAppearanceStatus();
        // These status brushes are selected in code according to the current microphone state.
        // Re-evaluate them after the palette swap so an already-open Audio page never keeps a
        // detached brush from the previous ResourceDictionary.
        RefreshMicrophones();
        UpdateControlCenterState();
        UpdateTranslationEngineState(_mainWindow.CurrentTranslationEngineHealth);
    }

    private void RefreshActivationState()
    {
        var previousLoading = _loading;
        _loading = true;
        try
        {
            ActivationCombo.SelectedItem = ActivationChoices.First(choice =>
                choice.Binding == _mainWindow.CurrentActivationBinding);
            ActivationStatus.Text = $"Сейчас: {_mainWindow.CurrentActivationDisplayName}";
            ActivationStatus.Foreground = MutedBrush;
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private void UpdateControlCenterState()
    {
        LastTimingText.Text = _mainWindow.LastOperationSummary;
        if (AutoQwenCheck.IsEnabled) LocalQwenStatusText.Text = _mainWindow.LocalQwenStatus;
        var state = _mainWindow.CurrentAudioCaptureState;
        string title;
        string detail;
        System.Windows.Media.Brush statusBrush;

        if (_mainWindow.IsRecording)
        {
            title = "Идёт запись";
            detail = "Говорите обычным голосом. Нажмите ещё раз, чтобы распознать и вставить текст.";
            statusBrush = NormalBrush;
        }
        else if (_mainWindow.IsProcessing)
        {
            title = "Распознаю речь";
            detail = "Обработка выполняется локально. Окно можно свернуть в трей.";
            statusBrush = NormalBrush;
        }
        else if (!state.IsAvailable)
        {
            title = "Микрофон недоступен";
            detail = "Откройте раздел «Аудио» и выберите доступное устройство.";
            statusBrush = LoudBrush;
        }
        else if (state.IsPaused)
        {
            title = "Микрофон приостановлен";
            detail = "Возобновите микрофон, чтобы использовать кнопку запуска.";
            statusBrush = LoudBrush;
        }
        else
        {
            title = "Готово к диктовке";
            detail = $"{state.DeviceName} · {_mainWindow.CurrentActivationDisplayName}";
            statusBrush = SuccessBrush;
        }

        VoiceStateTitle.Text = title;
        VoiceStateDetail.Text = detail;
        VoiceStatusText.Text = title;
        VoiceStatusDot.Fill = statusBrush;

        var canStart = _mainWindow.IsRecording || _mainWindow.CanStartRecording;
        StartStopButton.IsEnabled = canStart;
        StartStopButton.Content = _mainWindow.IsRecording ? "Остановить" : "Начать";
        StartStopButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _mainWindow.IsRecording ? "Остановить диктовку и распознать" : "Начать диктовку");

        GeneralPauseButton.Content = state.IsUserPaused ? "Возобновить" : "Приостановить";
        GeneralPauseButton.IsEnabled = !_mainWindow.IsCaptureOperationPending;
        GeneralPauseButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            state.IsUserPaused ? "Возобновить микрофон" : "Приостановить микрофон");

        if (!_recognitionModelPreviewActive)
        {
            UpdateRecognitionModelState(
                _mainWindow.AreRecognitionModelsReady,
                _mainWindow.RecognitionModelProgress);
        }
    }

    private void UpdateRecognitionModelState(
        bool allModelsReady,
        ModelTransferProgress? progress)
    {
        var presentation = ModelProgressFormatter.ControlCenter(allModelsReady, progress);
        RecognitionModelStatusText.Text = presentation.StatusText;
        if (VoiceRuntimeProfile.IsPortable && allModelsReady)
            RecognitionModelStatusText.Text = "GigaAM v3 · русский · словарь · готово без сети";
        if (VoiceRuntimeProfile.IsPortable && _mainWindow.IsSpeechFormattingUnavailable)
            RecognitionModelStatusText.Text = "Речь готова · оформление недоступно";
        RecognitionModelStatusText.Foreground = presentation.IsFailure ? LoudBrush : MutedBrush;
        ShowModelDownloadsButton.Visibility = presentation.ShowProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryModelDownloadsButton.Visibility = presentation.CanRetry
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateTranslationEngineState(TranslationEngineHealth health)
    {
        TranslationEngineStateText.Text = health.Title;
        TranslationEngineDetailText.Text = health.Detail;
        TranslationEngineStateText.Foreground = health.State switch
        {
            TranslationEngineHealthState.Ready => SuccessBrush,
            TranslationEngineHealthState.Checking => PrimaryBrush,
            _ => LoudBrush
        };
        TranslationEngineRefreshButton.Content = health.State == TranslationEngineHealthState.Checking
            ? "Проверяю…"
            : "Проверить снова";
        TranslationEngineRefreshButton.IsEnabled = health.State != TranslationEngineHealthState.Checking;
    }

    private void UpdateAudioState(AudioCaptureState state)
    {
        PauseButton.Content = state.IsUserPaused ? "Возобновить" : "Приостановить";
        PauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            state.IsUserPaused ? "Возобновить микрофон" : "Приостановить микрофон");
        GeneralPauseButton.Content = state.IsUserPaused ? "Возобновить" : "Приостановить";
        GeneralPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            state.IsUserPaused ? "Возобновить микрофон" : "Приостановить микрофон");
        MicrophoneCombo.IsEnabled = state.IsAvailable || state.SelectedDeviceId is not null || MicrophoneCombo.Items.Count > 1;
        if (!state.IsAvailable)
        {
            MicrophoneStatus.Text = state.IsTransientlyUnavailable
                ? "Устройство недоступно · ожидаю восстановления"
                : "Устройство недоступно · запись приостановлена";
            MicrophoneStatus.Foreground = LoudBrush;
            PauseButton.IsEnabled = !_mainWindow.IsCaptureOperationPending;
            GeneralPauseButton.IsEnabled = !_mainWindow.IsCaptureOperationPending;
        }
        else if (state.IsPaused)
        {
            MicrophoneStatus.Text = $"{state.DeviceName} · приостановлен";
            MicrophoneStatus.Foreground = MutedBrush;
            PauseButton.IsEnabled = true;
            GeneralPauseButton.IsEnabled = true;
        }
        else
        {
            MicrophoneStatus.Text = $"{state.DeviceName} · готов";
            MicrophoneStatus.Foreground = MutedBrush;
            PauseButton.IsEnabled = true;
            GeneralPauseButton.IsEnabled = true;
        }
    }

    private void UpdateLevelMeter()
    {
        var state = _mainWindow.CurrentAudioCaptureState;
        var level = state.IsMonitoring ? Math.Clamp(_mainWindow.CurrentAudioLevel, 0, 1) : 0;
        LevelMeter.Value = level * 100;
        switch (AudioLevelBandPolicy.Classify((float)level))
        {
            case AudioLevelBand.Quiet:
                LevelLabel.Text = state.IsPaused ? "Микрофон приостановлен" : "Тихо · подвиньтесь ближе";
                LevelLabel.Foreground = MutedBrush;
                LevelMeter.Foreground = NormalBrush;
                break;
            case AudioLevelBand.Normal:
                LevelLabel.Text = "Хороший уровень";
                LevelLabel.Foreground = MutedBrush;
                LevelMeter.Foreground = NormalBrush;
                break;
            case AudioLevelBand.TooLoud:
                LevelLabel.Text = "Слишком громко · отодвиньте микрофон";
                LevelLabel.Foreground = LoudBrush;
                LevelMeter.Foreground = LoudBrush;
                break;
        }
    }

    private void ShowAudioError(string message)
    {
        MicrophoneStatus.Text = string.IsNullOrWhiteSpace(message)
            ? "Не удалось изменить настройки микрофона."
            : message;
        MicrophoneStatus.Foreground = LoudBrush;
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_textOperation is not null)
            {
                _textOperation.Cancel();
                e.Handled = true;
                return;
            }
            Hide();
            e.Handled = true;
        }
        else if (SettingsTabs.SelectedItem == TextTab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O) { OpenAudio_OnClick(sender, e); e.Handled = true; }
            else if (e.Key == Key.Enter) { _ = EditTextAsync(false); e.Handled = true; }
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

    private sealed record MicrophoneChoice(string? Id, string Label);

    private sealed record ThemeChoice(AppTheme Theme, string Label);

    private sealed record ActivationChoice(ActivationBinding Binding, string Label);

    private static readonly IReadOnlyList<ThemeChoice> ThemeChoices =
    [
        new(AppTheme.System, "Системная"),
        new(AppTheme.Light, "Светлая"),
        new(AppTheme.Dark, "Тёмная")
    ];

    private static readonly IReadOnlyList<ActivationChoice> ActivationChoices =
    [
        new(ActivationBinding.Mouse5AndKeyboard, "Mouse 5 + Ctrl + Alt + Space"),
        new(ActivationBinding.Mouse5, "Mouse 5"),
        new(ActivationBinding.Mouse4, "Mouse 4"),
        new(ActivationBinding.Keyboard, "Ctrl + Alt + Space"),
        new(ActivationBinding.CustomKeyboard, "Своя клавиша")
    ];

    private sealed record HistoryItemPresentation(
        string Id,
        string TimeLabel,
        string DetailLabel,
        string PlayLabel,
        string PlayAutomationName)
    {
        internal static HistoryItemPresentation From(RecentRecording item, string? playingId)
        {
            var local = item.CreatedUtc.ToLocalTime();
            var today = DateTime.Today;
            var time = local.Date == today
                ? $"Сегодня, {local:HH:mm}"
                : local.Date == today.AddDays(-1)
                    ? $"Вчера, {local:HH:mm}"
                    : local.ToString("d MMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
            var totalSeconds = Math.Max(1, (int)Math.Round(item.Duration.TotalSeconds));
            var duration = $"{totalSeconds / 60}:{totalSeconds % 60:00}";
            var status = item.Status == RecentRecordingStatus.Recognized
                ? "Распознано"
                : "Обработка не завершена";
            var isPlaying = string.Equals(item.Id, playingId, StringComparison.Ordinal);
            return new HistoryItemPresentation(
                item.Id,
                time,
                $"{duration} · {status}",
                isPlaying ? "Остановить" : "Слушать",
                isPlaying ? "Остановить воспроизведение записи" : "Воспроизвести запись");
        }
    }
}

internal enum AudioLevelBand
{
    Quiet,
    Normal,
    TooLoud
}

internal static class AudioLevelBandPolicy
{
    internal static AudioLevelBand Classify(float normalizedLevel) => normalizedLevel switch
    {
        < 0.18f => AudioLevelBand.Quiet,
        > 0.88f => AudioLevelBand.TooLoud,
        _ => AudioLevelBand.Normal
    };
}
