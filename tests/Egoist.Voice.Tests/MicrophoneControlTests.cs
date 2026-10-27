using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class MicrophoneControlTests
{
    private static readonly MicrophoneDeviceInfo DefaultA = new("endpoint-a", "Studio A", true);
    private static readonly MicrophoneDeviceInfo DeviceB = new("endpoint-b", "Studio B", false);

    [Fact]
    public void NullSelectionTracksTheCurrentWindowsDefault()
    {
        var devices = new[] { DefaultA, DeviceB };

        Assert.True(MicrophoneSelectionPolicy.IsAvailable(null, devices));
        Assert.Equal("Системный · Studio A", MicrophoneSelectionPolicy.DisplayName(null, devices));
        Assert.Equal(
            MicrophoneTopologyAction.InventoryChanged,
            MicrophoneLifecyclePolicy.EvaluateTopologyChange(null, false, "endpoint-a", devices));
    }

    [Fact]
    public void ChangingTheWindowsDefaultRestartsOnlyDefaultSelection()
    {
        var changed = new[]
        {
            DefaultA with { IsDefault = false },
            DeviceB with { IsDefault = true }
        };

        Assert.Equal(
            MicrophoneTopologyAction.RestartOnDefault,
            MicrophoneLifecyclePolicy.EvaluateTopologyChange(null, false, "endpoint-a", changed));
        Assert.Equal(
            MicrophoneTopologyAction.InventoryChanged,
            MicrophoneLifecyclePolicy.EvaluateTopologyChange("endpoint-a", false, "endpoint-a", changed));
    }

    [Fact]
    public void RemovingAnExplicitEndpointPausesWithoutFallingBack()
    {
        var onlyDefault = new[] { DefaultA };

        Assert.False(MicrophoneSelectionPolicy.IsAvailable("endpoint-b", onlyDefault));
        Assert.Equal(
            MicrophoneTopologyAction.PauseUnavailable,
            MicrophoneLifecyclePolicy.EvaluateTopologyChange(
                "endpoint-b",
                false,
                "endpoint-b",
                onlyDefault));
    }

    [Fact]
    public void ReaddingAnExplicitEndpointDoesNotResumeAStoredPause()
    {
        var restored = new[] { DefaultA, DeviceB };

        Assert.Equal(
            MicrophoneTopologyAction.InventoryChanged,
            MicrophoneLifecyclePolicy.EvaluateTopologyChange(
                "endpoint-b",
                true,
                activeDeviceId: null,
                restored));
    }

    [Theory]
    [InlineData("  ", null)]
    [InlineData(" endpoint-b ", "endpoint-b")]
    public void DeviceIdsAreNormalizedBeforePersistence(string input, string? expected) =>
        Assert.Equal(expected, MicrophoneSelectionPolicy.NormalizeDeviceId(input));

    [Fact]
    public void TrayPresentationNamesPauseAndDisablesStart()
    {
        var state = new AudioCaptureState(
            "endpoint-b",
            "Studio B",
            IsPaused: true,
            IsMonitoring: false,
            IsAvailable: true);

        var presentation = TrayAudioPresentation.From(state);

        Assert.False(presentation.StartEnabled);
        Assert.Contains("пауза", presentation.StartText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Возобновить микрофон", presentation.PauseText);
        Assert.True(presentation.PauseEnabled);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.17, 0)]
    [InlineData(0.18, 1)]
    [InlineData(0.88, 1)]
    [InlineData(0.89, 2)]
    public void LiveLevelUsesThreeStableBands(float level, int expected) =>
        Assert.Equal((AudioLevelBand)expected, AudioLevelBandPolicy.Classify(level));

    [Fact]
    public void TailArcLivesInCanvasSoItIsNotClippedToTheDiscCell()
    {
        // Эллипс 40 больше ячейки 32: в Grid WPF обрезает его по границам ячейки (остаются углы), в Canvas нет.
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "MainWindow.xaml"));
        var arc = xaml.IndexOf("x:Name=\"TailArc\"", StringComparison.Ordinal);
        Assert.True(arc > 0);
        var canvas = xaml.LastIndexOf("<Canvas", arc, StringComparison.Ordinal);
        var canvasEnd = xaml.LastIndexOf("</Canvas>", arc, StringComparison.Ordinal);
        Assert.True(canvas > 0 && canvas > canvasEnd, "TailArc должен быть внутри Canvas");
    }

    [Fact]
    public void SettingsSurfaceHasKeyboardCloseAndNamedAudioControls()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "SettingsWindow.xaml"));

        Assert.Contains("Window_OnKeyDown", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Микрофон\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Уровень микрофона\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PART_Track\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PART_Indicator\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"История\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Сохранять три последние записи", xaml, StringComparison.Ordinal);
        Assert.Contains("Очистить всю историю записей", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"760\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"560\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TabStripPlacement=\"Left\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartStopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TranslationEngineStateText\"", xaml, StringComparison.Ordinal);

        var mainWindow = File.ReadAllText(Path.Combine(RepositoryRoot(), "MainWindow.xaml.cs"));
        Assert.Contains("MessageBoxButton.YesNo", mainWindow, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No", mainWindow, StringComparison.Ordinal);
        Assert.Contains("аудиобуфер очищен", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTrayClickOpensTheSingleControlCenterWithoutLegacyPopup()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "TrayService.cs"));

        Assert.Contains("ContextMenuStrip = null", source, StringComparison.Ordinal);
        Assert.Contains("Forms.MouseButtons.Left or Forms.MouseButtons.Right", source, StringComparison.Ordinal);
        Assert.Contains("ShowSettingsWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextMenuStrip = menu", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRecognitionModelIsActionableOnlyInTheControlCenter()
    {
        var progress = new ModelTransferProgress(
            "GigaAM v3 · ядро",
            1,
            5,
            ModelTransferStage.Failed,
            0,
            318_995_997,
            0,
            0,
            Error: "private diagnostic must not be rendered");

        var presentation = ModelProgressFormatter.ControlCenter(false, progress);

        Assert.True(presentation.IsFailure);
        Assert.True(presentation.CanRetry);
        Assert.False(presentation.ShowProgress);
        Assert.Contains("Повторите загрузку", presentation.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(progress.Error!, presentation.StatusText, StringComparison.Ordinal);

        var tray = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "TrayService.cs"));
        Assert.Contains("центр управления → Распознавание", tray, StringComparison.Ordinal);
        Assert.Contains("BalloonTipClicked", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("Откройте меню и выберите", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_notification_bursts_keep_one_worker_and_one_followup_for_midflight_changes()
    {
        var work = new Queue<Action>();
        var notifications = 0;
        DeviceNotificationDispatcher? dispatcher = null;
        dispatcher = new(() =>
        {
            notifications++;
            if (notifications == 1)
                for (var index = 0; index < 100; index++) dispatcher!.Request();
        }, scheduled => work.Enqueue(scheduled));
        using (dispatcher)
        {
            for (var index = 0; index < 100; index++) dispatcher.Request();
            Assert.Single(work);
            work.Dequeue()();
            Assert.Equal(2, notifications);
            Assert.Empty(work);
            // Idle does no additional work; a later real event queues just one new worker.
            dispatcher.Request();
            Assert.Single(work);
            work.Dequeue()();
            Assert.Equal(3, notifications);
            Assert.Empty(work);
        }
    }

    [Fact]
    public async Task Device_notifications_never_overlap_a_blocked_inventory_observer()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new CountdownEvent(2);
        var calls = 0;
        var active = 0;
        var maximum = 0;
        using var dispatcher = new DeviceNotificationDispatcher(() =>
        {
            var concurrency = Interlocked.Increment(ref active);
            if (concurrency > maximum) maximum = concurrency;
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(3));
            }
            Interlocked.Decrement(ref active);
            finished.Signal();
        });
        dispatcher.Request();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try { for (var index = 0; index < 100; index++) dispatcher.Request(); }
        finally { release.Set(); }
        await Task.Run(() => Assert.True(finished.Wait(TimeSpan.FromSeconds(3))));
        Assert.Equal(1, maximum);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Disposing_a_notification_dispatcher_suppresses_queued_and_later_callbacks()
    {
        var work = new Queue<Action>();
        var calls = 0;
        var dispatcher = new DeviceNotificationDispatcher(() => calls++, scheduled => work.Enqueue(scheduled));
        dispatcher.Request();
        dispatcher.Dispose();
        work.Dequeue()();
        dispatcher.Request();
        Assert.Equal(0, calls);
        Assert.Empty(work);
    }

    [Fact]
    public void A_throwing_inventory_observer_does_not_poison_later_notification_dispatch()
    {
        var work = new Queue<Action>();
        var calls = 0;
        using var dispatcher = new DeviceNotificationDispatcher(() =>
        {
            if (++calls == 1) throw new InvalidOperationException("Synthetic observer failure");
        }, scheduled => work.Enqueue(scheduled));
        dispatcher.Request();
        work.Dequeue()();
        dispatcher.Request();
        work.Dequeue()();
        Assert.Equal(2, calls);
        Assert.Empty(work);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_read_disposes_the_exact_lazy_wrappers_once_even_if_reading_fails(bool throwOnSecond)
    {
        var created = new List<SyntheticEndpoint>();
        var enumerations = 0;
        IEnumerable<SyntheticEndpoint> Enumerate()
        {
            enumerations++;
            for (var index = 0; index < 3; index++)
            {
                var device = new SyntheticEndpoint(index);
                created.Add(device);
                yield return device;
            }
        }
        MicrophoneDeviceInfo Read(SyntheticEndpoint endpoint)
        {
            if (throwOnSecond && endpoint.Index == 1) throw new InvalidOperationException("Synthetic metadata failure");
            return new(endpoint.Index.ToString(), "Synthetic "+endpoint.Index, endpoint.Index == 2);
        }
        if (throwOnSecond)
            Assert.Throws<InvalidOperationException>(() => MicrophoneInventoryReader.Read(Enumerate(), Read));
        else
        {
            var result = MicrophoneInventoryReader.Read(Enumerate(), Read);
            Assert.Equal("2", result[0].Id);
            Assert.Equal(3, result.Count);
        }
        Assert.Equal(1, enumerations);
        Assert.Equal(throwOnSecond ? 2 : 3, created.Count);
        Assert.All(created, endpoint => Assert.Equal(1, endpoint.DisposeCount));
    }

    private sealed class SyntheticEndpoint(int index) : IDisposable
    {
        internal int Index { get; } = index;
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Egoist.Voice.csproj")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
