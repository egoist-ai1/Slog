using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Egoist.Voice.Services;

/// <summary>
/// A continuously warm shared-mode WASAPI capture. Only a bounded pre-roll lives while idle;
/// accepted dictation is downmixed/resampled once into memory as it arrives.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    internal const int OutputSampleRate = 16_000;
    internal const int ConversionBlockSamples = 16_384;
    internal const int CaptureBufferMilliseconds = 20;
    // Кольцо тёплого захвата с отметками времени: начало записи берётся не «сейчас», а с момента
    // нажатия минус PreRollDuration. 2 с для 48 кГц stereo float — около 750 KiB, звук лежит
    // только в памяти и не живёт при паузе.
    internal static readonly TimeSpan RingDuration = TimeSpan.FromSeconds(2);
    // Русские слова начинаются с шипящих и взрывных, которые тихие и лежат раньше нажатия
    // (реакция на кнопку + задержка хоткея), поэтому 500 мс вместо прежних 320.
    internal static readonly TimeSpan PreRollDuration = TimeSpan.FromMilliseconds(500);
    // Адаптивный хвост после отпускания: минимум от момента отпускания, дальше пишем, пока не
    // накопится ReleaseTailSilence тишины, но не дольше ReleaseTailMaximum.
    internal static readonly TimeSpan ReleaseTailMinimum = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ReleaseTailSilence = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan ReleaseTailMaximum = TimeSpan.FromMilliseconds(900);
    // Порог тишины: и общий RMS, и энергия верхних частот (затухающее «с/ш/щ») не выше
    // оценки шума на это число дБ.
    internal static readonly double SilenceMarginDb = 5;
    // Запас реального времени сверх ReleaseTailMaximum, если устройство перестало отдавать буферы.
    internal static readonly TimeSpan ReleaseTailStallGrace = TimeSpan.FromMilliseconds(300);

    private readonly object _sync = new();
    // Never held while joining a capture thread. Audio callbacks acquire only _sync.
    private readonly object _lifecycleSync = new();
    private readonly Func<string?, AudioCaptureEndpoint> _captureFactory;
    private readonly Func<long> _clock;
    private readonly HashSet<Task> _pendingDisposals = [];
    // Reused per thread; nested synchronous observers may enter another service callback.
    [ThreadStatic] private static List<AudioCaptureService>? _callbackOwners;
    private long _lifecycleGeneration;
    private long _sessionGeneration;
    private bool _catalogDisposalStarted;
    private readonly VoiceSpectrumAnalyzer _spectrum = new();
    private readonly bool _persistCompletedTake;
    private readonly IMicrophoneDeviceCatalog _deviceCatalog;
    private readonly bool _ownsDeviceCatalog;
    private IWaveIn? _capture;
    private IDisposable? _captureDevice;
    private WaveFormat? _captureFormat;
    private CaptureSessionBuffer? _buffer;
    private string? _selectedDeviceId;
    private string? _activeDeviceId;
    private bool _paused;
    private bool _resumeWhenAvailable;
    private string? _lastRecoveryDeviceId;
    private bool _stopRequested;
    private bool _disposed;
    private Exception? _monitoringFailure;
    private float _smoothedLevel;
    private float _smoothedBass;
    private float _smoothedMid;
    private float _smoothedTreble;
    private long _feedbackSuppressedUntilTimestamp;
    // Оценка шума (дБ): падает мгновенно, растёт только вне записи, чтобы речь не поднимала порог.
    private double? _noiseFloorDb;
    private double? _trebleFloorDb;
    private TaskCompletionSource? _tailWait;
    private long _tailRelease;
    // Буфер прежнего эндпоинта, сохранённый при перезапуске на то же устройство.
    private CaptureSessionBuffer? _retainedBuffer;
    private string? _retainedDeviceId;
    private WaveFormat? _retainedFormat;

    public event EventHandler<float>? LevelChanged;
    public event EventHandler<VoiceTimbreLevel>? TimbreChanged;
    public event EventHandler<float[]>? SamplesAvailable;
    public event EventHandler<AudioCaptureStateChangedEventArgs>? StateChanged;

    public AudioCaptureService(
        bool persistCompletedTake = false,
        string? captureDeviceId = null,
        bool startPaused = false)
        : this(
            new MicrophoneDeviceCatalog(),
            ownsDeviceCatalog: true,
            persistCompletedTake,
            captureDeviceId,
            startPaused)
    {
    }

    internal AudioCaptureService(
        IMicrophoneDeviceCatalog deviceCatalog,
        bool ownsDeviceCatalog,
        bool persistCompletedTake,
        string? captureDeviceId,
        bool startPaused,
        Func<string?, AudioCaptureEndpoint>? captureFactory = null,
        Func<long>? timestampProvider = null)
    {
        _captureFactory = captureFactory ?? OpenWasapiCapture;
        _clock = timestampProvider ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _deviceCatalog = deviceCatalog;
        _ownsDeviceCatalog = ownsDeviceCatalog;
        _persistCompletedTake = persistCompletedTake;
        _selectedDeviceId = MicrophoneSelectionPolicy.NormalizeDeviceId(captureDeviceId);
        _paused = startPaused;
        _deviceCatalog.DevicesChanged += OnDevicesChanged;
        if (_paused)
        {
            return;
        }

        PendingDisposal? failedStart = null;
        try
        {
            lock (_lifecycleSync)
            {
                StartMonitoringLocked(out failedStart);
            }
        }
        catch (Exception exception)
        {
            // App start remains recoverable when the default endpoint is temporarily unavailable.
            // Start() retries and turns the same concrete failure into the capsule state.
            lock (_sync)
            {
                _monitoringFailure = exception;
                _resumeWhenAvailable = true;
            }
            AppLog.Write("WASAPI warm capture unavailable; will retry on trigger", exception);
        }
        finally
        {
            RunDisposal(failedStart);
        }
    }

    public AudioCaptureState GetState()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetStateLocked(GetDevicesSafe());
        }
    }

    public IReadOnlyList<MicrophoneDeviceInfo> GetCaptureDevices()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetDevicesSafe();
        }
    }

    public void SelectCaptureDevice(string? deviceId)
    {
        var normalized = MicrophoneSelectionPolicy.NormalizeDeviceId(deviceId);
        PendingDisposal? retired;
        bool cancelled;
        long generation;
        lock (_lifecycleSync)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var devices = GetDevicesSafe();
                if (!MicrophoneSelectionPolicy.IsAvailable(normalized, devices))
                    throw new MicrophoneUnavailableException(normalized is null
                        ? "Системный микрофон сейчас недоступен."
                        : "Выбранный микрофон сейчас недоступен.");
                var expected = ResolveActiveDeviceId(normalized, devices);
                if (string.Equals(_selectedDeviceId, normalized, StringComparison.Ordinal)
                    && (_paused && !_resumeWhenAvailable || _capture is not null
                        && string.Equals(_activeDeviceId, expected, StringComparison.Ordinal))) return;
                cancelled = _buffer?.IsSessionActive == true;
                DiscardSessionLocked();
                retired = DetachCaptureLocked(retainPreRoll: true);
                _selectedDeviceId = normalized;
                if (_resumeWhenAvailable) _paused = false;
                _resumeWhenAvailable = false;
                _lastRecoveryDeviceId = null;
                _monitoringFailure = null;
                generation = ++_lifecycleGeneration;
            }
        }
        RunDisposal(retired);
        StartMonitoringIfCurrent(generation);
        AudioCaptureStateChangedEventArgs? change;
        lock (_sync)
            change = _disposed || generation != _lifecycleGeneration ? null
                : new(GetStateLocked(GetDevicesSafe()), AudioCaptureChangeKind.DeviceChanged, cancelled);
        RaiseStateChanged(change, generation);
    }

    public void PauseMonitoring()
    {
        long generation;
        PendingDisposal? retired;
        AudioCaptureStateChangedEventArgs? change;
        lock (_lifecycleSync)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_paused && !_resumeWhenAvailable) return;
                _resumeWhenAvailable = false;
                _lastRecoveryDeviceId = null;
                var cancelled = _buffer?.IsSessionActive == true;
                _paused = true;
                generation = ++_lifecycleGeneration;
                _monitoringFailure = null;
                DiscardSessionLocked(clearPreRoll: true);
                retired = DetachCaptureLocked();
                change = new(GetStateLocked(GetDevicesSafe()), AudioCaptureChangeKind.Paused, cancelled);
            }
        }
        RunDisposal(retired);
        RaiseStateChanged(change, generation);
    }

    public void ResumeMonitoring()
    {
        long generation = 0;
        PendingDisposal? failedStart = null;
        AudioCaptureStateChangedEventArgs? change = null;
        try
        {
            lock (_lifecycleSync)
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_paused && _capture is not null) return;
                    var devices = GetDevicesSafe();
                    if (!MicrophoneSelectionPolicy.IsAvailable(_selectedDeviceId, devices))
                        throw new MicrophoneUnavailableException(_selectedDeviceId is null
                            ? "Системный микрофон сейчас недоступен."
                            : "Выбранный микрофон сейчас недоступен.");
                    _paused = false;
                    _resumeWhenAvailable = false;
                    _lastRecoveryDeviceId = null;
                    _monitoringFailure = null;
                    generation = ++_lifecycleGeneration;
                }
                try
                {
                    StartMonitoringLocked(out failedStart);
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _paused = true;
                        _resumeWhenAvailable = true;
                        _monitoringFailure = exception;
                    }
                    throw;
                }
                lock (_sync)
                    if (!_disposed) change = new(GetStateLocked(GetDevicesSafe()),
                        AudioCaptureChangeKind.Resumed, ActiveTakeCancelled: false);
            }
        }
        finally
        {
            RunDisposal(failedStart);
        }
        RaiseStateChanged(change, generation);
    }

    public void Start() => StartCore(null);

    /// <summary>
    /// Запись начинается с <paramref name="pressTimestamp"/> (Stopwatch.GetTimestamp момента нажатия)
    /// минус PreRollDuration, но не раньше конца предыдущей записи.
    /// </summary>
    public void Start(long pressTimestamp) => StartCore(pressTimestamp);

    private void StartCore(long? pressTimestamp)
    {
        var press = pressTimestamp ?? _clock();
        PendingDisposal? retired = null;
        PendingDisposal? failedStart = null;
        AudioCaptureStateChangedEventArgs? change = null;
        MicrophoneUnavailableException? unavailable = null;
        AudioCaptureChangeKind? successfulChange = null;
        long generation = 0;
        try
        {
            lock (_lifecycleSync)
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_buffer?.IsSessionActive == true)
                        throw new InvalidOperationException("Запись уже запущена.");
                    if (_paused && !_resumeWhenAvailable)
                        throw new InvalidOperationException("Запись приостановлена.");
                    // Windows notifications can be delayed/coalesced. Resolve the default anew
                    // before every take; a warm endpoint is not proof that it is still selected.
                    var devices = GetDevicesSafe();
                    if (!MicrophoneSelectionPolicy.IsAvailable(_selectedDeviceId, devices))
                    {
                        unavailable = new MicrophoneUnavailableException(_selectedDeviceId is null
                            ? "Системный микрофон сейчас недоступен." : "Выбранный микрофон сейчас недоступен.");
                        _paused = true;
                        _resumeWhenAvailable = true;
                        _lastRecoveryDeviceId = null;
                        _monitoringFailure = unavailable;
                        DiscardSessionLocked(clearPreRoll: true);
                        retired = DetachCaptureLocked();
                        generation = ++_lifecycleGeneration;
                        change = new(GetStateLocked(devices), AudioCaptureChangeKind.DeviceUnavailable, false);
                    }
                    else
                    {
                        var expected = ResolveActiveDeviceId(_selectedDeviceId, devices);
                        if (_resumeWhenAvailable) successfulChange = AudioCaptureChangeKind.Resumed;
                        else if (_capture is not null && !string.Equals(expected, _activeDeviceId, StringComparison.Ordinal))
                            successfulChange = AudioCaptureChangeKind.DefaultDeviceChanged;
                        if (_paused || _capture is not null
                            && !string.Equals(expected, _activeDeviceId, StringComparison.Ordinal))
                        {
                            DiscardSessionLocked();
                            retired = DetachCaptureLocked(retainPreRoll: !_paused);
                            ++_lifecycleGeneration;
                        }
                        _paused = false;
                        _monitoringFailure = null;
                        generation = _lifecycleGeneration;
                    }
                }
            }
            RunDisposal(retired);
            retired = null;
            if (unavailable is not null) throw unavailable;
            lock (_lifecycleSync)
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_paused || generation != _lifecycleGeneration)
                        throw new OperationCanceledException("Состояние микрофона изменилось во время запуска.");
                    if (_buffer?.IsSessionActive == true)
                        throw new InvalidOperationException("Запись уже запущена.");
                }
                try { StartMonitoringLocked(out failedStart); }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        if (!_disposed && generation == _lifecycleGeneration)
                        {
                            _paused = true;
                            _resumeWhenAvailable = true;
                            _monitoringFailure = exception;
                            change = new(GetStateLocked(GetDevicesSafe()), AudioCaptureChangeKind.DeviceUnavailable, false);
                        }
                    }
                    throw;
                }
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_paused || generation != _lifecycleGeneration || _monitoringFailure is not null)
                        throw new InvalidOperationException("Микрофон недоступен.", _monitoringFailure);
                    var format = _captureFormat ?? throw new InvalidOperationException("Формат микрофона не определён.");
                    _spectrum.Reset();
                    (_buffer ?? throw new InvalidOperationException("Буфер микрофона не создан."))
                        .Begin(Math.Max(format.AverageBytesPerSecond * 2, 4096), press, PreRollDuration);
                    ++_sessionGeneration;
                    _stopRequested = false;
                    _smoothedLevel = 0;
                    if (successfulChange is { } kind)
                        change = new(GetStateLocked(GetDevicesSafe()), kind, false);
                }
            }
        }
        finally
        {
            RunDisposal(retired);
            RunDisposal(failedStart);
            RaiseStateChanged(change, generation);
        }
    }

    /// <summary>
    /// Заменяет нулями содержимое буферов, пришедших до конца акустического хвоста сигнала.
    /// Длина и отметки времени сохраняются, поэтому хронология кольца не рвётся, а звук сигнала
    /// не попадает в принятое окно ASR. Захват остаётся активным: задержки UI и хоткея нет.
    /// </summary>
    public void SuppressFeedbackAudio(TimeSpan duration)
    {
        lock (_sync)
        {
            if (_disposed || duration <= TimeSpan.Zero)
            {
                return;
            }

            // Bound feedback acoustic suppression to the actual sound tone duration (max 40 ms)
            // so human speech is never clipped or delayed.
            var effectiveMs = Math.Min(duration.TotalMilliseconds, 40d);
            var now = _clock();
            var deadline = now + (long)Math.Ceiling(
                effectiveMs / 1000d * System.Diagnostics.Stopwatch.Frequency);
            _feedbackSuppressedUntilTimestamp = Math.Max(_feedbackSuppressedUntilTimestamp, deadline);
            _smoothedLevel = 0;
        }
    }

    public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken) =>
        StopCoreAsync(null, cancellationToken);

    /// <summary>
    /// Хвост отсчитывается от <paramref name="releaseTimestamp"/> (Stopwatch.GetTimestamp момента
    /// отпускания), а не от момента вызова.
    /// </summary>
    public Task<AudioCaptureResult> StopAsync(long releaseTimestamp, CancellationToken cancellationToken) =>
        StopCoreAsync(releaseTimestamp, cancellationToken);

    private async Task<AudioCaptureResult> StopCoreAsync(long? releaseTimestamp, CancellationToken cancellationToken)
    {
        long generation;
        TaskCompletionSource tailWait;
        TimeSpan fallback;
        lock (_sync)
        {
            if (_buffer?.IsSessionActive != true)
            {
                throw new InvalidOperationException("Запись не запущена.");
            }
            if (_stopRequested)
            {
                throw new InvalidOperationException("Остановка записи уже выполняется.");
            }
            _stopRequested = true;
            generation = _sessionGeneration;
            _tailRelease = releaseTimestamp ?? _clock();
            tailWait = _tailWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Данные после отпускания могли прийти ещё до вызова: проверяем сразу.
            CheckTailLocked();
            var elapsed = TimeSpan.FromSeconds(Math.Max(0, _clock() - _tailRelease)
                / (double)System.Diagnostics.Stopwatch.Frequency);
            fallback = (ReleaseTailMaximum > elapsed ? ReleaseTailMaximum - elapsed : TimeSpan.Zero)
                + ReleaseTailStallGrace;
        }

        try
        {
            // Сохраняем окончание слова/согласный: ждём минимум хвоста и тишину (см. CheckTailLocked).
            // WASAPI остаётся тёплым, поэтому следующее нажатие не переоткрывает устройство.
            try { await tailWait.Task.WaitAsync(fallback, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { /* устройство замолчало: берём то, что есть */ }

            CapturedPcm completed;
            WaveFormat format;
            lock (_sync)
            {
                if (generation != _sessionGeneration || _buffer?.IsSessionActive != true)
                    throw new OperationCanceledException("Запись была отменена или заменена.", cancellationToken);
                if (_monitoringFailure is not null)
                {
                    throw new InvalidOperationException("Микрофон отключился во время записи.", _monitoringFailure);
                }
                format = _captureFormat ?? throw new InvalidOperationException("Формат микрофона потерян.");
                completed = (_buffer ?? throw new OperationCanceledException(cancellationToken)).Complete();
                _stopRequested = false;
                _tailWait = null;
            }

            var preRollBytes = completed.PreRollBytes;
            var samples = await ConvertCompletedTakeAsync(completed, format, cancellationToken).ConfigureAwait(false);

            var preRollSamples = (int)Math.Min(
                samples.Length,
                Math.Round(preRollBytes / (double)Math.Max(1, format.AverageBytesPerSecond) * OutputSampleRate));
            var activity = Analyze(samples, preRollSamples);
            var path = _persistCompletedTake
                ? await PersistTakeAsync(samples, cancellationToken).ConfigureAwait(false)
                : null;

            var handler = SamplesAvailable;
            if (handler is not null && samples.Length > 0)
            {
                try
                {
                    handler(this, samples);
                }
                catch (Exception exception)
                {
                    AppLog.Write("Sample subscriber threw after capture", exception);
                }
            }

            return new AudioCaptureResult(
                path,
                samples,
                OutputSampleRate,
                activity.HasSpeech,
                activity.Duration,
                activity.DetectedSpeech,
                activity.PeakDecibels,
                DescribeRejection(activity.Rejection));
        }
        catch
        {
            lock (_sync)
            {
                if (generation == _sessionGeneration) DiscardSessionLocked();
            }
            throw;
        }
    }

    public Task<string?> CancelAsync()
    {
        lock (_sync)
        {
            DiscardSessionLocked();
        }
        return Task.FromResult<string?>(null);
    }

    // The lifecycle lock serializes open/start; cleanup is always returned to the caller.
    // A partially started endpoint may already have a callback waiting for lifecycle state.
    private void StartMonitoringLocked(out PendingDisposal? failedStart)
    {
        failedStart = null;
        AudioCaptureEndpoint? endpoint = null;
        bool published = false;
        string? selected;
        long generation;
        lock (_sync)
        {
            if (_capture is not null) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused) throw new InvalidOperationException("Запись приостановлена.");
            selected = _selectedDeviceId;
            generation = _lifecycleGeneration;
        }
        try
        {
            endpoint = _captureFactory(selected);
            var capture = endpoint.Capture;
            var format = capture.WaveFormat;
            var preRollBytes = AlignToBlock(
                (int)Math.Ceiling(format.AverageBytesPerSecond * RingDuration.TotalSeconds), format.BlockAlign);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_paused || generation != _lifecycleGeneration)
                    throw new OperationCanceledException("Состояние микрофона изменилось во время запуска.");
                _captureFormat = format;
                _buffer = TakeRetainedBuffer(endpoint.DeviceId, format)
                    ?? new CaptureSessionBuffer(preRollBytes, format.BlockAlign, format);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _capture = capture;
                _captureDevice = endpoint.Device;
                _activeDeviceId = endpoint.DeviceId;
                _monitoringFailure = null;
                published = true;
            }
            capture.StartRecording();
            lock (_sync)
            {
                if (!ReferenceEquals(capture, _capture))
                    throw new InvalidOperationException("Микрофон остановился во время запуска.", _monitoringFailure);
                _resumeWhenAvailable = false;
                _lastRecoveryDeviceId = null;
            }
            AppLog.Write($"WASAPI microphone warm: rate={format.SampleRate}, bits={format.BitsPerSample}, channels={format.Channels}");
        }
        catch
        {
            lock (_sync)
            {
                if (endpoint is not null && ReferenceEquals(endpoint.Capture, _capture))
                {
                    _lastRecoveryDeviceId = endpoint.DeviceId;
                    failedStart = DetachCaptureLocked();
                }
                else if (endpoint is not null && !published)
                    failedStart = RegisterDisposalLocked(() => DisposeEndpoint(endpoint));
            }
            throw;
        }
    }

    private void StartMonitoringIfCurrent(long generation)
    {
        PendingDisposal? failedStart = null;
        try
        {
            lock (_lifecycleSync)
            {
                lock (_sync)
                    if (_disposed || _paused || generation != _lifecycleGeneration) return;
                StartMonitoringLocked(out failedStart);
            }
        }
        finally
        {
            RunDisposal(failedStart);
        }
    }

    private AudioCaptureEndpoint OpenWasapiCapture(string? selectedDeviceId)
    {
        var device = _deviceCatalog.OpenCaptureDevice(selectedDeviceId);
        try
        {
            // Событийная синхронизация и короткий буфер: по умолчанию NAudio опрашивает раз в
            // 100 мс, из-за чего после отпускания в запись попадало лишь ~250-300 мс хвоста.
            return new(new WasapiCapture(device, true, CaptureBufferMilliseconds)
                { ShareMode = AudioClientShareMode.Shared }, device, device.ID);
        }
        catch
        {
            device.Dispose(); // No capture thread exists until StartRecording.
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var owners = _callbackOwners ??= [];
        owners.Add(this);
        try { ProcessDataAvailable(sender, args); }
        finally { owners.RemoveAt(owners.Count - 1); }
    }

    private void ProcessDataAvailable(object? sender, WaveInEventArgs args)
    {
        WaveFormat? format;
        bool measureSpectrum;
        long timestamp;
        lock (_sync)
        {
            // Unsubscribing cannot retract a callback that was already queued by the old WASAPI
            // endpoint. Identity is checked under the same lock as switch/clear so old samples
            // can neither enter the replacement buffer nor be measured with its format.
            if (!IsCurrentCaptureCallback(sender, _capture, _disposed, args.BytesRecorded))
            {
                return;
            }
            timestamp = _clock();
            if (timestamp < _feedbackSuppressedUntilTimestamp)
            {
                // Буфер не выбрасываем: кладём нули той же длины с той же отметкой времени.
                AppendSilenceLocked(args.BytesRecorded, timestamp);
                return;
            }
            format = _captureFormat;
            measureSpectrum = _buffer?.IsSessionActive == true;
        }

        double rms = 0, peak = 0, rawBass = 0, rawMid = 0, rawTreble = 0;
        var measured = format is not null && PcmLevelMeter.TryMeasureTimbre(
            args.Buffer, args.BytesRecorded, format,
            out rms, out peak, out rawBass, out rawMid, out rawTreble);
        lock (_sync)
        {
            if (!IsCurrentCaptureCallback(sender, _capture, _disposed, args.BytesRecorded))
            {
                return;
            }
            var silent = measured && ClassifyChunkLocked(rms, rawTreble);
            _buffer?.Append(args.Buffer.AsSpan(0, args.BytesRecorded), timestamp, silent);
            CheckTailLocked();
        }
        if (!measured)
        {
            return;
        }

        var rmsLevel = DbToLevel(rms, -62, -14);
        var spectrum = measureSpectrum ? _spectrum.Measure(args.Buffer, args.BytesRecorded, format) : default;
        var peakLevel = DbToLevel(peak, -56, -7);
        var level = (float)Math.Clamp((rmsLevel * 0.76) + (peakLevel * 0.24), 0, 1);

        var bassLevel = (float)Math.Clamp(DbToLevel(rawBass, -60, -16), 0, 1);
        var midLevel = (float)Math.Clamp(DbToLevel(rawMid, -62, -18), 0, 1);
        var trebleLevel = (float)Math.Clamp(DbToLevel(rawTreble, -58, -12), 0, 1);

        float smoothedLevel;
        float smoothedBass;
        float smoothedMid;
        float smoothedTreble;
        lock (_sync)
        {
            // The endpoint may have changed while level calculation ran outside the lock.
            if (!IsCurrentCaptureCallback(sender, _capture, _disposed, args.BytesRecorded))
            {
                return;
            }
            var smoothing = level > _smoothedLevel ? 0.62f : 0.20f;
            _smoothedLevel += (level - _smoothedLevel) * smoothing;
            _smoothedBass += (bassLevel - _smoothedBass) * smoothing;
            _smoothedMid += (midLevel - _smoothedMid) * smoothing;
            _smoothedTreble += (trebleLevel - _smoothedTreble) * (level > _smoothedLevel ? 0.75f : 0.25f);

            smoothedLevel = _smoothedLevel;
            smoothedBass = _smoothedBass;
            smoothedMid = _smoothedMid;
            smoothedTreble = _smoothedTreble;
        }
        try
        {
            LevelChanged?.Invoke(this, smoothedLevel);
            TimbreChanged?.Invoke(this, new VoiceTimbreLevel(smoothedLevel, smoothedBass, smoothedMid, smoothedTreble)
                { Spectrum = spectrum });
        }
        catch (Exception exception)
        {
            // A UI observer must never terminate the WASAPI callback thread.
            AppLog.Write("Microphone level subscriber threw", exception);
        }
    }

    // Нули вместо звука: нейтральны для PCM-16/24/32 и float; метка «тишина» для адаптивного хвоста.
    private void AppendSilenceLocked(int bytesRecorded, long timestamp)
    {
        if (_buffer is null) return;
        var zeros = ArrayPool<byte>.Shared.Rent(bytesRecorded);
        try
        {
            Array.Clear(zeros, 0, bytesRecorded);
            _buffer.Append(zeros.AsSpan(0, bytesRecorded), timestamp, silent: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
        _smoothedLevel = 0;
        CheckTailLocked();
    }

    // Тишина = и RMS, и верхние частоты (шипящие затухают последними) не выше шума + SilenceMarginDb.
    private bool ClassifyChunkLocked(double rms, double treble)
    {
        var sessionActive = _buffer?.IsSessionActive == true;
        var rmsDb = Math.Max(20 * Math.Log10(Math.Max(rms, 0.000001)), MinFloorDb);
        var trebleDb = Math.Max(20 * Math.Log10(Math.Max(treble, 0.000001)), MinFloorDb);
        _noiseFloorDb = TrackFloor(_noiseFloorDb, rmsDb, sessionActive);
        _trebleFloorDb = TrackFloor(_trebleFloorDb, trebleDb, sessionActive);
        return IsSilent(rmsDb, _noiseFloorDb!.Value, trebleDb, _trebleFloorDb!.Value);
    }

    internal const double MinFloorDb = -90;

    internal static double TrackFloor(double? floor, double levelDb, bool sessionActive)
    {
        if (floor is not { } current) return levelDb;
        if (levelDb < current) return levelDb;
        // Растёт медленно и только вне записи (постоянная времени ~1 с на буферах по 20 мс).
        return sessionActive ? current : current + ((levelDb - current) * 0.02);
    }

    internal static bool IsSilent(double rmsDb, double rmsFloorDb, double trebleDb, double trebleFloorDb) =>
        rmsDb < rmsFloorDb + SilenceMarginDb && trebleDb < trebleFloorDb + SilenceMarginDb;

    // Завершает ожидание хвоста: прошёл минимум от отпускания и накоплена тишина, либо достигнут максимум.
    private void CheckTailLocked()
    {
        if (_tailWait is { } wait && _buffer is { IsSessionActive: true } buffer
            && buffer.IsTailComplete(_tailRelease, ReleaseTailMinimum, ReleaseTailSilence, ReleaseTailMaximum))
        {
            wait.TrySetResult();
        }
    }

    private CaptureSessionBuffer? TakeRetainedBuffer(string deviceId, WaveFormat format)
    {
        var buffer = _retainedBuffer;
        var reuse = buffer is not null && string.Equals(_retainedDeviceId, deviceId, StringComparison.Ordinal)
            && _retainedFormat is { } previous && previous.Encoding == format.Encoding
            && previous.SampleRate == format.SampleRate && previous.Channels == format.Channels
            && previous.BitsPerSample == format.BitsPerSample;
        _retainedBuffer = null;
        _retainedDeviceId = null;
        _retainedFormat = null;
        if (reuse) return buffer;
        buffer?.Clear();
        return null;
    }

    internal static bool IsCurrentCaptureCallback(
        object? sender,
        object? currentCapture,
        bool disposed,
        int bytesRecorded) =>
        !disposed
        && bytesRecorded > 0
        && currentCapture is not null
        && ReferenceEquals(sender, currentCapture);

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        var owners = _callbackOwners ??= [];
        owners.Add(this);
        try { ProcessRecordingStopped(sender, args); }
        finally { owners.RemoveAt(owners.Count - 1); }
    }

    private void ProcessRecordingStopped(object? sender, StoppedEventArgs args)
    {
        long generation;
        PendingDisposal? retired;
        AudioCaptureStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(sender, _capture)) return;
            var cancelled = _buffer?.IsSessionActive == true;
            _monitoringFailure = args.Exception ?? new InvalidOperationException("WASAPI capture stopped unexpectedly.");
            _resumeWhenAvailable = !_paused || _resumeWhenAvailable;
            _paused = true;
            generation = ++_lifecycleGeneration;
            DiscardSessionLocked(clearPreRoll: true);
            retired = DetachCaptureLocked();
            change = new(GetStateLocked(GetDevicesSafe()), AudioCaptureChangeKind.DeviceUnavailable,
                cancelled, "Микрофон отключён. Выберите доступное устройство и возобновите запись.");
        }
        // Offload Join from both this callback and any synchronous subscriber re-entering Dispose.
        RunDisposal(retired);
        RaiseStateChanged(change, generation);
    }

    private void OnDevicesChanged(object? sender, EventArgs args)
    {
        PendingDisposal? retired = null;
        AudioCaptureStateChangedEventArgs? change;
        long generation;
        bool restart = false;
        lock (_lifecycleSync)
        {
            lock (_sync)
            {
                if (_disposed) return;
                var devices = GetDevicesSafe();
                var available = MicrophoneSelectionPolicy.IsAvailable(_selectedDeviceId, devices);
                var expected = ResolveActiveDeviceId(_selectedDeviceId, devices);
                var cancelled = _buffer?.IsSessionActive == true;
                if (!available)
                {
                    _resumeWhenAvailable = !_paused || _resumeWhenAvailable;
                    _paused = true;
                    _lastRecoveryDeviceId = null;
                    _monitoringFailure = new MicrophoneUnavailableException(_selectedDeviceId is null
                        ? "Системный микрофон сейчас недоступен." : "Выбранный микрофон сейчас недоступен.");
                    DiscardSessionLocked(clearPreRoll: true);
                    retired = DetachCaptureLocked();
                    ++_lifecycleGeneration;
                    change = new(GetStateLocked(devices), AudioCaptureChangeKind.DeviceUnavailable, cancelled,
                        "Микрофон отключён. Подключите устройство или выберите другое.");
                }
                else if ((_resumeWhenAvailable
                    && !string.Equals(expected, _lastRecoveryDeviceId, StringComparison.Ordinal))
                    || (!_paused && _capture is not null && _selectedDeviceId is null
                    && !string.Equals(expected, _activeDeviceId, StringComparison.Ordinal)))
                {
                    var recovering = _resumeWhenAvailable;
                    _paused = false;
                    _monitoringFailure = null;
                    _lastRecoveryDeviceId = expected;
                    DiscardSessionLocked();
                    retired = DetachCaptureLocked(retainPreRoll: true);
                    ++_lifecycleGeneration;
                    restart = true;
                    change = new(GetStateLocked(devices), recovering ? AudioCaptureChangeKind.Resumed
                        : AudioCaptureChangeKind.DefaultDeviceChanged, cancelled);
                }
                else
                {
                    if (!_resumeWhenAvailable && _paused && _monitoringFailure is MicrophoneUnavailableException)
                        _monitoringFailure = null;
                    change = new(GetStateLocked(devices), AudioCaptureChangeKind.InventoryChanged, false);
                }
                generation = _lifecycleGeneration;
            }
        }
        RunDisposal(retired);
        if (restart)
        {
            try
            {
                StartMonitoringIfCurrent(generation);
                lock (_sync)
                    change = _disposed || generation != _lifecycleGeneration ? null
                        : change! with { State = GetStateLocked(GetDevicesSafe()) };
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (_disposed || generation != _lifecycleGeneration) return;
                    _paused = true;
                    _resumeWhenAvailable = true;
                    _monitoringFailure = exception;
                    change = new(GetStateLocked(GetDevicesSafe()), AudioCaptureChangeKind.DeviceUnavailable,
                        change?.ActiveTakeCancelled ?? false,
                        "Не удалось открыть микрофон. Повторите запись или выберите другое устройство.");
                }
            }
        }
        RaiseStateChanged(change, generation);
    }

    private IReadOnlyList<MicrophoneDeviceInfo> GetDevicesSafe()
    {
        try
        {
            return _deviceCatalog.GetActiveDevices();
        }
        catch (Exception exception) when (exception is COMException or MicrophoneUnavailableException)
        {
            AppLog.Write("Could not enumerate capture endpoints", exception);
            return [];
        }
    }

    private AudioCaptureState GetStateLocked(IReadOnlyList<MicrophoneDeviceInfo> devices)
    {
        var available = MicrophoneSelectionPolicy.IsAvailable(_selectedDeviceId, devices);
        return new AudioCaptureState(
            _selectedDeviceId,
            MicrophoneSelectionPolicy.DisplayName(_selectedDeviceId, devices),
            _paused,
            _capture is not null && !_paused,
            available,
            !available || _monitoringFailure is not null ? "device-unavailable" : null)
            { IsTransientlyUnavailable = _resumeWhenAvailable };
    }

    private static string? ResolveActiveDeviceId(
        string? selectedDeviceId,
        IReadOnlyList<MicrophoneDeviceInfo> devices) =>
        selectedDeviceId ?? devices.FirstOrDefault(device => device.IsDefault)?.Id;

    private void RaiseStateChanged(AudioCaptureStateChangedEventArgs? change, long generation)
    {
        lock (_sync)
            if (_disposed || change is null || generation != _lifecycleGeneration) return;
        try
        {
            StateChanged?.Invoke(this, change);
        }
        catch (Exception exception)
        {
            AppLog.Write("Microphone state subscriber threw", exception);
        }
    }

    /// <summary>
    /// Uses the exact production speech gate for an already captured benchmark WAV. Exposed only
    /// inside the assembly so the offline harness can attribute a lost take without changing the
    /// interactive path or persisting another copy of the audio.
    /// </summary>
    internal static SpeechActivitySnapshot Analyze(float[] samples, int preRollSamples)
    {
        var detector = new SpeechActivityDetector();
        var noiseFloor = AudioSignalAnalyzer.EstimateNoiseFloorDb(samples, preRollSamples, OutputSampleRate);
        // Pre-roll can contain the very first word we intentionally preserved. Use the quieter
        // boundary as background evidence so that word cannot raise its own acceptance threshold.
        // This only calibrates session acceptance; every original sample still reaches ASR.
        var tailSamples = Math.Min(samples.Length, (int)(ReleaseTailMinimum.TotalSeconds * OutputSampleRate));
        var tailFloor = AudioSignalAnalyzer.EstimateNoiseFloorDb(samples.AsSpan(samples.Length - tailSamples), OutputSampleRate);
        if (tailFloor is { } tail && (noiseFloor is null || tail < noiseFloor.Value))
        {
            noiseFloor = tail;
        }
        detector.Reset(noiseFloor);
        const int frameSamples = OutputSampleRate / 50; // 20 ms
        for (var offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var count = Math.Min(frameSamples, samples.Length - offset);
            double sum = 0;
            double peak = 0;
            for (var index = 0; index < count; index++)
            {
                var sample = samples[offset + index];
                sum += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            detector.Process(Math.Sqrt(sum / Math.Max(1, count)), peak, count * 1000d / OutputSampleRate);
        }
        return detector.Snapshot();
    }

    internal static async Task<float[]> ConvertCompletedTakeAsync(
        CapturedPcm completed, WaveFormat format, CancellationToken cancellationToken)
    {
        using (completed)
        {
            // Ownership covers cancellation before Task.Run starts as well as conversion failure.
            return await Task.Run(
                () => completed.Convert(format), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static float[] ConvertToMono16Khz(ReadOnlyMemory<byte> raw, WaveFormat format)
    {
        if (raw.Length == 0)
        {
            return [];
        }

        var readableFormat = format.AsStandardWaveFormat();
        if (!MemoryMarshal.TryGetArray(raw, out var segment))
        {
            segment = new ArraySegment<byte>(raw.ToArray());
        }
        using var memory = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        using var source = new RawSourceWaveStream(memory, readableFormat);
        ISampleProvider provider = source.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1)
        {
            provider = new DownmixToMonoSampleProvider(provider);
        }
        if (provider.WaveFormat.SampleRate != OutputSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
        }

        var expected = Math.Max(1024, (int)Math.Ceiling(
            raw.Length / (double)Math.Max(1, format.AverageBytesPerSecond) * OutputSampleRate) + 512);
        var output = new ArrayBufferWriter<float>(expected);
        var buffer = ArrayPool<float>.Shared.Rent(ConversionBlockSamples);
        try
        {
            int read;
            while ((read = provider.Read(buffer, 0, ConversionBlockSamples)) > 0)
            {
                buffer.AsSpan(0, read).CopyTo(output.GetSpan(read));
                output.Advance(read);
            }
            return output.WrittenSpan.ToArray();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<string> PersistTakeAsync(float[] samples, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Egoist.Voice.Core.VoiceRuntimeProfile.DataRoot, "Temp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"voice-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var writer = new WaveFileWriter(path, new WaveFormat(OutputSampleRate, 16, 1));
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                var sampleOffset = 0;
                while (sampleOffset < samples.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length / 2, samples.Length - sampleOffset);
                    for (var index = 0; index < count; index++)
                    {
                        var pcm = (short)Math.Round(Math.Clamp(samples[sampleOffset + index], -1, 1) * short.MaxValue);
                        buffer[index * 2] = (byte)pcm;
                        buffer[(index * 2) + 1] = (byte)(pcm >> 8);
                    }
                    writer.Write(buffer, 0, count * 2);
                    sampleOffset += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }, cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static string? DescribeRejection(SpeechRejection rejection) => rejection switch
    {
        SpeechRejection.MicrophoneSilent => "Микрофон молчит",
        SpeechRejection.TooQuiet => "Слишком тихо",
        SpeechRejection.TooShort => "Слишком коротко",
        SpeechRejection.NoAudio => "Нет звука",
        _ => null
    };

    internal static float DbToLevel(double amplitude, double floorDb, double ceilingDb)
    {
        var decibels = 20 * Math.Log10(Math.Max(amplitude, 0.000001));
        return (float)Math.Clamp((decibels - floorDb) / (ceilingDb - floorDb), 0, 1);
    }

    private static int AlignToBlock(int bytes, int blockAlign) =>
        Math.Max(blockAlign, bytes - (bytes % Math.Max(1, blockAlign)));

    private void DiscardSessionLocked(bool clearPreRoll = false)
    {
        ++_sessionGeneration;
        _buffer?.CancelSession();
        _spectrum.Reset();
        if (clearPreRoll)
        {
            _buffer?.Clear();
        }
        _stopRequested = false;
        // StopAsync проснётся и увидит смену поколения.
        _tailWait?.TrySetResult();
        _tailWait = null;
    }

    private sealed record PendingDisposal(Action Dispose, TaskCompletionSource Completion);

    private PendingDisposal RegisterDisposalLocked(Action dispose)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDisposals.Add(completion.Task);
        return new(dispose, completion);
    }

    private PendingDisposal? DetachCaptureLocked(bool retainPreRoll = false)
    {
        if (_capture is null) return null;
        var endpoint = new AudioCaptureEndpoint(_capture, _captureDevice, _activeDeviceId ?? string.Empty);
        var oldFormat = _captureFormat;
        // Reserve completion while ownership is still protected: a concurrent ordinary Dispose
        // must see even retirement that has not yet been scheduled by the source callback.
        var retired = RegisterDisposalLocked(() => DisposeEndpoint(endpoint));
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture = null;
        _captureDevice = null;
        _activeDeviceId = null;
        _captureFormat = null;
        ++_sessionGeneration;
        // Перезапуск на то же устройство не должен терять кольцо: оно переиспользуется, если
        // идентификатор и формат совпадут (иначе TakeRetainedBuffer очистит его).
        _retainedBuffer?.Clear();
        _retainedBuffer = null;
        _retainedFormat = null;
        _retainedDeviceId = null;
        if (retainPreRoll && _buffer is not null)
        {
            _retainedBuffer = _buffer;
            _retainedDeviceId = endpoint.DeviceId;
            _retainedFormat = oldFormat;
        }
        else
        {
            _buffer?.Clear();
        }
        _buffer = null;
        return retired;
    }

    private static void DisposeEndpoint(AudioCaptureEndpoint endpoint)
    {
        try
        {
            try { endpoint.Capture.StopRecording(); }
            catch (Exception exception) { AppLog.Write("Could not stop retired capture", exception); }
            endpoint.Capture.Dispose();
        }
        finally
        {
            endpoint.Device?.Dispose();
        }
    }

    private void RunDisposal(PendingDisposal? pending)
    {
        if (pending is null) return;
        // ThreadStatic does not flow into the worker (unlike AsyncLocal). One work item is
        // created per detached owned resource, never per audio buffer or level notification.
        if (_callbackOwners?.Contains(this) == true)
            _ = Task.Run(() => CompleteDisposal(pending));
        else
            CompleteDisposal(pending);
    }

    private void CompleteDisposal(PendingDisposal pending)
    {
        try { pending.Dispose(); }
        catch (Exception exception) { AppLog.Write("Retired capture cleanup failed", exception); }
        finally
        {
            pending.Completion.TrySetResult();
            lock (_sync) _pendingDisposals.Remove(pending.Completion.Task);
        }
    }

    public void Dispose()
    {
        PendingDisposal? retired = null;
        PendingDisposal? catalog = null;
        lock (_lifecycleSync)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    ++_lifecycleGeneration;
                    _deviceCatalog.DevicesChanged -= OnDevicesChanged;
                    DiscardSessionLocked(clearPreRoll: true);
                    retired = DetachCaptureLocked();
                    _retainedBuffer?.Clear();
                    _retainedBuffer = null;
                }
                if (_ownsDeviceCatalog && !_catalogDisposalStarted)
                {
                    _catalogDisposalStarted = true;
                    catalog = RegisterDisposalLocked(_deviceCatalog.Dispose);
                }
            }
        }
        RunDisposal(retired);
        RunDisposal(catalog);
        // A synchronous observer is still on the capture thread. Waiting here would indirectly
        // self-join through the cleanup worker. A later ordinary Dispose may drain, even if the
        // logical disposed flag has already been set by that observer.
        if (_callbackOwners?.Contains(this) == true) return;
        Task[] pending;
        lock (_sync) pending = _pendingDisposals.ToArray();
        Task.WhenAll(pending).GetAwaiter().GetResult();
    }


}

