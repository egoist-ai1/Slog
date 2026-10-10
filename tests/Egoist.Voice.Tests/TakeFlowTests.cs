using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

/// <summary>Очередь доставки, TakeContext, порог «Подключаю» и повтор старта при неготовой аудио-службе.</summary>
public sealed class TakeSequencerTests
{
    [Fact]
    public async Task Later_take_waits_for_all_earlier_takes_in_release_order()
    {
        var sequencer = new TakeSequencer();
        var first = sequencer.Enter();
        var second = sequencer.Enter();
        var third = sequencer.Enter();

        var secondTurn = second.WaitForTurnAsync(CancellationToken.None);
        var thirdTurn = third.WaitForTurnAsync(CancellationToken.None);
        Assert.True(first.WaitForTurnAsync(CancellationToken.None).IsCompletedSuccessfully);
        Assert.False(secondTurn.IsCompleted);

        // Вторая закончила раньше первой — третья всё равно ждёт первую.
        second.Complete();
        await Task.Delay(30);
        Assert.False(secondTurn.IsCompleted);
        Assert.False(thirdTurn.IsCompleted);

        first.Complete();
        await secondTurn.WaitAsync(TimeSpan.FromSeconds(2));
        await thirdTurn.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_block_following_takes()
    {
        var sequencer = new TakeSequencer();
        var first = sequencer.Enter();
        var second = sequencer.Enter();
        using var cancellation = new CancellationTokenSource();
        var waiting = second.WaitForTurnAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => waiting);
        second.Complete();
        first.Complete();
        await sequencer.Enter().WaitForTurnAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Take_keeps_its_own_target_and_token_after_dispose()
    {
        using var lifetime = new CancellationTokenSource();
        var first = new TakeContext(1, 100, (nint)111, lifetime.Token);
        var second = new TakeContext(2, 200, (nint)222, lifetime.Token);

        first.Cancel();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.Equal((nint)111, first.TargetWindow);
        Assert.Equal((nint)222, second.TargetWindow);

        first.Dispose();
        first.Cancel();
        Assert.True(first.Token.IsCancellationRequested);
        second.Dispose();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(149, false)]
    [InlineData(150, true)]
    [InlineData(400, true)]
    public void Connecting_text_only_after_threshold_and_only_while_start_pending(int milliseconds, bool expected)
    {
        Assert.Equal(expected, ConnectingIndicatorPolicy.ShouldShow(TimeSpan.FromMilliseconds(milliseconds), true));
        Assert.False(ConnectingIndicatorPolicy.ShouldShow(TimeSpan.FromMilliseconds(milliseconds), false));
    }

    [Fact]
    public void Short_tap_is_decided_by_press_and_release_timestamps()
    {
        using var lifetime = new CancellationTokenSource();
        var take = new TakeContext(1, 1_000, 0, lifetime.Token);
        Assert.True(take.IsShortTapAt(1_000 + PushToTalkTiming.FromMs(100)));
        Assert.False(take.IsShortTapAt(1_000 + PushToTalkTiming.FromMs(400)));
        Assert.False(take.IsShortTapAt(0));
        take.Dispose();
    }
}

public sealed class MicrophoneStartupRetryTests
{
    private static COMException ServiceNotReady() => new("not ready", MicrophoneFailure.AudioServiceNotReadyHResult);

