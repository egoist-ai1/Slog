using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

[CollectionDefinition("Actual capture window", DisableParallelization = true)]
public sealed class CaptureWindowCollection : ICollectionFixture<CaptureWindowDispatcher> { }

[Collection("Actual capture window")]
public sealed class MainWindowCaptureOperationTests(CaptureWindowDispatcher dispatcher)
{
#if CAPTURE_BLOCKING_BASELINE
    [Fact]
    public Task Baseline_actual_start_and_device_selection_block_dispatcher() => dispatcher.RunAsync(async () =>
    {
        foreach(var operation in new[]{"start","select"})
        {
            await using var fixture = new WindowFixture();
            var gate = fixture.Capture.Block(operation);
            var ui = Dispatcher.CurrentDispatcher;
            var progressed = Task.Run(async () =>
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var marker = ui.InvokeAsync(() => true, DispatcherPriority.Send).Task;
                bool result; try { result = await marker.WaitAsync(TimeSpan.FromMilliseconds(150)); }
                catch(TimeoutException) { result = false; }
                finally { gate.Release.Set(); }
                return result;
            });
            if(operation=="start") await fixture.Window.ToggleRecordingAsync();
            else await fixture.Window.SelectMicrophoneAsync("other");
            Assert.False(await progressed);
            if(operation=="start") await fixture.CancelAsync();
        }
    });