// Internal factory seam: production owns a shared WasapiCapture and its MMDevice;
// tests supply a controllable IWaveIn without opening a microphone.
internal sealed record AudioCaptureEndpoint(IWaveIn Capture, IDisposable? Device, string DeviceId);

internal sealed class CapturedPcm(byte[] buffer, int length, int preRollBytes) : IDisposable
{
    private byte[]? _buffer = buffer;
    private StreamingCaptureConverter? _converter;

    internal CapturedPcm(StreamingCaptureConverter converter, int preRollBytes)
        : this([], 0, preRollBytes) => _converter = converter;

    internal ReadOnlyMemory<byte> Bytes =>
        (_buffer ?? throw new ObjectDisposedException(nameof(CapturedPcm))).AsMemory(0, length);
    internal int PreRollBytes { get; } = preRollBytes;

    internal float[] Convert(WaveFormat format) => _converter is { } converter
        ? converter.Complete()
        : AudioCaptureService.ConvertToMono16Khz(Bytes, format);

    public void Dispose()
    {
        Interlocked.Exchange(ref _converter, null)?.Dispose();
        var owned = Interlocked.Exchange(ref _buffer, null);
        if (owned is not null)
        {
            Array.Clear(owned, 0, length);
        }
    }
}

internal sealed class CaptureSessionBuffer
{
    private readonly PcmByteRingBuffer _preRoll;
    private readonly WaveFormat? _format;
    private readonly Queue<ChunkMark> _marks = new();
    private readonly int _blockAlign;
    private readonly int _bytesPerSecond;
    private StreamingCaptureConverter? _converter;
    private MemoryStream? _session;
    private int _sessionPreRollBytes;
    // Абсолютная позиция конца последней завершённой записи: следующая не заходит раньше неё.
    private long _lastSessionEnd;

