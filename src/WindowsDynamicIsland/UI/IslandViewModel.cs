using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.UI.Components;

namespace WindowsDynamicIsland.UI;

/// <summary>Media session chip for the session switcher.</summary>
public sealed record SessionChip(string SessionId, string Label, bool IsCurrent);

/// <summary>Timer preset chip on the dashboard.</summary>
public sealed record TimerPreset(double Minutes, string Label);

/// <summary>
/// Presentation layer over <see cref="IslandState"/>: formatted strings, brushes and commands for the views.
/// Owns a 4 Hz ticker that runs ONLY while a visible view shows live progress (expanded media timeline,
/// running timer) and a 1 Hz clock tick only while the dashboard is shown. Idle → no timers at all.
/// </summary>
public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(1);
    private const string Minus = "−";
    private const string Nbsp = " ";

    private readonly IEventBus _bus;
    private readonly DispatcherTimer _ticker;
    private bool _disposed;

    private readonly Brush _white;
    private readonly Brush _secondary;
    private readonly Brush _orange;
    private readonly Brush _green;
    private readonly Brush _red;
    private readonly Brush _blue;

    public IslandViewModel(IslandController controller)
    {
        _bus = controller.Bus;
        State = controller.State;

        _white = Res("TextPrimaryBrush", Colors.White);
        _secondary = Res("TextSecondaryBrush", Color.FromRgb(0x98, 0x98, 0x9F));
        _orange = Res("AccentOrangeBrush", Color.FromRgb(0xFF, 0x9F, 0x0A));
        _green = Res("AccentGreenBrush", Color.FromRgb(0x30, 0xD1, 0x58));
        _red = Res("AccentRedBrush", Color.FromRgb(0xFF, 0x45, 0x3A));
        _blue = Res("AccentBlueBrush", Color.FromRgb(0x0A, 0x84, 0xFF));
        _accentBrush = _white;
        _indicatorBrush = _white;
        _downloadBrush = _blue;
        _secondaryActivityBrush = _secondary;

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = FastTick };
        _ticker.Tick += (_, _) => OnTick();

        PlayPauseCommand = new RelayCommand(() => SendMedia(MediaCommandKind.TogglePlayPause), () => State.Media?.CanPlayPause == true);
        NextCommand = new RelayCommand(() => SendMedia(MediaCommandKind.Next), () => State.Media?.CanNext == true);
        PreviousCommand = new RelayCommand(() => SendMedia(MediaCommandKind.Previous), () => State.Media?.CanPrevious == true);
        SeekCommand = new RelayCommand(Seek, p => State.Media is { CanSeek: true, HasTimeline: true } && p is double);
        SelectSessionCommand = new RelayCommand(p =>
        {
            if (p is string id && id.Length > 0) _bus.Publish(new MediaCommand(MediaCommandKind.SelectSession, id));
        });

        TimerToggleCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.TogglePause)));
        TimerStopCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.Stop)));
        TimerAddMinuteCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.Extend, TimeSpan.FromMinutes(1))));
        StartStopwatchCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.StartStopwatch)));
        StartTimerCommand = new RelayCommand(p =>
        {
            if (p is double minutes && minutes > 0)
                _bus.Publish(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromMinutes(minutes)));
        });

        OpenSettingsCommand = new RelayCommand(() => _bus.Publish(new OpenSettingsRequested()));
        ExitCommand = new RelayCommand(() => _bus.Publish(new ExitRequested()));
        HideIslandCommand = new RelayCommand(() => _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleVisibility)));
        DismissCommand = new RelayCommand(() => _bus.Publish(new IslandActionRequested(IslandUserAction.DismissTransient)));
        CollapseCommand = new RelayCommand(() => _bus.Publish(new IslandActionRequested(IslandUserAction.Collapse)));

        State.PropertyChanged += OnStatePropertyChanged;
        RefreshAll();
    }

    public IslandState State { get; }

    // ───────────────────────────── Commands ─────────────────────────────

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    /// <summary>Parameter: double fraction 0..1 of the duration.</summary>
    public ICommand SeekCommand { get; }
    /// <summary>Parameter: session id (string).</summary>
    public ICommand SelectSessionCommand { get; }
    public ICommand TimerToggleCommand { get; }
    public ICommand TimerStopCommand { get; }
    public ICommand TimerAddMinuteCommand { get; }
    public ICommand StartStopwatchCommand { get; }
    /// <summary>Parameter: double minutes.</summary>
    public ICommand StartTimerCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand HideIslandCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand CollapseCommand { get; }

    // ───────────────────────────── View ─────────────────────────────

    private IslandViewKind _viewKind = IslandViewKind.Idle;
    public IslandViewKind ViewKind { get => _viewKind; private set => SetProperty(ref _viewKind, value); }

    // ───────────────────────────── Media ─────────────────────────────

    private string _mediaTitle = "";
    private string _mediaArtist = "";
    private string _mediaSourceName = "";
    private ImageSource? _thumbnail;
    private bool _isMediaPlaying;
    private bool _isMediaPaused;
    private string _playPauseGlyph = Glyphs.Play;
    private string _mediaStateText = "";
    private bool _hasTimeline;
    private bool _canSeek;
    private double _mediaProgress;
    private string _elapsedText = "";
    private string _remainingText = "";
    private Brush _accentBrush;
    private double _visualizerOpacity = 1;
    private bool _showPlayingBars;
    private string _playbackStateGlyph = Glyphs.Play;
    private IReadOnlyList<SessionChip> _sessionChips = Array.Empty<SessionChip>();
    private bool _hasMultipleSessions;
    private bool _showSourceLabel;
    private ImageSource? _accentSource;
    private IslandColorScheme _accentScheme;

    public string MediaTitle { get => _mediaTitle; private set => SetProperty(ref _mediaTitle, value); }
    public string MediaArtist { get => _mediaArtist; private set => SetProperty(ref _mediaArtist, value); }
    public string MediaSourceName { get => _mediaSourceName; private set => SetProperty(ref _mediaSourceName, value); }
    public ImageSource? Thumbnail { get => _thumbnail; private set => SetProperty(ref _thumbnail, value); }
    public bool IsMediaPlaying { get => _isMediaPlaying; private set => SetProperty(ref _isMediaPlaying, value); }
    public bool IsMediaPaused { get => _isMediaPaused; private set => SetProperty(ref _isMediaPaused, value); }
    public string PlayPauseGlyph { get => _playPauseGlyph; private set => SetProperty(ref _playPauseGlyph, value); }
    /// <summary>"Воспроизводится" / "Пауза" (empty for other states).</summary>
    public string MediaStateText { get => _mediaStateText; private set => SetProperty(ref _mediaStateText, value); }
    public bool HasTimeline { get => _hasTimeline; private set => SetProperty(ref _hasTimeline, value); }
    public bool CanSeek { get => _canSeek; private set => SetProperty(ref _canSeek, value); }
    public double MediaProgress { get => _mediaProgress; private set => SetProperty(ref _mediaProgress, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    /// <summary>White, or the album-art tint when ColorScheme == Adaptive.</summary>
    public Brush AccentBrush { get => _accentBrush; private set => SetProperty(ref _accentBrush, value); }
    /// <summary>1 while playing, dimmed while paused (bars decay to rest).</summary>
    public double VisualizerOpacity { get => _visualizerOpacity; private set => SetProperty(ref _visualizerOpacity, value); }
    /// <summary>Visualizer enabled AND media playing (compact video shows bars only then).</summary>
    public bool ShowPlayingBars { get => _showPlayingBars; private set => SetProperty(ref _showPlayingBars, value); }
    /// <summary>State glyph (not an action): Play while playing, Pause while paused.</summary>
    public string PlaybackStateGlyph { get => _playbackStateGlyph; private set => SetProperty(ref _playbackStateGlyph, value); }
    public IReadOnlyList<SessionChip> SessionChips { get => _sessionChips; private set => SetProperty(ref _sessionChips, value); }
    public bool HasMultipleSessions { get => _hasMultipleSessions; private set => SetProperty(ref _hasMultipleSessions, value); }
    public bool ShowSourceLabel { get => _showSourceLabel; private set => SetProperty(ref _showSourceLabel, value); }

    // ───────────────────────────── Timer ─────────────────────────────

    private string _timerText = "";
    private double _timerFraction;
    private bool _isCountdown;
    private bool _isTimerRunning;
    private bool _isTimerPaused;
    private string _timerToggleGlyph = Glyphs.Pause;
    private string _timerCaption = "";

    public string TimerText { get => _timerText; private set => SetProperty(ref _timerText, value); }
    /// <summary>Countdown: remaining fraction 1 → 0. Stopwatch: 0.</summary>
    public double TimerFraction { get => _timerFraction; private set => SetProperty(ref _timerFraction, value); }
    public bool IsCountdown { get => _isCountdown; private set => SetProperty(ref _isCountdown, value); }
    public bool IsTimerRunning { get => _isTimerRunning; private set => SetProperty(ref _isTimerRunning, value); }
    public bool IsTimerPaused { get => _isTimerPaused; private set => SetProperty(ref _isTimerPaused, value); }
    public string TimerToggleGlyph { get => _timerToggleGlyph; private set => SetProperty(ref _timerToggleGlyph, value); }
    public string TimerCaption { get => _timerCaption; private set => SetProperty(ref _timerCaption, value); }

    // ───────────────────────────── Download ─────────────────────────────

    private string _downloadFileName = "";
    private double _downloadFraction;
    private bool _isDownloadDeterminate;
    private bool _isDownloadInProgress;
    private bool _showDownloadRing;
    private string _downloadPercentText = "";
    private string _downloadSizeText = "";
    private string _downloadSpeedText = "";
    private string _downloadStatusText = "";
    private string _downloadCompactText = "";
    private string _downloadGlyph = Glyphs.Download;
    private Brush _downloadBrush;

    public string DownloadFileName { get => _downloadFileName; private set => SetProperty(ref _downloadFileName, value); }
    public double DownloadFraction { get => _downloadFraction; private set => SetProperty(ref _downloadFraction, value); }
    public bool IsDownloadDeterminate { get => _isDownloadDeterminate; private set => SetProperty(ref _isDownloadDeterminate, value); }
    public bool IsDownloadInProgress { get => _isDownloadInProgress; private set => SetProperty(ref _isDownloadInProgress, value); }
    public bool ShowDownloadRing { get => _showDownloadRing; private set => SetProperty(ref _showDownloadRing, value); }
    public string DownloadPercentText { get => _downloadPercentText; private set => SetProperty(ref _downloadPercentText, value); }
    /// <summary>"12,4 МБ из 80 МБ" or "12,4 МБ".</summary>
    public string DownloadSizeText { get => _downloadSizeText; private set => SetProperty(ref _downloadSizeText, value); }
    /// <summary>"3,1 МБ/с" while in progress and known.</summary>
    public string DownloadSpeedText { get => _downloadSpeedText; private set => SetProperty(ref _downloadSpeedText, value); }
    public string DownloadStatusText { get => _downloadStatusText; private set => SetProperty(ref _downloadStatusText, value); }
    public string DownloadCompactText { get => _downloadCompactText; private set => SetProperty(ref _downloadCompactText, value); }
    public string DownloadGlyph { get => _downloadGlyph; private set => SetProperty(ref _downloadGlyph, value); }
    public Brush DownloadBrush { get => _downloadBrush; private set => SetProperty(ref _downloadBrush, value); }

    // ───────────────────────────── Notification ─────────────────────────────

    private string _notificationAppName = "";
    private string _notificationTitle = "";
    private string _notificationBody = "";
    private ImageSource? _notificationIcon;
    private string _notificationGlyph = Glyphs.Notification;
    private string _notificationTimeText = "";
    private string _pendingBadgeText = "";

    public string NotificationAppName { get => _notificationAppName; private set => SetProperty(ref _notificationAppName, value); }
    public string NotificationTitle { get => _notificationTitle; private set => SetProperty(ref _notificationTitle, value); }
    public string NotificationBody { get => _notificationBody; private set => SetProperty(ref _notificationBody, value); }
    public ImageSource? NotificationIcon { get => _notificationIcon; private set => SetProperty(ref _notificationIcon, value); }
    public string NotificationGlyph { get => _notificationGlyph; private set => SetProperty(ref _notificationGlyph, value); }
    public string NotificationTimeText { get => _notificationTimeText; private set => SetProperty(ref _notificationTimeText, value); }
    /// <summary>"+2" when more notifications are queued, empty otherwise.</summary>
    public string PendingBadgeText { get => _pendingBadgeText; private set => SetProperty(ref _pendingBadgeText, value); }

    // ───────────────────────────── Indicator ─────────────────────────────

    private string _indicatorGlyph = "";
    private string _indicatorTitle = "";
    private string _indicatorValueText = "";
    private double _indicatorLevel;
    private bool _hasIndicatorLevel;
    private Brush _indicatorBrush;
    private double _indicatorBarOpacity = 1;

    public string IndicatorGlyph { get => _indicatorGlyph; private set => SetProperty(ref _indicatorGlyph, value); }
    public string IndicatorTitle { get => _indicatorTitle; private set => SetProperty(ref _indicatorTitle, value); }
    /// <summary>Subtitle from the module, or "45%" derived from the level.</summary>
    public string IndicatorValueText { get => _indicatorValueText; private set => SetProperty(ref _indicatorValueText, value); }
    public double IndicatorLevel { get => _indicatorLevel; private set => SetProperty(ref _indicatorLevel, value); }
    public bool HasIndicatorLevel { get => _hasIndicatorLevel; private set => SetProperty(ref _hasIndicatorLevel, value); }
    /// <summary>Tone color: Neutral white, Positive green, Warning orange, Critical red.</summary>
    public Brush IndicatorBrush { get => _indicatorBrush; private set => SetProperty(ref _indicatorBrush, value); }
    public double IndicatorBarOpacity { get => _indicatorBarOpacity; private set => SetProperty(ref _indicatorBarOpacity, value); }

    // ───────────────────────────── Secondary activity badge ─────────────────────────────

    private bool _hasSecondaryActivity;
    private string _secondaryActivityGlyph = "";
    private Brush _secondaryActivityBrush;

    public bool HasSecondaryActivity { get => _hasSecondaryActivity; private set => SetProperty(ref _hasSecondaryActivity, value); }
    public string SecondaryActivityGlyph { get => _secondaryActivityGlyph; private set => SetProperty(ref _secondaryActivityGlyph, value); }
    public Brush SecondaryActivityBrush { get => _secondaryActivityBrush; private set => SetProperty(ref _secondaryActivityBrush, value); }

    // ───────────────────────────── Dashboard ─────────────────────────────

    private string _clockText = "";
    private string _dateText = "";
    private IReadOnlyList<TimerPreset> _timerPresets = Array.Empty<TimerPreset>();

    public string ClockText { get => _clockText; private set => SetProperty(ref _clockText, value); }
    public string DateText { get => _dateText; private set => SetProperty(ref _dateText, value); }
    public IReadOnlyList<TimerPreset> TimerPresets { get => _timerPresets; private set => SetProperty(ref _timerPresets, value); }

    // ───────────────────────────── State plumbing ─────────────────────────────

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(IslandState.Media):
            case nameof(IslandState.MediaSessions):
            case nameof(IslandState.IsVisualizerActive):
                RefreshMedia();
                break;
            case nameof(IslandState.Timer):
                RefreshTimer();
                break;
            case nameof(IslandState.Download):
                RefreshDownload();
                break;
            case nameof(IslandState.Notification):
            case nameof(IslandState.PendingNotificationCount):
                RefreshNotification();
                break;
            case nameof(IslandState.Indicator):
                RefreshIndicator();
                break;
            case nameof(IslandState.SecondaryActivity):
            case nameof(IslandState.Activity):
                RefreshSecondary();
                break;
            case nameof(IslandState.Settings):
                RefreshSettings();
                RefreshMedia();
                break;
            case null:
            case "":
                RefreshAll();
                return;
        }
        RefreshViewKind();
    }

    private void RefreshAll()
    {
        RefreshSettings();
        RefreshMedia();
        RefreshTimer();
        RefreshDownload();
        RefreshNotification();
        RefreshIndicator();
        RefreshSecondary();
        RefreshViewKind();
    }

    private void RefreshViewKind()
    {
        ViewKind = IslandLayout.ResolveView(State);
        if (ViewKind == IslandViewKind.Dashboard) RefreshClock();
        UpdateTicker();
    }

    private void UpdateTicker()
    {
        if (_disposed) return;
        var fast = ViewKind switch
        {
            IslandViewKind.ExpandedMusic or IslandViewKind.ExpandedVideo => State.Media is { IsPlaying: true, HasTimeline: true },
            IslandViewKind.CompactTimer or IslandViewKind.ExpandedTimer => State.Timer is { State: TimerRunState.Running },
            _ => false,
        };
        var clock = ViewKind == IslandViewKind.Dashboard;

        if (!fast && !clock)
        {
            if (_ticker.IsEnabled) _ticker.Stop();
            return;
        }

        var interval = fast ? FastTick : ClockTick;
        if (_ticker.Interval != interval) _ticker.Interval = interval;
        if (!_ticker.IsEnabled) _ticker.Start();
    }

    private void OnTick()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        switch (ViewKind)
        {
            case IslandViewKind.ExpandedMusic:
            case IslandViewKind.ExpandedVideo:
                RefreshMediaProgress(now);
                break;
            case IslandViewKind.CompactTimer:
            case IslandViewKind.ExpandedTimer:
                RefreshTimerProgress(now);
                break;
            case IslandViewKind.Dashboard:
                RefreshClock();
                break;
        }
    }

    // ───────────────────────────── Refreshers ─────────────────────────────

    private void RefreshSettings()
    {
        var presets = State.Settings.Timer.Presets ?? new List<double>();
        var list = presets.Where(m => m > 0).Select(m => new TimerPreset(m, FormatPresetShort(m))).ToList();
        if (!list.SequenceEqual(TimerPresets)) TimerPresets = list;
    }

    private void RefreshMedia()
    {
        var m = State.Media;
        var settings = State.Settings;

        MediaTitle = m?.Title.Trim() ?? "";
        MediaArtist = m?.Artist.Trim() ?? "";
        MediaSourceName = m?.SourceDisplayName.Trim() ?? "";
        Thumbnail = m?.Thumbnail;
        IsMediaPlaying = m?.IsPlaying == true;
        IsMediaPaused = m is { State: MediaPlaybackState.Paused };
        PlayPauseGlyph = IsMediaPlaying ? Glyphs.Pause : Glyphs.Play;
        MediaStateText = m?.State switch
        {
            MediaPlaybackState.Playing => "Воспроизводится",
            MediaPlaybackState.Paused => "Пауза",
            _ => "",
        };
        HasTimeline = m?.HasTimeline == true;
        CanSeek = m is { CanSeek: true, HasTimeline: true };
        VisualizerOpacity = State.IsVisualizerActive ? 1.0 : 0.45;
        ShowPlayingBars = settings.Features.VisualizerEnabled && IsMediaPlaying;
        PlaybackStateGlyph = IsMediaPlaying ? Glyphs.Play : Glyphs.Pause;

        // Adaptive tint from the artwork (cached per image).
        var scheme = settings.Appearance.ColorScheme;
        if (!ReferenceEquals(_accentSource, m?.Thumbnail) || _accentScheme != scheme)
        {
            _accentSource = m?.Thumbnail;
            _accentScheme = scheme;
            Brush accent = _white;
            if (scheme == IslandColorScheme.Adaptive && ArtworkColorExtractor.GetAccentColor(m?.Thumbnail) is { } color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                accent = brush;
            }
            AccentBrush = accent;
        }

        // Session switcher.
        var sessions = State.MediaSessions;
        var chips = sessions.Count > 1
            ? sessions.Select(s => new SessionChip(s.SessionId, ChipLabel(s), s.SessionId == m?.SessionId)).ToList()
            : new List<SessionChip>();
        if (!chips.SequenceEqual(SessionChips)) SessionChips = chips;
        HasMultipleSessions = chips.Count > 1;
        ShowSourceLabel = !HasMultipleSessions && MediaSourceName.Length > 0;

        RefreshMediaProgress(DateTimeOffset.UtcNow);
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshMediaProgress(DateTimeOffset now)
    {
        var m = State.Media;
        if (m is null || !m.HasTimeline)
        {
            MediaProgress = 0;
            ElapsedText = "";
            RemainingText = "";
            return;
        }

        var pos = m.GetEstimatedPosition(now);
        var duration = m.Duration;
        MediaProgress = duration.Ticks > 0 ? Math.Clamp((double)pos.Ticks / duration.Ticks, 0, 1) : 0;
        var elapsed = TimeSpan.FromSeconds(Math.Floor(pos.TotalSeconds));
        var remaining = TimeSpan.FromSeconds(Math.Ceiling((duration - pos).TotalSeconds));
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        ElapsedText = FormatTime(elapsed);
        RemainingText = Minus + FormatTime(remaining);
    }

    private void RefreshTimer()
    {
        var t = State.Timer;
        IsCountdown = t is { Kind: TimerKind.Countdown };
        IsTimerRunning = t is { State: TimerRunState.Running };
        IsTimerPaused = t is { State: TimerRunState.Paused };
        TimerToggleGlyph = IsTimerRunning ? Glyphs.Pause : Glyphs.Play;
        TimerCaption = t is null ? "" : (IsCountdown ? "Таймер" : "Секундомер") + (IsTimerPaused ? " · пауза" : "");
        RefreshTimerProgress(DateTimeOffset.UtcNow);
    }

    private void RefreshTimerProgress(DateTimeOffset now)
    {
        var t = State.Timer;
        if (t is null)
        {
            TimerText = "";
            TimerFraction = 0;
            return;
        }

        if (t.Kind == TimerKind.Countdown && t.GetRemaining(now) is { } remaining)
        {
            var secs = Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
            TimerText = FormatTime(TimeSpan.FromSeconds(secs));
            var total = t.Duration?.TotalSeconds ?? 0;
            TimerFraction = total > 0 ? Math.Clamp(remaining.TotalSeconds / total, 0, 1) : 0;
        }
        else
        {
            TimerText = FormatTime(TimeSpan.FromSeconds(Math.Floor(t.GetElapsed(now).TotalSeconds)));
            TimerFraction = 0;
        }
    }

    private void RefreshDownload()
    {
        var d = State.Download;
        if (d is null)
        {
            DownloadFileName = "";
            DownloadFraction = 0;
            IsDownloadDeterminate = false;
            IsDownloadInProgress = false;
            ShowDownloadRing = false;
            DownloadPercentText = "";
            DownloadSizeText = "";
            DownloadSpeedText = "";
            DownloadStatusText = "";
            DownloadCompactText = "";
            return;
        }

        var inProgress = d.State == DownloadState.InProgress;
        var fraction = d.Fraction;
        DownloadFileName = d.FileName;
        IsDownloadInProgress = inProgress;
        IsDownloadDeterminate = fraction is not null || !inProgress;
        DownloadFraction = d.State == DownloadState.Completed ? 1.0 : fraction ?? 0;
        ShowDownloadRing = inProgress && fraction is not null;
        DownloadPercentText = inProgress && fraction is { } f ? ((int)Math.Floor(f * 100)).ToString(CultureInfo.InvariantCulture) + "%" : "";

        DownloadSizeText = d.TotalBytes is > 0 && inProgress
            ? FormatBytes(d.BytesReceived) + " из " + FormatBytes(d.TotalBytes.Value)
            : d.BytesReceived > 0 || d.TotalBytes is > 0 ? FormatBytes(Math.Max(d.BytesReceived, d.TotalBytes ?? 0)) : "";
        DownloadSpeedText = inProgress && d.BytesPerSecond is > 0 ? FormatBytes((long)d.BytesPerSecond.Value) + "/с" : "";

        var source = string.IsNullOrWhiteSpace(d.Source) ? "" : " · " + d.Source;
        DownloadStatusText = d.State switch
        {
            DownloadState.InProgress => "Загрузка" + source,
            DownloadState.Completed => "Загружено" + source,
            _ => "Не удалось загрузить",
        };
        DownloadCompactText = d.State switch
        {
            DownloadState.InProgress => DownloadPercentText.Length > 0 ? DownloadPercentText
                : d.BytesReceived > 0 ? FormatBytes(d.BytesReceived) : "",
            DownloadState.Completed => "Готово",
            _ => "Ошибка",
        };
        DownloadGlyph = d.State switch
        {
            DownloadState.InProgress => Glyphs.Download,
            DownloadState.Completed => Glyphs.Check,
            _ => Glyphs.Error,
        };
        DownloadBrush = d.State switch
        {
            DownloadState.InProgress => _blue,
            DownloadState.Completed => _green,
            _ => _red,
        };
    }

    private void RefreshNotification()
    {
        var n = State.Notification;
        NotificationAppName = n?.AppName.Trim() ?? "";
        NotificationTitle = n?.Title.Trim() ?? "";
        NotificationBody = n?.Body.Trim() ?? "";
        NotificationIcon = n?.AppIcon;
        NotificationGlyph = string.IsNullOrEmpty(n?.Glyph) ? Glyphs.Notification : n!.Glyph;
        NotificationTimeText = n is null ? "" : FormatAge(DateTimeOffset.Now - n.Timestamp);
        var pending = State.PendingNotificationCount;
        PendingBadgeText = pending > 0 ? "+" + pending.ToString(CultureInfo.InvariantCulture) : "";
    }

    private void RefreshIndicator()
    {
        var i = State.Indicator;
        if (i is null) return; // keep the last values while the HUD animates away

        IndicatorGlyph = i.Glyph;
        IndicatorTitle = i.Title;
        HasIndicatorLevel = i.Level is not null;
        IndicatorLevel = i.Level is { } level ? Math.Clamp(level, 0, 1) : 0;
        IndicatorValueText = !string.IsNullOrWhiteSpace(i.Subtitle)
            ? i.Subtitle!.Trim()
            : i.Level is { } l ? ((int)Math.Round(Math.Clamp(l, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%" : "";
        IndicatorBrush = i.Tone switch
        {
            IndicatorTone.Positive => _green,
            IndicatorTone.Warning => _orange,
            IndicatorTone.Critical => _red,
            _ => _white,
        };
        var muted = i.Flag == true && i.Kind is IndicatorKind.Volume or IndicatorKind.Microphone;
        IndicatorBarOpacity = muted ? 0.35 : 1.0;
    }

    private void RefreshSecondary()
    {
        var a = State.SecondaryActivity;
        HasSecondaryActivity = a != IslandActivity.None && a != State.Activity;
        SecondaryActivityGlyph = a switch
        {
            IslandActivity.Music => Glyphs.Music,
            IslandActivity.Video => Glyphs.Video,
            IslandActivity.Timer => Glyphs.Stopwatch,
            IslandActivity.Download => Glyphs.Download,
            _ => "",
        };
        SecondaryActivityBrush = a switch
        {
            IslandActivity.Timer => _orange,
            IslandActivity.Download => _blue,
            _ => _secondary,
        };
    }

    private void RefreshClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm", Ru);
        var date = now.ToString("dddd, d MMMM", Ru);
        DateText = date.Length > 0 ? char.ToUpper(date[0], Ru) + date[1..] : date;
    }

    // ───────────────────────────── Commands impl ─────────────────────────────

    private void SendMedia(MediaCommandKind kind)
        => _bus.Publish(new MediaCommand(kind, State.Media?.SessionId));

    private void Seek(object? parameter)
    {
        if (parameter is not double fraction || State.Media is not { CanSeek: true, HasTimeline: true } m) return;
        var position = TimeSpan.FromTicks((long)(m.Duration.Ticks * Math.Clamp(fraction, 0, 1)));
        _bus.Publish(new MediaCommand(MediaCommandKind.Seek, m.SessionId, position));
    }

    // ───────────────────────────── Formatting ─────────────────────────────

    /// <summary>m:ss or h:mm:ss.</summary>
    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var hours = (int)t.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}");
    }

    /// <summary>"12,4 МБ" (binary units, Russian decimal comma, non-breaking space).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = Math.Max(0, bytes);
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        var format = u == 0 || v >= 100 ? "0" : "0.#";
        return v.ToString(format, Ru) + Nbsp + units[u];
    }

    public static string FormatPreset(double minutes)
    {
        if (minutes < 1) return Math.Round(minutes * 60).ToString("0", Ru) + Nbsp + "сек";
        if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60).ToString("0", Ru) + Nbsp + "ч";
        return minutes.ToString("0.#", Ru) + Nbsp + "мин";
    }

    /// <summary>Chip label next to a trailing "мин" caption: "5", "30 с", "1 ч".</summary>
    public static string FormatPresetShort(double minutes)
    {
        if (minutes < 1) return Math.Round(minutes * 60).ToString("0", Ru) + Nbsp + "с";
        if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60).ToString("0", Ru) + Nbsp + "ч";
        return minutes.ToString("0.#", Ru);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "сейчас";
        if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + Nbsp + "мин назад";
        return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + Nbsp + "ч назад";
    }

    private static string ChipLabel(MediaSessionInfo s)
    {
        var name = s.SourceDisplayName.Trim();
        if (name.Length == 0) name = s.Title.Trim();
        return name.Length > 18 ? name[..17] + "…" : name;
    }

    private static Brush Res(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var brush = new SolidColorBrush(fallback);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Stop();
        State.PropertyChanged -= OnStatePropertyChanged;
    }
}