#else
    [Theory]
    [InlineData("start")]
    [InlineData("select")]
    [InlineData("pause")]
    [InlineData("resume")]
    public Task Native_operation_does_not_block_actual_window_dispatcher(string operation) => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var gate = f.Capture.Block(operation);
        var pending = operation switch
        {
            "start" => f.Window.ToggleRecordingAsync(),
            "select" => f.Window.SelectMicrophoneAsync("other"),
            "pause" => f.Window.SetMicrophonePausedAsync(true),
            _ => f.Window.SetMicrophonePausedAsync(false)
        };
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.IsCompleted);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true, DispatcherPriority.Send).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.True(f.Window.IsCaptureOperationPending);
            Assert.NotEqual(Environment.CurrentManagedThreadId, gate.ThreadId);
        }
        finally { gate.Release.Set(); }
        await pending;
        if(operation=="start") await f.CancelAsync();
        Assert.Equal(1, f.Capture.MaximumConcurrentOperations);
    });

    [Fact]
    public Task Hotkey_release_while_native_start_pending_stops_exactly_once() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var gate = f.Capture.Block("start"); f.Press(PushToTalkSource.Keyboard);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Press(PushToTalkSource.Keyboard); f.Press(PushToTalkSource.Keyboard);
            var release = f.ReleaseAsync(PushToTalkSource.Keyboard);
            Assert.False(release.IsCompleted); Assert.Equal(0, f.Capture.StopCount);
            gate.Release.Set(); await release;
        }
        finally { gate.Release.Set(); }
        Assert.Equal(1, f.Capture.StartCount); Assert.Equal(1, f.Capture.StopCount);
        Assert.False(f.Capture.Active); Assert.False(f.Window.IsRecording);
        Assert.True(f.Capture.Calls.IndexOf("start") < f.Capture.Calls.IndexOf("stop"));
    });

    [Fact]
    public Task Pending_start_cancel_clears_capture_without_transcription_or_second_start() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var gate=f.Capture.Block("start");var start=f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancel=f.CancelAsync(); var duplicate=f.CancelAsync();
            Assert.False(cancel.IsCompleted); Assert.False(f.Window.CanStartRecording);
            await f.Window.ToggleRecordingAsync();
            gate.Release.Set(); await Task.WhenAll(start,cancel,duplicate);
        }
        finally {gate.Release.Set();}
        Assert.Equal(1,f.Capture.StartCount);Assert.Equal(0,f.Capture.StopCount);
        Assert.Equal(1,f.Capture.CancelCount);Assert.False(f.Capture.Active);
        Assert.Equal(0,f.Transcription.TranscribeCount);Assert.False(f.Window.IsRecording);
    });

    [Fact]
    public Task Device_change_after_pending_start_cancels_before_selection() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();var gate=f.Capture.Block("start");var start=f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var select=f.Window.SelectMicrophoneAsync("other");
            Assert.Equal(0,f.Capture.SelectCount);gate.Release.Set();await start;Assert.True(await select);
        }
        finally{gate.Release.Set();}
        Assert.True(f.Capture.Calls.IndexOf("cancel")<f.Capture.Calls.IndexOf("select"));
        Assert.False(f.Capture.Active);Assert.Equal("other",f.Window.CurrentAudioCaptureState.SelectedDeviceId);
    });

    [Fact]
    public Task Manual_pause_survives_pending_start_and_does_not_retry_from_hotkey() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();var gate=f.Capture.Block("start");var start=f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var pause=f.Window.SetMicrophonePausedAsync(true);
            gate.Release.Set();await Task.WhenAll(start,pause);
        }
        finally{gate.Release.Set();}
        f.Press(PushToTalkSource.Mouse);await f.ReleaseAsync(PushToTalkSource.Mouse);
        Assert.True(f.Window.CurrentAudioCaptureState.IsUserPaused);Assert.True(f.Settings.Load().IsCapturePaused);
        Assert.Equal(1,f.Capture.StartCount);Assert.Equal(1,f.Capture.PauseCount);
    });

    [Fact]
    public Task Failed_start_resets_intent_and_later_transient_retry_succeeds() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();f.Capture.FailNextStart=true;
        await f.Window.ToggleRecordingAsync();Assert.False(f.Window.IsRecording);Assert.True(f.Window.CanStartRecording);
        Assert.True(f.Window.CurrentAudioCaptureState.IsTransientlyUnavailable);Assert.False(f.Settings.Load().IsCapturePaused);
        await f.Window.ToggleRecordingAsync();Assert.True(f.Window.IsRecording);await f.CancelAsync();Assert.Equal(2,f.Capture.StartCount);
    });

    [Fact]
    public Task Invisible_window_and_repeated_keyboard_mouse_intent_use_one_start() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();Assert.False(f.Window.IsVisible);
        var gate=f.Capture.Block("start");f.Press(PushToTalkSource.Mouse);f.Press(PushToTalkSource.Keyboard);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await f.ReleaseAsync(PushToTalkSource.Mouse);
            Assert.Equal(0,f.Capture.StopCount);gate.Release.Set();await f.ReleaseAsync(PushToTalkSource.Keyboard);
        }
        finally{gate.Release.Set();}
        Assert.Equal(1,f.Capture.StartCount);Assert.Equal(1,f.Capture.StopCount);
    });

    [Fact]
    public Task Foreground_target_is_captured_at_intent_before_native_open_completes() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();f.Foreground=1234;var gate=f.Capture.Block("start");var start=f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Foreground=5678;gate.Release.Set();await start;
            Assert.Equal((nint)1234,f.Window.CurrentTargetWindow);
        }
        finally{gate.Release.Set();}
        Assert.Equal(1,f.ForegroundReads);await f.CancelAsync();
    });

    [Fact]
    public Task Device_inventory_and_state_properties_are_memory_snapshots_on_dispatcher() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();var stateReads=f.Capture.StateReads;var deviceReads=f.Capture.DeviceReads;
        for(var i=0;i<50;i++){_ = f.Window.CurrentAudioCaptureState;_ = f.Window.CaptureDevices;}
        Assert.Equal(stateReads,f.Capture.StateReads);Assert.Equal(deviceReads,f.Capture.DeviceReads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId,f.Capture.NativeThreads);
    });

    [Fact]
    public Task Shutdown_waits_for_pending_start_and_native_disposal_without_blocking_dispatcher() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();var gate=f.Capture.Block("start");var start=f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var shutdown=f.Window.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);Assert.Equal(0,f.Capture.DisposeCount);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            gate.Release.Set();await Task.WhenAll(start,shutdown);Assert.Equal(1,f.Capture.DisposeCount);Assert.False(f.Capture.Active);
        }
        finally{gate.Release.Set();}
        Assert.False(f.Window.CanStartRecording);Assert.Equal(1,f.Transcription.DisposeCount);
    });

    [Fact]
    public Task Repeated_shutdown_drains_exactly_one_owned_disposal() => dispatcher.RunAsync(async () =>
    {
        await using var f=new WindowFixture();await f.ReadyAsync();var first=f.Window.ShutdownAsync();var second=f.Window.ShutdownAsync();
        Assert.Same(first,second);await Task.WhenAll(first,second);Assert.Equal(1,f.Capture.DisposeCount);
    });

    [Fact]
    public Task Delayed_constructor_factory_runs_off_actual_dispatcher() => dispatcher.RunAsync(async () =>
    {
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var uiThread=Environment.CurrentManagedThreadId;var nativeThread=0;
        var construction=App.CreateCaptureForStartupAsync(()=>{nativeThread=Environment.CurrentManagedThreadId;entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();return new ControlledCapture();});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(construction.IsCompleted);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(()=>true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally{release.Set();}
        var capture=await construction;Assert.NotEqual(uiThread,nativeThread);await Task.Run(capture.Dispose);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Theme_notification_during_construction_is_safe_with_reduced_motion(bool reducedMotion) => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(initialReducedMotion: reducedMotion);
        await f.ReadyAsync();
        Assert.Equal(reducedMotion, f.Window.ReducedMotion);
        Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
        await f.Window.ToggleRecordingAsync();
        Assert.True(f.Capture.Active);
        await f.CancelAsync();
    });

    [Fact]
    public Task Start_cue_plays_exactly_once_after_successful_capture_start() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        f.Settings.Save(f.Settings.Load() with { SoundFeedback = true });
        f.Window.ApplyDictationSettings();
        await f.Window.ToggleRecordingAsync();
        Assert.True(f.Capture.Active);
        // Быстрый старт: пользователь уже говорит, сигнал в запись не попадёт только если его нет.
        Assert.Equal(0, f.Capture.SuppressCount);
        await f.CancelAsync();
    });

    [Fact]
    public Task Start_cue_plays_once_after_slow_start_that_showed_connecting() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        f.Settings.Save(f.Settings.Load() with { SoundFeedback = true });
        f.Window.ApplyDictationSettings();
        var gate = f.Capture.Block("start");
        var start = f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (f.Window.CurrentVisualLabel != "Подключаю")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Подпись «Подключаю» не появилась.");
                await Task.Delay(10);
            }
            Assert.Equal(0, f.Capture.SuppressCount); // пока захват не открыт, сигнала нет
        }
        finally { gate.Release.Set(); }
        await start;
        // Пользователь ждал и молчал: сигнал играет один раз и подавляется весь его CaptureExclusionWindow.
        Assert.Equal(1, f.Capture.SuppressCount);
        await f.CancelAsync();
    });

    [Fact]
    public Task Intent_and_release_during_delayed_initial_inventory_remain_ordered() => dispatcher.RunAsync(async () =>
    {
        ControlledCapture.Gate? gate = null;
        await using var f = new WindowFixture(c => gate = c.Block("state"));
        Assert.Equal("\u041c\u0438\u043a\u0440\u043e\u0444\u043e\u043d", f.Window.CurrentAudioCaptureState.DeviceName);
        try
        {
            await gate!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Press(PushToTalkSource.Keyboard);
            var released = f.ReleaseAsync(PushToTalkSource.Keyboard);
            Assert.False(released.IsCompleted);
            Assert.Equal(0, f.Capture.StartCount);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            gate.Release.Set();
            await released;
        }
        finally { gate!.Release.Set(); }
        Assert.Equal(1, f.Capture.StartCount);
        Assert.Equal(1, f.Capture.StopCount);
    });

    [Fact]
    public Task Cancel_during_delayed_stop_drains_one_cancel_and_never_transcribes() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        await f.Window.ToggleRecordingAsync();
        var gate = f.Capture.Block("stop"); var stop = f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancel = f.CancelAsync();
            Assert.False(cancel.IsCompleted);
            gate.Release.Set(); await Task.WhenAll(stop, cancel);
        }
        finally { gate.Release.Set(); }
        Assert.Equal(1, f.Capture.StopCount); Assert.Equal(1, f.Capture.CancelCount);
        Assert.Equal(0, f.Transcription.TranscribeCount); Assert.False(f.Capture.Active);
    });

    [Fact]
    public Task Native_state_event_cannot_unpause_new_intent_during_manual_pause() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var gate = f.Capture.Block("pause"); var pause = f.Window.SetMicrophonePausedAsync(true);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Capture.EmitState(); f.Press(PushToTalkSource.Keyboard);
            Assert.Equal(0, f.Capture.StartCount);
            gate.Release.Set(); await pause; await f.ReleaseAsync(PushToTalkSource.Keyboard);
        }
        finally { gate.Release.Set(); }
        Assert.True(f.Window.CurrentAudioCaptureState.IsUserPaused);
        Assert.Equal(0, f.Capture.StartCount);
    });

    [Fact]
    public Task Stored_manual_pause_survives_window_initialization_without_resume() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(c => c.Paused = true, paused: true); await f.ReadyAsync();
        f.Press(PushToTalkSource.Keyboard); await f.ReleaseAsync(PushToTalkSource.Keyboard);
        Assert.True(f.Window.CurrentAudioCaptureState.IsUserPaused);
        Assert.False(f.Window.CanStartRecording);
        Assert.DoesNotContain("resume", f.Capture.Calls);
        Assert.Equal(0, f.Capture.StartCount);
    });

    [Fact]
    public Task Startup_shutdown_drains_delayed_constructor_then_disposes_off_dispatcher() => dispatcher.RunAsync(async () =>
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new ControlledCapture(); var uiThread = Environment.CurrentManagedThreadId;
        var constructing = App.CreateCaptureForStartupAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return capture;
        });
        var shutdown = App.DisposeCaptureForStartupAsync(constructing);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(shutdown.IsCompleted); Assert.Equal(0, capture.DisposeCount);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally { release.Set(); }
        await shutdown;
        Assert.Equal(1, capture.DisposeCount); Assert.DoesNotContain(uiThread, capture.NativeThreads);
    });

    [Fact]
    public Task Shutdown_cancels_and_drains_model_warmup_before_disposal() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync(); f.Transcription.DelayWarmup = true;
        f.Window.BeginWarmUp(showProgress: false);
        await f.Transcription.WarmupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Window.ShutdownAsync();
        Assert.True(f.Transcription.WarmupTerminated);
        Assert.False(f.Transcription.DisposedDuringWarmup);
        Assert.Equal(1, f.Transcription.DisposeCount);
    });

    [Fact]
    public Task Shutdown_drains_delayed_translation_readiness_without_blocking_dispatcher() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var readiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_translationWarmupTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(f.Window, readiness.Task);
        var shutdown = f.Window.ShutdownAsync();
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, f.Capture.DisposeCount);
            Assert.True(await Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally { readiness.TrySetResult(); }
        await shutdown;
        Assert.Equal(1, f.Capture.DisposeCount);
    });

    [Fact]
    public async Task Queue_is_bounded_and_a_failure_does_not_poison_next_operation()
    {
        var capture=new ControlledCapture();var queue=new CaptureOperationQueue(capture);var gate=capture.Block("resume");
        var first=queue.RunAsync(capture.ResumeMonitoring);await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending=Enumerable.Range(0,CaptureOperationQueue.MaximumPendingOperations-1).Select(_=>queue.RunAsync(()=>{})).ToArray();
        try{Assert.Throws<InvalidOperationException>(()=>{ _ = queue.RunAsync(()=>{}); });}finally{gate.Release.Set();}
        await Task.WhenAll(pending.Append(first));await Assert.ThrowsAsync<InvalidOperationException>(()=>queue.RunAsync(()=>throw new InvalidOperationException("fake")));
        var recovered=false;await queue.RunAsync(()=>recovered=true);Assert.True(recovered);await queue.ShutdownAsync();Assert.Equal(1,capture.DisposeCount);
    }