    // Отметка одного колбэка: диапазон байтов кольца и момент его прихода.
    private readonly record struct ChunkMark(long StartOffset, long EndOffset, long StartTimestamp, long EndTimestamp, bool Silent);

    internal CaptureSessionBuffer(int preRollCapacity, int blockAlign, WaveFormat? format = null)
    {
        _preRoll = new PcmByteRingBuffer(preRollCapacity, blockAlign);
        _format = format;
        _blockAlign = Math.Max(1, blockAlign);
        _bytesPerSecond = format?.AverageBytesPerSecond ?? 0;
    }

    internal bool IsSessionActive => _session is not null || _converter is not null;
    internal long RetainedSampleBytes => _converter?.RetainedSampleBytes ?? _session?.Length ?? 0;

    internal void Begin(int initialCapacity) => BeginFrom(initialCapacity, _preRoll.OldestOffset);

    /// <summary>
    /// Начало записи — pressTimestamp минус preRoll, но не раньше конца предыдущей записи и не
    /// раньше самого старого байта кольца.
    /// </summary>
    internal void Begin(int initialCapacity, long pressTimestamp, TimeSpan preRoll)
    {
        var from = pressTimestamp - (long)(preRoll.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        BeginFrom(initialCapacity, Math.Max(OffsetAt(from), _lastSessionEnd));
    }

    // Байтовая позиция кольца, соответствующая моменту времени (с интерполяцией внутри колбэка).
    private long OffsetAt(long timestamp)
    {
        foreach (var mark in _marks)
        {
            if (timestamp >= mark.EndTimestamp) continue;
            if (timestamp <= mark.StartTimestamp || _bytesPerSecond <= 0) return mark.StartOffset;
            var bytes = (long)((timestamp - mark.StartTimestamp)
                * (double)_bytesPerSecond / System.Diagnostics.Stopwatch.Frequency);
            return Math.Min(mark.EndOffset, mark.StartOffset + (bytes - (bytes % _blockAlign)));
        }
        return _preRoll.TotalWritten;
    }

    /// <summary>
    /// Хвост закончен: от последнего данных прошло не меньше minimum после отпускания и подряд
    /// накоплено не меньше silence тишины, либо прошёл maximum.
    /// </summary>
    internal bool IsTailComplete(long releaseTimestamp, TimeSpan minimum, TimeSpan silence, TimeSpan maximum)
    {
        if (_marks.Count == 0) return false;
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var elapsed = (_marks.Last().EndTimestamp - releaseTimestamp) / frequency;
        if (elapsed >= maximum.TotalSeconds) return true;
        if (elapsed < minimum.TotalSeconds) return false;
        long silent = 0;
        foreach (var mark in _marks)
        {
            // Колбэк, начавшийся до отпускания, содержит ещё голос: в тишину не засчитываем.
            if (mark.StartTimestamp < releaseTimestamp) continue;
            silent = mark.Silent ? silent + (mark.EndTimestamp - mark.StartTimestamp) : 0;
        }
        return silent / frequency >= silence.TotalSeconds;
    }

    private void BeginFrom(int initialCapacity, long fromOffset)
    {
        if (IsSessionActive)
        {
            throw new InvalidOperationException("Session already active.");
        }
        var prefix = _preRoll.Snapshot(fromOffset);
        try
        {
            if (_format is not null)
            {
                _converter = new StreamingCaptureConverter(_format);
                _converter.Append(prefix);
            }
            else
            {
                _session = new MemoryStream(Math.Max(initialCapacity, prefix.Length + 4096));
                _session.Write(prefix);
            }
            _sessionPreRollBytes = prefix.Length;
        }
        finally
        {
            Array.Clear(prefix);
        }
    }

    internal void Append(ReadOnlySpan<byte> bytes) =>
        Append(bytes, System.Diagnostics.Stopwatch.GetTimestamp(), silent: false);

    internal void Append(ReadOnlySpan<byte> bytes, long endTimestamp, bool silent)
    {
        _session?.Write(bytes);
        _converter?.Append(bytes);
        var start = _preRoll.TotalWritten;
        _preRoll.Write(bytes);
        var written = _preRoll.TotalWritten - start;
        if (written > 0)
        {
            var duration = _bytesPerSecond > 0
                ? (long)(written * (double)System.Diagnostics.Stopwatch.Frequency / _bytesPerSecond) : 0;
            _marks.Enqueue(new(start, start + written, endTimestamp - duration, endTimestamp, silent));
        }
        while (_marks.Count > 0 && _marks.Peek().EndOffset <= _preRoll.OldestOffset) _marks.Dequeue();
    }

    internal CapturedPcm Complete()
    {
        if (_converter is { } converter)
        {
            var completed = new CapturedPcm(converter, _sessionPreRollBytes);
            _converter = null;
            _sessionPreRollBytes = 0;
            _lastSessionEnd = _preRoll.TotalWritten;
            return completed;
        }
        var session = _session ?? throw new InvalidOperationException("Session is not active.");
        var result = new CapturedPcm(session.GetBuffer(), checked((int)session.Length), _sessionPreRollBytes);
        // The completed take now owns this buffer; clearing it here would erase the ASR input.
        DisposeSession(clear: false);
        _lastSessionEnd = _preRoll.TotalWritten;
        return result;
    }

    internal void CancelSession() => DisposeSession(clear: true);

    internal void Clear()
    {
        DisposeSession(clear: true);
        _preRoll.Clear();
        _marks.Clear();
    }

    internal void DiscardAudioPreservingSession()
    {
        var wasActive = IsSessionActive;
        DisposeSession(clear: true);
        _preRoll.Clear();
        _marks.Clear();
        if (wasActive)
        {
            Begin(4096);
        }
    }

    private void DisposeSession(bool clear)
    {
        _converter?.Dispose();
        _converter = null;
        if (_session is not null)
        {
            if (clear && _session.TryGetBuffer(out var buffer))
            {
                buffer.AsSpan(0, (int)_session.Length).Clear();
            }
            _session.Dispose();
        }
        _session = null;
        _sessionPreRollBytes = 0;
    }
}

/// <summary>
/// Runs the same NAudio decoder, downmix and WDL settings as completed-take conversion.
/// WDL receives a full requested input block before processing; only Complete may flush a
/// short block. Callback boundaries therefore cannot add silence or restart the filter.
/// </summary>
internal sealed class StreamingCaptureConverter : IDisposable
{
    private const int DecodeFrames = 4096;
    private readonly CaptureChunkProvider _source;
    private readonly ISampleProvider _decoder;
    private readonly float[] _decoded = new float[DecodeFrames];
    private readonly float[] _output = new float[AudioCaptureService.ConversionBlockSamples];
    private readonly List<float[]> _blocks = [];
    private readonly WdlResampler? _resampler;
    private float[] _input = [];
    private int _inputOffset;
    private int _inputNeeded;
    private int _inputWritten;
    private int _sampleCount;
    private bool _finished;

