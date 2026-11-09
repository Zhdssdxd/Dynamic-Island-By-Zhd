using System.Windows.Threading;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// The island's "brain": consumes module events from the bus and decides what the capsule presents.
/// Runs entirely on the UI thread (all subscriptions are marshalled there) and mutates <see cref="State"/>.
///
/// Priority rules:
///   Hidden (user/fullscreen) &gt; Indicator HUD &gt; Notification &gt; Expanded &gt; Compact activity &gt; Idle.
///   Activities: running timer &gt; playing media &gt; active download &gt; paused timer &gt; paused media &gt; finished download.
/// </summary>
public sealed class IslandController : IDisposable
{
    /// <summary>Paused media stops being a live activity after this long.</summary>
    internal static readonly TimeSpan PausedMediaTimeout = TimeSpan.FromMinutes(3);
    /// <summary>A completed/failed download lingers in the island for this long.</summary>
    internal static readonly TimeSpan DownloadLinger = TimeSpan.FromSeconds(4);
    /// <summary>A notification interrupted by an indicator resumes with at least this much time.</summary>
    private static readonly TimeSpan MinResumeTime = TimeSpan.FromSeconds(2);

    private readonly IEventBus _bus;
    private readonly SubscriptionBag _subs = new();
    private readonly DispatcherTimer _transientTimer;
    private readonly DispatcherTimer _housekeepingTimer;
    private readonly List<IslandNotification> _queue = new();
    private readonly Dictionary<string, DownloadProgressChanged> _downloads = new();
    private readonly Dictionary<string, DateTimeOffset> _downloadFinishedAt = new();

    private MediaSessionInfo? _rawMedia;
    private IReadOnlyList<MediaSessionInfo> _rawSessions = Array.Empty<MediaSessionInfo>();
    private DateTimeOffset? _mediaPausedSince;
    private DateTimeOffset _transientEndsAt;
    private bool _isFullscreen;
    private bool _disposed;

