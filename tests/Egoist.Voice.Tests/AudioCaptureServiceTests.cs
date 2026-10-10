using Egoist.Voice.Services;
using Xunit.Abstractions;

namespace Egoist.Voice.Tests;

public sealed class AudioCaptureServiceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(48_000, 2, 32, true)]
    [InlineData(44_100, 2, 16, false)]
    [InlineData(16_000, 1, 16, false)]
    [InlineData(16_000, 2, 32, true)]
    [InlineData(8_000, 1, 16, false)]
    [InlineData(96_000, 3, 24, false)]
    [InlineData(48_000, 2, 32, false)]
    public async Task Streaming_capture_matches_completed_conversion_across_callback_boundaries(
        int sampleRate, int channels, int bits, bool floatingPoint)
    {
        var format = floatingPoint
            ? NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new NAudio.Wave.WaveFormat(sampleRate, bits, channels);
        var raw = CreateDeviceAudio(format, sampleRate * 3 + 173);
        var expected = AudioCaptureService.ConvertToMono16Khz(raw, format);
        var preRollBytes = sampleRate * 32 / 100 * format.BlockAlign;
        var buffer = new CaptureSessionBuffer(preRollBytes, format.BlockAlign, format);
        buffer.Append(raw.AsSpan(0, preRollBytes));
        buffer.Begin(4096);
        int[] callbackFrames = [1, 31, 480, 441, 8192, 7, 4096];
        var callback = 0;
        for (var offset = preRollBytes; offset < raw.Length;)
        {
            var count = Math.Min(raw.Length - offset,
                callbackFrames[callback++ % callbackFrames.Length] * format.BlockAlign);
            buffer.Append(raw.AsSpan(offset, count));
            offset += count;
        }
        using var completed = buffer.Complete();

        var actual = await AudioCaptureService.ConvertCompletedTakeAsync(completed, format, CancellationToken.None);

        Assert.Equal(preRollBytes, completed.PreRollBytes);
        Assert.Equal(expected, actual);
        Assert.False(buffer.IsSessionActive);
        Assert.Equal(0, buffer.RetainedSampleBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(49_155)]
    [InlineData(49_156)]
    [InlineData(49_157)]
    [InlineData(98_309)]
    public void Streaming_capture_flushes_empty_short_and_exact_WDL_boundary_takes(int frames)
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var raw = CreateDeviceAudio(format, frames);
        var expected = AudioCaptureService.ConvertToMono16Khz(raw, format);
        using var converter = new StreamingCaptureConverter(format);
        converter.Append(raw);

        Assert.Equal(expected, converter.Complete());
    }

    [Fact]
    public async Task Streaming_capture_ownership_survives_cancellation_clear_and_restart()
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var first = CreateDeviceAudio(format, 60_000);
        var next = CreateDeviceAudio(format, 317);
        var buffer = new CaptureSessionBuffer(1024, format.BlockAlign, format);
        buffer.Begin(4096);
        buffer.Append(first);
        using var completed = buffer.Complete();
        buffer.Begin(4096);
        buffer.Append(first);
        buffer.CancelSession();
        buffer.Clear();
        buffer.Begin(4096);
        buffer.Append(next);
        using var restarted = buffer.Complete();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AudioCaptureService.ConvertCompletedTakeAsync(completed, format, new CancellationToken(true)));
        var actual = await AudioCaptureService.ConvertCompletedTakeAsync(restarted, format, CancellationToken.None);

        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(next, format), actual);
        Assert.Equal(0, buffer.RetainedSampleBytes);
    }

    [Fact]
    public void Streaming_capture_keeps_only_mono_ASR_audio_as_duration_grows()
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var chunk = CreateDeviceAudio(format, 480);
        var buffer = new CaptureSessionBuffer(122_880, format.BlockAlign, format);
        buffer.Begin(4096);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var callback = 0; callback < 6000; callback++) buffer.Append(chunk);
        clock.Stop();
        const long rawBytes = 60L * 48_000 * 2 * sizeof(float);

        output.WriteLine($"rawBytes={rawBytes}; retainedMonoBytes={buffer.RetainedSampleBytes}; " +
            $"syntheticMinuteAppendMs={clock.Elapsed.TotalMilliseconds:F1}; " +
            $"appendAllocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocatedBefore}");
        Assert.InRange(buffer.RetainedSampleBytes, 59L * 16_000 * sizeof(float),
            60L * 16_000 * sizeof(float));
        Assert.True(buffer.RetainedSampleBytes < rawBytes / 5);
        buffer.Clear();
        Assert.Equal(0, buffer.RetainedSampleBytes);
        Assert.False(buffer.IsSessionActive);
    }

    [Fact]
    public async Task Streaming_capture_discards_pending_filter_audio_and_pre_roll_before_restart()
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        var discarded = CreateDeviceAudio(format, 15_000);
        var accepted = CreateDeviceAudio(format, 30_000);
        var buffer = new CaptureSessionBuffer(10_000 * format.BlockAlign, format.BlockAlign, format);
        buffer.Append(discarded);
        buffer.Begin(4096);
        buffer.Append(discarded);
        buffer.DiscardAudioPreservingSession();
        Assert.True(buffer.IsSessionActive);
        Assert.Equal(0, buffer.RetainedSampleBytes);
        buffer.Append(accepted);
        using var completed = buffer.Complete();

        var actual = await AudioCaptureService.ConvertCompletedTakeAsync(completed, format, CancellationToken.None);

        Assert.Equal(0, completed.PreRollBytes);
        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(accepted, format), actual);
    }

    private static byte[] CreateDeviceAudio(NAudio.Wave.WaveFormat format, int frames)
    {
        var raw = new byte[frames * format.BlockAlign];
        var random = new Random(17);
        random.NextBytes(raw);
        if (format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
        {
            for (var index = 0; index < raw.Length; index += sizeof(float))
                BitConverter.TryWriteBytes(raw.AsSpan(index, sizeof(float)), (float)(random.NextDouble() * 2 - 1));
        }
        return raw;
    }

    [Fact]
    public void Completing_thirty_second_device_take_does_not_allocate_another_audio_buffer()
    {
        const int takeBytes = 30 * 48_000 * 2 * sizeof(float);
        var buffer = new CaptureSessionBuffer(preRollCapacity: 8, blockAlign: 8);
        buffer.Begin(initialCapacity: takeBytes);
        buffer.Append(new byte[takeBytes]);

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var completed = buffer.Complete();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"takeBytes={takeBytes}; completeAllocatedBytes={allocated}");
        Assert.Equal(takeBytes, completed.Bytes.Length);
        Assert.InRange(allocated, 0, 4_096);
    }

    [Theory]
    [InlineData(16_000, 1)]
    [InlineData(48_000, 2)]
    public async Task Transferred_take_converts_exact_valid_bytes_and_clears_source(int sampleRate, int channels)
    {
        var format = new NAudio.Wave.WaveFormat(sampleRate, 16, channels);
        var raw = new byte[sampleRate / 10 * format.BlockAlign];
        for (var index = 0; index < raw.Length; index++)
            raw[index] = (byte)(index % 251);
        var expected = AudioCaptureService.ConvertToMono16Khz(raw, format);
        var buffer = new CaptureSessionBuffer(preRollCapacity: format.BlockAlign, blockAlign: format.BlockAlign);
        buffer.Begin(initialCapacity: raw.Length * 3);
        buffer.Append(raw);
        using var completed = buffer.Complete();
        var transferred = completed.Bytes;
        Assert.Equal(raw, transferred.ToArray());

        var actual = await AudioCaptureService.ConvertCompletedTakeAsync(completed, format, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.All(transferred.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => completed.Bytes);
    }

    [Fact]
    public async Task Cancelled_conversion_clears_transferred_take_without_touching_next_session()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 2, blockAlign: 2);
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 1, 2, 3, 4 });
        using var completed = buffer.Complete();
        var transferred = completed.Bytes;
        buffer.Clear();
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 8, 9 });
        using var next = buffer.Complete();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AudioCaptureService.ConvertCompletedTakeAsync(completed, new NAudio.Wave.WaveFormat(16_000, 16, 1), cancellation.Token));

        Assert.All(transferred.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(new byte[] { 8, 9 }, next.Bytes.ToArray());
    }

    [Fact]
    public async Task Failed_conversion_still_clears_transferred_take()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 1, blockAlign: 1);
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 1, 2, 3, 4 });
        using var completed = buffer.Complete();
        var transferred = completed.Bytes;
        var unsupported = NAudio.Wave.WaveFormat.CreateCustomFormat(
            NAudio.Wave.WaveFormatEncoding.MpegLayer3, 16_000, 1, 16_000, 1, 8);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AudioCaptureService.ConvertCompletedTakeAsync(completed, unsupported, CancellationToken.None));

        Assert.All(transferred.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Quiet_speech_starting_in_pre_roll_is_not_mistaken_for_room_noise()
    {
        const int sampleRate = AudioCaptureService.OutputSampleRate;
        var samples = new float[sampleRate];
        // A quiet, peaky waveform starts before the trigger and stops before the release tail.
        // It meets the existing quiet-speech threshold when the background is estimated correctly.
        for (var index = 0; index < sampleRate * 65 / 100; index++)
            samples[index] = (index % 4) switch { 0 => 0.012f, 2 => -0.012f, _ => 0 };
        var original = samples.ToArray();

        var preRollOnly = new SpeechActivityDetector();
        preRollOnly.Reset(AudioSignalAnalyzer.EstimateNoiseFloorDb(samples, sampleRate * 32 / 100, sampleRate));
        preRollOnly.Process(0.012 / Math.Sqrt(2), 0.012, 650);
        Assert.False(preRollOnly.Snapshot().HasSpeech);

        var result = AudioCaptureService.Analyze(samples, sampleRate * 32 / 100);

        Assert.True(result.HasSpeech);
        Assert.Equal(original, samples);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
    }

    [Fact]
    public void Release_tail_noise_estimate_does_not_promote_stationary_room_noise()
    {
        const int sampleRate = AudioCaptureService.OutputSampleRate;
        var samples = Enumerable.Range(0, sampleRate)
            .Select(index => index % 2 == 0 ? 0.004f : -0.004f).ToArray();

        var result = AudioCaptureService.Analyze(samples, sampleRate * 32 / 100);

        Assert.False(result.HasSpeech);
    }

    [Fact]
    public void Quiet_boundaries_do_not_turn_one_transient_into_a_dictation()
    {
        var samples = new float[AudioCaptureService.OutputSampleRate];
        Array.Fill(samples, 0.2f, 5_120, 320);

        var result = AudioCaptureService.Analyze(samples, 5_120);

        Assert.False(result.HasSpeech);
    }

    [Fact]
    public void PreRollRingRetainsOnlyNewestAlignedFramesAcrossWraparound()
    {
        var ring = new PcmByteRingBuffer(capacity: 8, blockAlign: 2);
        ring.Write(new byte[] { 0, 1, 2, 3, 4, 5 });
        ring.Write(new byte[] { 6, 7, 8, 9, 10, 11 });

        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, ring.Snapshot());
        Assert.Equal(8, ring.Count);
    }

    [Fact]
    public void PreRollRingDropsPartialFramesAndClearsSensitiveBytes()
    {
        var ring = new PcmByteRingBuffer(capacity: 8, blockAlign: 2);
        ring.Write(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2 }, ring.Snapshot());

        ring.Clear();

        Assert.Empty(ring.Snapshot());
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void SessionBufferIncludesPreRollTailAndNeverLeaksCancelledSessionIntoNextTake()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 4, blockAlign: 1);
        buffer.Append(new byte[] { 1, 2, 3, 4 });
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 5, 6 });
        using var first = buffer.Complete();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, first.Bytes.ToArray());

        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 7 });
        buffer.CancelSession();
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 8 });
        using var next = buffer.Complete();

        Assert.Equal(new byte[] { 4, 5, 6, 7, 8 }, next.Bytes.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, first.Bytes.ToArray());
        Assert.Equal(4, next.PreRollBytes);
    }

    [Fact]
    public void EndpointSwitchClearRemovesBothActiveSessionAndWarmPreRoll()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 8, blockAlign: 1);
        buffer.Append(new byte[] { 1, 2, 3, 4 });
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 5, 6 });

        buffer.Clear();
        buffer.Begin(initialCapacity: 8);
        buffer.Append(new byte[] { 9 });
        using var nextEndpoint = buffer.Complete();

        Assert.Equal(new byte[] { 9 }, nextEndpoint.Bytes.ToArray());
        Assert.Equal(0, nextEndpoint.PreRollBytes);
    }

    [Fact]
    public void FeedbackExclusionDropsWarmAndActiveCueAudioButKeepsTheSessionAlive()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 8, blockAlign: 1);
        buffer.Append(new byte[] { 1, 2, 3 });
        buffer.Begin(initialCapacity: 16);
        buffer.Append(new byte[] { 4, 5 });

        buffer.DiscardAudioPreservingSession();
        buffer.Append(new byte[] { 8, 9 });
        using var accepted = buffer.Complete();

        Assert.Equal(new byte[] { 8, 9 }, accepted.Bytes.ToArray());
        Assert.Equal(0, accepted.PreRollBytes);
    }

    [Fact]
    public void QueuedCallbackFromReplacedEndpointIsRejectedByIdentity()
    {
        var replacedEndpoint = new object();
        var currentEndpoint = new object();

        Assert.False(AudioCaptureService.IsCurrentCaptureCallback(
            replacedEndpoint,
            currentEndpoint,
            disposed: false,
            bytesRecorded: 512));
        Assert.True(AudioCaptureService.IsCurrentCaptureCallback(
            currentEndpoint,
            currentEndpoint,
            disposed: false,
            bytesRecorded: 512));
        Assert.False(AudioCaptureService.IsCurrentCaptureCallback(
            currentEndpoint,
            currentEndpoint,
            disposed: true,
            bytesRecorded: 512));
        Assert.False(AudioCaptureService.IsCurrentCaptureCallback(
            currentEndpoint,
            currentEndpoint,
            disposed: false,
            bytesRecorded: 0));
    }

    [Fact]
    public void ThreeHundredSessionCyclesRemainBoundedAndOrdered()
    {
        var buffer = new CaptureSessionBuffer(preRollCapacity: 8, blockAlign: 1);
        for (var cycle = 0; cycle < 300; cycle++)
        {
            buffer.Begin(initialCapacity: 16);
            buffer.Append(new byte[] { (byte)cycle });
            using var completed = buffer.Complete();
            Assert.InRange(completed.Bytes.Length, 1, 9);
            Assert.Equal((byte)cycle, completed.Bytes.Span[^1]);
        }
    }

    [Fact]
    public void DevicePcmIsDownmixedAndResampledExactlyOnce()
    {
        const int sourceRate = 48_000;
        const int frames = sourceRate / 10;
        var raw = new byte[frames * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)Math.Round(Math.Sin(frame * 2 * Math.PI * 440 / sourceRate) * 8_000);
            raw[frame * 4] = (byte)sample;
            raw[(frame * 4) + 1] = (byte)(sample >> 8);
            raw[(frame * 4) + 2] = (byte)sample;
            raw[(frame * 4) + 3] = (byte)(sample >> 8);
        }

        var samples = AudioCaptureService.ConvertToMono16Khz(raw, new NAudio.Wave.WaveFormat(sourceRate, 16, 2));

        Assert.InRange(samples.Length, 1_560, 1_640);
        Assert.True(samples.Max(Math.Abs) > 0.15f);
    }

    [Fact]
    public void PreRollNoiseFloorLetsSustainedQuietSpeechThrough()
    {
        var detector = new SpeechActivityDetector();
        detector.Reset(noiseFloorDb: -62);
        for (var index = 0; index < 7; index++)
        {
            detector.Process(0.003, 0.014, 20);
        }

        Assert.True(detector.Snapshot().HasSpeech);
    }

    [Fact]
    public void AdaptiveGateDoesNotPromoteStationaryNoiseIntoSpeech()
    {
        var detector = new SpeechActivityDetector();
        detector.Reset(noiseFloorDb: -48);
        for (var index = 0; index < 30; index++)
        {
            detector.Process(0.004, 0.006, 20);
        }

        Assert.False(detector.Snapshot().HasSpeech);
    }

    [Fact]
    public void NoiseFloorUsesQuietPreRollFramesInsteadOfTriggerClick()
    {
        var samples = new float[3_200];
        Array.Fill(samples, 0.001f);
        Array.Fill(samples, 0.2f, 2_880, 320);

        var noise = AudioSignalAnalyzer.EstimateNoiseFloorDb(samples, samples.Length, 16_000);

        Assert.NotNull(noise);
        Assert.InRange(noise.Value, -60.1, -59.9);
    }

    [Fact]
    public void BoundaryWindowsStaySmallAndExplicit()
    {
        Assert.Equal(500, AudioCaptureService.PreRollDuration.TotalMilliseconds);
        Assert.Equal(2_000, AudioCaptureService.RingDuration.TotalMilliseconds);
        Assert.Equal(250, AudioCaptureService.ReleaseTailMinimum.TotalMilliseconds);
        Assert.Equal(200, AudioCaptureService.ReleaseTailSilence.TotalMilliseconds);
        Assert.Equal(900, AudioCaptureService.ReleaseTailMaximum.TotalMilliseconds);
        Assert.True(AudioCaptureService.PreRollDuration < AudioCaptureService.RingDuration);

        const int worstCaseBytesPerSecond = 48_000 * 2 * sizeof(float);
        var ringBytes = worstCaseBytesPerSecond * AudioCaptureService.RingDuration.TotalSeconds;
        Assert.InRange(ringBytes, 1, 1024 * 1024);
    }

    [Theory]
    [InlineData(-80, 0)]
    [InlineData(-58, 0)]
    [InlineData(-36, 0.5)]
    [InlineData(-14, 1)]
    [InlineData(-3, 1)]
    public void DbToLevelMapsAndClampsMicrophoneRange(double decibels, float expected)
    {
        var amplitude = Math.Pow(10, decibels / 20);

        var result = AudioCaptureService.DbToLevel(amplitude, -58, -14);

        Assert.Equal(expected, result, precision: 3);
    }

    [Fact]
    public void DbToLevelIsMonotonicAcrossSpeechRange()
    {
        var quiet = AudioCaptureService.DbToLevel(0.004, -58, -14);
        var normal = AudioCaptureService.DbToLevel(0.04, -58, -14);
        var loud = AudioCaptureService.DbToLevel(0.2, -58, -14);

        Assert.True(quiet < normal);
        Assert.True(normal < loud);
    }

    [Fact]
    public void SpeechGateRejectsSilenceAndSingleTransient()
    {
        var detector = new SpeechActivityDetector();
        for (var index = 0; index < 20; index++)
        {
            detector.Process(0.0002, 0.0008, 32);
        }
        detector.Process(0.2, 0.5, 32);

        var result = detector.Snapshot();

        Assert.False(result.HasSpeech);
        Assert.Equal(672, result.Duration.TotalMilliseconds);
    }

    [Fact]
    public void SpeechGateAcceptsSustainedQuietSpeech()
    {
        var detector = new SpeechActivityDetector();
        for (var index = 0; index < 6; index++)
        {
            detector.Process(0.006, 0.018, 32);
        }

        var result = detector.Snapshot();

        Assert.True(result.HasSpeech);
        Assert.Equal(192, result.DetectedSpeech.TotalMilliseconds);
        Assert.True(result.PeakDecibels > -36);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0.1, -20)]
    [InlineData(0.01, -40)]
    public void AmplitudeToDecibelsIsStable(double amplitude, double expected)
    {
        Assert.Equal(expected, SpeechActivityDetector.AmplitudeToDecibels(amplitude), precision: 3);
    }

    [Fact]
    public void A_silent_microphone_is_reported_rather_than_hidden()
    {
        // Before this, a dead microphone and a deliberate pause produced the same outcome: the
        // capsule vanished and the user was left guessing.
        var detector = new SpeechActivityDetector();
        for (var index = 0; index < 10; index++)
        {
            detector.Process(0.000001, 0.000002, 32);
        }

        var result = detector.Snapshot();

        Assert.False(result.HasSpeech);
        Assert.Equal(SpeechRejection.MicrophoneSilent, result.Rejection);
        Assert.Equal("Микрофон молчит", AudioCaptureService.DescribeRejection(result.Rejection));
    }

    [Fact]
    public void Audible_but_too_short_is_distinguished_from_too_quiet()
    {
        var detector = new SpeechActivityDetector();
        detector.Process(0.2, 0.5, 32);

        var result = detector.Snapshot();

        Assert.Equal(SpeechRejection.TooShort, result.Rejection);
    }

    [Fact]
    public void Room_noise_below_the_gate_is_reported_as_too_quiet()
    {
        var detector = new SpeechActivityDetector();
        for (var index = 0; index < 20; index++)
        {
            detector.Process(0.001, 0.004, 32);
        }

        var result = detector.Snapshot();

        Assert.Equal(SpeechRejection.TooQuiet, result.Rejection);
    }

    [Fact]
    public void A_successful_session_carries_no_rejection()
    {
        var detector = new SpeechActivityDetector();
        for (var index = 0; index < 6; index++)
        {
            detector.Process(0.006, 0.018, 32);
        }

        var result = detector.Snapshot();

        Assert.True(result.HasSpeech);
        Assert.Equal(SpeechRejection.None, result.Rejection);
        Assert.Null(AudioCaptureService.DescribeRejection(result.Rejection));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("select")]
    [InlineData("default-device")]
    [InlineData("unavailable")]
    [InlineData("dispose")]
    public async Task Retirement_does_not_join_a_queued_callback_under_the_capture_lock(string operation)
    {
        var first = new ControlledCapture();
        using var rig = new CaptureRig(first, new ControlledCapture());
        using var service = rig.CreateService();
        first.RunOnCaptureThread(first.SnapshotData(SyntheticPcm(0.2f)), waitForDispose: true);

        await Task.Run(() =>
        {
            switch (operation)
            {
                case "pause": service.PauseMonitoring(); break;
                case "select": service.SelectCaptureDevice("replacement"); break;
                case "default-device": rig.Catalog.ChangeDefault("replacement"); break;
                case "unavailable": rig.Catalog.RemoveAllDevices(); break;
                case "dispose": service.Dispose(); break;
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(first.CallbackFinished.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(first.JoinTimedOut);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, rig.Handles[0].DisposeCount);
        if (operation is "select" or "default-device") Assert.True(service.GetState().IsMonitoring);
        if (operation is "pause" or "unavailable") Assert.True(service.GetState().IsPaused);
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("pause")]
    public async Task Stopped_notification_can_reenter_lifecycle_without_indirect_self_join(string operation)
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var service = rig.CreateService(ownsCatalog: true);
        var notifications = 0;
        service.StateChanged += (_, args) =>
        {
            if (args.Kind != AudioCaptureChangeKind.DeviceUnavailable) return;
            Interlocked.Increment(ref notifications);
            if (operation == "dispose") service.Dispose();
            else service.PauseMonitoring();
        };
        capture.RunOnCaptureThread(capture.SnapshotStopped());

        Assert.True(capture.CallbackFinished.Wait(TimeSpan.FromSeconds(3)));
        // Ordinary callers drain the deferred retirement, including after a callback set disposed.
        await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, notifications);
        Assert.False(capture.JoinTimedOut);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, rig.Catalog.DisposeCount);
        Assert.Equal(1, rig.Handles[0].DisposeCount);
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("pause")]
    public async Task Level_notification_can_reenter_lifecycle_without_self_join(string operation)
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var service = rig.CreateService();
        var notifications = 0;
        service.LevelChanged += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            if (operation == "dispose") service.Dispose();
            else service.PauseMonitoring();
        };
        capture.RunOnCaptureThread(capture.SnapshotData(SyntheticPcm(0.2f)));

        Assert.True(capture.CallbackFinished.Wait(TimeSpan.FromSeconds(3)));
        await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, notifications);
        Assert.False(capture.JoinTimedOut);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, rig.Handles[0].DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_start_failure_retires_outside_callback_locks_and_allows_retry(bool constructorStarts)
    {
        var failed = new ControlledCapture { ThrowAfterStartingCallback = true };
        var replacement = new ControlledCapture();
        using var rig = new CaptureRig(failed, replacement);
        using var service = rig.CreateService(startPaused: !constructorStarts);
        if (!constructorStarts) Assert.Throws<InvalidOperationException>(service.ResumeMonitoring);

        Assert.True(failed.CallbackFinished.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(failed.JoinTimedOut);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, rig.Handles[0].DisposeCount);
        Assert.False(service.GetState().IsMonitoring);
        Assert.Equal("device-unavailable", service.GetState().ErrorCode);
        if (!constructorStarts) Assert.True(service.GetState().IsPaused);

        await Task.Run(service.ResumeMonitoring).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.GetState().IsMonitoring);
        Assert.Equal(1, replacement.StartCount);
    }

    [Fact]
    public async Task Concurrent_resume_and_terminal_dispose_cannot_resurrect_a_retired_capture()
    {
        var first = new ControlledCapture { HoldDispose = true };
        var second = new ControlledCapture();
        using var rig = new CaptureRig(first, second);
        var service = rig.CreateService();
        var pause = Task.Run(service.PauseMonitoring);
        Assert.True(first.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            // Cleanup of the old endpoint holds neither service lock.
            await Task.Run(service.ResumeMonitoring).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(service.GetState().IsMonitoring);
            var terminal = Task.Run(service.Dispose);
            Assert.True(second.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Throws<ObjectDisposedException>(service.ResumeMonitoring);
            Assert.Throws<ObjectDisposedException>(service.Start);
            Assert.Throws<ObjectDisposedException>(() => service.SelectCaptureDevice("replacement"));
            Assert.False(terminal.IsCompleted); // The ordinary disposer drains the older retirement.
            first.AllowDisposeFinish.Set();
            await Task.WhenAll(pause, terminal).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            first.AllowDisposeFinish.Set();
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, rig.OpenCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Stale_endpoint_data_and_stopped_events_cannot_change_the_replacement_session()
    {
        var first = new ControlledCapture();
        var replacement = new ControlledCapture();
        using var rig = new CaptureRig(first, replacement);
        using var service = rig.CreateService();
        var staleData = first.SnapshotData(SyntheticPcm(0.8f));
        var staleStopped = first.SnapshotStopped();
        var notifications = 0;
        service.LevelChanged += (_, _) => notifications++;
        service.SelectCaptureDevice("replacement");
        service.Start();
        staleData();
        staleStopped();
        Assert.Equal(0, notifications);
        Assert.True(service.GetState().IsMonitoring);
        var expected = SyntheticPcm(0.12f);
        replacement.SnapshotData(expected)();

        var result = await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, notifications);
        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(expected, replacement.WaveFormat), result.Samples);
        Assert.True(service.GetState().IsMonitoring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Obsolete_release_tail_cannot_complete_or_cancel_a_restarted_take(bool cancelOldStop)
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService();
        using var cancellation = new CancellationTokenSource();
        service.Start();
        var obsolete = service.StopAsync(cancellation.Token);
        service.PauseMonitoring();
        service.ResumeMonitoring();
        service.Start();
        var expected = SyntheticPcm(0.15f);
        rig.Captures[1].SnapshotData(expected)();
        if (cancelOldStop) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
        var current = await service.StopAsync(CancellationToken.None);

        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(expected, rig.Captures[1].WaveFormat), current.Samples);
        Assert.True(service.GetState().IsMonitoring);
        Assert.Equal(2, rig.OpenCount);
    }

    [Fact]
    public async Task Warm_capture_preserves_pre_roll_and_delivered_release_tail_without_reopening()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        using var service = rig.CreateService();
        var prefix = SyntheticPcm(0.1f);
        var speech = SyntheticPcm(0.2f);
        var tail = SyntheticPcm(0.03f);
        capture.SnapshotData(prefix)();
        service.Start();
        capture.SnapshotData(speech)();
        var stop = service.StopAsync(CancellationToken.None);
        capture.SnapshotData(tail)();

        var result = await stop;

        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(
            prefix.Concat(speech).Concat(tail).ToArray(), capture.WaveFormat), result.Samples);
        service.Start();
        await service.CancelAsync();
        Assert.True(service.GetState().IsMonitoring);
        Assert.Equal(1, rig.OpenCount);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(0, capture.StopCount);
    }

    [Fact]
    public async Task Nested_level_notifications_preserve_the_outer_capture_callback_ownership()
    {
        var outerCapture = new ControlledCapture();
        using var outerRig = new CaptureRig(outerCapture);
        using var innerRig = new CaptureRig(new ControlledCapture());
        var outer = outerRig.CreateService();
        using var inner = innerRig.CreateService();
        inner.LevelChanged += (_, _) => outer.Dispose();
        outer.LevelChanged += (_, _) => innerRig.Captures[0].SnapshotData(SyntheticPcm(0.1f))();

        outerCapture.RunOnCaptureThread(outerCapture.SnapshotData(SyntheticPcm(0.2f)));
        Assert.True(outerCapture.CallbackFinished.Wait(TimeSpan.FromSeconds(3)));
        await Task.Run(outer.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outerCapture.JoinTimedOut);
        Assert.Equal(1, outerCapture.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_during_endpoint_retirement_invalidates_an_obsolete_restart(bool topologyChange)
    {
        var first = new ControlledCapture { HoldDispose = true };
        using var rig = new CaptureRig(first, new ControlledCapture());
        using var service = rig.CreateService();
        var switchEndpoint = Task.Run(() =>
        {
            if (topologyChange) rig.Catalog.ChangeDefault("replacement");
            else service.SelectCaptureDevice("replacement");
        });
        Assert.True(first.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            await Task.Run(service.PauseMonitoring).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(service.GetState().IsPaused);
        }
        finally { first.AllowDisposeFinish.Set(); }
        await switchEndpoint.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.GetState().IsPaused);
        Assert.False(service.GetState().IsMonitoring);
        Assert.Equal(1, rig.OpenCount);
    }

    [Fact]
    public async Task A_completed_older_pause_does_not_notify_after_a_newer_resume()
    {
        var first = new ControlledCapture { HoldDispose = true };
        using var rig = new CaptureRig(first, new ControlledCapture());
        using var service = rig.CreateService();
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<AudioCaptureChangeKind>();
        service.StateChanged += (_, change) => notifications.Enqueue(change.Kind);
        var pause = Task.Run(service.PauseMonitoring);
        Assert.True(first.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
        try { await Task.Run(service.ResumeMonitoring).WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { first.AllowDisposeFinish.Set(); }
        await pause.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { AudioCaptureChangeKind.Resumed }, notifications.ToArray());
        Assert.True(service.GetState().IsMonitoring);
    }

    [Fact]
    public async Task Default_Start_refreshes_the_endpoint_before_a_delayed_topology_notification()
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService();
        rig.Catalog.ChangeDefault("replacement", notify: false);

        service.Start();
        var expected = SyntheticPcm(0.1f);
        rig.Captures[1].SnapshotData(expected)();
        var result = await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, rig.OpenCount);
        Assert.Equal(1, rig.Captures[0].DisposeCount);
        Assert.Equal(AudioCaptureService.ConvertToMono16Khz(expected, rig.Captures[1].WaveFormat), result.Samples);
        Assert.Null(service.GetState().SelectedDeviceId);
    }

    [Fact]
    public async Task Explicit_selection_survives_a_silent_Windows_default_change()
    {
        using var rig = new CaptureRig(new ControlledCapture());
        using var service = rig.CreateService(captureDeviceId: "default");
        rig.Catalog.ChangeDefault("replacement", notify: false);
        service.Start();
        rig.Captures[0].SnapshotData(SyntheticPcm(0.1f))();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, rig.OpenCount);
        Assert.Equal("default", service.GetState().SelectedDeviceId);
    }

    [Fact]
    public void Missing_default_at_Start_cannot_begin_the_stale_warm_capture()
    {
        using var rig = new CaptureRig(new ControlledCapture());
        using var service = rig.CreateService();
        rig.Catalog.RemoveAllDevices(notify: false);

        Assert.Throws<MicrophoneUnavailableException>(service.Start);

        Assert.False(service.GetState().IsMonitoring);
        Assert.Equal(1, rig.Captures[0].DisposeCount);
    }

    [Fact]
    public void A_returned_default_recovers_monitoring_from_device_loss_without_polling()
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService();
        rig.Catalog.RemoveAllDevices();
        Assert.True(service.GetState().IsPaused);
        rig.Catalog.RestoreDevices("replacement");

        Assert.True(service.GetState().IsMonitoring);
        Assert.False(service.GetState().IsPaused);
        for (var index = 0; index < 100; index++) rig.Catalog.Notify();
        Assert.Equal(2, rig.OpenCount);
        Assert.Null(service.GetState().SelectedDeviceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_user_pause_is_not_undone_by_hotplug_even_when_already_unavailable(bool storedPause)
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService(startPaused: storedPause);
        rig.Catalog.RemoveAllDevices();
        if (!storedPause) service.PauseMonitoring();
        rig.Catalog.RestoreDevices("replacement");

        Assert.True(service.GetState().IsPaused);
        Assert.False(service.GetState().IsMonitoring);
        Assert.Equal(storedPause ? 0 : 1, rig.OpenCount);
    }

    [Fact]
    public void Explicit_device_loss_recovers_only_that_same_selection()
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService(captureDeviceId: "default");
        rig.Catalog.RemoveAllDevices();
        rig.Catalog.SetDevices([new("replacement", "Synthetic replacement", true)]);
        Assert.False(service.GetState().IsMonitoring);
        Assert.Equal(1, rig.OpenCount);
        rig.Catalog.RestoreDevices("replacement");

        Assert.True(service.GetState().IsMonitoring);
        Assert.Equal("default", service.GetState().SelectedDeviceId);
        Assert.Equal(2, rig.OpenCount);
    }

    [Fact]
    public async Task An_explicit_Start_retries_a_stopped_default_capture()
    {
        var first = new ControlledCapture();
        using var rig = new CaptureRig(first, new ControlledCapture());
        using var service = rig.CreateService();
        first.SnapshotStopped()();

        await Task.Run(service.Start).WaitAsync(TimeSpan.FromSeconds(3));
        rig.Captures[1].SnapshotData(SyntheticPcm(0.1f))();
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.GetState().IsMonitoring);
        Assert.Equal(2, rig.OpenCount);
    }

    [Fact]
    public void Failed_hotplug_recovery_does_not_retry_on_unchanged_inventory_events()
    {
        using var rig = new CaptureRig(new ControlledCapture(),
            new ControlledCapture { ThrowAfterStartingCallback = true }, new ControlledCapture());
        using var service = rig.CreateService();
        rig.Catalog.RemoveAllDevices();
        rig.Catalog.RestoreDevices("replacement");
        Assert.False(service.GetState().IsMonitoring);
        for (var index = 0; index < 100; index++) rig.Catalog.Notify();

        Assert.Equal(2, rig.OpenCount);
        service.ResumeMonitoring();
        Assert.Equal(3, rig.OpenCount);
        Assert.True(service.GetState().IsMonitoring);
    }

    [Fact]
    public async Task A_weak_final_chunk_delivered_during_release_tail_keeps_every_captured_sample()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        service.Start();
        var voiced = SyntheticPcm(0.2f).Concat(SyntheticPcm(0.2f)).Concat(SyntheticPcm(0.2f)).ToArray();
        feed.Push(voiced);
        var finalWeak = new byte[1600 * sizeof(float)];
        for (var index = 0; index < 1600; index++)
            BitConverter.TryWriteBytes(finalWeak.AsSpan(index * sizeof(float), sizeof(float)),
                index == 1599 ? 1e-7f : (index % 2 == 0 ? 1e-5f : -1e-5f));
        var stop = service.StopAsync(CancellationToken.None);
        // Deliver a controlled callback after stop was requested, before the take is completed.
        feed.Push(finalWeak);
        // Хвост адаптивный: ещё 200 мс тишины после слабого куска, чтобы выполнить минимум и порог тишины.
        var quiet = SyntheticPcm(1e-5f);
        feed.Push(quiet);
        feed.Push(quiet);

        var completed = await stop;
        var expected = AudioCaptureService.ConvertToMono16Khz(
            voiced.Concat(finalWeak).Concat(quiet).Concat(quiet).ToArray(), capture.WaveFormat);

        Assert.True(completed.HasSpeech);
        Assert.Equal(expected, completed.Samples);
        Assert.Equal(9600, completed.Samples.Length);
        Assert.Equal(1e-7f, completed.Samples[6399]);
        Assert.Equal(250, AudioCaptureService.ReleaseTailMinimum.TotalMilliseconds);
        Assert.True(service.GetState().IsMonitoring);
    }

    [Fact]
    public void Transient_unavailability_does_not_change_user_pause_intent()
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService();
        rig.Catalog.RemoveAllDevices();
        Assert.True(service.GetState().IsTransientlyUnavailable);
        Assert.False(service.GetState().IsUserPaused);
        service.PauseMonitoring();
        Assert.False(service.GetState().IsTransientlyUnavailable);
        Assert.True(service.GetState().IsUserPaused);
        rig.Catalog.RestoreDevices("replacement");
        Assert.True(service.GetState().IsUserPaused);
        Assert.Equal(1, rig.OpenCount);
    }

    [Fact]
    public async Task Explicit_retry_and_silent_default_switch_notify_their_fresh_state()
    {
        using var rig = new CaptureRig(new ControlledCapture(), new ControlledCapture(), new ControlledCapture());
        using var service = rig.CreateService();
        var observed = new List<AudioCaptureStateChangedEventArgs>();
        service.StateChanged += (_, args) => observed.Add(args);
        rig.Catalog.ChangeDefault("replacement", notify: false);
        service.Start();
        Assert.Equal(AudioCaptureChangeKind.DefaultDeviceChanged, observed[^1].Kind);
        Assert.True(observed[^1].State.IsMonitoring);
        await service.CancelAsync();
        rig.Captures[1].SnapshotStopped()();
        service.Start();
        Assert.Equal(AudioCaptureChangeKind.Resumed, observed[^1].Kind);
        Assert.False(observed[^1].State.IsTransientlyUnavailable);
        Assert.True(observed[^1].State.IsMonitoring);
        await service.CancelAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pause_or_terminal_dispose_cannot_be_overwritten_by_a_default_Start_restart(bool terminal)
    {
        var first = new ControlledCapture { HoldDispose = true };
        using var rig = new CaptureRig(first, new ControlledCapture());
        var service = rig.CreateService();
        rig.Catalog.ChangeDefault("replacement", notify: false);
        var start = Task.Run(service.Start);
        Assert.True(first.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
        Task? dispose = null;
        try
        {
            if (terminal)
            {
                dispose = Task.Run(service.Dispose);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    try { service.GetState(); return false; }
                    catch (ObjectDisposedException) { return true; }
                }, TimeSpan.FromSeconds(2)));
            }
            else service.PauseMonitoring();
        }
        finally { first.AllowDisposeFinish.Set(); }
        if (terminal)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
            await dispose!.WaitAsync(TimeSpan.FromSeconds(3));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.True(service.GetState().IsUserPaused);
        }
        await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, rig.OpenCount);
        Assert.Equal(1, first.DisposeCount);
    }

    // ---- Границы записи: привязка к нажатию, адаптивный хвост, подавление сигнала ----

    private static long Ms(double milliseconds) =>
        (long)(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000);

    private const int ChunkSamples = 320; // 20 мс при 16 кГц mono float

    /// <summary>Управляемые часы и подача буферов: момент колбэка = конец буфера.</summary>
    private sealed class TimedFeed(ControlledCapture initial)
    {
        internal ControlledCapture Capture { get; set; } = initial;
        internal long Now { get; private set; } = Ms(5_000);
        internal int Count { get; private set; }
        internal long Clock() => Now;

        internal void Push(byte[] bytes)
        {
            Now += (long)(bytes.Length / (double)(16_000 * sizeof(float)) * System.Diagnostics.Stopwatch.Frequency);
            Capture.SnapshotData(bytes)();
            Count++;
        }

        // Знакочередующийся буфер амплитуды value (энергия в верхних частотах, как у шипящих).
        internal void PushTone(float value, int chunks = 1)
        {
            for (var chunk = 0; chunk < chunks; chunk++) Push(Tone(value));
        }

        // Речь, закодированная номером буфера: |sample| = (номер + 1) / 1000.
        internal void PushCoded(int chunks = 1)
        {
            for (var chunk = 0; chunk < chunks; chunk++) Push(Tone((Count + 1) / 1000f));
        }

        internal void PushQuiet(int chunks) => PushTone(1e-4f, chunks);
    }

    private static byte[] Tone(float value, int samples = ChunkSamples)
    {
        var bytes = new byte[samples * sizeof(float)];
        for (var index = 0; index < samples; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)),
                index % 2 == 0 ? value : -value);
        return bytes;
    }

    [Fact]
    public async Task Take_starts_at_press_timestamp_minus_preroll_not_at_start_call()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        var origin = feed.Now;
        feed.PushCoded(60); // 1,2 с «тёплого» звука
        var press = origin + Ms(1_010); // нажатие на 190 мс раньше текущего момента

        service.Start(press);
        feed.PushCoded(10);
        var release = feed.Now;
        var stop = service.StopAsync(release, CancellationToken.None);
        feed.PushQuiet(13);
        var result = await stop;

        // press - 500 мс = origin + 510 мс: буфер №25 (500..520 мс) с отступом 10 мс = 160 отсчётов.
        Assert.Equal(26 / 1000f, result.Samples[0]);
        Assert.Equal((83 - 25) * ChunkSamples - 160, result.Samples.Length);
    }

    [Fact]
    public async Task Take_never_reaches_back_before_the_end_of_the_previous_take()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushCoded(30);
        service.Start(feed.Now);
        feed.PushCoded(10);
        var firstStop = service.StopAsync(feed.Now, CancellationToken.None);
        feed.PushQuiet(13);
        await firstStop;
        var firstEnd = feed.Count;

        feed.PushCoded(3);
        // До нажатия 60 мс, а pre-roll 500 мс заходит в конец прошлой записи.
        service.Start(feed.Now - Ms(40));
        feed.PushCoded(5);
        var secondStop = service.StopAsync(feed.Now, CancellationToken.None);
        feed.PushQuiet(13);
        var second = await secondStop;

        Assert.Equal((firstEnd + 1) / 1000f, second.Samples[0]);
        Assert.Equal((3 + 5 + 13) * ChunkSamples, second.Samples.Length);
    }

    [Fact]
    public async Task Tail_waits_at_least_minimum_from_release_and_stops_on_silence()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushQuiet(10);
        service.Start(feed.Now);
        feed.PushTone(0.2f, 10);
        var stop = service.StopAsync(feed.Now, CancellationToken.None);

        feed.PushQuiet(12); // 240 мс: ещё меньше минимума 250 мс
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        feed.PushQuiet(1);   // 260 мс тишины: минимум выполнен и тишины >= 200 мс
        var result = await stop;

        Assert.Equal((10 + 10 + 13) * ChunkSamples, result.Samples.Length);
    }

    [Fact]
    public async Task Tail_is_counted_from_release_timestamp_not_from_the_stop_call()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushQuiet(10);
        service.Start(feed.Now);
        feed.PushTone(0.2f, 10);
        var release = feed.Now;
        feed.PushQuiet(13); // отпускание было 260 мс назад, StopAsync вызван с опозданием

        var result = await service.StopAsync(release, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal((10 + 10 + 13) * ChunkSamples, result.Samples.Length);
    }

    [Fact]
    public async Task Tail_never_exceeds_maximum_while_the_signal_continues()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushQuiet(10);
        service.Start(feed.Now);
        feed.PushTone(0.2f, 10);
        var stop = service.StopAsync(feed.Now, CancellationToken.None);

        feed.PushTone(0.2f, 44); // 880 мс
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        feed.PushTone(0.2f, 1);  // 900 мс
        var result = await stop;

        Assert.Equal((10 + 10 + 45) * ChunkSamples, result.Samples.Length);
    }

    [Fact]
    public async Task Fading_sibilant_extends_the_tail_until_real_silence()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushQuiet(10);
        service.Start(feed.Now);
        feed.PushTone(0.2f, 10);
        var stop = service.StopAsync(feed.Now, CancellationToken.None);

        feed.PushTone(0.01f, 14); // затухающее «с»: на 40 дБ выше шума, 280 мс
        await Task.Delay(50);
        Assert.False(stop.IsCompleted); // минимум уже выполнен, но это не тишина
        feed.PushQuiet(9);
        await Task.Delay(50);
        Assert.False(stop.IsCompleted); // тишины только 180 мс
        feed.PushQuiet(1);
        var result = await stop;

        Assert.Equal((10 + 10 + 14 + 10) * ChunkSamples, result.Samples.Length);
    }

    [Fact]
    public async Task Feedback_suppression_zeroes_the_window_but_keeps_length_and_timeline()
    {
        var capture = new ControlledCapture();
        using var rig = new CaptureRig(capture);
        var feed = new TimedFeed(capture);
        using var service = rig.CreateService(clock: feed.Clock);
        feed.PushQuiet(10);
        service.Start(feed.Now);
        feed.PushTone(0.2f, 1);
        service.SuppressFeedbackAudio(TimeSpan.FromMilliseconds(40));
        feed.PushTone(0.2f, 1); // конец через 20 мс: внутри окна подавления -> нули
        feed.PushTone(0.2f, 1); // конец ровно на границе 40 мс -> живой звук
        feed.PushTone(0.2f, 1);
        var stop = service.StopAsync(feed.Now, CancellationToken.None);
        feed.PushQuiet(13);
        var result = await stop;

        Assert.Equal((10 + 4 + 13) * ChunkSamples, result.Samples.Length);
        var window = result.Samples.Skip(10 * ChunkSamples + ChunkSamples).Take(ChunkSamples);
        Assert.True(window.All(sample => sample == 0f));
        Assert.Equal(0.2f, result.Samples[10 * ChunkSamples]);
        Assert.Equal(0.2f, result.Samples[10 * ChunkSamples + 2 * ChunkSamples]);
    }

    [Fact]
    public async Task Restart_on_the_same_device_keeps_the_pre_roll_ring()
    {
        var first = new ControlledCapture();
        var second = new ControlledCapture();
        using var rig = new CaptureRig(first, second);
        var feed = new TimedFeed(first);
        using var service = rig.CreateService(clock: feed.Clock, captureDeviceId: "default");
        feed.PushCoded(30);

        service.SelectCaptureDevice(null); // тот же "default", но эндпоинт переоткрывается
        Assert.Equal(2, rig.OpenCount);
        feed.Capture = second;

        service.Start(feed.Now);
        var stop = service.StopAsync(feed.Now, CancellationToken.None);
        feed.PushQuiet(13);
        var result = await stop;

        // pre-roll (500 мс = 25 буферов) пришёл из кольца прежнего эндпоинта.
        Assert.Equal((25 + 13) * ChunkSamples, result.Samples.Length);
        Assert.Equal(6 / 1000f, result.Samples[0]);
    }

    private static byte[] SyntheticPcm(float amplitude)
    {
        var bytes = new byte[1600 * sizeof(float)];
        for (var index = 0; index < 1600; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)),
                index % 2 == 0 ? amplitude : -amplitude);
        return bytes;
    }

    private sealed class CaptureRig(params ControlledCapture[] captures) : IDisposable
    {
        internal FakeCatalog Catalog { get; } = new();
        internal ControlledCapture[] Captures { get; } = captures;
        internal List<CountedHandle> Handles { get; } = [];
        internal int OpenCount { get; private set; }

        internal AudioCaptureService CreateService(
            bool startPaused = false, bool ownsCatalog = false, string? captureDeviceId = null,
            Func<long>? clock = null) => new(
            Catalog, ownsCatalog, persistCompletedTake: false, captureDeviceId, startPaused,
            selected =>
            {
                var capture = Captures[OpenCount++];
                var handle = new CountedHandle();
                Handles.Add(handle);
                return new(capture, handle, selected ?? Catalog.GetActiveDevices().Single(d => d.IsDefault).Id);
            },
            clock);

        public void Dispose()
        {
            foreach (var capture in Captures) capture.AllowDisposeFinish.Set();
        }
    }

    private sealed class CountedHandle : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FakeCatalog : IMicrophoneDeviceCatalog
    {
        private MicrophoneDeviceInfo[] _devices =
            [new("default", "Synthetic default", true), new("replacement", "Synthetic replacement", false)];
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public event EventHandler? DevicesChanged;
        public IReadOnlyList<MicrophoneDeviceInfo> GetActiveDevices() => Volatile.Read(ref _devices);
        public NAudio.CoreAudioApi.MMDevice OpenCaptureDevice(string? deviceId) =>
            throw new InvalidOperationException("The lifecycle fixture must never open microphone hardware.");
        internal void ChangeDefault(string id, bool notify = true)
        {
            Volatile.Write(ref _devices, _devices.Select(d => d with { IsDefault = d.Id == id }).ToArray());
            if (notify) Notify();
        }
        internal void RemoveAllDevices(bool notify = true)
        {
            Volatile.Write(ref _devices, []);
            if (notify) Notify();
        }
        internal void SetDevices(MicrophoneDeviceInfo[] devices)
        {
            Volatile.Write(ref _devices, devices);
            Notify();
        }
        internal void RestoreDevices(string defaultId) => SetDevices(
            [new("default", "Synthetic default", defaultId == "default"),
             new("replacement", "Synthetic replacement", defaultId == "replacement")]);
        internal void Notify() => DevicesChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ControlledCapture : NAudio.Wave.IWaveIn
    {
        private Thread? _callbackThread;
        private readonly ManualResetEventSlim _releaseCallback = new();
        private int _disposeCount;
        internal ManualResetEventSlim CallbackFinished { get; } = new();
        internal ManualResetEventSlim DisposeEntered { get; } = new();
        internal ManualResetEventSlim AllowDisposeFinish { get; } = new();
        internal bool HoldDispose { get; init; }
        internal bool ThrowAfterStartingCallback { get; init; }
        internal bool JoinTimedOut { get; private set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } =
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);
        public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;

        internal Action SnapshotData(byte[] bytes)
        {
            var queued = DataAvailable;
            return () => queued?.Invoke(this, new(bytes, bytes.Length));
        }
        internal Action SnapshotStopped()
        {
            var queued = RecordingStopped;
            return () => queued?.Invoke(this, new(new InvalidOperationException("Synthetic device failure")));
        }
        internal void RunOnCaptureThread(Action callback, bool waitForDispose = false)
        {
            _callbackThread = new Thread(() =>
            {
                try
                {
                    if (waitForDispose) _releaseCallback.Wait(TimeSpan.FromSeconds(4));
                    callback();
                }
                finally { CallbackFinished.Set(); }
            }) { IsBackground = true };
            _callbackThread.Start();
        }
        public void StartRecording()
        {
            StartCount++;
            if (!ThrowAfterStartingCallback) return;
            RunOnCaptureThread(SnapshotData(SyntheticPcm(0.2f)), waitForDispose: true);
            throw new InvalidOperationException("Synthetic partial start failure");
        }
        public void StopRecording() => StopCount++;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposeEntered.Set();
            _releaseCallback.Set();
            // This reproduces the blocking WasapiCapture.Dispose captureThread.Join boundary.
            if (_callbackThread is not null && !_callbackThread.Join(TimeSpan.FromSeconds(2))) JoinTimedOut = true;
            if (HoldDispose && !AllowDisposeFinish.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("Lifecycle test did not release the controlled cleanup.");
        }
    }

}
