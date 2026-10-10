using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class PushToTalkInputTests
{
    private static long Ms(double milliseconds) => PushToTalkTiming.FromMs(milliseconds);

    [Fact]
    public void DebounceWindowIsSeventyMilliseconds()
    {
        Assert.Equal(70, PushToTalkTiming.ReleaseDebounceMs);
        Assert.Equal(180, PushToTalkTiming.ShortTapMs);
    }

    [Fact]
    public void ReleaseIsDeferredUntilDebounceWindowPasses()
    {
        var debounce = new ButtonDebounce();
        Assert.Equal(DebounceDownResult.Press, debounce.OnDown(Ms(0)));
        Assert.True(debounce.OnUp(Ms(500)));

        Assert.False(debounce.TryCommitRelease(Ms(500 + PushToTalkTiming.ReleaseDebounceMs - 1), out _));
        Assert.True(debounce.TryCommitRelease(Ms(500 + PushToTalkTiming.ReleaseDebounceMs), out var args));
        Assert.Equal(Ms(0), args.PressTimestamp);
        Assert.Equal(Ms(500), args.ReleaseTimestamp);
        Assert.False(args.IsShortTap);
        Assert.False(debounce.IsHeld);
    }

    [Fact]
    public void PressRightAfterReleaseIsBounceAndKeepsHolding()
    {
        var debounce = new ButtonDebounce();
        debounce.OnDown(Ms(0));
        debounce.OnUp(Ms(400));

        Assert.Equal(DebounceDownResult.Bounce, debounce.OnDown(Ms(400 + PushToTalkTiming.ReleaseDebounceMs - 1)));

        Assert.True(debounce.IsHeld);
        Assert.False(debounce.ReleasePending);
        Assert.False(debounce.TryCommitRelease(Ms(1000), out _));

        // Настоящее отпускание после дребезга: время удержания считается от исходного нажатия.
        Assert.True(debounce.OnUp(Ms(900)));
        Assert.True(debounce.TryCommitRelease(Ms(1000), out var args));
        Assert.Equal(Ms(0), args.PressTimestamp);
        Assert.Equal(Ms(900), args.ReleaseTimestamp);
    }

    [Fact]
    public void PressAfterDebounceWindowIsNewPress()
    {
        var debounce = new ButtonDebounce();
        debounce.OnDown(Ms(0));
        debounce.OnUp(Ms(300));
        Assert.True(debounce.TryCommitRelease(Ms(300 + PushToTalkTiming.ReleaseDebounceMs + 5), out _));

        Assert.Equal(DebounceDownResult.Press, debounce.OnDown(Ms(500)));
        Assert.Equal(Ms(500), debounce.PressTimestamp);
    }

    [Fact]
    public void AutoRepeatWhileHeldIsNotAPress()
    {
        var debounce = new ButtonDebounce();
        debounce.OnDown(Ms(0));

        Assert.Equal(DebounceDownResult.Repeat, debounce.OnDown(Ms(30)));
        Assert.Equal(DebounceDownResult.Repeat, debounce.OnDown(Ms(60)));
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(179, true)]
    [InlineData(180, false)]
    [InlineData(600, false)]
    public void ShortTapFlagFollowsHeldDuration(double heldMs, bool expected)
    {
        var debounce = new ButtonDebounce();
        debounce.OnDown(Ms(0));
        debounce.OnUp(Ms(heldMs));
        Assert.True(debounce.TryCommitRelease(Ms(heldMs + 200), out var args));

        Assert.Equal(expected, args.IsShortTap);
        Assert.InRange(args.HeldMilliseconds, heldMs - 0.5, heldMs + 0.5);
    }

    [Fact]
    public void PressedEventArgsCarryNoRelease()
    {
        var args = new PushToTalkEventArgs(Ms(10), 0);

        Assert.Equal(Ms(10), args.PressTimestamp);
        Assert.Equal(0, args.ReleaseTimestamp);
        Assert.Equal(0, args.HeldMilliseconds);
        Assert.False(args.IsShortTap);
    }

    [Fact]
    public void ForceReleaseEndsHeldStateImmediately()
    {
        var debounce = new ButtonDebounce();
        debounce.OnDown(Ms(0));

        Assert.True(debounce.ForceRelease(Ms(800), out var args));
        Assert.Equal(Ms(800), args.ReleaseTimestamp);
        Assert.False(debounce.IsHeld);
        Assert.False(debounce.ForceRelease(Ms(900), out _));
    }

    [Fact]
    public void BounceOnMouseDoesNotStopRecordingStartedByKeyboardAndMouse()
    {
        // Двойной источник: клавиатура держит запись, у мыши дребезг — координатор ничего не видит.
        var coordinator = new PushToTalkCoordinator();
        var mouse = new ButtonDebounce();

        Assert.True(coordinator.Press(PushToTalkSource.Keyboard));
        Assert.Equal(DebounceDownResult.Press, mouse.OnDown(Ms(0)));
        Assert.False(coordinator.Press(PushToTalkSource.Mouse));

        mouse.OnUp(Ms(300));
        Assert.Equal(DebounceDownResult.Bounce, mouse.OnDown(Ms(320)));

        // Release мыши не дошёл до координатора, запись продолжается.
        Assert.False(mouse.TryCommitRelease(Ms(600), out _));
        Assert.False(coordinator.Release(PushToTalkSource.Keyboard));

        mouse.OnUp(Ms(700));
        Assert.True(mouse.TryCommitRelease(Ms(800), out _));
        Assert.True(coordinator.Release(PushToTalkSource.Mouse));
    }

    [Fact]
    public void BounceOnSingleMouseSourceKeepsRecordingUntilRealRelease()
    {
        var coordinator = new PushToTalkCoordinator();
        var mouse = new ButtonDebounce();

        Assert.Equal(DebounceDownResult.Press, mouse.OnDown(Ms(0)));
        Assert.True(coordinator.Press(PushToTalkSource.Mouse));

        mouse.OnUp(Ms(250));
        Assert.Equal(DebounceDownResult.Bounce, mouse.OnDown(Ms(270)));
        Assert.False(mouse.TryCommitRelease(Ms(400), out _));

        mouse.OnUp(Ms(1000));
        Assert.True(mouse.TryCommitRelease(Ms(1100), out var args));
        Assert.True(coordinator.Release(PushToTalkSource.Mouse));
        Assert.False(args.IsShortTap);
    }

    [Fact]
    public void TraceKeepsAtMostSixtyFourNewestEvents()
    {
        var trace = new PushToTalkEventTrace();
        for (var i = 0; i < 100; i++)
        {
            trace.Add(Ms(i * 10), i % 2 == 0 ? 'D' : 'U');
        }

        Assert.Equal(PushToTalkEventTrace.Capacity, trace.Count);
        var parts = trace.Format().Split(' ');
        Assert.Equal(64, parts.Length);
        Assert.Equal("D+0", parts[0]);   // старейшее из оставшихся (i = 36) — начало отсчёта
        Assert.Equal("U+630", parts[^1]);
    }

    [Fact]
    public void TraceFormatsEmptyAndClears()
    {
        var trace = new PushToTalkEventTrace();
        Assert.Equal("(empty)", trace.Format());

        trace.Add(Ms(0), 'D');
        trace.Add(Ms(95), 'U');
        Assert.Equal("D+0 U+95", trace.Format());

        trace.Clear();
        Assert.Equal("(empty)", trace.Format());
    }
}
