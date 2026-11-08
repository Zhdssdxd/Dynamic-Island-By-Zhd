using Windows.Media.Control;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Media;

/// <summary>
/// Media module: tracks every Windows media session through the Global System Media Transport Controls (GSMTC),
/// publishes <see cref="MediaSessionsChanged"/> and executes <see cref="MediaCommand"/>s.
/// </summary>
/// <remarks>
/// <para>Threading: WinRT events arrive on background threads. Shared state is guarded by <c>_gate</c>; bursts of
/// changes are coalesced (~120 ms) and timeline-only changes are published at most every 500 ms. Identical
/// consecutive events are not republished. Commands run on the thread pool, never on the publisher's thread.</para>
/// <para>Active session (<see cref="MediaSessionsChanged.Current"/>): a manually selected session sticks while it
/// exists and is playing or nothing else is playing; otherwise the most recently started playing session; otherwise
/// Windows' current session (unless closed/stopped); otherwise the most recently paused one; closed/stopped sessions
/// only as a last resort.</para>
/// <para>Platform limitations (not worked around, never faked):</para>
/// <list type="bullet">
/// <item>Browsers expose media only when the site uses the MediaSession API, and only one session per browser
/// (the most recently active tab).</item>
/// <item>Browsers and live streams usually report no timeline → <see cref="MediaSessionInfo.Duration"/> is Zero.</item>
/// <item>Many sources report no thumbnail, or no artist/album.</item>
/// <item>Apps that do not integrate with the system media controls (some Win32 players) are invisible.</item>
/// </list>
/// </remarks>
public sealed class MediaManager : IIslandModule
{
    private const int DebounceMs = 120;
    private const int TimelineMinIntervalMs = 500;

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly ThumbnailLoader _thumbnails = new();
    private readonly SubscriptionBag _busSubscriptions = new();
    private readonly global::System.Threading.Timer _publishTimer;

    // Lock order: _lifecycleLock → _syncLock → _gate → (tracker data lock). _publishLock → _gate.
    // WinRT subscribe/unsubscribe never happens under _gate, and WinRT callbacks never take _lifecycleLock.
    private readonly object _lifecycleLock = new();
    private readonly object _syncLock = new();
    private readonly object _gate = new();
    private readonly object _publishLock = new();

    // ── guarded by _lifecycleLock ──
    private bool _started;
    private bool _stopped;

    // ── guarded by _gate ──
    private readonly List<MediaSessionTracker> _trackers = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _systemCurrentSession;
    private string? _systemCurrentAppId;
    private string? _manualSelectionId;
    private bool _featureEnabled = true;
    private bool _running;
    private bool _publishScheduled;
    private long _publishDueAt;
    private long _lastPublishAt;

    // ── guarded by _publishLock ──
    private MediaSessionsChanged? _lastPublished;

    private int _disposed;

    /// <summary>Creates the module. Cheap: no OS calls until <see cref="StartAsync"/>.</summary>
    public MediaManager(IEventBus bus, SettingsManager settings)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _publishTimer = new global::System.Threading.Timer(OnPublishTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public string Name => "Медиа";

    // ───────────────────────────── Lifecycle ─────────────────────────────

    /// <summary>
    /// Requests the GSMTC session manager and starts tracking. Never throws for an unavailable API: publishes
    /// <see cref="ModuleStatusChanged"/> with the reason and stays idle.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_started || _stopped || Volatile.Read(ref _disposed) != 0) return;
            _started = true;
            _busSubscriptions.Add(_bus.Subscribe<SettingsChanged>(OnSettingsChanged));
            _busSubscriptions.Add(_bus.Subscribe<MediaCommand>(OnCommand));
        }

        var enabled = IsFeatureEnabled(_settings.Current);
        lock (_gate) _featureEnabled = enabled;

