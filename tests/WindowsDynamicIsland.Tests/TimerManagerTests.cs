using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Timing;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class TimerManagerTests
{
    private static (TestEnvironment Env, FakeClock Clock, TimerManager Timer, EventRecorder<TimerStateChanged> States) Create()
    {
        var env = new TestEnvironment();
        env.Settings.Load();
        env.Settings.Update(s => s.Timer.PlaySoundOnFinish = false);
        var clock = new FakeClock();
        var timer = new TimerManager(env.Bus, env.Settings, () => clock.Now, startTicker: false);
        var states = env.Record<TimerStateChanged>();
        return (env, clock, timer, states);
    }

    [Fact]
    public void Countdown_runs_and_reports_remaining()
    {
        var (env, clock, timer, states) = Create();
        using (env)
        using (timer)
        {
            timer.Handle(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromSeconds(60)));
            Assert.Equal(TimerRunState.Running, states.Last.State);
            Assert.Equal(TimerKind.Countdown, states.Last.Kind);
            Assert.Equal(TimeSpan.FromSeconds(60), states.Last.Duration);

            clock.AdvanceSeconds(20);
            timer.Tick();
            Assert.Equal(TimeSpan.FromSeconds(20), states.Last.ElapsedAtAnchor);
        }
    }

    [Fact]
    public void Pause_freezes_elapsed_and_resume_continues()
    {
        var (env, clock, timer, states) = Create();
        using (env)
        using (timer)
        {
            timer.Handle(new TimerCommand(TimerCommandKind.StartStopwatch));
            clock.AdvanceSeconds(5);
            timer.Handle(new TimerCommand(TimerCommandKind.Pause));
            Assert.Equal(TimerRunState.Paused, states.Last.State);
            Assert.Equal(TimeSpan.FromSeconds(5), states.Last.ElapsedAtAnchor);

            clock.AdvanceSeconds(30);
            timer.Handle(new TimerCommand(TimerCommandKind.Resume));
            Assert.Equal(TimerRunState.Running, states.Last.State);
            Assert.Equal(TimeSpan.FromSeconds(5), states.Last.ElapsedAtAnchor);

            clock.AdvanceSeconds(2);
            timer.Handle(new TimerCommand(TimerCommandKind.TogglePause));
            Assert.Equal(TimerRunState.Paused, states.Last.State);
            Assert.Equal(TimeSpan.FromSeconds(7), states.Last.ElapsedAtAnchor);
        }
    }

    [Fact]
    public void Countdown_finishes_with_notification_then_returns_to_idle()
    {
        var (env, clock, timer, states) = Create();
        using var notes = env.Record<NotificationReceived>();
        using (env)
        using (timer)
        {
            timer.Handle(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromSeconds(10)));
            clock.AdvanceSeconds(10.5);
            timer.Tick();
            Assert.Equal(TimerRunState.Finished, states.Last.State);
            Assert.Equal(1, notes.Count);
            Assert.Equal(NotificationPriority.Critical, notes.Last.Notification.Priority);
            Assert.Equal(NotificationSource.App, notes.Last.Notification.Source);

            clock.Advance(TimerManager.FinishedLinger + TimeSpan.FromSeconds(1));
            timer.Tick();
            Assert.Equal(TimerRunState.Idle, states.Last.State);
        }
    }

    [Fact]
    public void Extend_adds_time_to_countdown()
    {
        var (env, clock, timer, states) = Create();
        using (env)
        using (timer)
        {
            timer.Handle(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromSeconds(30)));
            timer.Handle(new TimerCommand(TimerCommandKind.Extend, TimeSpan.FromMinutes(1)));
            Assert.Equal(TimeSpan.FromSeconds(90), states.Last.Duration);
        }
    }

    [Fact]
    public void Stop_returns_to_idle()
    {
        var (env, clock, timer, states) = Create();
        using (env)
        using (timer)
        {
            timer.Handle(new TimerCommand(TimerCommandKind.StartStopwatch));
            timer.Handle(new TimerCommand(TimerCommandKind.Stop));
            Assert.Equal(TimerRunState.Idle, states.Last.State);
        }
    }
}
