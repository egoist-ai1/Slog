using System.IO;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class ThemeFeedbackNotificationTests
{
    [Theory]
    [InlineData(AppTheme.System, true, false, EffectiveAppTheme.Light)]
    [InlineData(AppTheme.System, false, false, EffectiveAppTheme.Dark)]
    [InlineData(AppTheme.Light, false, false, EffectiveAppTheme.Light)]
    [InlineData(AppTheme.Dark, true, false, EffectiveAppTheme.Dark)]
    [InlineData(AppTheme.Dark, false, true, EffectiveAppTheme.HighContrast)]
    public void Theme_resolution_respects_explicit_system_and_high_contrast(
        AppTheme preference,
        bool systemLight,
        bool highContrast,
        EffectiveAppTheme expected) =>
        Assert.Equal(expected, AppThemeService.Resolve(preference, systemLight, highContrast));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Notification_policy_allows_only_actionable_events(
        int kind,
        bool expected) =>
        Assert.Equal(expected, TrayNotificationPolicy.ShouldNotify((TrayNotificationKind)kind, enabled: true));

    [Fact]
    public void Activation_fallback_notice_names_requested_binding_and_reason()
    {
        var notice = ActivationBindingInfo.FallbackNotice("Mouse 5 + Ctrl + Alt + Space", "сочетание занято другим приложением.");

        Assert.Contains("Mouse 5 + Ctrl + Alt + Space", notice);
        Assert.Contains("сочетание занято другим приложением", notice);
        Assert.DoesNotContain("..", notice);
        Assert.False(string.IsNullOrWhiteSpace(ActivationBindingInfo.FallbackNotice("X", null)));
    }

    [Fact]
    public void Recognition_breakdown_line_has_all_fields_and_no_text()
    {
        var line = RecognitionDiagnostics.Format(new RecognitionBreakdown(
            2.5, 1, 3, 0, 12, 90, 80, 10, 120, 35, 40, 512, 4));

        foreach (var field in new[]
        {
            "audio=", "chunks=", "queue=", "warm=", "plan=", "decode=", "primary=", "formatterExtra=",
            "cpu=", "cpuRatio=", "sysBusy=", "faults=", "ws=", "gcPause="
        })
            Assert.Contains(field, line);
        Assert.Contains("audio=2.50s", line);
        Assert.Contains("cpuRatio=1.33", line);
        Assert.DoesNotContain("секретный текст", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Disabling_notifications_suppresses_every_kind()
    {
        foreach (var kind in Enum.GetValues<TrayNotificationKind>())
        {
            Assert.False(TrayNotificationPolicy.ShouldNotify(kind, enabled: false));
        }
    }

    [Fact]
    public void Duplicate_actionable_notifications_are_cooled_down_but_user_recovery_is_not()
    {
        Assert.True(TrayNotificationPolicy.IsDuplicate(
            TrayNotificationKind.MicrophoneUnavailable,
            "Микрофон недоступен",
            TrayNotificationKind.MicrophoneUnavailable,
            "Микрофон недоступен",
            TimeSpan.FromSeconds(5)));
        Assert.False(TrayNotificationPolicy.IsDuplicate(
            TrayNotificationKind.MicrophoneUnavailable,
            "Микрофон недоступен",
            TrayNotificationKind.MicrophoneUnavailable,
            "Микрофон недоступен",
            TimeSpan.FromSeconds(31)));
        Assert.False(TrayNotificationPolicy.IsDuplicate(
            TrayNotificationKind.RecoveryRequired,
            "Проверьте сочетание",
            TrayNotificationKind.RecoveryRequired,
            "Проверьте сочетание",
            TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Не удалось сохранить запись.", true)]
    [InlineData("Недостаточно места для локальной истории.", true)]
    [InlineData("Кодек AAC недоступен.", true)]
    [InlineData("История очищена.", false)]
    [InlineData(null, false)]
    public void History_notification_classifier_rejects_normal_status(string? message, bool expected) =>
        Assert.Equal(expected, TrayNotificationPolicy.IsActionableHistoryMessage(message));

    [Theory]
    [InlineData(FeedbackSound.RecordingStarted)]
    [InlineData(FeedbackSound.RecordingStopped)]
    [InlineData(FeedbackSound.TextInserted)]
    [InlineData(FeedbackSound.Error)]
    public void Every_cue_has_a_bounded_capture_exclusion_tail(FeedbackSound sound)
    {
        var wave = FeedbackSoundService.Synthesize(sound, 0.4);
        var sampleCount = (wave.Length - 44) / 2;
        var cueDuration = TimeSpan.FromSeconds(sampleCount / 44_100d);
        var exclusion = FeedbackSoundService.CaptureExclusionWindow(sound);

        Assert.InRange(exclusion - cueDuration, TimeSpan.FromMilliseconds(119), TimeSpan.FromMilliseconds(121));
        Assert.InRange(exclusion, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(240));
    }

    [Fact]
    public void Settings_use_semantic_theme_tokens_and_expose_accessible_controls()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "SettingsWindow.xaml"));

        Assert.Contains("Header=\"Внешний вид\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Тема приложения\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Прослушать звуковой сигнал\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Показывать важные уведомления\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AppBackgroundBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#08080A\"", xaml, StringComparison.Ordinal);

        foreach (var theme in new[] { "Dark.xaml", "Light.xaml", "HighContrast.xaml" })
        {
            var resources = File.ReadAllText(Path.Combine(root, "Themes", theme));
            foreach (var token in new[]
                     {
                         "AppBackgroundBrush", "AppSurfaceBrush", "AppTextPrimaryBrush",
                         "AppTextMutedBrush", "AppAccentBrush", "AppWarningBrush"
                     })
            {
                Assert.Contains($"x:Key=\"{token}\"", resources, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Shortcut_dialog_is_theme_driven_and_keyboard_focus_is_visible_and_named()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "CustomShortcutDialog.xaml"));

        Assert.Contains("Style x:Key=\"CaptureSurfaceStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocused\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Поле записи сочетания клавиш\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Закрыть диалог\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AppBackgroundBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AppAccentTextBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_change_updates_preference_checks_and_rebuilds_high_contrast_palette()
    {
        var root = RepositoryRoot();
        var themeSource = File.ReadAllText(Path.Combine(root, "Services", "AppThemeService.cs"));
        var traySource = File.ReadAllText(Path.Combine(root, "Services", "TrayService.cs"));
        var capsuleSource = File.ReadAllText(Path.Combine(root, "MainWindow.Visuals.cs"));

        Assert.Contains("preferenceChanged", themeSource, StringComparison.Ordinal);
        Assert.Contains("RefreshFromSystem(paletteMayHaveChanged);", themeSource, StringComparison.Ordinal);
        Assert.Contains("() => RefreshFromSystem(forceResourceRefresh)", themeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvoke(() => Apply(Preference", themeSource, StringComparison.Ordinal);
        Assert.Contains("RefreshSettingsChecks();", traySource[traySource.IndexOf("private void OnThemeChanged", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("ApplyReducedMotionImmediately();", capsuleSource, StringComparison.Ordinal);
        Assert.Contains("Waveform.HighContrast = args.EffectiveTheme == EffectiveAppTheme.HighContrast", capsuleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Feedback_scheduler_opens_the_capture_fence_immediately_before_playback()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "FeedbackSoundService.cs"));
        var start = source.IndexOf("private void PlayCore", StringComparison.Ordinal);
        var end = source.IndexOf("internal static byte[] Synthesize", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.True(
            method.IndexOf("_captureIsolation?.Invoke", StringComparison.Ordinal) <
            method.IndexOf("_player.Play", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Egoist.Voice.csproj")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
