using System.Diagnostics;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Timing;

/// <summary>
/// Countdown timer + stopwatch. Handles <see cref="TimerCommand"/> and publishes <see cref="TimerStateChanged"/>
/// on every state change and once per displayed second while running.
///
/// Time is measured with a monotonic clock (Stopwatch), never with wall-clock arithmetic, so DST changes,
/// NTP corrections or the user changing the system time do not affect the timer.
///
/// A single one-shot <see cref="System.Threading.Timer"/> is armed only while something can change by itself
/// (running timer, or a finished countdown waiting to return to Idle); in idle it is fully disarmed (0% CPU).
/// Ticks are aligned to whole seconds of elapsed (stopwatch) / remaining (countdown) time so the display
/// advances evenly instead of drifting between second boundaries.
/// </summary>
public sealed class TimerManager : IIslandModule
{
    /// <summary>How long a finished countdown stays in the "Finished" state before returning to Idle.</summary>
    internal static readonly TimeSpan FinishedLinger = TimeSpan.FromSeconds(3);

    /// <summary>Small margin so a tick lands just after a second boundary, not just before it.</summary>
    private const int TickSlackMs = 8;

    private static readonly TimeSpan DefaultExtension = TimeSpan.FromMinutes(1);

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly Func<TimeSpan> _clock;
    private readonly bool _useTicker;
    private readonly object _gate = new();

    private System.Threading.Timer? _ticker;
    private IDisposable? _commandSub;
    private bool _started;
    private bool _disposed;

    // ── Timer state (guarded by _gate)
    private TimerKind _kind = TimerKind.Stopwatch;
    private TimerRunState _state = TimerRunState.Idle;
    private TimeSpan _accumulated;      // elapsed time banked before the current running segment
    private TimeSpan _segmentStart;     // clock value when the current running segment started
    private TimeSpan? _duration;        // countdown total; null for stopwatch
    private TimeSpan _finishedAt;       // clock value when the countdown finished

    public TimerManager(IEventBus bus, SettingsManager settings)
        : this(bus, settings, CreateMonotonicClock(), startTicker: true)
    {
    }

