using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Egoist.Voice.Core;
using Forms = System.Windows.Forms;

namespace Egoist.Voice.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Icon _pausedIcon;
    private readonly MainWindow _window;
    private readonly IModelManager _modelManager;
    private readonly DictationSettingsService _settingsService;
    private readonly AppThemeService _themeService;
    private readonly EgoistTrayRenderer _renderer;
    private readonly Action _quit;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _startStopItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _microphoneMenu;
    private readonly Forms.ToolStripMenuItem _modelStatus;
    private readonly Forms.ToolStripMenuItem _showDownload;
    private readonly Forms.ToolStripMenuItem _retryDownload;
    private readonly Forms.ToolStripMenuItem _customActivationItem;
    private readonly Dictionary<ActivationBinding, Forms.ToolStripMenuItem> _activationItems = [];
    private readonly Forms.ToolStripMenuItem _mixedLanguageItem;
    private readonly Forms.ToolStripMenuItem _numbersItem;
    private readonly Forms.ToolStripMenuItem _voiceCommandsItem;
    private readonly Forms.ToolStripMenuItem _restoreClipboardItem;
    private readonly Forms.ToolStripMenuItem _soundItem;
    private readonly Forms.ToolStripMenuItem _notificationsItem;
    private readonly Forms.ToolStripMenuItem _themeMenu;
    private readonly Dictionary<AppTheme, Forms.ToolStripMenuItem> _themeItems = [];
    private readonly Forms.ToolStripMenuItem _historyMenu;
    private SettingsWindow? _settingsWindow;
    private ModelTransferProgress? _lastModelProgress;
    private string? _lastHistoryNotificationMessage;
    private TrayNotificationKind? _lastNotificationKind;
    private string? _lastNotificationMessage;
    private long _lastNotificationTimestamp;

    public TrayService(
        MainWindow window,
        IModelManager modelManager,
        DictationSettingsService settingsService,
        AppThemeService themeService,
        Action quit)
    {
        _window = window;
        _modelManager = modelManager;
        _settingsService = settingsService;
        _themeService = themeService;
        _quit = quit;

        EgoistTrayPalette.Apply(themeService.EffectiveTheme);
        var renderer = new EgoistTrayRenderer();
        _renderer = renderer;
        var menu = CreateDropDown<Forms.ContextMenuStrip>(renderer);
        _menu = menu;
        menu.ShowCheckMargin = true;
        _startStopItem = CreateItem("Начать / остановить", async (_, _) => await window.ToggleRecordingAsync());
        menu.Items.Add(_startStopItem);
        _pauseItem = CreateItem("Приостановить микрофон", async (_, _) => await TogglePauseAsync());
        menu.Items.Add(_pauseItem);

        _microphoneMenu = CreateItem("Микрофон");
        ConfigureDropDown(_microphoneMenu.DropDown, renderer);
        _microphoneMenu.DropDownOpening += (_, _) => RefreshMicrophoneMenu();
        menu.Items.Add(_microphoneMenu);

        var activationMenu = CreateItem("Кнопка запуска");
        ConfigureDropDown(activationMenu.DropDown, renderer);
        AddActivationItem(activationMenu, ActivationBinding.Mouse5AndKeyboard);
        AddActivationItem(activationMenu, ActivationBinding.Mouse5);
        AddActivationItem(activationMenu, ActivationBinding.Mouse4);
        AddActivationItem(activationMenu, ActivationBinding.Keyboard);
        activationMenu.DropDownItems.Add(CreateSeparator());
        _customActivationItem = CreateItem("Своя…", (_, _) => ConfigureCustomShortcut());
        _customActivationItem.CheckOnClick = false;
        activationMenu.DropDownItems.Add(_customActivationItem);
        menu.Items.Add(activationMenu);

        // Everything the post-processing pipeline can do was previously unreachable: the settings
        // existed only as a JSON file nobody knew about. A feature the user cannot find is a
        // feature that does not exist.
        var settingsMenu = CreateItem("Настройки");
        ConfigureDropDown(settingsMenu.DropDown, renderer);
        _mixedLanguageItem = AddToggle(settingsMenu, "Смешанная русско-английская речь",
            settings => settings with { MixedLanguageMode = !settings.MixedLanguageMode });
        _numbersItem = AddToggle(settingsMenu, "Числа цифрами",
            settings => settings with { ApplyNumberNormalization = !settings.ApplyNumberNormalization });
        _voiceCommandsItem = AddToggle(settingsMenu, "Голосовые команды",
            settings => settings with { ApplyVoiceCommands = !settings.ApplyVoiceCommands });
        _restoreClipboardItem = AddToggle(settingsMenu, "Возвращать буфер обмена",
            settings => settings with { RestoreClipboard = !settings.RestoreClipboard });
        _soundItem = AddToggle(settingsMenu, "Звуковые сигналы",
            settings => settings with { SoundFeedback = !settings.SoundFeedback });
        _notificationsItem = AddToggle(settingsMenu, "Важные уведомления",
            settings => settings with { DesktopNotifications = !settings.DesktopNotifications });
        settingsMenu.DropDownItems.Add(CreateSeparator());
        var dictionaryItem = CreateItem("Открыть словарь…", (_, _) => OpenDictionary());
        dictionaryItem.CheckOnClick = false;
        settingsMenu.DropDownItems.Add(dictionaryItem);
        menu.Items.Add(settingsMenu);

        _historyMenu = CreateItem("Последние записи");
        ConfigureDropDown(_historyMenu.DropDown, renderer);
        _historyMenu.DropDownOpening += (_, _) => RefreshHistoryMenu();
        menu.Items.Add(_historyMenu);

        _themeMenu = CreateItem("Тема");
        ConfigureDropDown(_themeMenu.DropDown, renderer);
        AddThemeItem(AppTheme.System, "Системная");
        AddThemeItem(AppTheme.Light, "Светлая");
        AddThemeItem(AppTheme.Dark, "Тёмная");
        menu.Items.Add(_themeMenu);
        menu.Items.Add(CreateItem("Открыть все настройки…", (_, _) => ShowSettingsWindow()));
        menu.Items.Add(CreateSeparator());
        RefreshSettingsChecks();

        _modelStatus = CreateItem(modelManager.AreAllModelsReady
            ? (VoiceRuntimeProfile.IsPortable ? "Whisper turbo · готово" : "GigaAM + Whisper · готовы")
            : (VoiceRuntimeProfile.IsPortable ? "Whisper turbo · подготовка…" : "GigaAM + Whisper · подготовка…"));
        _modelStatus.Enabled = false;
        menu.Items.Add(_modelStatus);

        _showDownload = CreateItem("Показать загрузку", (_, _) => window.ShowModelDownloadStatus());
        _showDownload.Visible = !modelManager.AreAllModelsReady;
        menu.Items.Add(_showDownload);

        _retryDownload = CreateItem("Повторить загрузку", (_, _) => window.RetryModelDownloads());
        _retryDownload.Visible = false;
        menu.Items.Add(_retryDownload);
        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateItem("Выход", (_, _) => quit()));

        _icon = LoadApplicationIcon();
        _pausedIcon = CreatePausedIcon(_icon);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Слог — локальная диктовка",
            // The control center is now the only tray surface. Keeping the legacy WinForms menu
            // attached would let Windows open it before MouseClick and recreate the illegible
            // light-theme popup reported by users.
            ContextMenuStrip = null,
            Visible = true
        };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        _menu.Opening += (_, _) =>
        {
            RefreshAudioControls();
            // Settings can also be changed in the WPF window. Refresh on every opening so the
            // checkmarks are a view of persisted truth rather than a cache of the last tray click.
            RefreshSettingsChecks();
        };
        _modelManager.ProgressChanged += OnModelProgressChanged;
        _window.ActivationBindingChanged += OnActivationBindingChanged;
        _window.AudioCaptureStateChanged += OnAudioCaptureStateChanged;
        _window.RecentRecordings.Changed += OnRecentRecordingsChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        UpdateActivationChecks();
        RefreshAudioControls();
        RefreshHistoryMenu();
    }

    /// <summary>
    /// A checkable item that writes the setting and re-applies it immediately. Nothing here needs a
    /// restart — the pipeline is rebuilt from disk on every change.
    /// </summary>
    private Forms.ToolStripMenuItem AddToggle(
        Forms.ToolStripMenuItem parent,
        string text,
        Func<DictationSettings, DictationSettings> toggle)
    {
        var item = CreateItem(text, (_, _) =>
        {
            _settingsService.Save(toggle(_settingsService.Load()));
            _window.ApplyDictationSettings();
            RefreshSettingsChecks();
        });
        item.CheckOnClick = false;
        parent.DropDownItems.Add(item);
        return item;
    }

    private void RefreshSettingsChecks()
    {
        var settings = _settingsService.Load();
        _mixedLanguageItem.Checked = settings.MixedLanguageMode;
        _numbersItem.Checked = settings.ApplyNumberNormalization;
        _voiceCommandsItem.Checked = settings.ApplyVoiceCommands;
        _restoreClipboardItem.Checked = settings.RestoreClipboard;
        _soundItem.Checked = settings.SoundFeedback;
        _notificationsItem.Checked = settings.DesktopNotifications;
        foreach (var (theme, item) in _themeItems)
        {
            item.Checked = theme == settings.Theme;
        }
    }

    private void AddThemeItem(AppTheme theme, string label)
    {
        var item = CreateItem(label, (_, _) =>
        {
            var settings = _settingsService.Load() with { Theme = theme };
            _settingsService.Save(settings);
            _window.ApplyDictationSettings();
            RefreshSettingsChecks();
        });
        item.CheckOnClick = false;
        _themeItems[theme] = item;
        _themeMenu.DropDownItems.Add(item);
    }

    /// <summary>
    /// Opens the dictionary in whatever handles .json, creating a commented template first. The
    /// alternative — a bespoke editor — is a lot of UI for a file most users will touch twice.
    /// </summary>
    private void OpenDictionary()
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
            AppLog.Write("Could not open the user dictionary", exception);
        }
    }

    private void AddActivationItem(Forms.ToolStripMenuItem parent, ActivationBinding binding)
    {
        var item = CreateItem(ActivationBindingInfo.DisplayName(binding), (_, _) => ChangeActivationBinding(binding));
        item.CheckOnClick = false;
        item.Tag = binding;
        _activationItems[binding] = item;
        parent.DropDownItems.Add(item);
    }

    private void ChangeActivationBinding(ActivationBinding binding)
    {
        if (_window.TrySetActivationBinding(binding, out var error))
        {
            UpdateActivationChecks();
            UpdateTrayTooltip(_window.CurrentAudioCaptureState);
            return;
        }

        NotifyActionable(
            TrayNotificationKind.RecoveryRequired,
            "Слог",
            error ?? "Не удалось сменить кнопку запуска.");
    }

    private void ConfigureCustomShortcut()
    {
        _window.SetActivationCaptureActive(true);
        CustomShortcutDialog dialog;
        bool accepted;
        try
        {
            dialog = new CustomShortcutDialog(_window.CurrentCustomShortcut);
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            _window.SetActivationCaptureActive(false);
        }

        if (!accepted)
        {
            return;
        }

        if (_window.TrySetCustomShortcut(dialog.SelectedShortcut, out var error))
        {
            UpdateActivationChecks();
            UpdateTrayTooltip(_window.CurrentAudioCaptureState);
            return;
        }

        NotifyActionable(
            TrayNotificationKind.RecoveryRequired,
            "Слог",
            error ?? "Не удалось назначить сочетание.");
    }

    private async void OnNotifyIconMouseClick(object? sender, Forms.MouseEventArgs args)
    {
        if (args.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
        {
            await _window.Dispatcher.InvokeAsync(ShowSettingsWindow);
        }
    }

    private async void OnBalloonTipClicked(object? sender, EventArgs args)
    {
        await _window.Dispatcher.InvokeAsync(() =>
        {
            ShowSettingsWindow();
            if (_lastNotificationKind == TrayNotificationKind.ModelFailure)
            {
                _settingsWindow?.ShowRecognitionAndActivate();
            }
            else if (_lastNotificationKind == TrayNotificationKind.MicrophoneUnavailable)
            {
                _settingsWindow?.ShowFeedbackAndActivate();
            }
        });
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            var state = _window.CurrentAudioCaptureState;
            await _window.SetMicrophonePausedAsync(!state.IsUserPaused);
            RefreshAudioControls();
        }
        catch (Exception exception)
        {
            ShowMicrophoneWarning(exception.Message);
        }
    }

    private async Task SelectMicrophoneAsync(string? deviceId)
    {
        try
        {
            await _window.SelectMicrophoneAsync(deviceId);
            RefreshAudioControls();
        }
        catch (Exception exception)
        {
            ShowMicrophoneWarning(exception.Message);
        }
    }

    private void RefreshAudioControls()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var state = _window.CurrentAudioCaptureState;
            var presentation = TrayAudioPresentation.From(state, _window.CanStartRecording, _window.IsRecording);
            _notifyIcon.Icon = state.IsUserPaused ? _pausedIcon : _icon;
            _startStopItem.Enabled = presentation.StartEnabled;
            _startStopItem.Text = presentation.StartText;
            _pauseItem.Text = presentation.PauseText;
            _pauseItem.Checked = state.IsUserPaused;
            _pauseItem.Enabled = presentation.PauseEnabled && !_window.IsCaptureOperationPending;
            _microphoneMenu.Text = $"Микрофон · {ShortDeviceName(state.DeviceName)}";
            UpdateTrayTooltip(state);
        }
        catch (Exception exception)
        {
            _startStopItem.Enabled = false;
            _pauseItem.Enabled = false;
            AppLog.Write("Tray audio controls refresh failed", exception);
        }
        finally
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (elapsed > TimeSpan.FromMilliseconds(250))
            {
                AppLog.Write($"Tray microphone refresh exceeded budget: {elapsed.TotalMilliseconds:0} ms");
            }
        }
    }

    private void RefreshMicrophoneMenu()
    {
        _microphoneMenu.DropDownItems.Clear();
        try
        {
            var state = _window.CurrentAudioCaptureState;
            var devices = _window.CaptureDevices;
            var defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
            var defaultItem = CreateItem(
                defaultDevice is null
                    ? "Системный микрофон · недоступен"
                    : $"Системный · {defaultDevice.Name}",
                async (_, _) => await SelectMicrophoneAsync(null));
            defaultItem.Checked = state.SelectedDeviceId is null;
            defaultItem.Enabled = defaultDevice is not null;
            _microphoneMenu.DropDownItems.Add(defaultItem);
            _microphoneMenu.DropDownItems.Add(CreateSeparator());

            foreach (var device in devices)
            {
                var item = CreateItem(
                    device.IsDefault ? $"{device.Name} · по умолчанию" : device.Name,
                    async (_, _) => await SelectMicrophoneAsync(device.Id));
                item.Checked = string.Equals(device.Id, state.SelectedDeviceId, StringComparison.Ordinal);
                _microphoneMenu.DropDownItems.Add(item);
            }

            if (state.SelectedDeviceId is not null
                && devices.All(device => !string.Equals(device.Id, state.SelectedDeviceId, StringComparison.Ordinal)))
            {
                var unavailable = CreateItem("Выбранный микрофон · недоступен");
                unavailable.Checked = true;
                unavailable.Enabled = false;
                _microphoneMenu.DropDownItems.Add(unavailable);
            }
        }
        catch (Exception exception)
        {
            var error = CreateItem("Не удалось получить список");
            error.Enabled = false;
            _microphoneMenu.DropDownItems.Add(error);
            AppLog.Write("Tray microphone inventory failed", exception);
        }
    }

    public void ShowSettingsWindowPublic() => ShowSettingsWindow();
    public void ShowHistoryWindowPublic() => ShowHistoryWindow();

    private void ShowSettingsWindow()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var wasWarm = _settingsWindow is not null;
        _settingsWindow ??= new SettingsWindow(_window, _settingsService, _quit);
        _settingsWindow.ShowAndActivate();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (wasWarm && elapsed > TimeSpan.FromMilliseconds(250))
        {
            AppLog.Write($"Warm settings open exceeded budget: {elapsed.TotalMilliseconds:0} ms");
        }
    }

    private void ShowHistoryWindow()
    {
        _settingsWindow ??= new SettingsWindow(_window, _settingsService, _quit);
        _settingsWindow.ShowHistoryAndActivate();
    }

    private void OnRecentRecordingsChanged(object? sender, EventArgs e)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _ = _window.Dispatcher.BeginInvoke(() => OnRecentRecordingsChanged(sender, e));
            return;
        }
        RefreshHistoryMenu();
        var message = _window.RecentRecordings.StateMessage;
        if (TrayNotificationPolicy.IsActionableHistoryMessage(message)
            && !string.Equals(message, _lastHistoryNotificationMessage, StringComparison.Ordinal))
        {
            _lastHistoryNotificationMessage = message;
            NotifyActionable(TrayNotificationKind.HistoryFailure, "Слог · история", message!);
        }
        else if (!TrayNotificationPolicy.IsActionableHistoryMessage(message))
        {
            _lastHistoryNotificationMessage = null;
        }
    }

    private void RefreshHistoryMenu()
    {
        _historyMenu.DropDownItems.Clear();
        var items = _window.RecentRecordings.GetItems();
        _historyMenu.Text = items.Count == 0 ? "Последние записи" : $"Последние записи · {items.Count}";
        if (items.Count == 0)
        {
            var empty = CreateItem("Записей пока нет");
            empty.Enabled = false;
            _historyMenu.DropDownItems.Add(empty);
        }
        else
        {
            foreach (var item in items.Take(RecentRecordingHistoryService.Capacity))
            {
                var local = item.CreatedUtc.ToLocalTime();
                var seconds = Math.Max(1, (int)Math.Round(item.Duration.TotalSeconds));
                _historyMenu.DropDownItems.Add(CreateItem(
                    $"{local:HH:mm} · {seconds / 60}:{seconds % 60:00}",
                    (_, _) => ShowHistoryWindow()));
            }
        }

        _historyMenu.DropDownItems.Add(CreateSeparator());
        _historyMenu.DropDownItems.Add(CreateItem("Открыть историю…", (_, _) => ShowHistoryWindow()));
    }

    private void OnAudioCaptureStateChanged(object? sender, AudioCaptureStateChangedEventArgs change)
    {
        RefreshAudioControls();
        if (change.Kind == AudioCaptureChangeKind.DeviceUnavailable && !string.IsNullOrWhiteSpace(change.UserMessage))
        {
            ShowMicrophoneWarning(change.UserMessage);
        }
    }

    private void ShowMicrophoneWarning(string message) => NotifyActionable(
        TrayNotificationKind.MicrophoneUnavailable,
        "Слог · микрофон",
        string.IsNullOrWhiteSpace(message) ? "Микрофон недоступен." : message);

    private void NotifyActionable(TrayNotificationKind kind, string title, string message)
    {
        var enabled = _settingsService.Load().DesktopNotifications;
        if (!TrayNotificationPolicy.ShouldNotify(kind, enabled))
        {
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastNotificationKind is { } previousKind
            && _lastNotificationMessage is { } previousMessage
            && TrayNotificationPolicy.IsDuplicate(
                kind,
                message,
                previousKind,
                previousMessage,
                System.Diagnostics.Stopwatch.GetElapsedTime(_lastNotificationTimestamp, now)))
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(4500, title, message, Forms.ToolTipIcon.Warning);
        _lastNotificationKind = kind;
        _lastNotificationMessage = message;
        _lastNotificationTimestamp = now;
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs args)
    {
        EgoistTrayPalette.Apply(args.EffectiveTheme);
        ApplyDropDownTheme(_menu);
        RefreshSettingsChecks();
    }

    private void ApplyDropDownTheme(Forms.ToolStripDropDown dropDown)
    {
        ConfigureDropDown(dropDown, _renderer);
        foreach (var item in dropDown.Items.OfType<Forms.ToolStripMenuItem>())
        {
            if (item.HasDropDownItems)
            {
                ApplyDropDownTheme(item.DropDown);
            }
        }
        dropDown.Invalidate(true);
    }

    private void UpdateTrayTooltip(AudioCaptureState state)
    {
        if (!state.IsPaused
            && state.IsAvailable
            && _lastModelProgress is { Stage: not ModelTransferStage.Ready } progress)
        {
            _notifyIcon.Text = ModelProgressFormatter.TrayTooltip(progress);
            return;
        }
        var status = state.IsUserPaused ? "пауза" : state.IsTransientlyUnavailable ? "микрофон недоступен" : state.IsAvailable ? "готов" : "нет микрофона";
        _notifyIcon.Text = TruncateTooltip($"Слог — {status} · {ShortDeviceName(state.DeviceName)}");
    }

    private static string ShortDeviceName(string value) => value.Length <= 25 ? value : value[..24] + "…";

    private void OnActivationBindingChanged(object? sender, EventArgs args) => UpdateActivationChecks();

    private void UpdateActivationChecks()
    {
        foreach (var (binding, item) in _activationItems)
        {
            item.Checked = binding == _window.CurrentActivationBinding;
        }

        _customActivationItem.Checked = _window.CurrentActivationBinding == ActivationBinding.CustomKeyboard;
        _customActivationItem.Text = _window.CurrentCustomShortcut is { IsValid: true } custom
            ? $"Своя…  ·  {custom.DisplayName}"
            : "Своя…";
    }

    private void OnModelProgressChanged(object? sender, ModelTransferProgress progress)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _ = _window.Dispatcher.BeginInvoke(() => UpdateModelProgress(progress));
            return;
        }

        UpdateModelProgress(progress);
    }

    private void UpdateModelProgress(ModelTransferProgress progress)
    {
        var wasFailed = _lastModelProgress?.Stage == ModelTransferStage.Failed;
        _lastModelProgress = progress;
        var failed = progress.Stage == ModelTransferStage.Failed;
        var ready = progress.Stage == ModelTransferStage.Ready && _modelManager.AreAllModelsReady;
        _modelStatus.Text = failed
            ? (VoiceRuntimeProfile.IsPortable ? "Whisper turbo · ошибка загрузки" : "GigaAM + Whisper · ошибка загрузки")
            : ready
                ? (VoiceRuntimeProfile.IsPortable ? "Whisper turbo · готово" : "GigaAM + Whisper · готовы")
                : ModelProgressFormatter.Detail(progress);
        _showDownload.Visible = !ready && !failed;
        _retryDownload.Visible = failed;
        UpdateTrayTooltip(_window.CurrentAudioCaptureState);
        if (failed && !wasFailed)
        {
            NotifyActionable(
                TrayNotificationKind.ModelFailure,
                "Слог · модели",
                "Не удалось подготовить модель. Откройте центр управления → Распознавание и нажмите «Повторить».");
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _modelManager.ProgressChanged -= OnModelProgressChanged;
        _window.ActivationBindingChanged -= OnActivationBindingChanged;
        _window.AudioCaptureStateChanged -= OnAudioCaptureStateChanged;
        _window.RecentRecordings.Changed -= OnRecentRecordingsChanged;
        _themeService.ThemeChanged -= OnThemeChanged;
        _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
        _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        _settingsWindow?.CloseForExit();
        _menu.Dispose();
        _notifyIcon.Dispose();
        _pausedIcon.Dispose();
        _icon.Dispose();
    }

    private static T CreateDropDown<T>(Forms.ToolStripRenderer renderer)
        where T : Forms.ToolStripDropDown, new()
    {
        var dropDown = new T();
        ConfigureDropDown(dropDown, renderer);
        return dropDown;
    }

    internal static void ConfigureDropDown(Forms.ToolStripDropDown dropDown, Forms.ToolStripRenderer renderer)
    {
        dropDown.RenderMode = Forms.ToolStripRenderMode.ManagerRenderMode;
        dropDown.Renderer = renderer;
        dropDown.BackColor = EgoistTrayPalette.Background;
        dropDown.ForeColor = EgoistTrayPalette.Primary;
        dropDown.Font = CreateMenuFont();
        dropDown.Padding = new Forms.Padding(5);
        if (dropDown is Forms.ToolStripDropDownMenu menu)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
        }
    }

    internal static Forms.ToolStripMenuItem CreateItem(string text, EventHandler? click = null)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            BackColor = EgoistTrayPalette.Background,
            ForeColor = EgoistTrayPalette.Primary,
            Padding = new Forms.Padding(5, 3, 5, 3),
            AutoToolTip = false
        };
        if (click is not null)
        {
            item.Click += click;
        }
        return item;
    }

    internal static Forms.ToolStripSeparator CreateSeparator() => new()
    {
        BackColor = EgoistTrayPalette.Background,
        ForeColor = EgoistTrayPalette.Separator,
        Margin = new Forms.Padding(0, 3, 0, 3)
    };

    private static Font CreateMenuFont()
    {
        using var variable = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        return string.Equals(variable.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase)
            ? (Font)variable.Clone()
            : new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>
    /// Picks the frame the notification area actually asks for.
    /// </summary>
    /// <remarks>
    /// <c>Icon.ExtractAssociatedIcon</c> returns a single frame — normally 32×32 — and discards the
    /// rest. EgoistVoice.ico carries seven sizes, so at 125 % or 150 % scaling the tray was
    /// requesting 20 or 24 pixels and receiving a downscaled 32, which is why the icon looked soft.
    /// Now the required size is stated and Windows selects the matching frame.
    /// </remarks>
    private static Icon LoadApplicationIcon()
    {
        var required = SystemInformation.SmallIconSize;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "EgoistVoice.ico");

        try
        {
            if (File.Exists(iconPath))
            {
                using var stream = File.OpenRead(iconPath);
                return new Icon(stream, required);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not load the multi-resolution tray icon", exception);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                // The embedded resource still carries every frame, so asking for a size works here
                // as well — unlike ExtractAssociatedIcon, which always hands back one.
                using var embedded = Icon.ExtractIcon(Environment.ProcessPath, 0, required.Width);
                if (embedded is not null)
                {
                    return (Icon)embedded.Clone();
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not extract a sized icon from the executable", exception);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static Icon CreatePausedIcon(Icon source)
    {
        using var bitmap = source.ToBitmap();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var badge = Math.Max(8, (int)Math.Round(Math.Min(bitmap.Width, bitmap.Height) * 0.56));
            var left = bitmap.Width - badge;
            var top = bitmap.Height - badge;
            using var fill = new SolidBrush(EgoistTrayPalette.Accent);
            graphics.FillEllipse(fill, left, top, badge - 1, badge - 1);
            var barWidth = Math.Max(1.3f, badge * 0.13f);
            using var pen = new Pen(Color.White, barWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var y1 = top + (badge * 0.29f);
            var y2 = top + (badge * 0.70f);
            graphics.DrawLine(pen, left + (badge * 0.38f), y1, left + (badge * 0.38f), y2);
            graphics.DrawLine(pen, left + (badge * 0.62f), y1, left + (badge * 0.62f), y2);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..62] + "…";
}

internal sealed record TrayAudioPresentation(
    bool StartEnabled,
    string StartText,
    bool PauseEnabled,
    string PauseText)
{
    internal static TrayAudioPresentation From(AudioCaptureState state, bool? canStartRecording = null,
        bool isRecording = false) => new(
        StartEnabled: isRecording || (canStartRecording ??
            (!state.IsUserPaused && (state.IsAvailable || state.IsTransientlyUnavailable))),
        StartText: isRecording ? "Остановить диктовку" : state.IsUserPaused ? "Начать диктовку · пауза" : "Начать / остановить",
        PauseEnabled: true,
        PauseText: state.IsUserPaused ? "Возобновить микрофон" : "Приостановить микрофон");
}
