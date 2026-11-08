using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Media;

/// <summary>
/// Fetches synchronized lyrics for the current music track from LRCLIB (https://lrclib.net, free and open, no key).
/// Windows' media sessions (and Spotify's own lyrics) are not exposed to other apps, so this public database is used.
/// Only the track title, artist, album and duration are sent. Results are cached in memory for the session.
/// </summary>
public sealed partial class LyricsProvider : IIslandModule
{
    private const string ApiBase = "https://lrclib.net/api/";
    private const int MaxCacheEntries = 200;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(6);

    private static readonly HttpClient Http = CreateClient();

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly SubscriptionBag _subs = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, LyricsChanged> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    private CancellationTokenSource? _requestCts;
    private string _currentKey = "";
    private MediaSessionInfo? _currentMedia;
    private bool _enabled;
    private bool _disposed;

    public LyricsProvider(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public string Name => "Тексты песен";

    /// <summary>Stable identity of a track for lyrics purposes (artist|title|duration in seconds).</summary>
    public static string TrackKey(MediaSessionInfo? media)
    {
        if (media is null || media.Kind == MediaKind.Video || string.IsNullOrWhiteSpace(media.Title)) return "";
        var seconds = (int)Math.Round(media.Duration.TotalSeconds);
        return $"{media.Artist.Trim().ToLowerInvariant()}|{media.Title.Trim().ToLowerInvariant()}|{seconds}";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _enabled = _settings.Current.Features.LyricsEnabled;
        _subs.Add(_bus.Subscribe<SettingsChanged>(OnSettings));
        _subs.Add(_bus.Subscribe<MediaSessionsChanged>(e => OnMedia(e.Current)));
        if (_bus.GetLast<MediaSessionsChanged>() is { } last) OnMedia(last.Current);
        _bus.Publish(new ModuleStatusChanged(Name, true, _enabled ? "Источник: lrclib.net" : "Выключено в настройках"));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _subs.Dispose();
        CancelRequest();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        CancelRequest();
    }

    // ───────────────────────────── Events ─────────────────────────────

    private void OnSettings(SettingsChanged e)
    {
        var enabled = e.Settings.Features.LyricsEnabled;
        if (enabled == _enabled) return;
        _enabled = enabled;
        _bus.Publish(new ModuleStatusChanged(Name, true, enabled ? "Источник: lrclib.net" : "Выключено в настройках"));

        MediaSessionInfo? media;
        lock (_gate)
        {
            media = _currentMedia;
            _currentKey = ""; // force re-evaluation
        }
        OnMedia(media);
    }

    private void OnMedia(MediaSessionInfo? media)
    {
        if (_disposed) return;
        var key = _enabled ? TrackKey(media) : "";

        CancellationToken token;
        lock (_gate)
        {
            _currentMedia = media;
            if (key == _currentKey) return;
            _currentKey = key;

            _requestCts?.Cancel();
            _requestCts?.Dispose();
            _requestCts = null;

            if (key.Length == 0)
            {
                _bus.Publish(new LyricsChanged("", LyricsStatus.None, Array.Empty<LyricLine>()));
                return;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                _bus.Publish(cached);
                return;
            }

            _requestCts = new CancellationTokenSource();
            token = _requestCts.Token;
        }

        _bus.Publish(new LyricsChanged(key, LyricsStatus.Loading, Array.Empty<LyricLine>()));
        var snapshot = media!;
        _ = Task.Run(() => FetchAsync(key, snapshot, token), token);
    }

    private void CancelRequest()
    {
        lock (_gate)
        {
            _requestCts?.Cancel();
            _requestCts?.Dispose();
            _requestCts = null;
        }
    }

    // ───────────────────────────── Fetch ─────────────────────────────

    private async Task FetchAsync(string key, MediaSessionInfo media, CancellationToken token)
    {
        LyricsChanged result;
        try
        {
            // Track skips arrive in bursts: wait a moment before hitting the network.
            await Task.Delay(Debounce, token).ConfigureAwait(false);
            result = await LookupAsync(key, media, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Info($"Lyrics lookup failed for '{media.Artist} — {media.Title}': {ex.Message}");
            result = new LyricsChanged(key, LyricsStatus.Error, Array.Empty<LyricLine>());
        }

        lock (_gate)
        {
            if (token.IsCancellationRequested || key != _currentKey) return;
            if (result.Status != LyricsStatus.Error) AddToCache(key, result);
        }
        _bus.Publish(result);
    }

    private static async Task<LyricsChanged> LookupAsync(string key, MediaSessionInfo media, CancellationToken token)
    {
        var title = media.Title.Trim();
        var artist = media.Artist.Trim();
        var album = media.AlbumTitle.Trim();
        var duration = media.Duration;

        // 1) Exact match (LRCLIB requires all four fields and matches duration within ~2 s).
        if (duration > TimeSpan.Zero && artist.Length > 0)
        {
            var url = ApiBase + "get?track_name=" + Uri.EscapeDataString(title)
                      + "&artist_name=" + Uri.EscapeDataString(artist)
                      + "&album_name=" + Uri.EscapeDataString(album)
                      + "&duration=" + ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                var fromGet = FromRecord(key, doc.RootElement);
                if (fromGet is not null) return fromGet;
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new HttpRequestException($"LRCLIB get: HTTP {(int)response.StatusCode}");
            }
        }

        // 2) Search with a cleaned-up title and the first artist; pick the best synced match by duration.
        var cleanTitle = CleanTitle(title);
        var firstArtist = FirstArtist(artist);
        var searchUrl = ApiBase + "search?track_name=" + Uri.EscapeDataString(cleanTitle)
                        + (firstArtist.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(firstArtist) : "");
        using (var response = await Http.GetAsync(searchUrl, token).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"LRCLIB search: HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new LyricsChanged(key, LyricsStatus.NotFound, Array.Empty<LyricLine>());

            JsonElement? best = null;
            var bestDelta = double.MaxValue;
            var anyInstrumental = false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (GetBool(item, "instrumental")) anyInstrumental = true;
                if (string.IsNullOrWhiteSpace(GetString(item, "syncedLyrics"))) continue;

                var itemDuration = GetDouble(item, "duration");
                var delta = duration > TimeSpan.Zero && itemDuration > 0
                    ? Math.Abs(itemDuration - duration.TotalSeconds)
                    : 0;
                if (duration > TimeSpan.Zero && itemDuration > 0 && delta > DurationTolerance.TotalSeconds) continue;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = item;
                }
            }

            if (best is { } found && FromRecord(key, found) is { } result) return result;
            return new LyricsChanged(key, anyInstrumental ? LyricsStatus.Instrumental : LyricsStatus.NotFound, Array.Empty<LyricLine>());
        }
    }

    private static LyricsChanged? FromRecord(string key, JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;
        if (GetBool(record, "instrumental"))
            return new LyricsChanged(key, LyricsStatus.Instrumental, Array.Empty<LyricLine>());

        var lines = LrcParser.Parse(GetString(record, "syncedLyrics"));
        return lines.Count > 0 ? new LyricsChanged(key, LyricsStatus.Found, lines) : null;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|with|prod\.?)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeatPart();

    [GeneratedRegex(@"\s+-\s+(.*(remaster|version|edit|mix|live|mono|stereo).*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuffixPart();

    /// <summary>"Song (feat. X) - Remastered 2011" → "Song".</summary>
    internal static string CleanTitle(string title)
    {
        var t = FeatPart().Replace(title, "");
        t = SuffixPart().Replace(t, "");
        return t.Trim();
    }

    /// <summary>"A, B & C" → "A".</summary>
    internal static string FirstArtist(string artist)
    {
        var separators = new[] { ", ", " & ", " feat. ", " ft. ", " x ", "; " };
        var cut = artist.Length;
        foreach (var s in separators)
        {
            var i = artist.IndexOf(s, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i < cut) cut = i;
        }
        return artist[..cut].Trim();
    }

    private void AddToCache(string key, LyricsChanged value)
    {
        if (_cache.ContainsKey(key)) return;
        _cache[key] = value;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > MaxCacheEntries) _cache.Remove(_cacheOrder.Dequeue());
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDynamicIsland/1.0");
        return client;
    }
}
