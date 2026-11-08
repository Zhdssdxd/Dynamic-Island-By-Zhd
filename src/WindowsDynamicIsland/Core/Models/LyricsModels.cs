namespace WindowsDynamicIsland.Core.Models;

/// <summary>One timed line of synchronized lyrics (time is relative to the start of the track).</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

public enum LyricsStatus
{
    /// <summary>Nothing to look up (no music, feature disabled).</summary>
    None,
    Loading,
    /// <summary>Synchronized lyrics are available in <see cref="LyricsChanged.Lines"/>.</summary>
    Found,
    /// <summary>The track is known to be instrumental.</summary>
    Instrumental,
    /// <summary>No synchronized lyrics exist for this track.</summary>
    NotFound,
    /// <summary>Network or service error.</summary>
    Error,
}

/// <summary>
/// Published by the lyrics provider for the current music track. <see cref="TrackKey"/> identifies the track
/// (see <c>LyricsProvider.TrackKey</c>) so consumers can ignore results for a track that is no longer current.
/// </summary>
public sealed record LyricsChanged(string TrackKey, LyricsStatus Status, IReadOnlyList<LyricLine> Lines);
