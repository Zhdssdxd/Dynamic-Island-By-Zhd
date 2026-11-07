using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Core;

/// <summary>Long-lived "live activity" the island can host.</summary>
public enum IslandActivity
{
    None,
    Music,
    Video,
    Timer,
    Download,
}

/// <summary>What the capsule is presenting right now. The UI maps each value to a layout + size.</summary>
public enum IslandPresentation
{
    /// <summary>Capsule fully hidden (fullscreen app, user toggle, or idle with ShowWhenIdle = false).</summary>
    Hidden,
    /// <summary>Small empty pill.</summary>
    Idle,
    /// <summary>Compact live activity for <see cref="IslandState.Activity"/> (album art + bars, timer digits, …).</summary>
    Compact,
    /// <summary>User expanded the island. Activity may be None → dashboard (clock, quick actions).</summary>
    Expanded,
    /// <summary>Transient notification banner (<see cref="IslandState.Notification"/>).</summary>
    Notification,
    /// <summary>Transient system HUD (<see cref="IslandState.Indicator"/>).</summary>
    Indicator,
}

/// <summary>
/// Observable state the UI binds to. Owned and mutated only by <see cref="IslandController"/> on the UI thread.
/// </summary>
public sealed class IslandState : ObservableObject
{
    private IslandPresentation _presentation = IslandPresentation.Idle;
    private IslandActivity _activity;
    private IslandActivity _secondaryActivity;
    private bool _isExpanded;
    private MediaSessionInfo? _media;
    private IReadOnlyList<MediaSessionInfo> _mediaSessions = Array.Empty<MediaSessionInfo>();
    private IslandNotification? _notification;
    private SystemIndicatorEvent? _indicator;
    private TimerStateChanged? _timer;
    private DownloadProgressChanged? _download;
    private AppSettings _settings = new();
    private int _pendingNotificationCount;
    private int _transientSequence;
    private bool _isVisualizerActive;
    private bool _isFullscreenSuppressed;
    private bool _isUserHidden;

    public IslandPresentation Presentation { get => _presentation; internal set => SetProperty(ref _presentation, value); }

    /// <summary>Highest-priority live activity (shown in Compact/Expanded).</summary>
    public IslandActivity Activity { get => _activity; internal set => SetProperty(ref _activity, value); }

    /// <summary>Second live activity, if any (UI may show it as a small badge on the right).</summary>
    public IslandActivity SecondaryActivity { get => _secondaryActivity; internal set => SetProperty(ref _secondaryActivity, value); }

    public bool IsExpanded { get => _isExpanded; internal set => SetProperty(ref _isExpanded, value); }

    /// <summary>Displayed media session (already filtered by music/video settings). Null when none.</summary>
    public MediaSessionInfo? Media { get => _media; internal set => SetProperty(ref _media, value); }

    /// <summary>All known media sessions (for the session switcher).</summary>
    public IReadOnlyList<MediaSessionInfo> MediaSessions { get => _mediaSessions; internal set => SetProperty(ref _mediaSessions, value); }

    /// <summary>Notification being displayed (valid when Presentation == Notification).</summary>
    public IslandNotification? Notification { get => _notification; internal set => SetProperty(ref _notification, value); }

    /// <summary>Indicator being displayed (valid when Presentation == Indicator). Updated in place for same-kind events.</summary>
    public SystemIndicatorEvent? Indicator { get => _indicator; internal set => SetProperty(ref _indicator, value); }

    /// <summary>Latest timer state (null if the timer was never used).</summary>
    public TimerStateChanged? Timer { get => _timer; internal set => SetProperty(ref _timer, value); }

    /// <summary>Most relevant download (null if none).</summary>
    public DownloadProgressChanged? Download { get => _download; internal set => SetProperty(ref _download, value); }

    public AppSettings Settings { get => _settings; internal set => SetProperty(ref _settings, value); }

    /// <summary>Notifications waiting in the queue behind the current one.</summary>
    public int PendingNotificationCount { get => _pendingNotificationCount; internal set => SetProperty(ref _pendingNotificationCount, value); }

    /// <summary>Increments whenever a NEW transient item (notification or indicator of another kind) starts showing.
    /// UI uses it to play the "switch" animation even when Presentation does not change.</summary>
    public int TransientSequence { get => _transientSequence; internal set => SetProperty(ref _transientSequence, value); }

    /// <summary>True when the visualizer should animate (music playing + visualizer enabled).</summary>
    public bool IsVisualizerActive { get => _isVisualizerActive; internal set => SetProperty(ref _isVisualizerActive, value); }

    public bool IsFullscreenSuppressed { get => _isFullscreenSuppressed; internal set => SetProperty(ref _isFullscreenSuppressed, value); }

    public bool IsUserHidden { get => _isUserHidden; internal set => SetProperty(ref _isUserHidden, value); }
}
