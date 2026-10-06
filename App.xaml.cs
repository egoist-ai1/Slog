using System.Threading;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\Egoist.Voice.SingleInstance";
    private const string ShutdownEventName = "Local\\Egoist.Voice.Shutdown";
    private Mutex? _singleInstance;
    private EventWaitHandle? _shutdownEvent;
    private RegisteredWaitHandle? _shutdownRegistration;
    private TrayService? _tray;
    private AppThemeService? _themeService;
    private Task<IAudioCaptureService>? _captureConstructionTask;
    private Task? _shutdownTask;
    private bool _shutdownRequested;
    private IModelManager? _startupModelManager;
    private RecentRecordingHistoryService? _startupRecentRecordings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Core has no reference to the service layer, so the logging hook is wired here rather
        // than taken as a dependency.
        Core.UserDictionary.AppLogWrite = message => AppLog.Write(message);
        // CLI arguments often contain corpus/audio/output paths. Logging their values would turn a
        // local benchmark into a path-disclosure channel, so diagnostics keep only the mode/count.
        AppLog.Write($"Startup mode={(e.Args.FirstOrDefault() ?? "interactive")} argCount={e.Args.Length}");
        DispatcherUnhandledException += (_, args) =>
            AppLog.Write("Dispatcher unhandled exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("AppDomain unhandled exception", args.ExceptionObject as Exception);

        if (e.Args.Length == 2 && e.Args[0] == "--export-russian-quality-models")
        {
            File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(ModelCatalog.CreateRussianQualityModels()));
            RequestShutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--export-whisper-models")
        {
            File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(ModelCatalog.CreateWhisperRussianModels()));
            RequestShutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--export-compact-models")
        {
            File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(ModelCatalog.CreateCompactModels()));
            RequestShutdown();
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--local-entity-asr-check")
        {
            _ = RunLocalAsrCheckAsync(e.Args[1], e.Args[2], e.Args[3], scorePostProcessing: true);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--local-asr-check")
        {
            _ = RunLocalAsrCheckAsync(e.Args[1], e.Args[2], e.Args[3]);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--local-history-check")
        {
            _ = RunLocalHistoryCheckAsync(e.Args[1], e.Args[2], e.Args[3]);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--asr-thread-check")
        {
            _ = RunAsrThreadCheckAsync(e.Args[1], e.Args[2], e.Args[3]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--local-qwen-check")
        {
            _ = RunLocalQwenCheckAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--local-translation-comparison")
        {
            _ = RunLocalTranslationComparisonAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--qwen-lifetime-probe")
        {
            _ = RunQwenLifetimeProbeAsync(e.Args[1]);
            return;
        }

        if (e.Args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            SignalRunningInstanceToShutdown();
            if (!WaitForRunningInstanceToExit(TimeSpan.FromSeconds(20)))
            {
                Environment.ExitCode = 2;
            }
            RequestShutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--transcribe-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunTranscriptionSmokeAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--entity-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunEntitySmokeAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBenchmarkAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--corpus-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunCorpusBenchmarkAsync(
                e.Args[1],
                e.Args[2],
                e.Args.Length >= 4 ? e.Args[3] : "hybrid",
                e.Args.Length >= 5 ? e.Args[4] : "baseline",
                e.Args.Length >= 6 && !e.Args[5].Equals("-", StringComparison.Ordinal) ? e.Args[5] : null,
                e.Args.Length >= 7 ? e.Args[6] : "auto");
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--corpus-record", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunCorpusRecorderAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--stress-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var iterations = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var parsed)
                ? Math.Clamp(parsed, 2, 200)
                : 30;
            _ = RunStressBenchmarkAsync(e.Args[1], e.Args[2], iterations);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--giga-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunGigaBenchmarkAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--whisper-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunWhisperBenchmarkAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--pipeline-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunPipelineSmokeAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--microphone-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunMicrophoneSmokeAsync(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--microphone-control-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunMicrophoneControlSmokeAsync(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--giga-hotword-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunGigaHotwordSmokeAsync(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--model-source-smoke", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunModelSourceSmokeAsync(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-tray-preview", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var trayTheme = e.Args.Length >= 3 &&
                                e.Args[2].Equals("light", StringComparison.OrdinalIgnoreCase)
                    ? EffectiveAppTheme.Light
                    : e.Args.Length >= 3 && e.Args[2].Equals("contrast", StringComparison.OrdinalIgnoreCase)
                        ? EffectiveAppTheme.HighContrast
                        : EffectiveAppTheme.Dark;
                EgoistTrayVisualPreview.Render(e.Args[1], trayTheme);
            }
            catch (Exception exception)
            {
                AppLog.Write("Tray preview failed", exception);
                Environment.ExitCode = 1;
            }
            RequestShutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-shortcut-preview", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _themeService = new AppThemeService();
            var shortcutTheme = e.Args.Length >= 3 &&
                                e.Args[2].Equals("light", StringComparison.OrdinalIgnoreCase)
                ? EffectiveAppTheme.Light
                : e.Args.Length >= 3 && e.Args[2].Equals("contrast", StringComparison.OrdinalIgnoreCase)
                    ? EffectiveAppTheme.HighContrast
                    : EffectiveAppTheme.Dark;
            _themeService.ApplyDiagnostic(
                shortcutTheme,
                reducedMotion: shortcutTheme == EffectiveAppTheme.HighContrast);
            var dialog = new CustomShortcutDialog(new KeyboardShortcut(
                HotkeyModifiers.Control | HotkeyModifiers.Shift,
                0x56));
            dialog.Show();
            _ = Dispatcher.InvokeAsync(() =>
            {
                dialog.RenderPreview(e.Args[1]);
                dialog.Close();
                RequestShutdown();
            }, DispatcherPriority.ContextIdle);
            return;
        }

        var isolatedVisualPreview = e.Args.Length >= 2 &&
            (e.Args[0].Equals("--render-settings-preview", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--render-state-preview", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--background-render-preview", StringComparison.OrdinalIgnoreCase));
        if (!isolatedVisualPreview)
        {
            _singleInstance = new Mutex(true, MutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                RequestShutdown();
                return;
            }

            _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
            _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
                _shutdownEvent,
                (_, _) => Dispatcher.BeginInvoke(RequestShutdown),
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        var requiredModels = VoiceRuntimeProfile.Models;
        var modelManager = new ModelManager(requiredModels);
        _startupModelManager = modelManager;
        var delivery = new DictationDeliveryService(
            new ClipboardService(),
            new TextInsertionService());
        var settingsService = new DictationSettingsService();
        var settings = settingsService.Load();
        _themeService = new AppThemeService();
        _themeService.Apply(settings.Theme);
        var recentRecordings = new RecentRecordingHistoryService();
        _startupRecentRecordings = recentRecordings;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _captureConstructionTask = CreateCaptureForStartupAsync(() => new AudioCaptureService(
            captureDeviceId: settings.CaptureDeviceId,
            startPaused: settings.IsCapturePaused || isolatedVisualPreview));
        IAudioCaptureService capture;
        try { capture = await _captureConstructionTask; }
        catch (Exception exception)
        {
            AppLog.Write("Microphone startup construction failed", exception);
            RequestShutdown();
            return;
        }
        if (_shutdownRequested) return;
        var window = new MainWindow(
            capture,
            VoiceRuntimeProfile.CreateTranscription(modelManager),
            delivery,
            modelManager,
            settingsService,
            recentRecordings,
            _themeService);

        MainWindow = window;
        _startupModelManager = null;
        _startupRecentRecordings = null;
        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-settings-preview", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settingsWindow = new SettingsWindow(window, settingsService, quit: () => { });
            settingsWindow.DiagnosticPreview = true;
            settingsWindow.ShowActivated = false;
            settingsWindow.ShowInTaskbar = false;
            settingsWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            settingsWindow.Left = -20000;
            settingsWindow.Top = -20000;
            settingsWindow.Show();
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var renderMode = e.Args.Length >= 3 ? e.Args[2] : "default";
                if (renderMode.Contains("light", StringComparison.OrdinalIgnoreCase))
                {
                    _themeService.ApplyDiagnostic(EffectiveAppTheme.Light, reducedMotion: false);
                }
                else if (renderMode.Contains("contrast", StringComparison.OrdinalIgnoreCase))
                {
                    _themeService.ApplyDiagnostic(EffectiveAppTheme.HighContrast, reducedMotion: true);
                }
                else if (renderMode.Contains("dark", StringComparison.OrdinalIgnoreCase))
                {
                    _themeService.ApplyDiagnostic(EffectiveAppTheme.Dark, reducedMotion: false);
                }
                if (renderMode.StartsWith("text", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.ShowTextPreview(renderMode.Contains("filled", StringComparison.OrdinalIgnoreCase));
                    if (renderMode.Contains("result", StringComparison.OrdinalIgnoreCase)) settingsWindow.ScrollTextResultForPreview();
                }
                else if (renderMode.Contains("appearance", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.ShowAppearanceAndActivate();
                    await Task.Delay(80);
                }
                else if (renderMode.Contains("feedback", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.ShowFeedbackAndActivate();
                    await Task.Delay(80);
                }
                else if (renderMode.Contains("general", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.ShowGeneralAndActivate();
                    await Task.Delay(80);
                }
                else if (renderMode.Contains("models", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.ShowModelsAndActivate();
                    await Task.Delay(80);
                }
                else if (renderMode.Contains("recognition", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderMode.Contains("formatting-unavailable", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsWindow.ShowRecognitionFormattingUnavailablePreview();
                    }
                    else if (renderMode.Contains("model-error", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsWindow.ShowRecognitionModelFailurePreview();
                    }
                    else if (renderMode.Contains("engine-error", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsWindow.ShowTranslationEngineRepairPreview();
                    }
                    else
                    {
                        settingsWindow.ShowRecognitionAndActivate();
                    }
                    await Task.Delay(80);
                }
                if (renderMode.StartsWith("history", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderMode.Contains("filled", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsWindow.ShowFilledHistoryPreview();
                    }
                    else
                    {
                        settingsWindow.ShowHistoryAndActivate();
                    }
                    await Task.Delay(80);
                }
                if (renderMode.Equals("min", StringComparison.OrdinalIgnoreCase) ||
                    renderMode.EndsWith("min", StringComparison.OrdinalIgnoreCase))
                {
                    settingsWindow.Width = settingsWindow.MinWidth;
                    settingsWindow.Height = settingsWindow.MinHeight;
                    settingsWindow.UpdateLayout();
                    await Task.Delay(80);
                }
                await Task.Delay(200);
                var renderOpenDropdown = renderMode.StartsWith("open", StringComparison.OrdinalIgnoreCase);
                if (renderOpenDropdown)
                {
                    settingsWindow.Left = 100;
                    settingsWindow.Top = 100;
                    settingsWindow.Topmost = true;
                    settingsWindow.ShowAndActivate();
                    settingsWindow.OpenMicrophoneDropdownForPreview();
                    await Task.Delay(180);
                    settingsWindow.RenderScreenPreview(e.Args[1]);
                    settingsWindow.Topmost = false;
                }
                else
                {
                    settingsWindow.RenderPreview(e.Args[1]);
                }
                settingsWindow.CloseForExit();
                RequestShutdown();
            }, DispatcherPriority.ContextIdle);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--render-state-preview", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Args.Length >= 4)
            {
                var stateTheme = e.Args[3].Equals("light", StringComparison.OrdinalIgnoreCase)
                    ? EffectiveAppTheme.Light
                    : e.Args[3].Equals("contrast", StringComparison.OrdinalIgnoreCase)
                        ? EffectiveAppTheme.HighContrast
                        : EffectiveAppTheme.Dark;
                _themeService.ApplyDiagnostic(
                    stateTheme,
                    reducedMotion: stateTheme == EffectiveAppTheme.HighContrast);
            }
            window.Show();
            window.ShowStatePreview(e.Args[1]);
            _ = RenderStatePreviewAsync(window, e.Args[2]);
            return;
        }

        if (e.Args.Length >= 2 &&
            (e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--background-render-preview", StringComparison.OrdinalIgnoreCase)))
        {
            if (e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
            {
                window.Show();
            }
            window.ShowListeningPreview();
            _ = Dispatcher.InvokeAsync(() =>
            {
                window.RenderPreview(e.Args[1]);
                window.Close();
                RequestShutdown();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }

        // Background startup does not show MainWindow, so its Loaded event is not an engine
        // lifecycle boundary. Start the shared Host explicitly after all isolated CLI/render modes
        // have returned. The operation never frames recognized text and cannot block dictation.
        window.BeginTranslationEngineWarmup();
        window.BeginTextModelWarmup();
        _tray = new TrayService(window, modelManager, settingsService, _themeService, RequestShutdown);
        window.RequestOpenSettings = () => _tray?.ShowSettingsWindowPublic();
        window.RequestOpenHistory = () => _tray?.ShowHistoryWindowPublic();
        window.RequestExit = RequestShutdown;
        window.InitializeHotkey();
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!background)
        {
            window.ShowReadyBriefly();
        }
        window.BeginWarmUp(showProgress: !background, announceModelDownloads: !modelManager.AreAllModelsReady);
        AppLog.Write($"Startup complete, background={background}");
    }

    internal static Task<IAudioCaptureService> CreateCaptureForStartupAsync(Func<IAudioCaptureService> factory) =>
        Task.Run(factory);

    internal static async Task DisposeCaptureForStartupAsync(Task<IAudioCaptureService> constructing)
    {
        var capture = await constructing;
        await Task.Run(capture.Dispose);
    }

    private static void DisposeStartupOwned(IDisposable? owned)
    {
        try { owned?.Dispose(); }
        catch (Exception exception) { AppLog.Write("Startup service disposal failed", exception); }
    }

    private void RequestShutdown()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(RequestShutdown); return; }
        _shutdownTask ??= ShutdownOwnedServicesAsync();
    }

    private async Task ShutdownOwnedServicesAsync()
    {
        _shutdownRequested = true;
        try
        {
            if (MainWindow is MainWindow window) await window.ShutdownAsync();
            else
            {
                if (_captureConstructionTask is { } constructing)
                {
                    try { await DisposeCaptureForStartupAsync(constructing); }
                    catch (Exception exception) { AppLog.Write("Capture constructor ended during shutdown", exception); }
                }
                await Task.Run(() =>
                {
                    DisposeStartupOwned(_startupRecentRecordings);
                    DisposeStartupOwned(_startupModelManager);
                });
            }
        }
        catch (Exception exception) { AppLog.Write("Application shutdown cleanup failed", exception); }
        base.Shutdown();
    }

    private static void SignalRunningInstanceToShutdown()
    {
        try
        {
            using var shutdownEvent = EventWaitHandle.OpenExisting(ShutdownEventName);
            shutdownEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No running instance.
        }
    }

    private async Task RenderStatePreviewAsync(MainWindow window, string outputPath)
    {
        await Task.Delay(320);
        window.RenderPreview(outputPath);
        window.Close();
        RequestShutdown();
    }

    private static bool WaitForRunningInstanceToExit(TimeSpan timeout)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The former process terminated without releasing the mutex.
            }

            mutex.ReleaseMutex();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return true;
        }
    }

    private async Task RunTranscriptionSmokeAsync(string audioPath, string outputPath)
    {
        try
        {
            using var manager = new ModelManager(VoiceRuntimeProfile.Models, allowDownload: false);
            using var service = VoiceRuntimeProfile.CreateTranscription(manager);
            var result = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            await File.WriteAllTextAsync(outputPath, result.Text);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunEntitySmokeAsync(string audioPath, string outputPath)
    {
        try
        {
            using var sensitiveLogScope = AppLog.SuppressSensitiveData();
            using var service = CreateTranscriptionService();
            var result = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            var profile = EntityProfilePolicy.Resolve(
                result.Text,
                processName: null,
                isGame: false,
                technologyRequested: false);
            var text = new TranscriptPostProcessor(UserDictionary.BuiltIn).Process(result.Text, profile);
            await File.WriteAllTextAsync(outputPath, text);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception.GetType().Name}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunModelSourceSmokeAsync(string outputPath)
    {
        var lines = new List<string>();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            foreach (var descriptor in ModelCatalog.CreateRequiredModels())
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.DownloadUri);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None);
                response.EnsureSuccessStatusCode();

                var advertisedSize = response.Content.Headers.ContentRange?.Length ??
                                     response.Content.Headers.ContentLength;
                if (advertisedSize is not null && advertisedSize != descriptor.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"{descriptor.Id}: source size {advertisedSize} does not match {descriptor.SizeBytes}.");
                }

                lines.Add($"{descriptor.Id}|{(int)response.StatusCode}|{advertisedSize ?? descriptor.SizeBytes}");
            }

            lines.Insert(0, "PASS");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            lines.Clear();
            lines.Add("ERROR");
            lines.Add(exception.ToString());
        }
        finally
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllLinesAsync(outputPath, lines);
            RequestShutdown();
        }
    }

    private async Task RunMicrophoneSmokeAsync(string outputPath)
    {
        try
        {
            using var capture = new AudioCaptureService();
            capture.Start();
            await Task.Delay(450);
            var captureResult = await capture.StopAsync(CancellationToken.None);
            var bytes = captureResult.Samples.Length * sizeof(float);
            if (captureResult.Samples.Length == 0)
            {
                throw new InvalidDataException("Microphone capture produced no in-memory samples.");
            }

            await File.WriteAllTextAsync(outputPath,
                $"PASS{Environment.NewLine}" +
                $"bytes={bytes}{Environment.NewLine}" +
                $"hasSpeech={captureResult.HasSpeech}{Environment.NewLine}" +
                $"speechMs={captureResult.DetectedSpeech.TotalMilliseconds:0}{Environment.NewLine}" +
                $"peakDb={captureResult.PeakDecibels:0.0}");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunMicrophoneControlSmokeAsync(string outputPath)
    {
        try
        {
            using var capture = new AudioCaptureService(startPaused: true);
            var devices = capture.GetCaptureDevices();
            if (devices.Count < 2)
            {
                throw new InvalidOperationException(
                    $"Microphone control smoke requires two active endpoints; found {devices.Count}.");
            }

            capture.SelectCaptureDevice(devices[0].Id);
            var firstPaused = capture.GetState();
            if (!firstPaused.IsPaused || firstPaused.IsMonitoring)
            {
                throw new InvalidOperationException("Selecting while paused unexpectedly opened capture.");
            }

            capture.ResumeMonitoring();
            await Task.Delay(180);
            var firstRunning = capture.GetState();
            if (!firstRunning.IsMonitoring || firstRunning.IsPaused)
            {
                throw new InvalidOperationException("The first endpoint did not enter monitoring state.");
            }

            capture.PauseMonitoring();
            capture.SelectCaptureDevice(devices[1].Id);
            var secondPaused = capture.GetState();
            if (!secondPaused.IsPaused || secondPaused.IsMonitoring
                || !string.Equals(secondPaused.SelectedDeviceId, devices[1].Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Paused endpoint switch did not preserve safe state.");
            }

            capture.ResumeMonitoring();
            await Task.Delay(180);
            if (!capture.GetState().IsMonitoring)
            {
                throw new InvalidOperationException("The second endpoint did not enter monitoring state.");
            }
            capture.PauseMonitoring();

            await File.WriteAllTextAsync(outputPath,
                $"PASS{Environment.NewLine}" +
                $"activeEndpoints={devices.Count}{Environment.NewLine}" +
                "switchWithoutRestart=true" + Environment.NewLine +
                "pauseStopsMonitoring=true");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunGigaHotwordSmokeAsync(string outputPath)
    {
        try
        {
            if (!RussianAsrProfile.ContextualBiasSupported)
                throw new NotSupportedException("Plain RNNT does not support the historical E2E BPE hotwords.");
            int baselineSilenceChars;
            using (var baseline = new GigaAmTranscriptionService())
            {
                await baseline.WarmUpAsync(null, CancellationToken.None);
                if (baseline.ContextualBiasActive)
                {
                    throw new InvalidDataException("Baseline GigaAM unexpectedly enabled contextual bias.");
                }
                var baselineResult = await baseline.TranscribeSamplesAsync(
                    new float[GigaAmTranscriptionService.BenchmarkSampleRate / 2],
                    GigaAmTranscriptionService.BenchmarkSampleRate,
                    CancellationToken.None);
                baselineSilenceChars = baselineResult.Text.Length;
            }

            int phraseCount;
            int hotwordSilenceChars;
            using (var candidate = new GigaAmTranscriptionService(enableContextualBias: true))
            {
                await candidate.WarmUpAsync(null, CancellationToken.None);
                if (!candidate.ContextualBiasActive || candidate.ContextualBiasPhraseCount <= 0)
                {
                    throw new InvalidDataException("GigaAM contextual bias fell back to baseline.");
                }
                var candidateResult = await candidate.TranscribeSamplesAsync(
                    new float[GigaAmTranscriptionService.BenchmarkSampleRate / 2],
                    GigaAmTranscriptionService.BenchmarkSampleRate,
                    CancellationToken.None);
                phraseCount = candidate.ContextualBiasPhraseCount;
                hotwordSilenceChars = candidateResult.Text.Length;
            }

            await File.WriteAllTextAsync(outputPath,
                $"PASS{Environment.NewLine}" +
                $"phrases={phraseCount}{Environment.NewLine}" +
                $"baselineSilenceChars={baselineSilenceChars}{Environment.NewLine}" +
                $"hotwordSilenceChars={hotwordSilenceChars}");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath,
                $"FAIL{Environment.NewLine}type={exception.GetType().Name}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    /// <summary>
    /// Transcribes an entire corpus and writes WER/CER plus latency percentiles. Runs the whole
    /// corpus even when individual clips fail: a report that stops at the first bad file cannot
    /// be compared against a baseline.
    /// </summary>
    private async Task RunCorpusBenchmarkAsync(
        string corpusDirectory,
        string outputPath,
        string label,
        string decoderMode,
        string? profilePath,
        string whisperRuntimeMode)
    {
        try
        {
            label = CorpusBenchmark.ValidateLabel(label);
            var enableContextualBias = decoderMode.Trim().ToLowerInvariant() switch
            {
                "baseline" => false,
                "hotwords" => true,
                _ => throw new ArgumentException("Decoder mode must be baseline or hotwords.", nameof(decoderMode))
            };
            if (enableContextualBias && !RussianAsrProfile.ContextualBiasSupported)
                throw new NotSupportedException("Use baseline decoder mode for plain GigaAM v3 RNNT.");
            var whisperRuntimePreference = WhisperRuntimePolicy.ConfigureForBenchmark(whisperRuntimeMode);
            corpusDirectory = Path.GetFullPath(corpusDirectory);
            var script = CorpusScript.Load(corpusDirectory);
            var profile = string.IsNullOrWhiteSpace(profilePath)
                ? null
                : CorpusBenchmarkProfile.Load(Path.GetFullPath(profilePath), script);
            var referenceDocument = CorpusBenchmark.LoadReferenceDocument(corpusDirectory);
            var inventory = CorpusBenchmark.ValidateAndFingerprint(
                corpusDirectory,
                script,
                referenceDocument,
                profile?.SelectedIds);
            var selectedIds = profile?.SelectedIds.ToHashSet(StringComparer.Ordinal);
            var references = selectedIds is null
                ? referenceDocument.Entries
                : referenceDocument.Entries.Where(entry => selectedIds.Contains(entry.Id)).ToArray();
            var models = ModelCatalog.CreateRequiredModels();
            var environment = CorpusBenchmark.CaptureEnvironment(models);
            var resourcesBefore = BenchmarkResourceSnapshot.Capture();

            // A benchmark must not turn into a hidden network operation. Missing current models are
            // a typed failure; candidates remain behind their own explicit download gate.
            using var sensitiveLogScope = AppLog.SuppressSensitiveData();
            using var service = CreateTranscriptionService(
                allowModelDownload: false,
                enableContextualBias: enableContextualBias);
            var observedService = service as IBenchmarkTranscriptionService
                ?? throw new InvalidOperationException("Hybrid benchmark observation is unavailable.");
            // Warm-up is excluded from the measurements on purpose: the first decode pays for ONNX
            // graph optimization and would dominate every percentile computed after it.
            await service.WarmUpAsync(null, CancellationToken.None);
            var parameters = CorpusBenchmark.CaptureParameters(
                enableContextualBias,
                whisperRuntimePreference,
                WhisperRuntimePolicy.LoadedLibrary);
            var postProcessor = new TranscriptPostProcessor(UserDictionary.BuiltIn);
            using var uiStalls = new UiThreadStallMonitor(Dispatcher);

            var entries = new List<BenchmarkEntry>(references.Count);
            var progressPath = outputPath + ".progress.json";
            for (var referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
            {
                var reference = references[referenceIndex];
                CorpusBenchmark.SaveProgress(
                    progressPath,
                    label,
                    "started",
                    referenceIndex,
                    references.Count,
                    reference.Id);
                var audioPath = Path.Combine(corpusDirectory, reference.Audio);
                var buckets = profile?.BucketsFor(reference.Id) ?? [];
                if (!File.Exists(audioPath))
                {
                    entries.Add(new BenchmarkEntry(
                        reference.Id,
                        reference.Set,
                        reference.Text,
                        string.Empty,
                        0,
                        0,
                        "AudioMissing",
                        Buckets: buckets,
                        CaptureCode: "CaptureMissing",
                        AttributionCode: "CaptureReadFailed"));
                    CorpusBenchmark.SaveProgress(
                        progressPath,
                        label,
                        "completed",
                        referenceIndex + 1,
                        references.Count,
                        reference.Id);
                    continue;
                }

                var captureCode = "CaptureNotMeasured";
                var gateCode = "GateNotMeasured";
                var failureAttribution = "CaptureReadFailed";
                try
                {
                    var captureStarted = Stopwatch.GetTimestamp();
                    var samples = await Task.Run(
                        () => AudioSampleReader.ReadMono16Khz(audioPath),
                        CancellationToken.None);
                    var preRollSamples = Math.Min(
                        samples.Length,
                        (int)Math.Round(
                            AudioCaptureService.PreRollDuration.TotalSeconds *
                            AudioCaptureService.OutputSampleRate));
                    var activity = AudioCaptureService.Analyze(samples, preRollSamples);
                    var captureElapsed = Stopwatch.GetElapsedTime(captureStarted);
                    captureCode = CorpusBenchmark.ClassifyCapture(samples, activity);
                    gateCode = CorpusBenchmark.ClassifyGate(activity);
                    failureAttribution = "CaptureIntegrity";
                    if (captureCode == "CaptureNoAudio")
                    {
                        throw new InvalidDataException("Corpus audio contains no decodable samples.");
                    }

                    failureAttribution = "DecodeFailed";
                    var observation = await observedService.TranscribeObservedAsync(
                        audioPath,
                        CancellationToken.None);

                    // Benchmark the string users actually receive, including the same built-in
                    // dictionary and deterministic command/format stages as normal dictation.
                    failureAttribution = "NormalizationFailed";
                    var normalizationStarted = Stopwatch.GetTimestamp();
                    var entityProfile = EntityProfilePolicy.Resolve(
                        observation.Result.Text,
                        processName: null,
                        isGame: false,
                        technologyRequested: false);
                    var text = postProcessor.Process(observation.Result.Text, entityProfile);
                    var normalizationElapsed = Stopwatch.GetElapsedTime(normalizationStarted);
                    var diagnostics = CorpusBenchmark.AnalyzeStages(
                        reference.Text,
                        samples,
                        activity,
                        observation,
                        text,
                        captureElapsed,
                        normalizationElapsed);
                    entries.Add(new BenchmarkEntry(
                        reference.Id,
                        reference.Set,
                        reference.Text,
                        text,
                        diagnostics.StageTimings.PipelineMs,
                        observation.Result.Elapsed.TotalMilliseconds,
                        ExpectedEntities: reference.Entities,
                        TranslationCommandExpected: reference.TranslationCommandExpected,
                        Boundary: reference.Boundary,
                        BoundaryTarget: reference.BoundaryTarget,
                        Buckets: buckets,
                        CaptureCode: diagnostics.CaptureCode,
                        GateCode: diagnostics.GateCode,
                        AttributionCode: diagnostics.AttributionCode,
                        FallbackTrigger: diagnostics.FallbackTrigger,
                        FallbackRan: diagnostics.FallbackRan,
                        FallbackUnavailable: diagnostics.FallbackUnavailable,
                        SelectedEngine: diagnostics.SelectedEngine,
                        PrimaryWordErrors: diagnostics.PrimaryWordErrors,
                        FallbackWordErrors: diagnostics.FallbackWordErrors,
                        SelectedWordErrors: diagnostics.SelectedWordErrors,
                        StageTimings: diagnostics.StageTimings));
                }
                catch (Exception exception)
                {
                    AppLog.Write($"Corpus clip failed id={reference.Id} type={exception.GetType().Name}");
                    entries.Add(new BenchmarkEntry(
                        reference.Id,
                        reference.Set,
                        reference.Text,
                        string.Empty,
                        Error: "TranscriptionFailed",
                        ExpectedEntities: reference.Entities,
                        TranslationCommandExpected: reference.TranslationCommandExpected,
                        Boundary: reference.Boundary,
                        BoundaryTarget: reference.BoundaryTarget,
                        Buckets: buckets,
                        CaptureCode: captureCode,
                        GateCode: gateCode,
                        AttributionCode: failureAttribution));
                }
                CorpusBenchmark.SaveProgress(
                    progressPath,
                    label,
                    "completed",
                    referenceIndex + 1,
                    references.Count,
                    reference.Id);
            }

            var uiStallSummary = uiStalls.StopAndSummarize();
            var resourcesAfter = BenchmarkResourceSnapshot.Capture();
            parameters = parameters with
            {
                WhisperRuntimeLoaded = WhisperRuntimePolicy.LoadedLibrary
            };
            var context = new BenchmarkRunContext(
                inventory,
                environment,
                parameters,
                resourcesBefore,
                resourcesAfter,
                profile is null ? null : CorpusBenchmark.SummarizeProfile(profile),
                uiStallSummary);
            var report = CorpusBenchmark.Summarize(label, entries, context: context);
            CorpusBenchmark.Save(report, outputPath);
            CorpusBenchmark.SaveProgress(
                progressPath,
                label,
                "complete",
                references.Count,
                references.Count,
                currentId: null);
            if (entries.Any(entry => entry.Error is not null))
            {
                Environment.ExitCode = 3;
            }
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            AppLog.Write($"Corpus benchmark failed type={exception.GetType().Name}");
            var errorCode = exception switch
            {
                InvalidDataException => "InvalidCorpus",
                FileNotFoundException => "RequiredFileOrModelMissing",
                UnauthorizedAccessException => "AccessDenied",
                IOException => "InputOutputFailure",
                _ => "BenchmarkFailed"
            };
            try
            {
                CorpusBenchmark.SaveFailure(outputPath, label, errorCode);
            }
            catch
            {
                // There is nowhere safer to persist this failure. The exit code remains the source
                // of truth and diagnostics still do not receive the private path or exception text.
            }
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunBenchmarkAsync(string audioPath, string outputPath)
    {
        try
        {
            using var service = CreateTranscriptionService();
            var cold = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            var warm = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            await File.WriteAllTextAsync(outputPath,
                $"cold={cold.Elapsed.TotalSeconds:0.00}s{Environment.NewLine}" +
                $"warm={warm.Elapsed.TotalSeconds:0.00}s{Environment.NewLine}" +
                warm.Text);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunStressBenchmarkAsync(string audioPath, string outputPath, int iterations)
    {
        try
        {
            using var service = CreateTranscriptionService();
            using var process = Process.GetCurrentProcess();
            var cold = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            string? expectedText = null;
            for (var index = 0; index < 5; index++)
            {
                var settling = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
                expectedText ??= settling.Text;
                if (!string.Equals(expectedText, settling.Text, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("ASR output changed during native-runtime settling.");
                }
            }
            process.Refresh();
            var privateBytesBefore = process.PrivateMemorySize64;
            var handlesBefore = process.HandleCount;
            var elapsed = new double[iterations];
            for (var index = 0; index < iterations; index++)
            {
                var result = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
                expectedText ??= result.Text;
                if (!string.Equals(expectedText, result.Text, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"ASR output changed during deterministic stress run {index + 1}.");
                }
                elapsed[index] = result.Elapsed.TotalMilliseconds;
            }

            Array.Sort(elapsed);
            process.Refresh();
            var privateBytesAfter = process.PrivateMemorySize64;
            var handlesAfter = process.HandleCount;
            var p50 = elapsed[(int)Math.Ceiling(iterations * 0.50) - 1];
            var p95 = elapsed[(int)Math.Ceiling(iterations * 0.95) - 1];
            var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedText!))).ToLowerInvariant();
            await File.WriteAllTextAsync(outputPath,
                $"PASS{Environment.NewLine}" +
                $"iterations={iterations}{Environment.NewLine}" +
                $"coldMs={cold.Elapsed.TotalMilliseconds:0.0}{Environment.NewLine}" +
                $"p50Ms={p50:0.0}{Environment.NewLine}" +
                $"p95Ms={p95:0.0}{Environment.NewLine}" +
                $"maxMs={elapsed[^1]:0.0}{Environment.NewLine}" +
                $"privateBytesDelta={privateBytesAfter - privateBytesBefore}{Environment.NewLine}" +
                $"handleDelta={handlesAfter - handlesBefore}{Environment.NewLine}" +
                $"textSha256={textHash}");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunGigaBenchmarkAsync(string audioPath, string outputPath)
    {
        try
        {
            using var manager = new ModelManager(ModelCatalog.CreateRequiredModels());
            using var service = new GigaAmTranscriptionService(manager);
            var cold = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            var warm = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            await File.WriteAllTextAsync(outputPath,
                $"cold={cold.Elapsed.TotalSeconds:0.000}s{Environment.NewLine}" +
                $"warm={warm.Elapsed.TotalSeconds:0.000}s{Environment.NewLine}" +
                warm.Text);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunWhisperBenchmarkAsync(string audioPath, string outputPath)
    {
        try
        {
            using var manager = new ModelManager(ModelCatalog.CreateRequiredModels());
            using var service = new WhisperTranscriptionService(manager);
            var cold = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            var warm = await service.TranscribeAsync(audioPath, null, CancellationToken.None);
            await File.WriteAllTextAsync(outputPath,
                $"cold={cold.Elapsed.TotalSeconds:0.000}s{Environment.NewLine}" +
                $"warm={warm.Elapsed.TotalSeconds:0.000}s{Environment.NewLine}" +
                warm.Text);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            RequestShutdown();
        }
    }

    private async Task RunPipelineSmokeAsync(string audioPath, string outputPath)
    {
        Window? testWindow = null;
        try
        {
            // AcceptsReturn is required, not cosmetic: a single-line TextBox silently truncates a
            // multi-line paste at the first newline. Long-form dictation produces paragraphs, so
            // without this the harness reports a character-count mismatch and blames the product
            // for a limitation of its own test window.
            var textBox = new System.Windows.Controls.TextBox
            {
                FontSize = 16,
                Margin = new Thickness(12),
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            testWindow = new Window
            {
                Title = "Egoist Voice pipeline smoke",
                Width = 520,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = textBox,
                ShowInTaskbar = false,
                Topmost = true
            };
            testWindow.Show();
            testWindow.Activate();
            textBox.Focus();
            Keyboard.Focus(textBox);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var target = new WindowInteropHelper(testWindow).Handle;
            NativeMethods.ActivateForDiagnostics(target);
            testWindow.Activate();
            textBox.Focus();
            Keyboard.Focus(textBox);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var activationDeadline = DateTime.UtcNow.AddSeconds(12);
            while (NativeMethods.GetForegroundWindow() != target && DateTime.UtcNow < activationDeadline)
            {
                await Task.Delay(100);
            }
            if (NativeMethods.GetForegroundWindow() != target)
            {
                throw new InvalidOperationException(
                    $"Pipeline smoke window was not activated within 12 seconds: target=0x{target:X}, foreground=0x{NativeMethods.GetForegroundWindow():X}.");
            }

            using var transcription = CreateTranscriptionService();
            var result = await transcription.TranscribeAsync(audioPath, null, CancellationToken.None);
            var text = TranscriptNormalizer.Normalize(result.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Smoke audio produced no text.");
            }

            NativeMethods.ActivateForDiagnostics(target);
            testWindow.Activate();
            textBox.Focus();
            Keyboard.Focus(textBox);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await new ClipboardService().CopyAsync(text, CancellationToken.None);
            await new TextInsertionService().InsertAsync(text, target, CancellationToken.None);
            await Task.Delay(150);

            if (!string.Equals(textBox.Text, text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"SendInput mismatch: expected {text.Length}, received {textBox.Text.Length} characters.");
            }

            await File.WriteAllTextAsync(outputPath,
                $"PASS{Environment.NewLine}" +
                $"characters={text.Length}{Environment.NewLine}" +
                $"elapsed={result.Elapsed.TotalSeconds:0.00}s");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(outputPath, $"ERROR: {exception}");
        }
        finally
        {
            testWindow?.Close();
            RequestShutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write($"Exit code={e.ApplicationExitCode}");
        _tray?.Dispose();
        (MainWindow as IDisposable)?.Dispose();
        _themeService?.Dispose();
        _shutdownRegistration?.Unregister(null);
        _shutdownEvent?.Dispose();
        _singleInstance?.Dispose();

        // Last, and after everything else has had its say: logging is asynchronous now, so the
        // records that explain a shutdown are still in the queue at this point.
        AppLog.Flush(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    private static ITranscriptionService CreateTranscriptionService(
        bool allowModelDownload = true,
        bool enableContextualBias = false)
    {
        var manager = new ModelManager(
            ModelCatalog.CreateRequiredModels(),
            allowDownload: allowModelDownload);
        return new OwnedHybridTranscriptionService(manager, enableContextualBias);
    }
}

internal sealed class OwnedHybridTranscriptionService : ITranscriptionService, IBenchmarkTranscriptionService
{
    private readonly IModelManager _manager;
    private readonly HybridTranscriptionService _inner;

    internal OwnedHybridTranscriptionService(IModelManager manager, bool enableContextualBias = false)
    {
        _manager = manager;
        _inner = new HybridTranscriptionService(manager, enableContextualBias);
    }

    public Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken) =>
        _inner.WarmUpAsync(progress, cancellationToken);

    public Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        IProgress<ModelProgress>? progress,
        CancellationToken cancellationToken) => _inner.TranscribeAsync(audioPath, progress, cancellationToken);

    public Task<HybridTranscriptionObservation> TranscribeObservedAsync(
        string audioPath,
        CancellationToken cancellationToken) => _inner.TranscribeObservedAsync(audioPath, cancellationToken);

    public void Dispose()
    {
        _inner.Dispose();
        _manager.Dispose();
    }
}