    public IslandController(IEventBus bus, Dispatcher dispatcher)
    {
        _bus = bus;
        State = new IslandState();
        _transientTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _transientTimer.Tick += (_, _) => OnTransientExpired();
        _housekeepingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Housekeeping(), dispatcher);
        _housekeepingTimer.Stop();
    }

    public IslandState State { get; }

    public IEventBus Bus => _bus;

    /// <summary>Subscribes to the bus. Call once on the UI thread after settings were loaded.</summary>
    public void Start(AppSettings initialSettings)
    {
        State.Settings = initialSettings;
        _subs.Add(_bus.Subscribe<SettingsChanged>(e => { State.Settings = e.Settings; Recompute(); }, onUiThread: true));
        _subs.Add(_bus.Subscribe<MediaSessionsChanged>(OnMedia, onUiThread: true));
        _subs.Add(_bus.Subscribe<NotificationReceived>(e => OnNotification(e.Notification), onUiThread: true));
        _subs.Add(_bus.Subscribe<SystemIndicatorEvent>(OnIndicator, onUiThread: true));
        _subs.Add(_bus.Subscribe<TimerStateChanged>(OnTimer, onUiThread: true));
        _subs.Add(_bus.Subscribe<DownloadProgressChanged>(OnDownload, onUiThread: true));
        _subs.Add(_bus.Subscribe<FullscreenStateChanged>(e => { _isFullscreen = e.IsFullscreen; Recompute(); }, onUiThread: true));
        _subs.Add(_bus.Subscribe<IslandActionRequested>(e => Handle(e.Action), onUiThread: true));

        // Pick up state published before we subscribed.
        if (_bus.GetLast<MediaSessionsChanged>() is { } m) OnMedia(m);
        if (_bus.GetLast<TimerStateChanged>() is { } t) OnTimer(t);
        Recompute();
    }

    // ───────────────────────────── User actions ─────────────────────────────

    public void Handle(IslandUserAction action)
    {
        switch (action)
        {
            case IslandUserAction.ToggleExpanded: SetExpanded(!State.IsExpanded); break;
            case IslandUserAction.Expand: SetExpanded(true); break;
            case IslandUserAction.Collapse: SetExpanded(false); break;
            case IslandUserAction.ToggleVisibility:
                State.IsUserHidden = !State.IsUserHidden;
                Recompute();
                break;
            case IslandUserAction.DismissTransient: DismissTransient(); break;
            case IslandUserAction.ToggleClipboard:
                if (State.IsClipboardOpen && State.IsExpanded)
                {
                    SetExpanded(false);
                }
                else
                {
                    if (State.IsUserHidden) State.IsUserHidden = false;
                    State.IsClipboardOpen = true;
                    SetExpanded(true);
                }
                break;
        }
    }

    public void SetExpanded(bool expanded)
    {
        // Clicking an active transient dismisses it and expands the underlying activity.
        if (expanded && State.Presentation is IslandPresentation.Notification or IslandPresentation.Indicator)
            ClearTransient(showNext: false);
        State.IsExpanded = expanded;
        if (!expanded) State.IsClipboardOpen = false;
        Recompute();
    }

    public void DismissTransient() => ClearTransient(showNext: true);

    // ───────────────────────────── Event handlers ─────────────────────────────

    private void OnMedia(MediaSessionsChanged e)
    {
        var sameSession = _rawMedia?.SessionId == e.Current?.SessionId;
        _rawMedia = e.Current;
        _rawSessions = e.All;

        if (e.Current is null || e.Current.IsPlaying)
            _mediaPausedSince = null;
        else if (e.Current.State == MediaPlaybackState.Paused && (!sameSession || _mediaPausedSince is null))
            _mediaPausedSince = DateTimeOffset.UtcNow;

        Recompute();
    }

    private void OnNotification(IslandNotification n)
    {
        var s = State.Settings;
        if (n.Source == NotificationSource.System && !s.Features.NotificationsEnabled) return;

        // Critical first, then by priority; FIFO within the same priority.
        var index = _queue.FindIndex(q => q.Priority < n.Priority);
        if (index < 0) _queue.Add(n); else _queue.Insert(index, n);

        // Higher-priority notification pre-empts the one currently shown.
        if (State.Presentation == IslandPresentation.Notification && State.Notification is { } current && n.Priority > current.Priority)
        {
            _queue.Insert(_queue.IndexOf(n) + 1, current);
            ShowNextNotification();
            return;
        }

        if (State.Presentation is not (IslandPresentation.Notification or IslandPresentation.Indicator))
            ShowNextNotification();
        else
            State.PendingNotificationCount = _queue.Count;
    }

    private void OnIndicator(SystemIndicatorEvent e)
    {
        if (!IsIndicatorEnabled(e.Kind, State.Settings.Indicators)) return;
        if (State.IsUserHidden) return;

        // An indicator interrupts a notification; the notification resumes afterwards.
        if (State.Presentation == IslandPresentation.Notification && State.Notification is { } current)
        {
            var remaining = _transientEndsAt - DateTimeOffset.UtcNow;
            if (remaining < MinResumeTime) remaining = MinResumeTime;
            _queue.Insert(0, current with { DisplayDuration = remaining });
            State.Notification = null;
        }

        var sameKind = State.Presentation == IslandPresentation.Indicator && State.Indicator?.Kind == e.Kind;
        State.Indicator = e;
        if (!sameKind) State.TransientSequence++;
        StartTransientTimer(TimeSpan.FromSeconds(State.Settings.Indicators.DurationSeconds));
        State.PendingNotificationCount = _queue.Count;
        Recompute();
    }

    private void OnTimer(TimerStateChanged e)
    {
        State.Timer = e;
        Recompute();
    }

    private void OnDownload(DownloadProgressChanged e)
    {
        if (!State.Settings.Features.DownloadsEnabled) return;
        _downloads[e.Id] = e;
        if (e.State == DownloadState.InProgress) _downloadFinishedAt.Remove(e.Id);
        else _downloadFinishedAt[e.Id] = DateTimeOffset.UtcNow;
        Recompute();
    }

    // ───────────────────────────── Transients ─────────────────────────────

    private void ShowNextNotification()
    {
        if (_queue.Count == 0)
        {
            State.Notification = null;
            State.PendingNotificationCount = 0;
            Recompute();
            return;
        }

        var next = _queue[0];
        _queue.RemoveAt(0);
        State.Indicator = null;
        State.Notification = next;
        State.PendingNotificationCount = _queue.Count;
        State.TransientSequence++;
        StartTransientTimer(next.DisplayDuration ?? TimeSpan.FromSeconds(State.Settings.Features.NotificationDurationSeconds));
        Recompute();
    }

    private void StartTransientTimer(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(300)) duration = TimeSpan.FromMilliseconds(300);
        _transientEndsAt = DateTimeOffset.UtcNow + duration;
        _transientTimer.Stop();
        _transientTimer.Interval = duration;
        _transientTimer.Start();
    }

    private void OnTransientExpired() => ClearTransient(showNext: true);

    private void ClearTransient(bool showNext)
    {
        _transientTimer.Stop();
        State.Indicator = null;
        State.Notification = null;
        if (showNext && _queue.Count > 0)
        {
            ShowNextNotification();
            return;
        }
        if (!showNext) State.PendingNotificationCount = _queue.Count;
        Recompute();
    }

    // ───────────────────────────── Recompute ─────────────────────────────

    private void Housekeeping()
    {
        // Expire lingering downloads / long-paused media.
        Recompute();
    }

    /// <summary>Recomputes derived state (activities, presentation). Cheap; called after every change.</summary>
    private void Recompute()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        var s = State.Settings;

        // Media filtered by settings.
        var media = FilterMedia(_rawMedia, s.Features);
        if (media is { State: MediaPlaybackState.Paused } && _mediaPausedSince is { } since && now - since > PausedMediaTimeout)
            media = null;
        State.Media = media;
        State.MediaSessions = _rawSessions;

        // Downloads: prune finished ones after linger time, pick the most relevant.
        foreach (var id in _downloadFinishedAt.Where(kv => now - kv.Value > DownloadLinger).Select(kv => kv.Key).ToList())
        {
            _downloadFinishedAt.Remove(id);
            _downloads.Remove(id);
        }
        State.Download = _downloads.Values
            .OrderBy(d => d.State == DownloadState.InProgress ? 0 : 1)
            .ThenByDescending(d => d.BytesPerSecond ?? 0)
            .FirstOrDefault();

        var (primary, secondary) = ChooseActivities(State.Timer, media, State.Download);
        State.Activity = primary;
        State.SecondaryActivity = secondary;

        State.IsVisualizerActive = s.Features.VisualizerEnabled && media is { IsPlaying: true };
        State.IsFullscreenSuppressed = _isFullscreen && s.System.HideInFullscreen;

        // An expanded island with nothing left to show (no activity) stays expanded as dashboard — that is intended.
        State.Presentation = ComputePresentation(
            userHidden: State.IsUserHidden,
            fullscreenSuppressed: State.IsFullscreenSuppressed,
            indicator: State.Indicator,
            notification: State.Notification,
            expanded: State.IsExpanded,
            activity: primary,
            showWhenIdle: s.Appearance.ShowWhenIdle);

        // Housekeeping timer only runs while something can expire.
        var needsHousekeeping = _downloadFinishedAt.Count > 0 || _mediaPausedSince is not null && media is not null;
        if (needsHousekeeping && !_housekeepingTimer.IsEnabled) _housekeepingTimer.Start();
        else if (!needsHousekeeping && _housekeepingTimer.IsEnabled) _housekeepingTimer.Stop();
    }

    internal static MediaSessionInfo? FilterMedia(MediaSessionInfo? media, FeatureSettings f)
    {
        if (media is null) return null;
        if (media.State is MediaPlaybackState.Closed or MediaPlaybackState.Stopped) return null;
        if (media.Kind == MediaKind.Video && !f.VideoModeEnabled) return null;
        if (media.Kind != MediaKind.Video && !f.MusicModeEnabled) return null;
        return media;
    }

    internal static (IslandActivity Primary, IslandActivity Secondary) ChooseActivities(
        TimerStateChanged? timer, MediaSessionInfo? media, DownloadProgressChanged? download)
    {
        var mediaActivity = media is null ? IslandActivity.None
            : media.Kind == MediaKind.Video ? IslandActivity.Video : IslandActivity.Music;

        // (activity, rank) — lower rank wins.
        var candidates = new List<(IslandActivity Activity, int Rank)>(3);
        if (timer is { State: TimerRunState.Running }) candidates.Add((IslandActivity.Timer, 0));
        else if (timer is { State: TimerRunState.Paused }) candidates.Add((IslandActivity.Timer, 3));

        if (media is { IsPlaying: true }) candidates.Add((mediaActivity, 1));
        else if (media is not null) candidates.Add((mediaActivity, 4));

        if (download is { State: DownloadState.InProgress }) candidates.Add((IslandActivity.Download, 2));
        else if (download is not null) candidates.Add((IslandActivity.Download, 5));

        candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        var primary = candidates.Count > 0 ? candidates[0].Activity : IslandActivity.None;
        var secondary = candidates.Count > 1 ? candidates[1].Activity : IslandActivity.None;
        return (primary, secondary);
    }

    internal static IslandPresentation ComputePresentation(
        bool userHidden, bool fullscreenSuppressed, SystemIndicatorEvent? indicator, IslandNotification? notification,
        bool expanded, IslandActivity activity, bool showWhenIdle)
    {
        if (userHidden) return IslandPresentation.Hidden;
        if (fullscreenSuppressed && notification is not { Priority: NotificationPriority.Critical })
            return IslandPresentation.Hidden;
        if (indicator is not null) return IslandPresentation.Indicator;
        if (notification is not null) return IslandPresentation.Notification;
        if (expanded) return IslandPresentation.Expanded;
        if (activity != IslandActivity.None) return IslandPresentation.Compact;
        return showWhenIdle ? IslandPresentation.Idle : IslandPresentation.Hidden;
    }

    internal static bool IsIndicatorEnabled(IndicatorKind kind, IndicatorSettings s) => kind switch
    {
        IndicatorKind.Volume => s.Volume,
        IndicatorKind.Brightness => s.Brightness,
        IndicatorKind.Battery => s.Battery,
        IndicatorKind.Charger => s.Charger,
        IndicatorKind.UsbDevice => s.Usb,
        IndicatorKind.Bluetooth => s.Bluetooth,
        IndicatorKind.Microphone => s.Microphone,
        _ => true,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        _transientTimer.Stop();
        _housekeepingTimer.Stop();
    }
}