#endif
}

public sealed class CaptureWindowDispatcher:IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    public CaptureWindowDispatcher()
    {
        var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread=new Thread(()=>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                var app = new CaptureTestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Egoist.Voice;component/Themes/Dark.xaml", UriKind.Absolute)
                });
                app.Resources["UiFont"] = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
                app.Resources["AppPopupAnimation"] = System.Windows.Controls.Primitives.PopupAnimation.Fade;
                ready.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch(Exception e){ready.TrySetException(e);}
        }){IsBackground=true};_thread.SetApartmentState(ApartmentState.STA);_thread.Start();_dispatcher=ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    public Task RunAsync(Func<Task> action)=>_dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(15));
    public void Dispose(){_dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);if(!_thread.Join(TimeSpan.FromSeconds(5)))throw new TimeoutException("Own WPF dispatcher did not terminate");}
}

// Test Application never starts Voice, registers hotkeys, opens a device or loads models.
internal sealed class CaptureTestApplication : Application
{
    protected override void OnStartup(StartupEventArgs e) { }
}

internal sealed class WindowFixture:IAsyncDisposable
{
    private readonly string? _previousDataRoot;
    private readonly string _root;
    private readonly AppThemeService _theme;
    internal ControlledCapture Capture {get;}=new();
    internal FakeWindowTranscription Transcription {get;}=new();
    internal DictationSettingsService Settings {get;}
    internal MainWindow Window {get;}
    internal nint Foreground {get;set;}=1234;
    internal int ForegroundReads {get;private set;}
    internal WindowFixture(Action<ControlledCapture>? configure = null, bool paused = false, bool? initialReducedMotion = null, DictationDeliveryService? delivery = null)
    {
        configure?.Invoke(Capture);
        _root=Path.Combine(Environment.GetEnvironmentVariable("EGOIST_VOICE_TEST_ROOT")??Path.GetTempPath(),"voice-window-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_root);
        _previousDataRoot=Environment.GetEnvironmentVariable("EGOIST_VOICE_DATA_ROOT");Environment.SetEnvironmentVariable("EGOIST_VOICE_DATA_ROOT",_root);
        Settings=new(_root);Settings.Save(DictationSettings.Default with{SaveRecentRecordings=false, SoundFeedback=false, Theme=AppTheme.Dark, IsCapturePaused=paused});
        _theme=new AppThemeService();
        // testhost owns Application.ResourceAssembly; use the exact preinstalled Voice palette
        // and prime its existing-state bookkeeping. Theme loading is outside this capture test.
        typeof(AppThemeService).GetField("_activeDictionary", BindingFlags.Instance|BindingFlags.NonPublic)!
            .SetValue(_theme, Application.Current.Resources.MergedDictionaries[0]);
        typeof(AppThemeService).GetProperty(nameof(AppThemeService.EffectiveTheme))!
            .SetValue(_theme, SystemParameters.HighContrast ? EffectiveAppTheme.HighContrast : EffectiveAppTheme.Dark);
        typeof(AppThemeService).GetProperty(nameof(AppThemeService.ReducedMotion))!
            .SetValue(_theme, !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast);
        if (initialReducedMotion is { } reducedMotion)
        {
            // Simulate the initial Windows presentation notification inside this isolated test
            // process. No registry or OS animation preference is changed.
            _theme.ThemeChanged += (_, args) =>
            {
                typeof(AppThemeChangedEventArgs).GetProperty(nameof(AppThemeChangedEventArgs.ReducedMotion))!
                    .SetValue(args, reducedMotion);
                typeof(AppThemeService).GetProperty(nameof(AppThemeService.ReducedMotion))!
                    .SetValue(_theme, reducedMotion);
            };
        }
        Window=new(Capture,Transcription,delivery??new(new ClipboardService(),new TextInsertionService()),new FakeWindowModels(),Settings,new RecentRecordingHistoryService(Path.Combine(_root,"history")),_theme,
            new MainWindowInteractionHooks(()=>{ForegroundReads++;return Foreground;},()=>{},()=>{},()=>true));
        Window.ShowActivated=false;Window.Left=-20000;Window.Top=-20000;
    }
    internal async Task ReadyAsync()
    {
        var method=typeof(MainWindow).GetMethod("RefreshCaptureDevicesAsync");if(method is not null)await (Task)method.Invoke(Window,null)!;
    }
    internal void Press(PushToTalkSource source)=>typeof(MainWindow).GetMethod("BeginPushToTalk",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,[source,System.Diagnostics.Stopwatch.GetTimestamp()]);
    internal Task ReleaseAsync(PushToTalkSource source)=>(Task)typeof(MainWindow).GetMethod("EndPushToTalkAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,[source,System.Diagnostics.Stopwatch.GetTimestamp()])!;
    internal Task CancelAsync()=>(Task)typeof(MainWindow).GetMethod("CancelDictationAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,null)!;
    public async ValueTask DisposeAsync()
    {
        try{var method=typeof(MainWindow).GetMethod("ShutdownAsync");if(method is not null)await (Task)method.Invoke(Window,null)!;else Window.Dispose();Window.Hide();Window.Close();_theme.Dispose();}
        finally{Environment.SetEnvironmentVariable("EGOIST_VOICE_DATA_ROOT",_previousDataRoot);}
    }
}

internal sealed class ControlledCapture:IAudioCaptureService
{
    internal sealed class Gate
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release {get;}=new();
        internal int ThreadId;
    }
    private readonly ConcurrentDictionary<string,Gate> _gates=new();
    private readonly object _stateGate=new();
    private int _concurrent;
    internal List<string> Calls {get;}=[];
    internal ConcurrentBag<int> NativeThreads {get;}=[];
    internal int MaximumConcurrentOperations {get;private set;}
    internal int StartCount,StopCount,CancelCount,PauseCount,SelectCount,DisposeCount,StateReads,DeviceReads,SuppressCount;
    internal bool Active,Paused,Transient,FailNextStart;
    internal bool ReturnSpeech;
    internal long LastPressTimestamp, LastReleaseTimestamp;
    private string? _selected;
    public event EventHandler<float>? LevelChanged { add { } remove { } }
    public event EventHandler<VoiceTimbreLevel>? TimbreChanged { add { } remove { } }
    public event EventHandler<AudioCaptureStateChangedEventArgs>? StateChanged;
    public event EventHandler<float[]>? SamplesAvailable { add { } remove { } }
    internal Gate Block(string operation){var gate=new Gate();Assert.True(_gates.TryAdd(operation,gate));return gate;}
    private void Native(string operation)
    {
        var concurrent=Interlocked.Increment(ref _concurrent);lock(_stateGate){MaximumConcurrentOperations=Math.Max(MaximumConcurrentOperations,concurrent);Calls.Add(operation);}NativeThreads.Add(Environment.CurrentManagedThreadId);
        try{if(_gates.TryRemove(operation,out var gate)){gate.ThreadId=Environment.CurrentManagedThreadId;gate.Entered.TrySetResult();if(!gate.Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Fake device deadline");}}
        finally{Interlocked.Decrement(ref _concurrent);}
    }
    public AudioCaptureState GetState(){Interlocked.Increment(ref StateReads);Native("state");return new(_selected,"Fake",Paused,!Paused,true){IsTransientlyUnavailable=Transient};}
    public IReadOnlyList<MicrophoneDeviceInfo> GetCaptureDevices(){Interlocked.Increment(ref DeviceReads);Native("devices");return[new("test","Fake",true),new("other","Other",false)];}
    internal void EmitState(bool activeTakeCancelled = false) => StateChanged?.Invoke(this,
        new AudioCaptureStateChangedEventArgs(GetState(), AudioCaptureChangeKind.InventoryChanged, activeTakeCancelled, null));
    internal int TailCapCount;
    internal long LastTailCap;
    public void CapTail(long pressTimestamp){Interlocked.Increment(ref TailCapCount);LastTailCap=pressTimestamp;}
    public void Start(long pressTimestamp){LastPressTimestamp=pressTimestamp;Start();}
    public Task<AudioCaptureResult> StopAsync(long releaseTimestamp,CancellationToken token){LastReleaseTimestamp=releaseTimestamp;return StopAsync(token);}
    public void Start(){Interlocked.Increment(ref StartCount);Native("start");if(FailNextStart){FailNextStart=false;Paused=true;Transient=true;throw new MicrophoneUnavailableException("Fake unavailable");}Active=true;Paused=false;Transient=false;}
    public void SelectCaptureDevice(string? id){Interlocked.Increment(ref SelectCount);Native("select");_selected=id;Active=false;}
    public void PauseMonitoring(){Interlocked.Increment(ref PauseCount);Native("pause");Paused=true;Transient=false;Active=false;}
    public void ResumeMonitoring(){Native("resume");Paused=false;Transient=false;}
    public void SuppressFeedbackAudio(TimeSpan d)=>Interlocked.Increment(ref SuppressCount);
    public Task<AudioCaptureResult> StopAsync(CancellationToken token){Interlocked.Increment(ref StopCount);Native("stop");token.ThrowIfCancellationRequested();Active=false;return Task.FromResult(ReturnSpeech?new AudioCaptureResult(null,[0.1f],16000,true,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1),-20):new AudioCaptureResult(null,[],16000,false,TimeSpan.Zero,TimeSpan.Zero,-96));}
    public Task<string?> CancelAsync(){Interlocked.Increment(ref CancelCount);Native("cancel");Active=false;return Task.FromResult<string?>(null);}
    public void Dispose(){Interlocked.Increment(ref DisposeCount);Native("dispose");Active=false;}
}
internal sealed class FakeWindowTranscription:ITranscriptionService,ISampleTranscriptionService
{
    internal Func<int,CancellationToken,Task<string>>? SampleHandler;
    internal int SampleCalls;
    public async Task<TranscriptionResult> TranscribeSamplesAsync(float[] samples,int rate,CancellationToken token)
    {
        var call=Interlocked.Increment(ref SampleCalls);
        if(SampleHandler is null)throw new InvalidOperationException("No real audio decode permitted");
        return new TranscriptionResult(await SampleHandler(call,token),TimeSpan.Zero);
    }
    internal int TranscribeCount,DisposeCount;
    internal bool DelayWarmup, WarmupTerminated, DisposedDuringWarmup;
    internal TaskCompletionSource WarmupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task WarmUpAsync(IProgress<ModelProgress>? p,CancellationToken token)
    {
        if (!DelayWarmup) return;
        WarmupEntered.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        finally { WarmupTerminated = true; }
    }
    public Task<TranscriptionResult> TranscribeAsync(string path,IProgress<ModelProgress>? p,CancellationToken token){TranscribeCount++;throw new InvalidOperationException("No real audio decode permitted");}
    public void Dispose() { DisposedDuringWarmup = DelayWarmup && !WarmupTerminated; DisposeCount++; }
}
internal sealed class FakeWindowModels:IModelManager
{
    public event EventHandler<ModelTransferProgress>? ProgressChanged { add { } remove { } }
    public IReadOnlyList<ModelDescriptor> RequiredModels=>[];public bool AreAllModelsReady=>true;public ModelTransferProgress? CurrentProgress=>null;
    public Task<string> EnsureModelAsync(ModelDescriptor d,IProgress<ModelTransferProgress>? p,CancellationToken token)=>throw new InvalidOperationException("No real model bytes permitted");
    public Task DownloadRequiredModelsAsync(CancellationToken token)=>throw new InvalidOperationException("No download permitted");public void Dispose(){}
}
