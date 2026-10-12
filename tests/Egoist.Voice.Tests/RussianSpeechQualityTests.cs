using Egoist.Voice.Services;
using Egoist.Voice.Core;

namespace Egoist.Voice.Tests;

public sealed class RussianSpeechQualityTests
{
    private static float[] Samples() => Enumerable.Range(0, 16_000).Select(i => .1f * MathF.Sin(i * .08f)).ToArray();

    [Fact]
    public async Task Same_pcm_reaches_both_engines_and_only_audio_punctuation_is_borrowed()
    {
        var pcm = Samples(); var original = pcm.ToArray();
        var primary = new FakeEngine("мой друг как дела");
        var formatter = new FakeEngine("Мой друг, как дела?");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(pcm, 16_000, CancellationToken.None);
        Assert.Equal("Мой друг, как дела?", result.Text);
        Assert.Equal(original, pcm);
        Assert.Equal(primary.LastSamples, formatter.LastSamples);
        Assert.Equal(original, primary.LastSamples);
        Assert.True(service.FormattingAvailable);
        Assert.Equal(AudioFormattingStatus.Completed, result.AudioFormatting);
    }

    [Fact]
    public async Task Short_memory_dictation_reuses_original_samples_without_a_recording_copy()
    {
        var pcm = new float[320_000];
        var primary = new FakeEngine("исходные слова");
        var formatter = new FakeEngine("Исходные слова.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(pcm, 16_000, CancellationToken.None);
        Assert.Same(pcm, primary.ReceivedSamples);
        Assert.Same(pcm, formatter.ReceivedSamples);
        Assert.Equal("Исходные слова.", result.Text);
        Assert.Equal(primary.LastSamples, formatter.LastSamples);
    }

    [Fact]
    public async Task Empty_primary_never_inserts_a_hallucinated_secondary_phrase()
    {
        var primary = new FakeEngine("");
        var formatter = new FakeEngine("Продолжение следует.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Empty_primary_still_observes_a_failing_formatter_without_disabling_it()
    {
        var primary = new FakeEngine("");
        var formatter = new FakeEngine("x") { DecodeFailure = new InvalidOperationException("unused result") };
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal("", result.Text);
        Assert.Equal(1, formatter.DecodeCalls);
        Assert.True(service.FormattingAvailable);
    }

    [Fact]
    public async Task Both_engines_decode_the_same_chunk_concurrently()
    {
        var primaryIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var formatterIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine("мой друг как дела")
        {
            BeforeDecode = async _ => { primaryIn.SetResult(); await formatterIn.Task; }
        };
        var formatter = new FakeEngine("Мой друг, как дела?")
        {
            BeforeDecode = async _ => { formatterIn.SetResult(); await primaryIn.Task; }
        };
        using var service = new RussianSpeechQualityService(primary, formatter);
        // Each decode waits for the other to start: this only completes when they overlap.
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Мой друг, как дела?", result.Text);
    }

    [Fact]
    public async Task Parallel_result_equals_the_sequential_composition()
    {
        var primary = new FakeEngine("открой гитхаб");
        var formatter = new FakeEngine("Открой GitHub.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        var expected = AudioConfirmedNameFormatter.Apply(
            AudioTranscriptComposer.Compose("открой гитхаб", "Открой GitHub."), "Открой GitHub.");
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task Failing_formatter_is_observed_when_primary_also_fails()
    {
        var primary = new FakeEngine("слова") { DecodeFailure = new InvalidOperationException("primary") };
        var formatter = new FakeEngine("Слова.") { DecodeFailure = new InvalidOperationException("formatter") };
        using var service = new RussianSpeechQualityService(primary, formatter);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None));
        Assert.Equal("primary", error.Message);
        Assert.Equal(1, formatter.DecodeCalls);
    }

    [Fact]
    public async Task Cancellation_reaches_both_concurrent_engines()
    {
        using var cancel = new CancellationTokenSource();
        var started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<int, Task> gate = async _ =>
        {
            if (Interlocked.Increment(ref started) == 2) both.SetResult();
            await Task.Delay(Timeout.Infinite, cancel.Token);
        };
        var primary = new FakeEngine("слова") { BeforeDecode = gate };
        var formatter = new FakeEngine("Слова.") { BeforeDecode = gate };
        using var service = new RussianSpeechQualityService(primary, formatter);
        var request = service.TranscribeSamplesAsync(Samples(), 16_000, cancel.Token);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Warmup_loads_both_models_concurrently()
    {
        var primaryIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var formatterIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine("a") { BeforeWarmup = async _ => { primaryIn.SetResult(); await formatterIn.Task; } };
        var formatter = new FakeEngine("b") { BeforeWarmup = async _ => { formatterIn.SetResult(); await primaryIn.Task; } };
        using var service = new RussianSpeechQualityService(primary, formatter);
        await service.WarmUpAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.PrimaryAvailable);
        Assert.True(service.FormattingAvailable);
    }

    [Fact]
    public async Task Failed_primary_warmup_does_not_leave_the_formatter_task_unobserved()
    {
        var primary = new FakeEngine("a") { WarmupFailure = new InvalidOperationException("primary") };
        var formatter = new FakeEngine("b") { WarmupFailure = new InvalidOperationException("formatter") };
        using var service = new RussianSpeechQualityService(primary, formatter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WarmUpAsync(null, CancellationToken.None));
        Assert.False(service.PrimaryAvailable);
        Assert.False(service.FormattingAvailable);
    }

    [Fact]
    public async Task Repeated_warmup_after_early_warmup_does_not_reload_ready_state()
    {
        var primary = new FakeEngine("a"); var formatter = new FakeEngine("b");
        using var service = new RussianSpeechQualityService(primary, formatter);
        await service.WarmUpAsync(null, CancellationToken.None);
        await service.WarmUpAsync(null, CancellationToken.None);
        Assert.Equal(1, formatter.WarmupCalls);
        Assert.True(service.PrimaryAvailable && service.FormattingAvailable);
    }

    [Fact]
    public async Task Disabled_formatting_does_not_load_or_decode_the_second_model()
    {
        var primary = new FakeEngine("весь исходный текст");
        var formatter = new FakeEngine("Весь исходный текст.");
        using var service = new RussianSpeechQualityService(primary, formatter) { FormatSpeechPunctuation = false };
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal("весь исходный текст", result.Text);
        Assert.Equal(0, formatter.WarmupCalls); Assert.Equal(0, formatter.DecodeCalls);
        Assert.Equal(AudioFormattingStatus.NotRequested, result.AudioFormatting);
    }

    [Fact]
    public async Task Formatting_failure_keeps_words_and_retries_after_backoff()
    {
        long ticks = 0;
        var primary = new FakeEngine("проверь сообщение");
        var formatter = new FakeEngine("Проверь сообщение.") { DecodeFailure = new InvalidOperationException("test") };
        using var service = new RussianSpeechQualityService(primary, formatter, () => ticks);
        Assert.Equal("проверь сообщение", (await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None)).Text);
        Assert.False(service.FormattingAvailable); Assert.Equal(1, formatter.DecodeCalls);
        formatter.DecodeFailure = null;
        Assert.Equal("проверь сообщение", (await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None)).Text);
        Assert.Equal(1, formatter.DecodeCalls);
        ticks = 30_000;
        Assert.Equal("Проверь сообщение.", (await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None)).Text);
        Assert.Equal(2, formatter.DecodeCalls); Assert.True(service.FormattingAvailable);
    }

    [Fact]
    public async Task Known_name_is_canonicalized_only_when_both_audio_decodes_agree_on_the_phrase()
    {
        var primary = new FakeEngine("\u043e\u0442\u043a\u0440\u043e\u0439 \u0433\u0438\u0442\u0445\u0430\u0431");
        var formatter = new FakeEngine("\u041e\u0442\u043a\u0440\u043e\u0439 GitHub.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal("\u041e\u0442\u043a\u0440\u043e\u0439 GitHub.", result.Text);
        Assert.Equal(primary.LastSamples, formatter.LastSamples);
    }

    [Fact]
    public async Task Failed_audio_formatting_is_reported_even_when_primary_words_succeed()
    {
        var primary = new FakeEngine("\u0441\u043b\u043e\u0432\u0430");
        var formatter = new FakeEngine("unused") { WarmupFailure = new InvalidOperationException("model failure") };
        using var service = new RussianSpeechQualityService(primary, formatter);
        var result = await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal("\u0441\u043b\u043e\u0432\u0430", result.Text);
        Assert.Equal(AudioFormattingStatus.Unavailable, result.AudioFormatting);
        Assert.True(service.PrimaryAvailable);
        Assert.False(service.FormattingAvailable);
    }

    [Fact]
    public async Task Missing_formatter_does_not_break_primary_dictation()
    {
        var primary = new FakeEngine("исходные слова");
        var formatter = new FakeEngine("wrong") { WarmupFailure = new InvalidOperationException("missing model") };
        using var service = new RussianSpeechQualityService(primary, formatter);
        Assert.Equal("исходные слова", (await service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None)).Text);
        Assert.False(service.FormattingAvailable); Assert.Equal(0, formatter.DecodeCalls);
    }

    [Fact]
    public async Task Cancel_during_native_like_decode_is_rechecked_before_output()
    {
        using var cancel = new CancellationTokenSource();
        var primary = new FakeEngine("слова");
        var formatter = new FakeEngine("Слова.") { AfterDecode = cancel.Cancel };
        using var service = new RussianSpeechQualityService(primary, formatter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranscribeSamplesAsync(Samples(), 16_000, cancel.Token));
    }

    [Fact]
    public async Task Concurrent_requests_are_serialized_and_return_their_own_words()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine("первый текст")
        {
            BeforeDecode = async call => { if (call == 1) { firstStarted.SetResult(); await release.Task; } }
        };
        var formatter = new FakeEngine("Первый текст.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var first = service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        Assert.Equal(1, primary.DecodeCalls);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, x => Assert.Equal("Первый текст.", x.Text));
        Assert.Equal(2, primary.DecodeCalls); Assert.Equal(2, formatter.DecodeCalls);
    }

    [Fact]
    public async Task Incorrect_sample_rate_fails_before_loading_any_model()
    {
        var primary = new FakeEngine("text"); var formatter = new FakeEngine("text");
        using var service = new RussianSpeechQualityService(primary, formatter);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TranscribeSamplesAsync(Samples(), 48_000, CancellationToken.None));
        Assert.Equal(0, primary.WarmupCalls);
    }

    [Fact]
    public async Task Warmup_is_serialized_with_decode_and_cannot_override_newer_formatter_backoff()
    {
        var warming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine("исходные слова");
        var formatter = new FakeEngine("Исходные слова.")
        {
            WarmupFailure = null,
            DecodeFailure = new InvalidOperationException("synthetic formatter failure"),
            BeforeWarmup = async call =>
            {
                if (call == 1)
                {
                    warming.SetResult();
                    await release.Task;
                }
            }
        };
        using var service = new RussianSpeechQualityService(primary, formatter, () => 0);
        var startupWarmup = service.WarmUpAsync(null, CancellationToken.None);
        try
        {
            await warming.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
            // A completed second fake warmup would otherwise let this request run before
            // startup warmup finishes and later erase the decode failure's readiness/backoff.
            var warmupCallsWhileBlocked = formatter.WarmupCalls;
            release.SetResult();
            await startupWarmup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("исходные слова", (await request.WaitAsync(TimeSpan.FromSeconds(5))).Text);
            Assert.Equal(1, warmupCallsWhileBlocked);
            Assert.False(service.FormattingAvailable);
            formatter.DecodeFailure = null;
            Assert.Equal("исходные слова", (await service.TranscribeSamplesAsync(
                Samples(), 16_000, CancellationToken.None)).Text);
            Assert.Equal(1, formatter.DecodeCalls);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Disposing_during_native_like_decode_cannot_return_a_successful_transcript()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine("исходные слова")
        {
            BeforeDecode = async _ => { started.SetResult(); await release.Task; }
        };
        var formatter = new FakeEngine("Исходные слова.");
        using var service = new RussianSpeechQualityService(primary, formatter);
        var request = service.TranscribeSamplesAsync(Samples(), 16_000, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
            release.SetResult();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class FakeEngine(string text) : ITranscriptionEngine, ISampleTranscriptionService
    {
        public string EngineName => "Synthetic test engine";
        public int WarmupCalls;
        public int DecodeCalls;
        public float[]? LastSamples;
        public float[]? ReceivedSamples;
        public Exception? WarmupFailure;
        public Exception? DecodeFailure;
        public Action? AfterDecode;
        public Func<int, Task>? BeforeDecode;
        public Func<int, Task>? BeforeWarmup;
        public async Task WarmUpAsync(IProgress<ModelProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref WarmupCalls);
            if (BeforeWarmup is not null) await BeforeWarmup(call);
            if (WarmupFailure is not null) throw WarmupFailure;
        }
        public async Task<TranscriptionResult> TranscribeSamplesAsync(float[] samples, int sampleRate, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref DecodeCalls); ReceivedSamples = samples; LastSamples = samples.ToArray();
            if (BeforeDecode is not null) await BeforeDecode(call);
            if (DecodeFailure is not null) throw DecodeFailure;
            AfterDecode?.Invoke(); return new TranscriptionResult(text, TimeSpan.Zero);
        }
        public Task<TranscriptionResult> TranscribeAsync(string audioPath, IProgress<ModelProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