    internal StreamingCaptureConverter(WaveFormat format)
    {
        _source = new CaptureChunkProvider(format.AsStandardWaveFormat(), DecodeFrames);
        _decoder = _source.ToSampleProvider();
        if (_decoder.WaveFormat.Channels > 1)
            _decoder = new DownmixToMonoSampleProvider(_decoder);
        if (format.SampleRate != AudioCaptureService.OutputSampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(false);
            _resampler.SetRates(format.SampleRate, AudioCaptureService.OutputSampleRate);
            Prepare();
        }
    }

    internal long RetainedSampleBytes => (long)_sampleCount * sizeof(float);

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        while (!bytes.IsEmpty)
        {
            var consumed = _source.Load(bytes);
            bytes = bytes[consumed..];
            int read;
            while ((read = _decoder.Read(_decoded, 0, _decoded.Length)) > 0)
            {
                var samples = _decoded.AsSpan(0, read);
                if (_resampler is null)
                {
                    Store(samples);
                    continue;
                }
                while (!samples.IsEmpty)
                {
                    var count = Math.Min(samples.Length, _inputNeeded - _inputWritten);
                    samples[..count].CopyTo(_input.AsSpan(_inputOffset + _inputWritten));
                    _inputWritten += count;
                    samples = samples[count..];
                    if (_inputWritten == _inputNeeded)
                    {
                        Store(_output.AsSpan(0, _resampler.ResampleOut(
                            _output, 0, _inputWritten, _output.Length, 1)));
                        Prepare();
                    }
                }
            }
        }
    }

