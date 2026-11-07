using System.Windows.Media;

namespace WindowsDynamicIsland.Core.Models;

public enum MediaKind
{
    Unknown,
    Music,
    Video,
}

public enum MediaPlaybackState
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Immutable snapshot of one GSMTC media session.
/// Only data actually reported by Windows/the source app is filled in — absent data stays empty/null.
/// </summary>
public sealed record MediaSessionInfo
{
    /// <summary>Stable id for the session during its lifetime (SourceAppUserModelId-based).</summary>
    public required string SessionId { get; init; }

    /// <summary>AppUserModelId reported by GSMTC (e.g. "Spotify.exe", "Chrome", "MSEdge").</summary>
    public required string SourceAppId { get; init; }

    /// <summary>Friendly name of the source app/browser ("Spotify", "Google Chrome", …).</summary>
    public required string SourceDisplayName { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";

    public MediaKind Kind { get; init; } = MediaKind.Unknown;
    public MediaPlaybackState State { get; init; } = MediaPlaybackState.Closed;

    /// <summary>Album art / video thumbnail. MUST be frozen (cross-thread). Null when the source provides none.</summary>
    public ImageSource? Thumbnail { get; init; }

    /// <summary>Position reported at <see cref="PositionUpdatedAt"/>. Zero when the source has no timeline.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>Total duration. <see cref="TimeSpan.Zero"/> when unknown — UI must then hide the progress bar.</summary>
    public TimeSpan Duration { get; init; }

    public DateTimeOffset PositionUpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Playback rate reported by the session (1.0 normal).</summary>
    public double PlaybackRate { get; init; } = 1.0;

    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }

    public bool IsPlaying => State == MediaPlaybackState.Playing;
    public bool HasTimeline => Duration > TimeSpan.Zero;

    /// <summary>Extrapolated current position (for smooth progress bars between GSMTC updates).</summary>
    public TimeSpan GetEstimatedPosition(DateTimeOffset now)
    {
        if (!HasTimeline) return TimeSpan.Zero;
        var pos = Position;
        if (IsPlaying)
        {
            var delta = now - PositionUpdatedAt;
            if (delta > TimeSpan.Zero) pos += TimeSpan.FromTicks((long)(delta.Ticks * Math.Max(0.0, PlaybackRate)));
        }
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (pos > Duration) pos = Duration;
        return pos;
    }
}

/// <summary>
/// Published by the media module whenever the set of sessions or the active session's data changes.
/// <see cref="Current"/> is the session the island should display (null = nothing to show).
/// </summary>
public sealed record MediaSessionsChanged(MediaSessionInfo? Current, IReadOnlyList<MediaSessionInfo> All);

public enum MediaCommandKind
{
    TogglePlayPause,
    Play,
    Pause,
    Next,
    Previous,
    /// <summary>Seek the current session to <see cref="MediaCommand.Position"/>.</summary>
    Seek,
    /// <summary>Make <see cref="MediaCommand.SessionId"/> the displayed session (manual choice).</summary>
    SelectSession,
}

/// <summary>Published by UI / hotkeys; handled by the media module.</summary>
public sealed record MediaCommand(MediaCommandKind Kind, string? SessionId = null, TimeSpan? Position = null);