    /// <summary>
    /// Test constructor. <paramref name="clock"/> returns monotonic time since an arbitrary origin.
    /// With <paramref name="startTicker"/> = false no background timer is created; tests drive time
    /// by advancing the clock and calling <see cref="Tick"/>.
    /// </summary>
    internal TimerManager(IEventBus bus, SettingsManager settings, Func<TimeSpan> clock, bool startTicker)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _useTicker = startTicker;
    }

    public string Name => "Таймер";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started || _disposed) return Task.CompletedTask;
            _started = true;
            if (_useTicker)
                _ticker = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            _commandSub = _bus.Subscribe<TimerCommand>(Handle);
        }
        _bus.Publish(new ModuleStatusChanged(Name, true, null));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _commandSub?.Dispose();
            _commandSub = null;
            DisarmTicker();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _commandSub?.Dispose();
            _commandSub = null;
            _ticker?.Dispose();
            _ticker = null;
        }
    }

    // ───────────────────────────── Commands ─────────────────────────────

    /// <summary>Applies a command. Thread-safe; normally invoked by the bus subscription.</summary>
    internal void Handle(TimerCommand command)
    {
        if (command is null) return;
        var playSound = false;

        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();

            // Bring a countdown that expired between ticks up to date before acting on the command.
            if (_state == TimerRunState.Running && _kind == TimerKind.Countdown && ElapsedAt(now) >= _duration)
                playSound = FinishLocked(now);

            switch (command.Kind)
            {
                case TimerCommandKind.StartCountdown:
                {
                    var duration = command.Duration
                                   ?? TimeSpan.FromMinutes(_settings.Current.Timer.DefaultCountdownMinutes);
                    if (duration <= TimeSpan.Zero) break;
                    StartLocked(TimerKind.Countdown, duration, now);
                    break;
                }

                case TimerCommandKind.StartStopwatch:
                    StartLocked(TimerKind.Stopwatch, null, now);
                    break;

                case TimerCommandKind.Pause:
                    PauseLocked(now);
                    break;

                case TimerCommandKind.Resume:
                    ResumeLocked(now);
                    break;

                case TimerCommandKind.TogglePause:
                    if (_state == TimerRunState.Running) PauseLocked(now);
                    else if (_state == TimerRunState.Paused) ResumeLocked(now);
                    break;

                case TimerCommandKind.Stop:
                    if (_state != TimerRunState.Idle)
                    {
                        _state = TimerRunState.Idle;
                        _accumulated = TimeSpan.Zero;
                        DisarmTicker();
                        PublishLocked(now);
                    }
                    break;

                case TimerCommandKind.Extend:
                {
                    var extra = command.Duration ?? DefaultExtension;
                    if (extra <= TimeSpan.Zero || _kind != TimerKind.Countdown) break;
                    if (_state is TimerRunState.Running or TimerRunState.Paused && _duration is { } d)
                    {
                        _duration = d + extra;
                        PublishLocked(now);
                        if (_state == TimerRunState.Running) ScheduleNextTickLocked(now);
                    }
                    else if (_state == TimerRunState.Finished)
                    {
                        // "+1 min" pressed on the finished banner: start a fresh countdown of that length.
                        StartLocked(TimerKind.Countdown, extra, now);
                    }
                    break;
                }
            }
        }

        if (playSound) PlayFinishSound();
    }

    /// <summary>
    /// Periodic evaluation: publishes the running time, finishes an expired countdown and returns a finished
    /// countdown to Idle after <see cref="FinishedLinger"/>. Called by the internal ticker (or by tests).
    /// </summary>
    internal void Tick()
    {
        var playSound = false;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();

            switch (_state)
            {
                case TimerRunState.Running:
                    if (_kind == TimerKind.Countdown && ElapsedAt(now) >= _duration)
                    {
                        playSound = FinishLocked(now);
                    }
                    else
                    {
                        PublishLocked(now);
                        ScheduleNextTickLocked(now);
                    }
                    break;

                case TimerRunState.Finished:
                    if (now - _finishedAt >= FinishedLinger)
                    {
                        _state = TimerRunState.Idle;
                        _accumulated = TimeSpan.Zero;
                        DisarmTicker();
                        PublishLocked(now);
                    }
                    else
                    {
                        ArmTicker(FinishedLinger - (now - _finishedAt));
                    }
                    break;

                default:
                    DisarmTicker();
                    break;
            }
        }

        if (playSound) PlayFinishSound();
    }

    // ───────────────────────────── State transitions (call under _gate) ─────────────────────────────

    private void StartLocked(TimerKind kind, TimeSpan? duration, TimeSpan now)
    {
        _kind = kind;
        _duration = duration;
        _accumulated = TimeSpan.Zero;
        _segmentStart = now;
        _state = TimerRunState.Running;
        PublishLocked(now);
        ScheduleNextTickLocked(now);
    }

    private void PauseLocked(TimeSpan now)
    {
        if (_state != TimerRunState.Running) return;
        _accumulated = ElapsedAt(now);
        _state = TimerRunState.Paused;
        DisarmTicker();
        PublishLocked(now);
    }

    private void ResumeLocked(TimeSpan now)
    {
        if (_state != TimerRunState.Paused) return;
        _segmentStart = now;
        _state = TimerRunState.Running;
        PublishLocked(now);
        ScheduleNextTickLocked(now);
    }

    /// <summary>Moves a countdown to Finished and raises the notification. Returns true when a sound should play.</summary>
    private bool FinishLocked(TimeSpan now)
    {
        var duration = _duration ?? TimeSpan.Zero;
        _accumulated = duration;
        _state = TimerRunState.Finished;
        _finishedAt = now;
        PublishLocked(now);
        ArmTicker(FinishedLinger);

        var timerSettings = _settings.Current.Timer;
        if (timerSettings.NotifyOnFinish)
        {
            _bus.Publish(new NotificationReceived(new IslandNotification
            {
                Id = "timer-finished-" + Guid.NewGuid().ToString("N"),
                AppName = Name,
                Glyph = Glyphs.Stopwatch,
                Title = "Таймер завершён",
                Body = FormatDuration(duration) + " истекло",
                Priority = NotificationPriority.Critical,
                Source = NotificationSource.App,
            }));
        }
        return timerSettings.PlaySoundOnFinish;
    }

    private TimeSpan ElapsedAt(TimeSpan now)
    {
        var elapsed = _accumulated;
        if (_state == TimerRunState.Running) elapsed += now - _segmentStart;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (_duration is { } d && elapsed > d) elapsed = d;
        return elapsed;
    }

    /// <summary>
    /// Publishes the current state. Done under the lock on purpose: it guarantees subscribers observe
    /// states in order (a late tick can never overtake a Stop). Bus subscribers of this event are either
    /// UI-marshalled (asynchronous) or trivial, so holding the lock is safe.
    /// </summary>
    private void PublishLocked(TimeSpan now)
    {
        _bus.Publish(new TimerStateChanged
        {
            Kind = _kind,
            State = _state,
            ElapsedAtAnchor = ElapsedAt(now),
            AnchorUtc = DateTimeOffset.UtcNow,
            Duration = _kind == TimerKind.Countdown ? _duration : null,
        });
    }

    // ───────────────────────────── Ticker ─────────────────────────────

    private void ScheduleNextTickLocked(TimeSpan now)
    {
        if (_state != TimerRunState.Running) return;
        var elapsed = ElapsedAt(now);
        double dueMs;
        if (_kind == TimerKind.Countdown && _duration is { } d)
        {
            // Countdown displays ceil(remaining): the digit changes when remaining crosses a whole second.
            var remainingMs = (d - elapsed).TotalMilliseconds;
            if (remainingMs <= 0) dueMs = 0;
            else
            {
                dueMs = remainingMs % 1000.0;
                if (dueMs < 1) dueMs = 1000;
            }
        }
        else
        {
            // Stopwatch displays floor(elapsed): next change at the next whole second of elapsed.
            dueMs = 1000.0 - elapsed.TotalMilliseconds % 1000.0;
        }
        ArmTicker(TimeSpan.FromMilliseconds(dueMs + TickSlackMs));
    }

    private void ArmTicker(TimeSpan due)
    {
        var ms = (long)Math.Ceiling(Math.Max(1, due.TotalMilliseconds));
        try { _ticker?.Change(ms, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void DisarmTicker()
    {
        try { _ticker?.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private static Func<TimeSpan> CreateMonotonicClock()
    {
        var origin = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(origin);
    }

    private static void PlayFinishSound()
    {
        try
        {
            global::System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Timer finish sound failed", ex);
        }
    }

    // ───────────────────────────── Formatting ─────────────────────────────

    /// <summary>"5 мин", "1 ч 30 мин", "45 сек", "2 мин 10 сек".</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Round(Math.Max(0, duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        var parts = new List<string>(3);
        if (hours > 0) parts.Add(hours + " ч");
        if (minutes > 0) parts.Add(minutes + " мин");
        if (seconds > 0 || parts.Count == 0) parts.Add(seconds + " сек");
        return string.Join(" ", parts);
    }
}