    internal float[] Complete()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_resampler is not null)
        {
            int read;
            while ((read = _resampler.ResampleOut(_output, 0, _inputWritten, _output.Length, 1)) > 0)
            {
                Store(_output.AsSpan(0, read));
                Prepare();
            }
        }
        // The existing ASR contract requires a contiguous float[]. Until it consumes blocks,
        // completion briefly owns both this result and the accumulated mono blocks.
        var result = new float[_sampleCount];
        var offset = 0;
        foreach (var block in _blocks)
        {
            block.CopyTo(result, offset);
            offset += block.Length;
        }
        Dispose();
        return result;
    }

    private void Prepare()
    {
        _inputNeeded = _resampler!.ResamplePrepare(_output.Length, 1, out _input, out _inputOffset);
        _inputWritten = 0;
    }

    private void Store(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        _sampleCount = checked(_sampleCount + samples.Length);
        _blocks.Add(samples.ToArray());
    }

    public void Dispose()
    {
        _finished = true;
        foreach (var block in _blocks) Array.Clear(block);
        _blocks.Clear();
        _sampleCount = 0;
        Array.Clear(_input);
        Array.Clear(_decoded);
        Array.Clear(_output);
        _source.Clear();
        _resampler?.Reset();
    }

    private sealed class CaptureChunkProvider(WaveFormat format, int capacityFrames) : IWaveProvider
    {
        private readonly byte[] _bytes = new byte[checked(capacityFrames * format.BlockAlign)];
        private int _count;
        private int _position;
        public WaveFormat WaveFormat { get; } = format;

        internal int Load(ReadOnlySpan<byte> bytes)
        {
            _count = Math.Min(bytes.Length, _bytes.Length);
            bytes[.._count].CopyTo(_bytes);
            _position = 0;
            return _count;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _count - _position);
            _bytes.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset));
            _position += read;
            return read;
        }

        internal void Clear() => Array.Clear(_bytes);
    }
}

