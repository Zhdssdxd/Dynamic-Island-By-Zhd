using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WinPlaybackType = Windows.Media.MediaPlaybackType;

namespace WindowsDynamicIsland.Media;

/// <summary>What kind of application a GSMTC source is, judged by its AppUserModelId.</summary>
public enum MediaAppCategory
{
    /// <summary>Not recognised.</summary>
    Unknown,

    /// <summary>Dedicated music player/streaming app (Spotify, AIMP, …).</summary>
    Music,

    /// <summary>Dedicated video player/streaming app (MPC-HC, Netflix, …).</summary>
    Video,

    /// <summary>Web browser: one GSMTC session for whichever tab last used the MediaSession API.</summary>
    Browser,

    /// <summary>Player that handles both audio and video (VLC, Windows Media Player, Plex, …).</summary>
    GenericPlayer,
}

/// <summary>Friendly name and category of a media source application.</summary>
/// <param name="DisplayName">Name to show in the UI ("Spotify", "Google Chrome", …).</param>
/// <param name="Category">Application category used by <see cref="MediaClassifier.Classify"/>.</param>
public readonly record struct MediaAppDescriptor(string DisplayName, MediaAppCategory Category);

/// <summary>
/// Best-effort classification of GSMTC sessions (music vs video) and friendly source-app names.
/// All members are thread-safe and never throw.
/// </summary>
/// <remarks>
/// Platform notes:
/// <list type="bullet">
/// <item>Chromium- and Gecko-based browsers report <c>MediaPlaybackType.Music</c> for every tab (they always
/// publish through MusicProperties), so for browsers only a reported <c>Video</c> type is trusted; otherwise the
/// metadata/thumbnail shape decides.</item>
/// <item>Firefox registers its AUMID as a 16-hex-digit hash of the install directory
/// (<c>308046B0AF4A39CB</c> for the default location); any such hash is treated as Firefox.</item>
/// </list>
/// </remarks>
public static class MediaClassifier
{
    /// <summary>Thumbnails at least this wide relative to their height are treated as video frames (16:9, 4:3).</summary>
    internal const double WideThumbnailAspect = 1.25;

    /// <summary>Thumbnails at most this wide relative to their height are treated as square album art.</summary>
    internal const double SquareThumbnailAspect = 1.1;

    private const string UnknownAppName = "Неизвестное приложение";