        GlobalSystemMediaTransportControlsSessionManager? manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // app is shutting down
        }
        catch (Exception ex)
        {
            ReportUnavailable(ex.Message, ex);
            return;
        }

        if (manager is null)
        {
            ReportUnavailable("Windows не вернул менеджер медиасеансов", null);
            return;
        }

        lock (_lifecycleLock)
        {
            if (_stopped || Volatile.Read(ref _disposed) != 0) return;
            try
            {
                manager.SessionsChanged += OnSessionsChanged;
                manager.CurrentSessionChanged += OnCurrentSessionChanged;
            }
            catch (Exception ex)
            {
                UnsubscribeManager(manager);
                ReportUnavailable(ex.Message, ex);
                return;
            }
            lock (_gate)
            {
                _manager = manager;
                _running = true;
            }
        }

        // If RequestAsync completed synchronously we may still be on the UI thread: the initial enumeration makes
        // several cross-process calls (and may resolve process names), so always do it on the thread pool.
        await Task.Run(InitializeTracking).ConfigureAwait(false);
    }

    private void InitializeTracking()
    {
        try
        {
            RefreshSystemCurrent();
            SyncSessions();

            int sessionCount;
            bool enabled;
            lock (_gate)
            {
                if (!_running) return;
                sessionCount = _trackers.Count;
                enabled = _featureEnabled;
            }
            Log.Info($"Media: GSMTC started, {sessionCount} session(s), tracking {(enabled ? "enabled" : "paused by settings")}");
            _bus.Publish(new ModuleStatusChanged(Name, true, null));
            PublishNow(force: true);
        }
        catch (Exception ex)
        {
            Log.Error("Media: initial session enumeration failed", ex);
            _bus.Publish(new ModuleStatusChanged(Name, false, "Ошибка чтения медиасеансов: " + ex.Message));
        }
    }

    /// <summary>Stops tracking, releases every WinRT subscription and publishes an empty media state. Idempotent.</summary>
    public Task StopAsync()
    {
        StopCore();
        return Task.CompletedTask;
    }

    /// <summary>Stops (if needed) and frees the timer and thumbnail decoder. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopCore();
        _publishTimer.Dispose();
        _thumbnails.Dispose();
    }

    private void StopCore()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        List<MediaSessionTracker> trackers;
        bool wasRunning;

        lock (_lifecycleLock)
        {
            if (_stopped) return;
            _stopped = true;
            lock (_gate)
            {
                wasRunning = _running;
                _running = false;
                manager = _manager;
                _manager = null;
                trackers = new List<MediaSessionTracker>(_trackers);
                _trackers.Clear();
                _systemCurrentSession = null;
                _systemCurrentAppId = null;
                _manualSelectionId = null;
                _publishScheduled = false;
            }
            if (manager is not null) UnsubscribeManager(manager);
        }

        try { _publishTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        _busSubscriptions.Dispose();
        foreach (var tracker in trackers) tracker.Dispose();
        _thumbnails.Clear();

        if (wasRunning)
        {
            var empty = new MediaSessionsChanged(null, Array.Empty<MediaSessionInfo>());
            lock (_publishLock)
            {
                _lastPublished = empty;
                _bus.Publish(empty);
            }
        }
    }

    private void UnsubscribeManager(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try { manager.SessionsChanged -= OnSessionsChanged; } catch (Exception ex) { Log.Debug($"Media: unsubscribe SessionsChanged failed: {ex.Message}"); }
        try { manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch (Exception ex) { Log.Debug($"Media: unsubscribe CurrentSessionChanged failed: {ex.Message}"); }
    }

    private void ReportUnavailable(string reason, Exception? ex)
    {
        Log.Warn("Media: GSMTC unavailable: " + reason, ex);
        _bus.Publish(new ModuleStatusChanged(Name, false, "GSMTC недоступен: " + reason));
    }

    private static bool IsFeatureEnabled(AppSettings settings)
        => settings.Features is { } f && (f.MusicModeEnabled || f.VideoModeEnabled);

    // ───────────────────────────── Bus handlers ─────────────────────────────

    private void OnSettingsChanged(SettingsChanged e)
    {
        var enabled = IsFeatureEnabled(e.Settings);
        lock (_gate)
        {
            if (_featureEnabled == enabled) return;
            _featureEnabled = enabled;
            if (!_running) return;
        }

        Log.Info(enabled ? "Media: tracking resumed (music/video enabled)" : "Media: tracking paused (music and video disabled)");
        // Both directions resync: disabling drops every tracker (unsubscribing per-session events), enabling recreates them.
        _ = Task.Run(() =>
        {
            try
            {
                SyncSessions();
                RequestPublish(MediaChangeKind.Full);
            }
            catch (Exception ex)
            {
                Log.Warn("Media: resync after settings change failed", ex);
            }
        });
    }

    private void OnCommand(MediaCommand command)
    {
        lock (_gate)
        {
            if (!_running) return;
        }
        // Never run WinRT calls on the publisher's thread (may be the UI thread).
        _ = Task.Run(() => HandleCommandAsync(command));
    }

    private async Task HandleCommandAsync(MediaCommand command)
    {
        try
        {
            if (command.Kind == MediaCommandKind.SelectSession)
            {
                var changed = false;
                lock (_gate)
                {
                    if (!string.IsNullOrEmpty(command.SessionId) && _trackers.Any(t => t.SessionId == command.SessionId))
                    {
                        changed = _manualSelectionId != command.SessionId;
                        _manualSelectionId = command.SessionId;
                    }
                }
                if (changed) PublishNow(force: false);
                return;
            }

            MediaSessionTracker? target;
            lock (_gate) target = ResolveTargetLocked(command.SessionId);
            if (target is null)
            {
                Log.Debug($"Media: no session for command {command.Kind} ({command.SessionId ?? "current"})");
                return;
            }

            await target.ExecuteAsync(command.Kind, command.Position).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: command {command.Kind} failed", ex);
        }
    }

    /// <summary>The explicitly addressed session, or the session that is (or would be) published as Current.</summary>
    private MediaSessionTracker? ResolveTargetLocked(string? sessionId)
    {
        if (!_featureEnabled || _trackers.Count == 0) return null;
        if (!string.IsNullOrEmpty(sessionId)) return _trackers.FirstOrDefault(t => t.SessionId == sessionId);

        var states = _trackers.Select(t => t.GetState()).ToList();
        var current = SelectCurrent(states, _manualSelectionId, FindSystemCurrentIdLocked(), out _);
        return current is null ? null : _trackers.FirstOrDefault(t => t.SessionId == current.SessionId);
    }

    // ───────────────────────────── GSMTC events ─────────────────────────────

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _ = Task.Run(ResyncFromEvent);

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        // SessionsChanged is known to be missed on some Windows builds; a current-session change also resyncs.
        => _ = Task.Run(ResyncFromEvent);

    private void ResyncFromEvent()
    {
        try
        {
            RefreshSystemCurrent();
            SyncSessions();
        }
        catch (Exception ex)
        {
            Log.Warn("Media: session resync failed", ex);
        }
    }

    private void RefreshSystemCurrent()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate) manager = _manager;
        if (manager is null) return;

        GlobalSystemMediaTransportControlsSession? current = null;
        string? appId = null;
        try
        {
            current = manager.GetCurrentSession();
            if (current is not null) appId = current.SourceAppUserModelId;
        }
        catch (Exception ex)
        {
            Log.Debug($"Media: GetCurrentSession failed: {ex.Message}");
        }

        lock (_gate)
        {
            if (!_running) return;
            _systemCurrentSession = current;
            _systemCurrentAppId = appId;
        }
        RequestPublish(MediaChangeKind.Full);
    }

    /// <summary>
    /// Reconciles trackers with <c>GetSessions()</c>: keeps trackers whose session object is unchanged, rebinds
    /// trackers whose app now has a new session object, creates trackers for new sessions and disposes the rest.
    /// With music and video disabled, all trackers are dropped (no per-session events, ~0 CPU).
    /// </summary>
    private void SyncSessions()
    {
        lock (_syncLock)
        {
            GlobalSystemMediaTransportControlsSessionManager? manager;
            bool enabled;
            lock (_gate)
            {
                if (!_running) return;
                manager = _manager;
                enabled = _featureEnabled;
            }
            if (manager is null) return;

            var live = new List<(GlobalSystemMediaTransportControlsSession Session, string AppId, MediaAppDescriptor App)>();
            if (enabled)
            {
                try
                {
                    foreach (var session in manager.GetSessions())
                    {
                        if (session is null) continue;
                        var appId = SafeGetAppId(session);
                        live.Add((session, appId, MediaClassifier.Describe(appId)));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Media: GetSessions failed", ex);
                    return;
                }
            }

            var removed = new List<MediaSessionTracker>();
            var added = new List<MediaSessionTracker>();
            var rebound = new List<(MediaSessionTracker Tracker, GlobalSystemMediaTransportControlsSession Session)>();

            lock (_gate)
            {
                if (!_running) return;

                var unmatched = new List<MediaSessionTracker>(_trackers);
                var fresh = new List<(GlobalSystemMediaTransportControlsSession Session, string AppId, MediaAppDescriptor App)>();

                foreach (var item in live)
                {
                    var index = unmatched.FindIndex(t => ReferenceEquals(t.Session, item.Session));
                    if (index >= 0) unmatched.RemoveAt(index);
                    else fresh.Add(item);
                }

                foreach (var item in fresh)
                {
                    var index = unmatched.FindIndex(t => string.Equals(t.SourceAppId, item.AppId, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        rebound.Add((unmatched[index], item.Session));
                        unmatched.RemoveAt(index);
                        continue;
                    }

                    var tracker = new MediaSessionTracker(item.Session, MakeUniqueIdLocked(item.AppId), item.AppId, item.App, _thumbnails, OnTrackerChanged);
                    _trackers.Add(tracker);
                    added.Add(tracker);
                }

                foreach (var tracker in unmatched)
                {
                    _trackers.Remove(tracker);
                    removed.Add(tracker);
                }

                if (_manualSelectionId is not null && !_trackers.Any(t => t.SessionId == _manualSelectionId))
                    _manualSelectionId = null;
            }

            // WinRT (un)subscriptions outside _gate.
            foreach (var tracker in removed) tracker.Dispose();
            foreach (var (tracker, session) in rebound) tracker.Rebind(session);
            foreach (var tracker in added) tracker.Attach();

            if (removed.Count + added.Count + rebound.Count > 0)
                Log.Debug($"Media: sessions synced (+{added.Count} -{removed.Count} ~{rebound.Count})");
        }
        RequestPublish(MediaChangeKind.Full);
    }

    private static string SafeGetAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? ""; }
        catch (Exception) { return ""; }
    }

    private string MakeUniqueIdLocked(string appId)
    {
        var baseId = string.IsNullOrWhiteSpace(appId) ? "unknown" : appId;
        if (!_trackers.Any(t => t.SessionId == baseId)) return baseId;
        for (var i = 2; ; i++)
        {
            var candidate = baseId + "#" + i.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            if (!_trackers.Any(t => t.SessionId == candidate)) return candidate;
        }
    }

    private string? FindSystemCurrentIdLocked()
    {
        if (_systemCurrentSession is { } session)
        {
            foreach (var tracker in _trackers)
            {
                if (ReferenceEquals(tracker.Session, session)) return tracker.SessionId;
            }
        }
        if (!string.IsNullOrEmpty(_systemCurrentAppId))
        {
            foreach (var tracker in _trackers)
            {
                if (string.Equals(tracker.SourceAppId, _systemCurrentAppId, StringComparison.OrdinalIgnoreCase)) return tracker.SessionId;
            }
        }
        return null;
    }

    // ───────────────────────────── Publishing ─────────────────────────────

    private void OnTrackerChanged(MediaSessionTracker tracker, MediaChangeKind kind) => RequestPublish(kind);

    /// <summary>
    /// Schedules a publish. Full changes go out ~120 ms after the first change of a burst; timeline-only changes
    /// additionally wait until 500 ms have passed since the previous publish. A pending later publish is pulled
    /// forward when a full change arrives.
    /// </summary>
    private void RequestPublish(MediaChangeKind kind)
    {
        lock (_gate)
        {
            if (!_running) return;
            var now = Environment.TickCount64;
            var due = now + DebounceMs;
            if (kind == MediaChangeKind.TimelineOnly) due = Math.Max(due, _lastPublishAt + TimelineMinIntervalMs);
            if (_publishScheduled && _publishDueAt <= due) return;

            _publishScheduled = true;
            _publishDueAt = due;
            try { _publishTimer.Change(Math.Max(0, due - now), Timeout.Infinite); }
            catch (ObjectDisposedException) { _publishScheduled = false; }
        }
    }

    private void OnPublishTimer(object? state)
    {
        lock (_gate) _publishScheduled = false;
        try
        {
            PublishNow(force: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Media: publish failed", ex);
        }
    }

    private void PublishNow(bool force)
    {
        lock (_publishLock)
        {
            MediaSessionsChanged evt;
            lock (_gate)
            {
                if (!_running) return;
                _lastPublishAt = Environment.TickCount64;
                evt = BuildEventLocked();
            }

            if (!force && _lastPublished is { } last && IsSame(last, evt)) return;
            _lastPublished = evt;
            _bus.Publish(evt);
        }
    }

    private MediaSessionsChanged BuildEventLocked()
    {
        if (!_featureEnabled || _trackers.Count == 0)
            return new MediaSessionsChanged(null, Array.Empty<MediaSessionInfo>());

        var states = new List<MediaTrackerState>(_trackers.Count);
        foreach (var tracker in _trackers) states.Add(tracker.GetState());

        var current = SelectCurrent(states, _manualSelectionId, FindSystemCurrentIdLocked(), out var keepManual);
        if (!keepManual) _manualSelectionId = null;

        var all = new MediaSessionInfo[states.Count];
        for (var i = 0; i < states.Count; i++) all[i] = states[i].Info;
        return new MediaSessionsChanged(current, all);
    }

    private static bool IsSame(MediaSessionsChanged a, MediaSessionsChanged b)
        => a.Current == b.Current && a.All.SequenceEqual(b.All);

    // ───────────────────────────── Selection ─────────────────────────────

    /// <summary>
    /// Chooses the session to display. Pure function (unit-testable).
    /// </summary>
    /// <param name="sessions">All tracked sessions.</param>
    /// <param name="manualId">Session chosen by the user, if any.</param>
    /// <param name="systemCurrentId">Session Windows considers current, if known.</param>
    /// <param name="keepManual">True when the manual choice is still in effect (otherwise it should be cleared).</param>
    internal static MediaSessionInfo? SelectCurrent(
        IReadOnlyList<MediaTrackerState> sessions, string? manualId, string? systemCurrentId, out bool keepManual)
    {
        keepManual = false;
        if (sessions.Count == 0) return null;

        // 1. Manual choice sticks while it is playing or nothing else is.
        if (manualId is not null)
        {
            foreach (var s in sessions)
            {
                if (s.Info.SessionId != manualId) continue;
                var otherPlaying = sessions.Any(o => o.Info.IsPlaying && o.Info.SessionId != manualId);
                if (s.Info.IsPlaying || !otherPlaying)
                {
                    keepManual = true;
                    return s.Info;
                }
                break;
            }
        }

        // 2. The session that most recently started playing.
        MediaTrackerState? playing = null;
        foreach (var s in sessions)
        {
            if (s.Info.IsPlaying && (playing is null || s.PlayingSince > playing.Value.PlayingSince)) playing = s;
        }
        if (playing is { } p) return p.Info;

        // 3. Windows' own current session, unless it is closed/stopped.
        MediaTrackerState? system = null;
        if (systemCurrentId is not null)
        {
            foreach (var s in sessions)
            {
                if (s.Info.SessionId == systemCurrentId) { system = s; break; }
            }
        }
        if (system is { } sys && !IsInactive(sys.Info.State)) return sys.Info;

        // 4. The most recently paused session.
        MediaTrackerState? paused = null;
        foreach (var s in sessions)
        {
            if (s.Info.State == MediaPlaybackState.Paused && (paused is null || s.PausedAt > paused.Value.PausedAt)) paused = s;
        }
        if (paused is { } pa) return pa.Info;

        // 5. Any session that is at least open (Opened/Changing); closed/stopped ones only as a last resort.
        foreach (var s in sessions)
        {
            if (!IsInactive(s.Info.State)) return s.Info;
        }
        return system?.Info ?? sessions[0].Info;
    }

    private static bool IsInactive(MediaPlaybackState state)
        => state is MediaPlaybackState.Closed or MediaPlaybackState.Stopped;
}