internal sealed class PcmByteRingBuffer
{
    private readonly byte[] _buffer;
    private readonly int _blockAlign;
    private int _writeOffset;
    private int _count;
    private long _totalWritten;

    internal PcmByteRingBuffer(int capacity, int blockAlign)
    {
        _blockAlign = Math.Max(1, blockAlign);
        capacity -= capacity % _blockAlign;
        _buffer = new byte[Math.Max(_blockAlign, capacity)];
    }

    internal int Count => _count;
    // Абсолютное число записанных байт (монотонно, не обнуляется Clear) и позиция старейшего в кольце.
    internal long TotalWritten => _totalWritten;
    internal long OldestOffset => _totalWritten - _count;

    internal void Write(ReadOnlySpan<byte> bytes)
    {
        var alignedLength = bytes.Length - (bytes.Length % _blockAlign);
        if (alignedLength <= 0)
        {
            return;
        }
        bytes = bytes[..alignedLength];
        _totalWritten += bytes.Length;
        if (bytes.Length >= _buffer.Length)
        {
            bytes[^_buffer.Length..].CopyTo(_buffer);
            _writeOffset = 0;
            _count = _buffer.Length;
            return;
        }

        var first = Math.Min(bytes.Length, _buffer.Length - _writeOffset);
        bytes[..first].CopyTo(_buffer.AsSpan(_writeOffset));
        bytes[first..].CopyTo(_buffer);
        _writeOffset = (_writeOffset + bytes.Length) % _buffer.Length;
        _count = Math.Min(_buffer.Length, _count + bytes.Length);
    }