    [Fact]
    public void Delays_grow_from_half_a_second_to_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.5), MicrophoneStartupRetry.Delay(1));
        var previous = TimeSpan.Zero;
        for (var retry = 1; retry <= MicrophoneStartupRetry.MaximumRetries; retry++)
        {
            var delay = MicrophoneStartupRetry.Delay(retry);
            Assert.True(delay >= previous && delay <= TimeSpan.FromSeconds(5));
            previous = delay;
        }
        Assert.Equal(TimeSpan.FromSeconds(5), previous);
    }

    [Fact]
    public async Task Retries_until_audio_service_is_ready_and_reports_each_wait()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var notified = new List<int>();
        var result = await MicrophoneStartupRetry.CreateAsync(
            () => ++attempts < 4 ? throw ServiceNotReady() : Task.FromResult("ok"),
            notified.Add,
            (delay, _) => { waits.Add(delay); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(4, attempts);
        Assert.Equal([1, 2, 3], notified);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public async Task Gives_up_after_ten_retries_and_rethrows()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<COMException>(() => MicrophoneStartupRetry.CreateAsync<string>(
            () => { attempts++; throw ServiceNotReady(); },
            null,
            (_, _) => Task.CompletedTask,
            CancellationToken.None));
        Assert.Equal(MicrophoneStartupRetry.MaximumRetries + 1, attempts);
    }

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => MicrophoneStartupRetry.CreateAsync<string>(
            () => { attempts++; throw new InvalidOperationException("другая ошибка"); },
            null,
            (_, _) => Task.CompletedTask,
            CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Wrapped_service_not_ready_is_still_recognised()
    {
        Assert.True(MicrophoneFailure.IsAudioServiceNotReady(new InvalidOperationException("x", ServiceNotReady())));
    }

    [Fact]
    public void Missing_and_busy_devices_get_different_messages()
    {
        var missing = MicrophoneFailure.Message(new MicrophoneUnavailableException("Системный микрофон сейчас недоступен."));
        var busy = MicrophoneFailure.Message(new COMException("busy", unchecked((int)0x8889000A)));
        Assert.Equal("Микрофон не найден", missing);
        Assert.Equal("Микрофон занят или недоступен", busy);
        Assert.NotEqual(missing, busy);
    }
}

[Collection("Actual capture window")]
public sealed class MainWindowTakeFlowTests(CaptureWindowDispatcher dispatcher)
{
    private sealed class RecordingInsertion : ITextInsertionService
    {
        internal ConcurrentQueue<(string Text, nint Target)> Inserted { get; } = new();

        public Task InsertAsync(string text, nint targetWindow, CancellationToken cancellationToken)
        {
            Inserted.Enqueue((text, targetWindow));
            return Task.CompletedTask;
        }
    }

    private sealed class NullClipboard : IClipboardService
    {
        public Task CopyAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public Task New_recording_starts_while_previous_is_recognizing_and_inserts_in_order_to_own_windows() =>
        dispatcher.RunAsync(async () =>
        {
            var insertion = new RecordingInsertion();
            await using var f = new WindowFixture(
                configure: capture => capture.ReturnSpeech = true,
                delivery: new DictationDeliveryService(new NullClipboard(), insertion) { RestoreClipboard = false });
            await f.ReadyAsync();
            var firstGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transcription.SampleHandler = (call, _) => call == 1 ? firstGate.Task : Task.FromResult("второй");

            f.Foreground = 111;
            f.Press(PushToTalkSource.Keyboard);
            var firstRelease = f.ReleaseAsync(PushToTalkSource.Keyboard);
            await WaitUntil(() => f.Transcription.SampleCalls == 1);

            // Первая фраза ещё распознаётся — вторая запись разрешена и берёт своё окно-цель.
            Assert.True(f.Window.IsProcessing);
            Assert.True(f.Window.CanStartRecording);
            f.Foreground = 222;
            f.Press(PushToTalkSource.Keyboard);
            Assert.True(f.Window.IsRecording);
            Assert.Equal((nint)222, f.Window.CurrentTargetWindow);
            var secondRelease = f.ReleaseAsync(PushToTalkSource.Keyboard);
            await WaitUntil(() => f.Transcription.SampleCalls == 2);

            // Вторая уже распознана, но вставлять её нельзя раньше первой.
            await Task.Delay(100);
            Assert.True(insertion.Inserted.IsEmpty);

            firstGate.SetResult("первый");
            await Task.WhenAll(firstRelease, secondRelease).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [("первый", (nint)111), ("второй", (nint)222)],
                insertion.Inserted.ToArray());
            Assert.Equal(2, f.Capture.StartCount);
            Assert.False(f.Window.IsProcessing);
            Assert.Equal(0, f.Window.ActiveTakeCount);
        });

    [Fact]
    public Task Failure_of_one_take_does_not_break_the_next() => dispatcher.RunAsync(async () =>
    {
        var insertion = new RecordingInsertion();
        await using var f = new WindowFixture(
            configure: capture => capture.ReturnSpeech = true,
            delivery: new DictationDeliveryService(new NullClipboard(), insertion) { RestoreClipboard = false });
        await f.ReadyAsync();
        f.Transcription.SampleHandler = (call, _) =>
            call == 1 ? throw new InvalidOperationException("сбой модели") : Task.FromResult("после сбоя");

        f.Press(PushToTalkSource.Keyboard);
        await f.ReleaseAsync(PushToTalkSource.Keyboard);
        f.Press(PushToTalkSource.Keyboard);
        await f.ReleaseAsync(PushToTalkSource.Keyboard);

        Assert.Equal(["после сбоя"], insertion.Inserted.Select(item => item.Text).ToArray());
        Assert.Equal(0, f.Window.ActiveTakeCount);
    });

    [Fact]
    public Task Slow_start_shows_connecting_label_only_after_threshold() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        var gate = f.Capture.Block("start");
        var start = f.Window.ToggleRecordingAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // До порога подписи нет: компактное состояние.
            Assert.Equal(CapsuleVisualStateKind.Arming, f.Window.CurrentVisualKind);
            Assert.Null(f.Window.CurrentVisualLabel);
            await WaitUntil(() => f.Window.CurrentVisualLabel == "Подключаю");
            Assert.Equal(CapsuleVisualStateKind.Recognizing, f.Window.CurrentVisualKind);
        }
        finally { gate.Release.Set(); }
        await start;
        Assert.Equal(CapsuleVisualStateKind.Listening, f.Window.CurrentVisualKind);
        await f.CancelAsync();
    });

    [Fact]
    public Task Fast_start_goes_straight_to_listening_without_connecting_label() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        await f.Window.ToggleRecordingAsync();
        Assert.Equal(CapsuleVisualStateKind.Listening, f.Window.CurrentVisualKind);
        Assert.NotEqual("Подключаю", f.Window.CurrentVisualLabel);
        await f.CancelAsync();
    });

    [Fact]
    public Task Timestamps_reach_capture_and_short_tap_hides_quietly() => dispatcher.RunAsync(async () =>
    {
        await using var f = new WindowFixture(); await f.ReadyAsync();
        f.Press(PushToTalkSource.Mouse);
        await f.ReleaseAsync(PushToTalkSource.Mouse);

        Assert.NotEqual(0, f.Capture.LastPressTimestamp);
        Assert.True(f.Capture.LastReleaseTimestamp >= f.Capture.LastPressTimestamp);
        // Без речи короткий тап не оставляет красной ошибки.
        Assert.NotEqual(CapsuleVisualStateKind.Error, f.Window.CurrentVisualKind);
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Условие не выполнилось за 5 с.");
            await Task.Delay(10);
        }
    }
}
