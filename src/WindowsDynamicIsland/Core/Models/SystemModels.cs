using System.Windows.Media;

namespace WindowsDynamicIsland.Core.Models;

// ───────────────────────────── Notifications ─────────────────────────────

public enum NotificationPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

public enum NotificationSource
{
    /// <summary>Windows toast notification read via UserNotificationListener.</summary>
    System,
    /// <summary>Raised by the app itself (timer finished, download done…).</summary>
    App,
}

public sealed record IslandNotification
{
    public required string Id { get; init; }
    public required string AppName { get; init; }
    /// <summary>Frozen app logo, null if unavailable.</summary>
    public ImageSource? AppIcon { get; init; }
    /// <summary>Fallback glyph (see <see cref="Glyphs"/>) when there is no icon.</summary>
    public string Glyph { get; init; } = Glyphs.Notification;
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
    public NotificationSource Source { get; init; } = NotificationSource.System;
    /// <summary>Null = use the user's configured duration.</summary>
    public TimeSpan? DisplayDuration { get; init; }
}

public sealed record NotificationReceived(IslandNotification Notification);

// ───────────────────────────── System indicators ─────────────────────────────

public enum IndicatorKind
{
    Volume,
    Brightness,
    Battery,
    Charger,
    UsbDevice,
    Bluetooth,
    Microphone,
    /// <summary>Something was copied (clipboard history).</summary>
    Clipboard,
}

/// <summary>
/// Short-lived system HUD event (volume changed, USB plugged, …).
/// Successive events of the same kind replace each other in place (e.g. dragging the volume slider).
/// </summary>
public sealed record SystemIndicatorEvent
{
    public required IndicatorKind Kind { get; init; }
    /// <summary>Glyph from <see cref="Glyphs"/>.</summary>
    public required string Glyph { get; init; }
    /// <summary>Main label: "Громкость", device name, "Зарядка подключена"…</summary>
    public required string Title { get; init; }
    /// <summary>Optional secondary text: "45%", "Подключено"…</summary>
    public string? Subtitle { get; init; }
    /// <summary>0..1 level for a level bar; null when not applicable.</summary>
    public double? Level { get; init; }
    /// <summary>Kind-specific flag: muted (volume/mic), connected (usb/bt), charging (battery/charger).</summary>
    public bool? Flag { get; init; }
    /// <summary>Accent hint for the UI (e.g. low battery → Warning).</summary>
    public IndicatorTone Tone { get; init; } = IndicatorTone.Neutral;
}

public enum IndicatorTone
{
    Neutral,
    Positive,
    Warning,
    Critical,
}

/// <summary>True while a full-screen app / game / presentation is in the foreground on the island's monitor.</summary>
public sealed record FullscreenStateChanged(bool IsFullscreen, string? MonitorDeviceName);

// ───────────────────────────── Downloads ─────────────────────────────

public enum DownloadState
{
    InProgress,
    Completed,
    Failed,
}

/// <summary>
/// Download tracking. <see cref="TotalBytes"/> is null when the source does not expose it
/// (e.g. browser temp files) — UI then shows an indeterminate bar with size + speed only.
/// </summary>
public sealed record DownloadProgressChanged
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required DownloadState State { get; init; }
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double? BytesPerSecond { get; init; }
    /// <summary>Where the info comes from ("Chrome", "Edge", "Firefox", "Downloads").</summary>
    public string? Source { get; init; }
    public string? FullPath { get; init; }

    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

// ───────────────────────────── Timer / Stopwatch ─────────────────────────────

public enum TimerKind
{
    Countdown,
    Stopwatch,
}

public enum TimerRunState
{
    Idle,
    Running,
    Paused,
    Finished,
}

/// <summary>
/// Published by TimerManager on every state change and once per second while running.
/// UI may extrapolate between ticks using <see cref="AnchorUtc"/> and <see cref="ElapsedAtAnchor"/>.
/// </summary>
public sealed record TimerStateChanged
{
    public required TimerKind Kind { get; init; }
    public required TimerRunState State { get; init; }
    /// <summary>Elapsed time at <see cref="AnchorUtc"/>.</summary>
    public TimeSpan ElapsedAtAnchor { get; init; }
    public DateTimeOffset AnchorUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Countdown total; null for stopwatch.</summary>
    public TimeSpan? Duration { get; init; }

    public TimeSpan GetElapsed(DateTimeOffset nowUtc)
    {
        var e = ElapsedAtAnchor;
        if (State == TimerRunState.Running) e += nowUtc - AnchorUtc;
        if (e < TimeSpan.Zero) e = TimeSpan.Zero;
        if (Duration is { } d && e > d) e = d;
        return e;
    }

    public TimeSpan? GetRemaining(DateTimeOffset nowUtc)
        => Duration is { } d ? d - GetElapsed(nowUtc) : null;

    public bool IsActive => State is TimerRunState.Running or TimerRunState.Paused;
}

public enum TimerCommandKind
{
    StartCountdown,
    StartStopwatch,
    Pause,
    Resume,
    TogglePause,
    Stop,
    /// <summary>Adds <see cref="TimerCommand.Duration"/> to a running countdown (e.g. "+1 min").</summary>
    Extend,
}

public sealed record TimerCommand(TimerCommandKind Kind, TimeSpan? Duration = null);

// ───────────────────────────── App-level ─────────────────────────────

/// <summary>Published by modules to report health (shown in Settings → Diagnostics).</summary>
public sealed record ModuleStatusChanged(string ModuleName, bool IsWorking, string? Message);

/// <summary>Requests the app shell to open the settings window (from tray, hotkey, context menu).</summary>
public sealed record OpenSettingsRequested();

/// <summary>Requests app shutdown.</summary>
public sealed record ExitRequested();

public enum IslandUserAction
{
    ToggleExpanded,
    Expand,
    Collapse,
    ToggleVisibility,
    DismissTransient,
}

/// <summary>User intent coming from hotkeys / tray / UI, handled by IslandController.</summary>
public sealed record IslandActionRequested(IslandUserAction Action);