    /// <summary>Package family publisher hash ("_8wekyb3d8bbwe") optionally followed by "!AppId".</summary>
    private static readonly Regex PublisherHash = new("_[a-z0-9]{13}(?=!|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, MediaAppDescriptor> DescriptorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordered: more specific entries first (PWAs and music apps before their host browsers).</summary>
    private static readonly KnownApp[] KnownApps =
    {
        // Progressive web apps / app-specific ids (must precede browsers).
        new(new[] { "cinhimbnkkaeohfgghhklpknlkffjgod", "music.youtube", "youtube music", "youtubemusic" }, "YouTube Music", MediaAppCategory.Music),
        new(new[] { "yandex" }, "Яндекс Музыка", MediaAppCategory.Music, RequiresAlso: new[] { "music", "музыка" }),

        // Music apps.
        new(new[] { "spotify" }, "Spotify", MediaAppCategory.Music),
        new(new[] { "microsoft.zunemusic" }, "Медиаплеер", MediaAppCategory.Music),
        new(new[] { "aimp" }, "AIMP", MediaAppCategory.Music),
        new(new[] { "foobar2000" }, "foobar2000", MediaAppCategory.Music),
        new(new[] { "musicbee" }, "MusicBee", MediaAppCategory.Music),
        new(new[] { "itunes" }, "iTunes", MediaAppCategory.Music),
        new(new[] { "applemusic", "apple music" }, "Apple Music", MediaAppCategory.Music),
        new(new[] { "deezer" }, "Deezer", MediaAppCategory.Music),
        new(new[] { "tidal" }, "TIDAL", MediaAppCategory.Music),
        new(new[] { "soundcloud" }, "SoundCloud", MediaAppCategory.Music),
        new(new[] { "amazonmusic", "amazon music" }, "Amazon Music", MediaAppCategory.Music),
        new(new[] { "winamp" }, "Winamp", MediaAppCategory.Music),
        new(new[] { "qobuz" }, "Qobuz", MediaAppCategory.Music),
        new(new[] { "dopamine" }, "Dopamine", MediaAppCategory.Music),

        // Video apps.
        new(new[] { "microsoft.zunevideo" }, "Кино и ТВ", MediaAppCategory.Video),
        new(new[] { "appletv", "apple tv" }, "Apple TV", MediaAppCategory.Video),
        new(new[] { "mpc-hc" }, "MPC-HC", MediaAppCategory.Video),
        new(new[] { "mpc-be" }, "MPC-BE", MediaAppCategory.Video),
        new(new[] { "potplayer" }, "PotPlayer", MediaAppCategory.Video),
        new(new[] { "kmplayer" }, "KMPlayer", MediaAppCategory.Video),
        new(new[] { "netflix" }, "Netflix", MediaAppCategory.Video),
        new(new[] { "twitch" }, "Twitch", MediaAppCategory.Video),
        new(new[] { "primevideo", "prime video" }, "Prime Video", MediaAppCategory.Video),
        new(new[] { "disney" }, "Disney+", MediaAppCategory.Video),
        new(new[] { "kinopoisk" }, "Кинопоиск", MediaAppCategory.Video),

        // Players that handle both.
        new(new[] { "vlc" }, "VLC", MediaAppCategory.GenericPlayer),
        new(new[] { "microsoft.windowsmediaplayer", "wmplayer" }, "Проигрыватель Windows Media", MediaAppCategory.GenericPlayer),
        new(new[] { "plex" }, "Plex", MediaAppCategory.GenericPlayer),
        new(new[] { "kodi" }, "Kodi", MediaAppCategory.GenericPlayer),

        // Browsers.
        new(new[] { "msedge", "microsoft edge" }, "Microsoft Edge", MediaAppCategory.Browser),
        new(new[] { "chromium" }, "Chromium", MediaAppCategory.Browser),
        new(new[] { "chrome" }, "Google Chrome", MediaAppCategory.Browser),
        new(new[] { "firefox", "308046b0af4a39cb" }, "Firefox", MediaAppCategory.Browser),
        new(new[] { "librewolf" }, "LibreWolf", MediaAppCategory.Browser),
        new(new[] { "waterfox" }, "Waterfox", MediaAppCategory.Browser),
        new(new[] { "opera" }, "Opera", MediaAppCategory.Browser),
        new(new[] { "brave" }, "Brave", MediaAppCategory.Browser),
        new(new[] { "vivaldi" }, "Vivaldi", MediaAppCategory.Browser),
        new(new[] { "yandex" }, "Яндекс Браузер", MediaAppCategory.Browser),
        new(new[] { "thebrowsercompany", "arc.exe" }, "Arc", MediaAppCategory.Browser),
    };

    /// <summary>
    /// Returns the friendly name and category for a GSMTC <c>SourceAppUserModelId</c>. Results are cached per id.
    /// Unknown <c>*.exe</c> ids are resolved through the running process' file description when possible.
    /// </summary>
    public static MediaAppDescriptor Describe(string? appUserModelId)
    {
        var id = (appUserModelId ?? "").Trim();
        if (id.Length == 0) return new MediaAppDescriptor(UnknownAppName, MediaAppCategory.Unknown);
        try
        {
            return DescriptorCache.GetOrAdd(id, DescribeCore);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media: could not describe source '{id}'", ex);
            return new MediaAppDescriptor(DeriveNameFromId(id), MediaAppCategory.Unknown);
        }
    }

    /// <summary>
    /// Decides whether a session is music or video.
    /// Order: the type reported by the session (except a browser's "Music", which browsers report for everything) →
    /// the app category → metadata heuristics (album, artist, thumbnail aspect ratio).
    /// </summary>
    /// <param name="category">Category from <see cref="Describe"/>.</param>
    /// <param name="playbackInfoType">PlaybackInfo.PlaybackType (null when not reported).</param>
    /// <param name="propertiesType">MediaProperties.PlaybackType (null when not reported).</param>
    /// <param name="title">Track/video title.</param>
    /// <param name="artist">Artist (for web videos usually the channel name).</param>
    /// <param name="albumTitle">Album title (set by music services, not by video sites).</param>
    /// <param name="thumbnailAspect">Thumbnail width/height, null when there is no thumbnail (yet).</param>
    public static MediaKind Classify(
        MediaAppCategory category,
        WinPlaybackType? playbackInfoType,
        WinPlaybackType? propertiesType,
        string title,
        string artist,
        string albumTitle,
        double? thumbnailAspect)
    {
        var reported = FromPlaybackType(playbackInfoType) ?? FromPlaybackType(propertiesType);

        switch (category)
        {
            case MediaAppCategory.Browser:
                // Browsers always say "Music"; only an explicit "Video" carries information.
                if (reported == MediaKind.Video) return MediaKind.Video;
                return ClassifyByMetadata(title, artist, albumTitle, thumbnailAspect, fallbackWhenTitled: MediaKind.Video);

            case MediaAppCategory.Music:
                return reported ?? MediaKind.Music;

            case MediaAppCategory.Video:
                return reported ?? MediaKind.Video;

            case MediaAppCategory.GenericPlayer:
                return reported ?? ClassifyByMetadata(title, artist, albumTitle, thumbnailAspect, fallbackWhenTitled: MediaKind.Video);

            default:
                return reported ?? ClassifyByMetadata(title, artist, albumTitle, thumbnailAspect, fallbackWhenTitled: MediaKind.Unknown);
        }
    }

    private static MediaKind ClassifyByMetadata(string title, string artist, string albumTitle, double? thumbnailAspect, MediaKind fallbackWhenTitled)
    {
        // Music services (YouTube Music, Yandex Music, Spotify Web, SoundCloud…) fill the album; video sites do not.
        if (!string.IsNullOrWhiteSpace(albumTitle)) return MediaKind.Music;

        // Video sites publish a frame-shaped thumbnail (16:9 / 4:3); music services square cover art.
        if (thumbnailAspect is { } aspect)
        {
            if (aspect >= WideThumbnailAspect) return MediaKind.Video;
            if (aspect <= SquareThumbnailAspect && !string.IsNullOrWhiteSpace(artist)) return MediaKind.Music;
        }

        // YouTube puts the channel into "artist", so artist alone is weak evidence — but it is the best we have
        // when there is no thumbnail.
        if (thumbnailAspect is null && !string.IsNullOrWhiteSpace(artist)) return MediaKind.Music;

        return string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist) ? MediaKind.Unknown : fallbackWhenTitled;
    }

