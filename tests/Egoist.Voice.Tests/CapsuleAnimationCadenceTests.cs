using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Egoist.Voice.Controls;

namespace Egoist.Voice.Tests;

public sealed class CapsuleAnimationCadenceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void Fast_displays_keep_the_meter_near_sixty_updates_per_second(int refreshRate)
    {
        var cadence = new CapsuleAnimationCadence();
        var accepted = 0;
        for (var frame = 0; frame < refreshRate * 2; frame++)
        {
            if (cadence.TryAdvance(TimeSpan.FromSeconds((double)frame / refreshRate), false, out _))
                accepted++;
        }
        Assert.InRange(accepted, 119, 121);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    [InlineData(240, 144)]
    [InlineData(165, 144)]
    public void Target_rate_follows_min_of_refresh_and_144(int refreshRate, int expectedRate)
    {
        Assert.Equal(expectedRate, Motion.ResolveFrameRate(refreshRate));
        var cadence = new CapsuleAnimationCadence { TargetFps = Motion.ResolveFrameRate(refreshRate) };
        var accepted = 0;
        for (var frame = 0; frame < refreshRate * 2; frame++)
        {
            if (cadence.TryAdvance(TimeSpan.FromSeconds((double)frame / refreshRate), false, out _))
                accepted++;
        }
        Assert.InRange(accepted, expectedRate * 2 - 3, expectedRate * 2 + 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Unknown_refresh_falls_back_to_sixty(int refreshRate) =>
        Assert.Equal(60, Motion.ResolveFrameRate(refreshRate));

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void Wave_time_is_independent_of_the_frame_rate(int refreshRate)
    {
        var cadence = new CapsuleAnimationCadence { TargetFps = refreshRate };
        var total = 0d;
        var last = 0d;
        for (var frame = 0; frame <= refreshRate * 3; frame++)
        {
            var now = (double)frame / refreshRate;
            if (cadence.TryAdvance(TimeSpan.FromSeconds(now), false, out var delta) && frame > 0)
            {
                total += delta;
                last = now;
            }
        }
        // Фаза растёт по реальным секундам: сумма шагов равна прошедшему времени при любой частоте.
        Assert.InRange(total, last - 0.02, last + 0.02);
        Assert.InRange(total, 2.95, 3.01);
    }

    [Fact]
    public void Frame_drops_do_not_jerk_the_phase()
    {
        var cadence = new CapsuleAnimationCadence { TargetFps = 120 };
        cadence.TryAdvance(TimeSpan.Zero, false, out _);
        // Кадр пропущен (3 интервала): шаг растёт пропорционально времени, а не остаётся «кадровым».
        Assert.True(cadence.TryAdvance(TimeSpan.FromSeconds(3d / 120), false, out var delta));
        Assert.Equal(3d / 120, delta, precision: 4);
    }

    [Fact]
    public void Target_rate_can_change_between_takes_without_losing_the_clock()
    {
        var cadence = new CapsuleAnimationCadence { TargetFps = 60 };
        Assert.True(cadence.TryAdvance(TimeSpan.Zero, false, out _));
        cadence.TargetFps = 144;
        Assert.True(cadence.TryAdvance(TimeSpan.FromSeconds(1d / 144), false, out var delta));
        Assert.InRange(delta, 1d / 240, 1d / 60);
    }

    [Fact]
    public void Frame_stats_report_percentiles_and_slow_frames()
    {
        var stats = new FrameIntervalStats();
        var time = TimeSpan.Zero;
        for (var frame = 0; frame < 200; frame++)
        {
            stats.Record(time);
            stats.Record(time); // повтор события с тем же временем не считается кадром
            time += TimeSpan.FromMilliseconds(frame == 100 ? 30 : 1000d / 120);
        }
        var summary = stats.Summarize(120);
        Assert.NotNull(summary);
        Assert.Contains("frames=200", summary);
        Assert.Contains("p50=8.33ms", summary);
        Assert.Contains("slow(>1.5x)=1", summary);
        stats.Reset();
        Assert.Null(stats.Summarize(120));
    }

    [Fact]
    public void Motion_tokens_in_xaml_match_the_code_constants()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MotionTokens.xaml")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var xaml = File.ReadAllText(Path.Combine(root, "MotionTokens.xaml"));
        void Check(string key, int milliseconds)
        {
            var match = System.Text.RegularExpressions.Regex.Match(xaml, $"x:Key=\"{key}\">0:0:([0-9.]+)<");
            Assert.True(match.Success, key);
            Assert.Equal(milliseconds, (int)Math.Round(double.Parse(match.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) * 1000));
        }
        Check("MotionEnter", Motion.EnterMs);
        Check("MotionExit", Motion.ExitMs);
        Check("MotionContent", Motion.ContentMs);
        Check("MotionDisc", Motion.DiscMs);
        Check("MotionRing", Motion.RingMs);
        Check("MotionReduced", Motion.ReducedMs);
    }

    [Fact]
    public void Duplicate_composition_events_are_ignored_even_at_time_zero()
    {
        var cadence = new CapsuleAnimationCadence();
        Assert.True(cadence.TryAdvance(TimeSpan.Zero, false, out _));
        Assert.False(cadence.TryAdvance(TimeSpan.Zero, false, out _));
    }

    [Fact]
    public void Reduced_motion_samples_level_ten_times_per_second()
    {
        var cadence = new CapsuleAnimationCadence();
        var accepted = 0;
        for (var frame = 0; frame < 240; frame++)
        {
            if (cadence.TryAdvance(TimeSpan.FromSeconds(frame / 120d), true, out var delta))
            {
                accepted++;
                Assert.InRange(delta, 1d / 240d, 0.2);
            }
        }
        Assert.InRange(accepted, 20, 21);
    }

    [Fact]
    public void A_dispatcher_pause_skips_missed_frames_without_catching_up()
    {
        var cadence = new CapsuleAnimationCadence();
        cadence.TryAdvance(TimeSpan.Zero, false, out _);
        Assert.True(cadence.TryAdvance(TimeSpan.FromSeconds(5), false, out var delta));
        Assert.Equal(0.05, delta);
        Assert.False(cadence.TryAdvance(TimeSpan.FromSeconds(5.001), false, out _));
    }

    [Fact]
    public void Motion_setting_and_restart_apply_on_the_next_frame()
    {
        var cadence = new CapsuleAnimationCadence();
        cadence.TryAdvance(TimeSpan.Zero, false, out _);
        Assert.True(cadence.TryAdvance(TimeSpan.FromMilliseconds(1), true, out var delta));
        Assert.Equal(0.1, delta);
        cadence.Reset();
        Assert.True(cadence.TryAdvance(TimeSpan.FromMilliseconds(2), false, out _));
    }

    [Fact]
    public void A_silent_waveform_does_not_request_repeated_retained_drawings() => RunSta(() =>
    {
        var waveform = new CapsuleWaveform();
        for (var frame = 0; frame < 600; frame++)
            waveform.Advance(0, frame, 1d / 60d, false);
        Assert.Equal(0, waveform.RedrawRequestCount);
        waveform.Advance(0.8, 1, 1d / 60d, false);
        Assert.Equal(1, waveform.RedrawRequestCount);
    });

    [Fact]
    public void Static_reduced_motion_level_settles_without_perpetual_redraws() => RunSta(() =>
    {
        var waveform = new CapsuleWaveform();
        for (var frame = 0; frame < 60; frame++)
            waveform.Advance(0.5, frame, 0.1, true);
        var settled = waveform.RedrawRequestCount;
        for (var frame = 0; frame < 60; frame++)
            waveform.Advance(0.5, frame, 0.1, true);
        Assert.Equal(settled, waveform.RedrawRequestCount);
        Assert.InRange(settled, 1, 12);
    });

    [Fact]
    public void Non_focusable_status_surface_has_an_automation_peer_and_name() => RunSta(() =>
    {
        var surface = new CapsuleStatusSurface { Focusable = false };
        AutomationProperties.SetName(surface, "Запись идёт");
        AutomationProperties.SetLiveSetting(surface, AutomationLiveSetting.Polite);
        var peer = UIElementAutomationPeer.CreatePeerForElement(surface);
        Assert.NotNull(peer);
        Assert.Equal(AutomationControlType.StatusBar, peer.GetAutomationControlType());
        Assert.Equal("Запись идёт", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Polite, peer.GetLiveSetting());
        Assert.False(surface.Focusable);
    });

    [Fact]
    public void Zero_stroke_means_no_hairline_in_the_flat_brand_surface()
    {
        var profile = PixelPerfectCapsuleBorder.CalculateRasterProfile(256, 48, 1.5, 1.5, 0, 4);
        Assert.Equal(0, profile.StrokeDip);
    }

    [Fact]
    public void Hidden_request_is_suspended_and_resumes_once_without_duplicate_handlers()
    {
        var attached = 0;
        var detached = 0;
        var lifetime = new CapsuleAnimationSubscription(() => attached++, () => detached++);
        lifetime.Start(eligible: false);
        Assert.True(lifetime.IsRequested);
        Assert.False(lifetime.IsAttached);
        lifetime.Refresh(eligible: true);
        lifetime.Start(eligible: true);
        lifetime.Refresh(eligible: true);
        Assert.Equal(1, attached);
        lifetime.Refresh(eligible: false);
        lifetime.Refresh(eligible: false);
        Assert.Equal(1, detached);
        Assert.True(lifetime.IsRequested);
        lifetime.Refresh(eligible: true);
        Assert.Equal(2, attached);
        Assert.True(lifetime.IsAttached);
    }

    [Fact]
    public void Stopping_a_hidden_or_minimized_request_prevents_resubscription_after_restore()
    {
        var attached = 0;
        var detached = 0;
        var lifetime = new CapsuleAnimationSubscription(() => attached++, () => detached++);
        lifetime.Start(eligible: true);
        lifetime.Refresh(eligible: false);
        lifetime.Stop();
        lifetime.Stop();
        lifetime.Refresh(eligible: true);
        Assert.Equal(1, attached);
        Assert.Equal(1, detached);
        Assert.False(lifetime.IsRequested);
        Assert.False(lifetime.IsAttached);
    }

    [Fact]
    public void A_new_take_after_stop_has_one_subscription_and_a_fresh_frame_clock()
    {
        var cadence = new CapsuleAnimationCadence();
        var attached = 0;
        var detached = 0;
        var lifetime = new CapsuleAnimationSubscription(
            () => { cadence.Reset(); attached++; },
            () => { cadence.Reset(); detached++; });
        lifetime.Start(eligible: true);
        Assert.True(cadence.TryAdvance(TimeSpan.Zero, false, out _));
        lifetime.Refresh(eligible: false);
        lifetime.Refresh(eligible: true);
        Assert.True(cadence.TryAdvance(TimeSpan.FromSeconds(30), false, out var resumedDelta));
        Assert.Equal(1d / 60d, resumedDelta, precision: 6);
        Assert.False(cadence.TryAdvance(TimeSpan.FromSeconds(30), false, out _));
        lifetime.Stop();
        lifetime.Start(eligible: true);
        Assert.True(cadence.TryAdvance(TimeSpan.FromSeconds(60), true, out var newDelta));
        Assert.Equal(0.1, newDelta);
        Assert.Equal(3, attached);
        Assert.Equal(2, detached);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF test dispatcher timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
