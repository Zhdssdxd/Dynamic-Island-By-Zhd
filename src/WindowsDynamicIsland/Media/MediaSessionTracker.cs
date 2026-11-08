using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WinPlaybackType = Windows.Media.MediaPlaybackType;

namespace WindowsDynamicIsland.Media;

/// <summary>What changed in a tracked session (drives publish throttling).</summary>
internal enum MediaChangeKind
{
    /// <summary>Metadata, playback state, controls or thumbnail changed — publish after the short debounce.</summary>
    Full,

    /// <summary>Only the timeline (position) changed — publish at most every 500 ms.</summary>
    TimelineOnly,
}

/// <summary>Immutable view of a tracker used for active-session selection.</summary>
/// <param name="Info">The published snapshot.</param>
/// <param name="PlayingSince">When the session last switched to Playing (MinValue if never).</param>
/// <param name="PausedAt">When the session last switched to Paused (MinValue if never).</param>
internal readonly record struct MediaTrackerState(MediaSessionInfo Info, DateTimeOffset PlayingSince, DateTimeOffset PausedAt);

/// <summary>
/// Wraps one <see cref="GlobalSystemMediaTransportControlsSession"/>: listens to its events, keeps the latest data
/// and builds a cached <see cref="MediaSessionInfo"/> snapshot. Thread-safe; WinRT events arrive on arbitrary threads.
/// </summary>
/// <remarks>
/// Locks: <c>_subscriptionLock</c> only guards attaching/detaching WinRT handlers (handlers never take it, so an
/// unsubscribe waiting for an in-flight callback cannot deadlock); <c>_dataLock</c> guards cached data and is never
/// held during WinRT calls or callbacks to the owner.
/// Data that the source does not report stays empty (no invented values): browsers often have no timeline
/// (Duration = Zero) and many sources have no thumbnail.
/// </remarks>
internal sealed class MediaSessionTracker : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PropertiesRetryDelay = TimeSpan.FromMilliseconds(350);

    private readonly object _subscriptionLock = new();
    private readonly object _dataLock = new();
    private readonly ThumbnailLoader _thumbnails;
    private readonly Action<MediaSessionTracker, MediaChangeKind> _changed;

    private volatile GlobalSystemMediaTransportControlsSession _session;
    private volatile bool _disposed;
    private bool _attached;               // _subscriptionLock
    private int _propertiesGeneration;    // Interlocked

    // ── Data (guarded by _dataLock) ──
    private string _title = "";
    private string _artist = "";
    private string _albumTitle = "";
    private WinPlaybackType? _propertiesType;
    private BitmapSource? _thumbnail;
    private string? _thumbnailKey;

    private MediaPlaybackState _state = MediaPlaybackState.Closed;
    private double _playbackRate = 1.0;
    private WinPlaybackType? _playbackInfoType;
    private bool _canPlay;
    private bool _canPause;
    private bool _canToggle;
    private bool _canNext;
    private bool _canPrevious;
    private bool _canChangePosition;

    private TimeSpan _startTime;
    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _positionUpdatedAt = DateTimeOffset.UtcNow;

    private DateTimeOffset _playingSince = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedAt = DateTimeOffset.MinValue;

    private MediaTrackerState? _cachedState;

    /// <param name="session">The GSMTC session.</param>
    /// <param name="sessionId">Unique id within the manager (AUMID, plus "#n" for duplicates).</param>
    /// <param name="sourceAppId">The session's SourceAppUserModelId.</param>
    /// <param name="app">Friendly name and category of the source app.</param>
    /// <param name="thumbnails">Shared thumbnail loader.</param>
    /// <param name="changed">Called (outside any lock, on any thread) when the snapshot changed.</param>
    public MediaSessionTracker(
        GlobalSystemMediaTransportControlsSession session,
        string sessionId,
        string sourceAppId,
        MediaAppDescriptor app,
        ThumbnailLoader thumbnails,
        Action<MediaSessionTracker, MediaChangeKind> changed)
    {
        _session = session;
        SessionId = sessionId;
        SourceAppId = sourceAppId;
        App = app;
        _thumbnails = thumbnails;
        _changed = changed;
    }

    /// <summary>Unique id published as <see cref="MediaSessionInfo.SessionId"/>.</summary>
    public string SessionId { get; }

    /// <summary>GSMTC SourceAppUserModelId.</summary>
    public string SourceAppId { get; }

    /// <summary>Friendly name and category of the source application.</summary>
    public MediaAppDescriptor App { get; }

    /// <summary>The currently wrapped WinRT session object.</summary>
    public GlobalSystemMediaTransportControlsSession Session => _session;

    // ───────────────────────────── Lifecycle ─────────────────────────────

    /// <summary>Subscribes to the session's events and loads its current data. No-op if disposed or attached.</summary>
    public void Attach()
    {
        lock (_subscriptionLock)
        {
            if (_disposed || _attached) return;
            Subscribe(_session);
            _attached = true;
        }
        RefreshAll();
    }

    /// <summary>
    /// Switches to another WinRT object for the same source (GSMTC may hand out a new session object for an app);
    /// keeps the id, selection timestamps and cached data, then reloads everything.
    /// </summary>
    public void Rebind(GlobalSystemMediaTransportControlsSession session)
    {
        lock (_subscriptionLock)
        {
            if (_disposed || ReferenceEquals(session, _session)) return;
            if (_attached)
            {
                Unsubscribe(_session);
                Subscribe(session);
            }
            _session = session;
            Interlocked.Increment(ref _propertiesGeneration);
            if (!_attached) return;
        }
        RefreshAll();
    }

    /// <summary>Unsubscribes from WinRT events and stops in-flight refreshes from applying. Idempotent.</summary>
    public void Dispose()
    {
        lock (_subscriptionLock)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _propertiesGeneration);
            if (_attached)
            {
                Unsubscribe(_session);
                _attached = false;
            }
        }
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            // The source app vanished between enumeration and subscription. Its removal arrives via SessionsChanged.
            Log.Debug($"Media: subscribe to {SessionId} failed: {ex.Message}");
        }
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        // Each removal separately: a dead session may throw on one and we still want to drop the others.
        try { session.MediaPropertiesChanged -= OnMediaPropertiesChanged; } catch (Exception ex) { Log.Debug($"Media: unsubscribe failed: {ex.Message}"); }
        try { session.PlaybackInfoChanged -= OnPlaybackInfoChanged; } catch (Exception ex) { Log.Debug($"Media: unsubscribe failed: {ex.Message}"); }
        try { session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged; } catch (Exception ex) { Log.Debug($"Media: unsubscribe failed: {ex.Message}"); }
    }

    // ───────────────────────────── WinRT event handlers (any thread) ─────────────────────────────

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_disposed) return;
        _ = RefreshMediaPropertiesAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            var infoChanged = RefreshPlaybackInfo();
            // Apps often update position together with state; read both so extrapolation starts from fresh data.
            var timelineChanged = RefreshTimeline();
            if (infoChanged) Notify(MediaChangeKind.Full);
            else if (timelineChanged) Notify(MediaChangeKind.TimelineOnly);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: playback info handler failed for {SessionId}", ex);
        }
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            if (RefreshTimeline()) Notify(MediaChangeKind.TimelineOnly);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: timeline handler failed for {SessionId}", ex);
        }
    }

    private void Notify(MediaChangeKind kind)
    {
        if (_disposed) return;
        try { _changed(this, kind); }
        catch (Exception ex) { Log.Warn("Media: change callback failed", ex); }
    }

    // ───────────────────────────── Refresh ─────────────────────────────

    private void RefreshAll()
    {
        RefreshPlaybackInfo();
        RefreshTimeline();
        Notify(MediaChangeKind.Full);
        _ = RefreshMediaPropertiesAsync();
    }

    /// <summary>Reads PlaybackInfo (synchronous WinRT call). Returns true when anything visible changed.</summary>
    private bool RefreshPlaybackInfo()
    {
        MediaPlaybackState state;
        double rate;
        WinPlaybackType? type;
        bool canPlay = false, canPause = false, canToggle = false, canNext = false, canPrevious = false, canPosition = false;

        try
        {
            var info = _session.GetPlaybackInfo();
            if (info is null) return false;

            state = MapStatus(info.PlaybackStatus);
            var reportedRate = info.PlaybackRate;
            rate = reportedRate is { } r && double.IsFinite(r) && r > 0 ? r : 1.0;
            type = info.PlaybackType;

            var controls = info.Controls;
            if (controls is not null)
            {
                canPlay = controls.IsPlayEnabled;
                canPause = controls.IsPauseEnabled;
                canToggle = controls.IsPlayPauseToggleEnabled;
                canNext = controls.IsNextEnabled;
                canPrevious = controls.IsPreviousEnabled;
                canPosition = controls.IsPlaybackPositionEnabled;
            }
        }
        catch (Exception ex)
        {
            // COMException: the source app closed or is busy. Keep the previous values.
            Log.Debug($"Media: GetPlaybackInfo failed for {SessionId}: {ex.Message}");
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_dataLock)
        {
            var changed = state != _state
                || !rate.Equals(_playbackRate)
                || type != _playbackInfoType
                || canPlay != _canPlay || canPause != _canPause || canToggle != _canToggle
                || canNext != _canNext || canPrevious != _canPrevious || canPosition != _canChangePosition;
            if (!changed) return false;

            if (state == MediaPlaybackState.Playing && _state != MediaPlaybackState.Playing) _playingSince = now;
            if (state == MediaPlaybackState.Paused && _state != MediaPlaybackState.Paused) _pausedAt = now;

            _state = state;
            _playbackRate = rate;
            _playbackInfoType = type;
            _canPlay = canPlay;
            _canPause = canPause;
            _canToggle = canToggle;
            _canNext = canNext;
            _canPrevious = canPrevious;
            _canChangePosition = canPosition;
            _cachedState = null;
            return true;
        }
    }

    /// <summary>Reads timeline properties (synchronous WinRT call). Returns true when position/duration changed.</summary>
    private bool RefreshTimeline()
    {
        TimeSpan start, end, position;
        DateTimeOffset updatedAt;
        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline is null) return false;
            start = timeline.StartTime;
            end = timeline.EndTime;
            position = timeline.Position;
            updatedAt = timeline.LastUpdatedTime;
        }
        catch (Exception ex)
        {
            Log.Debug($"Media: GetTimelineProperties failed for {SessionId}: {ex.Message}");
            return false;
        }

        // EndTime <= 0 means the source has no timeline (typical for browsers / live streams): Duration stays Zero.
        var duration = end > TimeSpan.Zero && end > start ? end - start : TimeSpan.Zero;
        var relative = position - start;
        if (relative < TimeSpan.Zero) relative = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && relative > duration) relative = duration;
        if (duration == TimeSpan.Zero) relative = TimeSpan.Zero;

        var now = DateTimeOffset.UtcNow;
        if (updatedAt == default || updatedAt.Year < 2000 || updatedAt > now)
            updatedAt = now;

        lock (_dataLock)
        {
            if (start == _startTime && duration == _duration && relative == _position && updatedAt == _positionUpdatedAt)
                return false;
            _startTime = start;
            _duration = duration;
            _position = relative;
            _positionUpdatedAt = updatedAt;
            _cachedState = null;
            return true;
        }
    }

    /// <summary>
    /// Loads title/artist/album/type and then the thumbnail. A newer call supersedes older in-flight ones.
    /// Transient failures ("device not ready") keep previously known values. Never throws.
    /// </summary>
    private async Task RefreshMediaPropertiesAsync()
    {
        var generation = Interlocked.Increment(ref _propertiesGeneration);
        try
        {
            var session = _session;
            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
            for (var attempt = 0; attempt < 2 && props is null; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(PropertiesRetryDelay).ConfigureAwait(false);
                    if (IsStale(generation)) return;
                }
                try
                {
                    props = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Media: TryGetMediaPropertiesAsync failed for {SessionId} (attempt {attempt + 1}): {ex.Message}");
                }
            }
            if (props is null || IsStale(generation)) return;

            string title, artist, albumTitle;
            WinPlaybackType? type;
            IRandomAccessStreamReference? thumbnailRef;
            try
            {
                title = (props.Title ?? "").Trim();
                artist = (props.Artist ?? "").Trim();
                if (artist.Length == 0) artist = (props.AlbumArtist ?? "").Trim();
                albumTitle = (props.AlbumTitle ?? "").Trim();
                type = props.PlaybackType;
                thumbnailRef = props.Thumbnail;
            }
            catch (Exception ex)
            {
                Log.Debug($"Media: reading media properties failed for {SessionId}: {ex.Message}");
                return;
            }

            var key = ThumbnailLoader.MakeKey(SessionId, title, artist);
            bool changed;
            lock (_dataLock)
            {
                if (IsStale(generation)) return;
                var trackChanged = !string.Equals(_thumbnailKey, key, StringComparison.Ordinal);
                changed = trackChanged || albumTitle != _albumTitle || type != _propertiesType;

                _title = title;
                _artist = artist;
                _albumTitle = albumTitle;
                _propertiesType = type;

                if (trackChanged)
                {
                    // New track: never keep the previous track's art; show a cached one for this track immediately.
                    _thumbnailKey = key;
                    _thumbnail = thumbnailRef is null ? null : _thumbnails.TryGetCached(key);
                }
                else if (thumbnailRef is null && _thumbnail is not null)
                {
                    _thumbnail = null;
                    changed = true;
                }
                if (changed) _cachedState = null;
            }
            if (changed) Notify(MediaChangeKind.Full);
            if (thumbnailRef is null) return;

            // Always re-read: apps often publish the new title first and the new art a moment later under the same
            // key. Identical bytes give back the cached instance, so nothing is republished.
            var image = await _thumbnails.LoadAsync(key, thumbnailRef).ConfigureAwait(false);
            if (image is null) return; // keep whatever this track already shows (cached art or none)

            var imageChanged = false;
            lock (_dataLock)
            {
                if (IsStale(generation) || !string.Equals(_thumbnailKey, key, StringComparison.Ordinal)) return;
                if (!ReferenceEquals(_thumbnail, image))
                {
                    _thumbnail = image;
                    _cachedState = null;
                    imageChanged = true;
                }
            }
            if (imageChanged) Notify(MediaChangeKind.Full);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: media properties refresh failed for {SessionId}", ex);
        }
    }

    private bool IsStale(int generation) => _disposed || Volatile.Read(ref _propertiesGeneration) != generation;

    // ───────────────────────────── Snapshot ─────────────────────────────

    /// <summary>Returns the cached snapshot, rebuilding it only after a change. Cheap; thread-safe.</summary>
    public MediaTrackerState GetState()
    {
        lock (_dataLock)
        {
            if (_cachedState is { } cached) return cached;

            double? aspect = _thumbnail is { PixelHeight: > 0 } t ? (double)t.PixelWidth / t.PixelHeight : null;
            var kind = MediaClassifier.Classify(App.Category, _playbackInfoType, _propertiesType, _title, _artist, _albumTitle, aspect);

            // If the source did not refresh its timeline when playback resumed, the reported position is the one
            // frozen while paused: extrapolate from the moment playback started, not from the stale timestamp.
            var positionUpdatedAt = _positionUpdatedAt;
            if (_state == MediaPlaybackState.Playing && _playingSince > positionUpdatedAt)
                positionUpdatedAt = _playingSince;

            var info = new MediaSessionInfo
            {
                SessionId = SessionId,
                SourceAppId = SourceAppId,
                SourceDisplayName = App.DisplayName,
                Title = _title,
                Artist = _artist,
                AlbumTitle = _albumTitle,
                Kind = kind,
                State = _state,
                Thumbnail = _thumbnail,
                Position = _position,
                Duration = _duration,
                PositionUpdatedAt = positionUpdatedAt,
                PlaybackRate = _playbackRate,
                CanPlayPause = _canToggle || _canPlay || _canPause,
                CanNext = _canNext,
                CanPrevious = _canPrevious,
                CanSeek = _canChangePosition && _duration > TimeSpan.Zero,
            };

            var state = new MediaTrackerState(info, _playingSince, _pausedAt);
            _cachedState = state;
            return state;
        }
    }

    // ───────────────────────────── Commands ─────────────────────────────

    /// <summary>
    /// Executes a transport command if the session allows it (Can* checked first). Returns false when the command
    /// is not supported, the app refused it, timed out, or the session disappeared. Never throws.
    /// </summary>
    public async Task<bool> ExecuteAsync(MediaCommandKind kind, TimeSpan? position)
    {
        if (_disposed) return false;
        try
        {
            // Controls change with content (e.g. "next" disabled on the last track); read fresh values.
            if (RefreshPlaybackInfo()) Notify(MediaChangeKind.Full);

            bool canPlay, canPause, canToggle, canNext, canPrevious, canPosition, isPlaying;
            TimeSpan start, duration;
            lock (_dataLock)
            {
                canPlay = _canPlay;
                canPause = _canPause;
                canToggle = _canToggle;
                canNext = _canNext;
                canPrevious = _canPrevious;
                canPosition = _canChangePosition;
                isPlaying = _state == MediaPlaybackState.Playing;
                start = _startTime;
                duration = _duration;
            }

            var session = _session;
            using var cts = new CancellationTokenSource(CommandTimeout);
            Task<bool>? operation = null;
            switch (kind)
            {
                case MediaCommandKind.TogglePlayPause:
                    if (canToggle) operation = session.TryTogglePlayPauseAsync().AsTask(cts.Token);
                    else if (isPlaying && canPause) operation = session.TryPauseAsync().AsTask(cts.Token);
                    else if (!isPlaying && canPlay) operation = session.TryPlayAsync().AsTask(cts.Token);
                    break;

                case MediaCommandKind.Play:
                    if (canPlay) operation = session.TryPlayAsync().AsTask(cts.Token);
                    else if (canToggle && !isPlaying) operation = session.TryTogglePlayPauseAsync().AsTask(cts.Token);
                    break;

                case MediaCommandKind.Pause:
                    if (canPause) operation = session.TryPauseAsync().AsTask(cts.Token);
                    else if (canToggle && isPlaying) operation = session.TryTogglePlayPauseAsync().AsTask(cts.Token);
                    break;

                case MediaCommandKind.Next:
                    if (canNext) operation = session.TrySkipNextAsync().AsTask(cts.Token);
                    break;

                case MediaCommandKind.Previous:
                    if (canPrevious) operation = session.TrySkipPreviousAsync().AsTask(cts.Token);
                    break;

                case MediaCommandKind.Seek:
                    if (canPosition && position is { } target)
                    {
                        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                        if (duration > TimeSpan.Zero && target > duration) target = duration;
                        // GSMTC positions are absolute in the source's timeline (100 ns ticks), offset by StartTime.
                        operation = session.TryChangePlaybackPositionAsync((start + target).Ticks).AsTask(cts.Token);
                    }
                    break;
            }

            if (operation is null)
            {
                Log.Debug($"Media: command {kind} not supported by {SessionId} right now");
                return false;
            }

            var ok = await operation.ConfigureAwait(false);
            if (ok && kind == MediaCommandKind.Seek && RefreshTimeline()) Notify(MediaChangeKind.TimelineOnly);
            return ok;
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Media: command {kind} to {SessionId} timed out");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: command {kind} to {SessionId} failed: {ex.Message}");
            return false;
        }
    }

    private static MediaPlaybackState MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        _ => MediaPlaybackState.Closed,
    };
}