    internal byte[] Snapshot() => Snapshot(OldestOffset);

    // Содержимое от абсолютной позиции fromOffset (ограничено старейшим и последним байтом).
    internal byte[] Snapshot(long fromOffset)
    {
        var skip = (int)Math.Clamp(fromOffset - OldestOffset, 0, _count);
        skip -= skip % _blockAlign;
        var length = _count - skip;
        var result = new byte[length];
        if (length == 0)
        {
            return result;
        }
        var start = (_writeOffset - length + _buffer.Length) % _buffer.Length;
        var first = Math.Min(length, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(result);
        _buffer.AsSpan(0, length - first).CopyTo(result.AsSpan(first));
        return result;
    }

    internal void Clear()
    {
        Array.Clear(_buffer);
        _writeOffset = 0;
        _count = 0;
    }
}

internal static class AudioSignalAnalyzer
{
    internal static double? EstimateNoiseFloorDb(float[] samples, int preRollSamples, int sampleRate)
    {
        preRollSamples = Math.Clamp(preRollSamples, 0, samples.Length);
        return EstimateNoiseFloorDb(samples.AsSpan(0, preRollSamples), sampleRate);
    }

    internal static double? EstimateNoiseFloorDb(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frameSize = Math.Max(1, sampleRate / 100);
        if (samples.Length < frameSize * 4)
        {
            return null;
        }

        var levels = new List<double>(samples.Length / frameSize);
        for (var offset = 0; offset + frameSize <= samples.Length; offset += frameSize)
        {
            double sum = 0;
            for (var index = 0; index < frameSize; index++)
            {
                var sample = samples[offset + index];
                sum += sample * sample;
            }
            levels.Add(SpeechActivityDetector.AmplitudeToDecibels(Math.Sqrt(sum / frameSize)));
        }
        levels.Sort();
        return levels[Math.Min(levels.Count - 1, (int)Math.Floor(levels.Count * 0.25))];
    }
}

internal sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _sourceBuffer = [];