    private static MediaKind? FromPlaybackType(WinPlaybackType? type) => type switch
    {
        WinPlaybackType.Music => MediaKind.Music,
        WinPlaybackType.Video => MediaKind.Video,
        // Picture slideshows are visual content: never show a music visualizer for them.
        WinPlaybackType.Image => MediaKind.Video,
        _ => null,
    };

    private static MediaAppDescriptor DescribeCore(string id)
    {
        var key = PublisherHash.Replace(id, "").ToLowerInvariant();
        foreach (var app in KnownApps)
        {
            if (app.Matches(key)) return new MediaAppDescriptor(app.DisplayName, app.Category);
        }

        if (id.Length == 16 && id.All(char.IsAsciiHexDigit))
            return new MediaAppDescriptor("Firefox", MediaAppCategory.Browser);

        return new MediaAppDescriptor(ResolveUnknownName(id), MediaAppCategory.Unknown);
    }

    private static string ResolveUnknownName(string id)
    {
        var fileName = LastPathSegment(id);
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var description = TryGetProcessDescription(fileName);
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        return DeriveNameFromId(id);
    }

    /// <summary>
    /// Derives a readable name from an AUMID: "{GUID}\VideoLAN\VLC\vlc.exe" → "Vlc",
    /// "Publisher.SomeApp_8wekyb3d8bbwe!App" → "Some App", "Spotify.exe" → "Spotify".
    /// </summary>
    internal static string DeriveNameFromId(string id)
    {
        var name = LastPathSegment(id.Trim());

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        else
        {
            var bang = name.IndexOf('!');
            if (bang > 0) name = name[..bang];
            name = PublisherHash.Replace(name, "");
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        }

        return Prettify(name);
    }

    private static string LastPathSegment(string s)
    {
        var sep = s.LastIndexOfAny(new[] { '\\', '/' });
        return sep >= 0 && sep < s.Length - 1 ? s[(sep + 1)..] : s;
    }

    /// <summary>"zuneMusic" → "Zune Music", "slack" → "Slack".</summary>
    private static string Prettify(string raw)
    {
        var s = raw.Replace('_', ' ').Trim();
        if (s.Length == 0) return UnknownAppName;

        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    /// <summary>Reads FileDescription of a running process with the given exe name. Never throws; null on failure.</summary>
    private static string? TryGetProcessDescription(string exeFileName)
    {
        var processName = Path.GetFileNameWithoutExtension(exeFileName);
        if (string.IsNullOrWhiteSpace(processName)) return null;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            Log.Debug($"Media: process lookup for '{processName}' failed: {ex.Message}");
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
                }
                catch (Exception)
                {
                    // Access denied (elevated process), 32/64-bit mismatch or the process exited — try the next one.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return null;
    }

    /// <summary>Known application: matches when the normalized id contains any pattern (and any of <paramref name="RequiresAlso"/>, if given).</summary>
    private sealed record KnownApp(string[] Patterns, string DisplayName, MediaAppCategory Category, string[]? RequiresAlso = null)
    {
        public bool Matches(string normalizedId)
        {
            if (!Patterns.Any(p => normalizedId.Contains(p, StringComparison.Ordinal))) return false;
            return RequiresAlso is null || RequiresAlso.Any(p => normalizedId.Contains(p, StringComparison.Ordinal));
        }
    }
}