    internal DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var required = checked(count * _channels);
        if (_sourceBuffer.Length < required)
        {
            _sourceBuffer = new float[required];
        }
        var read = _source.Read(_sourceBuffer, 0, required);
        var frames = read / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var sourceOffset = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _sourceBuffer[sourceOffset + channel];
            }
            buffer[offset + frame] = (float)(sum / _channels);
        }
        return frames;
    }
}

internal static class PcmLevelMeter
{
    internal static bool TryMeasure(byte[] buffer, int bytesRecorded, WaveFormat format, out double rms, out double peak) =>
        TryMeasureTimbre(buffer, bytesRecorded, format, out rms, out peak, out _, out _, out _);

    internal static bool TryMeasureTimbre(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format,
        out double rms,
        out double peak,
        out double bass,
        out double mid,
        out double treble)
    {
        rms = 0;
        peak = 0;
        bass = 0;
        mid = 0;
        treble = 0;
        var readable = format.AsStandardWaveFormat();
        var bytesPerSample = readable.BitsPerSample / 8;
        if (bytesPerSample <= 0 || bytesRecorded < bytesPerSample)
        {
            return false;
        }

        var sampleRate = readable.SampleRate > 0 ? readable.SampleRate : 16000;
        // 1-pole low-pass cutoff at ~300 Hz
        var alphaLow = Math.Clamp(2 * Math.PI * 300 / sampleRate, 0.01, 0.4);
        // 1-pole high-pass cutoff at ~2800 Hz
        var alphaHigh = Math.Clamp(2 * Math.PI * 2800 / sampleRate, 0.1, 0.85);

        double lowState = 0;
        double highState = 0;
        double sum = 0;
        double bassSum = 0;
        double midSum = 0;
        double trebleSum = 0;
        var count = 0;

        for (var offset = 0; offset + bytesPerSample <= bytesRecorded; offset += bytesPerSample)
        {
            double sample;
            if (readable.Encoding == WaveFormatEncoding.IeeeFloat && readable.BitsPerSample == 32)
            {
                sample = BitConverter.ToSingle(buffer, offset);
            }
            else if (readable.Encoding == WaveFormatEncoding.Pcm && readable.BitsPerSample == 16)
            {
                sample = BitConverter.ToInt16(buffer, offset) / 32768d;
            }
            else if (readable.Encoding == WaveFormatEncoding.Pcm && readable.BitsPerSample == 24)
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                sample = value / 8388608d;
            }
            else if (readable.Encoding == WaveFormatEncoding.Pcm && readable.BitsPerSample == 32)
            {
                sample = BitConverter.ToInt32(buffer, offset) / 2147483648d;
            }
            else
            {
                return false;
            }

            if (!double.IsFinite(sample))
            {
                continue;
            }

            lowState += alphaLow * (sample - lowState);
            highState += alphaHigh * (sample - highState);
            var highSample = sample - highState;
            var midSample = highState - lowState;

            sum += sample * sample;
            bassSum += lowState * lowState;
            midSum += midSample * midSample;
            trebleSum += highSample * highSample;
            peak = Math.Max(peak, Math.Abs(sample));
            count++;
        }
        if (count == 0)
        {
            return false;
        }
        rms = Math.Sqrt(sum / count);
        bass = Math.Sqrt(bassSum / count);
        mid = Math.Sqrt(midSum / count);
        treble = Math.Sqrt(trebleSum / count);
        return true;
    }
}
