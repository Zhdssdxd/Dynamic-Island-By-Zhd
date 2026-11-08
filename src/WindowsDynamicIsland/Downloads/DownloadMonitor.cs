using System.Text.RegularExpressions;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.SystemIntegration;
using WindowsDynamicIsland.SystemIntegration.Native;

namespace WindowsDynamicIsland.Downloads;

/// <summary>
/// Honest download tracking (<c>Features.DownloadsEnabled</c>) based on browser temp files in the Downloads folder
/// (<c>Features.DownloadsFolder</c>, else the Downloads known folder, else %USERPROFILE%\Downloads).
///
/// A <see cref="FileSystemWatcher"/> (file names only, 64 KB buffer, recreated on overflow/error) detects:
///  * Chromium browsers (Chrome, Edge, Yandex, Opera, Brave): <c>*.crdownload</c> — "Unconfirmed 12345.crdownload"
///    is shown as «Загрузка…» until it is renamed to "name.ext.crdownload";
///  * Firefox: <c>*.part</c> (next to a zero-byte placeholder with the final name);
///  * generic <c>*.partial</c> and <c>*.download</c>;
///  * <c>*.tmp</c> only once it has grown past 256 KB (ignores ordinary temp-file churn).
/// While at least one download is active a single 500 ms timer samples file sizes →
/// <see cref="DownloadProgressChanged.BytesReceived"/> and an EMA-smoothed (τ ≈ 2 s) speed.
/// <see cref="DownloadProgressChanged.TotalBytes"/> is always null: temp files do not reveal the final size.
///
/// Completion: the temp file is renamed to a non-temp name, or disappears while the final file exists and is
/// non-empty → Completed + a "Загрузка завершена" notification. Disappears without a final file → Failed
/// (cancelled), no notification. No growth for 2 minutes → Failed (stale/paused) and the file is remembered so a
/// later rename still reports completion. Temp files that already exist at start are only shown once they grow.
/// </summary>
public sealed class DownloadMonitor : IIslandModule
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan VanishGrace = TimeSpan.FromMilliseconds(600);
    private const double SpeedTimeConstantSeconds = 2.0;
    private const long TmpMinBytes = 256 * 1024;
    private const int MaxDormant = 64;
    private const string PendingName = "Загрузка…";

    /// <summary>Chromium's not-yet-named download, e.g. "Unconfirmed 123456.crdownload" (prefix is localized).</summary>
    private static readonly Regex UnconfirmedChromium =
        new(@"^[^.]+\s\d+\.crdownload$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private enum TempKind
    {
        None,
        Chromium,
        Firefox,
        Partial,
        SafariLike,
        Tmp,
    }

    private sealed class TrackedDownload
    {
        public TrackedDownload(string tempPath, TempKind kind, DateTime nowUtc)
        {
            Id = "dl:" + Guid.NewGuid().ToString("N");
            TempPath = tempPath;
            Kind = kind;
            LastChangeUtc = nowUtc;
            LastSampleUtc = nowUtc;
        }

        public string Id { get; }
        public string TempPath;
        public TempKind Kind;
        public long Bytes;
        public DateTime LastChangeUtc;
        public DateTime LastSampleUtc;
        public double? Speed;
        public DateTime? VanishedAtUtc;
        public long PublishedBytes = -1;
        public double? PublishedSpeed;
    }

    private sealed class Candidate
    {
        public Candidate(string path, TempKind kind, long baseline, long minBytes, DateTime nowUtc)
        {
            Path = path;
            Kind = kind;
            BaselineSize = baseline;
            MinBytes = minBytes;
            LastSize = baseline;
            LastChangeUtc = nowUtc;
        }

        public string Path { get; }
        public TempKind Kind { get; }
        public long BaselineSize { get; }
        public long MinBytes { get; }
        public long LastSize;
        public DateTime LastChangeUtc;
    }

    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedDownload> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedDownload> _dormant = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _sampler;
    private bool _samplerArmed;
    private string? _folder;
    private bool _running;

    public DownloadMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Features.DownloadsEnabled,
            s => (s.Features.DownloadsFolder ?? "").Trim(),
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Загрузки";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    // ───────────────────────────── Activation ─────────────────────────────

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        var configured = (settings.Features.DownloadsFolder ?? "").Trim();
        string folder;
        if (configured.Length > 0)
        {
            if (!Directory.Exists(configured))
                return Task.FromResult(FeatureStatus.Unavailable($"Папка «{configured}» не найдена — укажите существующую папку загрузок в настройках"));
            folder = configured;
        }
        else
        {
            folder = ResolveDefaultFolder();
            if (!Directory.Exists(folder))
                return Task.FromResult(FeatureStatus.Unavailable($"Папка загрузок «{folder}» не найдена — укажите папку в настройках"));
        }

        lock (_gate)
        {
            _running = true;
            _folder = folder;
            _sampler = new System.Threading.Timer(_ => OnSample(), null, Timeout.Infinite, Timeout.Infinite);
            _samplerArmed = false;
        }

        var error = CreateWatcher();
        if (error is not null) return Task.FromResult(FeatureStatus.Unavailable(error));

        ScanExisting();
        return Task.FromResult(FeatureStatus.Ok($"Отслеживается папка {folder}"));
    }

    private static string ResolveDefaultFolder()
    {
        var known = NativeMethods.TryGetKnownFolderPath(NativeMethods.FOLDERID_Downloads);
        if (!string.IsNullOrWhiteSpace(known)) return known;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Creates (or recreates) the watcher. Returns null or a Russian error.</summary>
    private string? CreateWatcher()
    {
        string? folder;
        lock (_gate)
        {
            if (!_running) return null;
            folder = _folder;
        }
        if (folder is null) return "Папка загрузок не задана";

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnCreated;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Downloads: cannot watch {folder}", ex);
            return $"Не удалось отслеживать папку «{folder}»: {ex.Message}";
        }

        FileSystemWatcher? stale = null;
        var adopted = false;
        lock (_gate)
        {
            if (_running)
            {
                stale = _watcher;
                _watcher = watcher;
                adopted = true;
            }
        }
        DisposeWatcher(stale);
        if (!adopted) DisposeWatcher(watcher); // deactivated meanwhile
        return null;
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: watcher dispose failed", ex);
        }
    }

    /// <summary>Temp files present at start (or after a watcher restart) are shown only once they grow.</summary>
    private void ScanExisting()
    {
        string? folder;
        lock (_gate) folder = _folder;
        if (folder is null) return;

        try
        {
            var now = DateTime.UtcNow;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var kind = Classify(path);
                if (kind == TempKind.None) continue;
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                lock (_gate)
                {
                    if (!_running) return;
                    if (_active.ContainsKey(path) || _candidates.ContainsKey(path) || _dormant.ContainsKey(path)) continue;
                    _candidates[path] = new Candidate(path, kind, size, kind == TempKind.Tmp ? TmpMinBytes : 0, now);
                    EnsureSamplerLocked();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: initial scan failed", ex);
        }
    }

    // ───────────────────────────── Watcher events (thread pool) ─────────────────────────────

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        var pending = new List<DownloadProgressChanged>(1);
        try
        {
            var kind = Classify(e.FullPath);
            if (kind == TempKind.None) return;
            lock (_gate)
            {
                if (!_running || _active.ContainsKey(e.FullPath)) return;
                _dormant.Remove(e.FullPath);
                StartTrackingLocked(e.FullPath, kind, pending, existingSize: 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: Created handler failed", ex);
        }
        finally
        {
            PublishAll(pending, null);
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (!_running) return;
                if (_active.TryGetValue(e.FullPath, out var download))
                {
                    // Resolved by the sampler after a short grace period (final file may appear right after).
                    download.VanishedAtUtc ??= DateTime.UtcNow;
                    EnsureSamplerLocked();
                }
                _candidates.Remove(e.FullPath);
                _dormant.Remove(e.FullPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: Deleted handler failed", ex);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var progress = new List<DownloadProgressChanged>(1);
        var notifications = new List<IslandNotification>(1);
        try
        {
            var oldPath = e.OldFullPath;
            var newPath = e.FullPath;
            var newKind = Classify(newPath);
            lock (_gate)
            {
                if (!_running) return;

                if (_active.Remove(oldPath, out var download) || _dormant.Remove(oldPath, out download))
                {
                    if (newKind != TempKind.None)
                    {
                        // Still a temp file (Chromium "Unconfirmed …" → "name.ext.crdownload"): follow it.
                        download.TempPath = newPath;
                        download.Kind = newKind;
                        download.VanishedAtUtc = null;
                        download.LastChangeUtc = DateTime.UtcNow;
                        _active[newPath] = download;
                        progress.Add(ToEvent(download, DownloadState.InProgress));
                        download.PublishedBytes = download.Bytes;
                        download.PublishedSpeed = download.Speed;
                        EnsureSamplerLocked();
                    }
                    else
                    {
                        CompleteLocked(download, newPath, progress, notifications);
                    }
                }
                else if (_candidates.Remove(oldPath, out var candidate))
                {
                    if (newKind != TempKind.None)
                        _candidates[newPath] = new Candidate(newPath, newKind, candidate.BaselineSize, candidate.MinBytes, DateTime.UtcNow);
                    // A never-promoted candidate renamed to a final name is not announced (we never saw it download).
                }
                else if (newKind != TempKind.None && !_active.ContainsKey(newPath))
                {
                    long size = 0;
                    try { size = new FileInfo(newPath).Length; } catch { /* vanished */ }
                    StartTrackingLocked(newPath, newKind, progress, size);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: Renamed handler failed", ex);
        }
        finally
        {
            PublishAll(progress, notifications);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Log.Warn("Downloads: watcher error, recreating", e.GetException());
        // Never dispose a watcher inside its own callback.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var error = CreateWatcher();
                if (error is not null)
                {
                    _lifecycle.PublishStatus(false, error);
                    return;
                }
                ScanExisting();
            }
            catch (Exception ex)
            {
                Log.Warn("Downloads: watcher recreation failed", ex);
            }
        });
    }

    // ───────────────────────────── Tracking (caller holds _gate) ─────────────────────────────

    private void StartTrackingLocked(string path, TempKind kind, List<DownloadProgressChanged> pending, long existingSize)
    {
        if (kind == TempKind.Tmp)
        {
            // Plain .tmp files are only interesting once they are clearly a download.
            _candidates[path] = new Candidate(path, kind, existingSize, TmpMinBytes, DateTime.UtcNow);
            EnsureSamplerLocked();
            return;
        }

        var download = new TrackedDownload(path, kind, DateTime.UtcNow) { Bytes = existingSize };
        _active[path] = download;
        pending.Add(ToEvent(download, DownloadState.InProgress));
        download.PublishedBytes = download.Bytes;
        EnsureSamplerLocked();
    }

    private void CompleteLocked(TrackedDownload download, string finalPath, List<DownloadProgressChanged> progress, List<IslandNotification> notifications)
    {
        long size = download.Bytes;
        try
        {
            var info = new FileInfo(finalPath);
            if (info.Exists) size = Math.Max(size, info.Length);
        }
        catch
        {
            // keep the last sampled size
        }

        var fileName = Path.GetFileName(finalPath);
        progress.Add(new DownloadProgressChanged
        {
            Id = download.Id,
            FileName = fileName,
            State = DownloadState.Completed,
            BytesReceived = size,
            TotalBytes = size,
            BytesPerSecond = null,
            Source = SourceFor(download.Kind),
            FullPath = finalPath,
        });
        notifications.Add(new IslandNotification
        {
            Id = "download-complete:" + download.Id,
            AppName = "Загрузки",
            Glyph = Glyphs.Download,
            Title = "Загрузка завершена",
            Body = fileName,
            Priority = NotificationPriority.Normal,
            Source = NotificationSource.App,
        });
    }

    private void EnsureSamplerLocked()
    {
        if (_samplerArmed || _sampler is null || !_running) return;
        _samplerArmed = true;
        try { _sampler.Change(SampleInterval, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { _samplerArmed = false; }
    }

    // ───────────────────────────── Sampler (one timer for all downloads) ─────────────────────────────

    private void OnSample()
    {
        var progress = new List<DownloadProgressChanged>();
        var notifications = new List<IslandNotification>();
        try
        {
            lock (_gate)
            {
                _samplerArmed = false;
                if (!_running) return;
                var now = DateTime.UtcNow;

                foreach (var download in _active.Values.ToList())
                    SampleLocked(download, now, progress, notifications);

                foreach (var candidate in _candidates.Values.ToList())
                    SampleCandidateLocked(candidate, now, progress);

                if (_active.Count > 0 || _candidates.Count > 0) EnsureSamplerLocked();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Downloads: sampling failed", ex);
            lock (_gate)
            {
                if (_active.Count > 0 || _candidates.Count > 0) EnsureSamplerLocked();
            }
        }
        finally
        {
            PublishAll(progress, notifications);
        }
    }

    private void SampleLocked(TrackedDownload download, DateTime now, List<DownloadProgressChanged> progress, List<IslandNotification> notifications)
    {
        if (download.VanishedAtUtc is { } vanished)
        {
            if (now - vanished < VanishGrace) return;
            _active.Remove(download.TempPath);

            if (download.Kind != TempKind.Tmp)
            {
                var finalPath = StripTempExtension(download.TempPath);
                var finalInfo = new FileInfo(finalPath);
                // Browsers pick a unique final name, so a non-empty file with exactly that name means success
                // (Firefox leaves only its zero-byte placeholder behind when a download is cancelled).
                if (finalInfo.Exists && finalInfo.Length > 0)
                {
                    CompleteLocked(download, finalPath, progress, notifications);
                    return;
                }
            }
            progress.Add(ToEvent(download, DownloadState.Failed));
            return;
        }

        var info = new FileInfo(download.TempPath);
        if (!info.Exists)
        {
            download.VanishedAtUtc = now; // missed Deleted event (buffer overflow)
            return;
        }

        var size = info.Length;
        var dt = Math.Max(0.05, (now - download.LastSampleUtc).TotalSeconds);
        download.LastSampleUtc = now;
        var alpha = 1 - Math.Exp(-dt / SpeedTimeConstantSeconds);

        if (size != download.Bytes)
        {
            var instantaneous = Math.Max(0, (size - download.Bytes) / dt);
            download.Speed = download.Speed is { } s ? s + alpha * (instantaneous - s) : instantaneous;
            download.Bytes = size;
            download.LastChangeUtc = now;
        }
        else if (download.Speed is { } s)
        {
            var decayed = s * (1 - alpha);
            download.Speed = decayed < 1024 ? 0 : decayed;
        }

        if (now - download.LastChangeUtc > StaleAfter)
        {
            // Paused or abandoned: stop showing it, but remember the file for a later completion.
            _active.Remove(download.TempPath);
            progress.Add(ToEvent(download, DownloadState.Failed));
            RememberDormantLocked(download);
            return;
        }

        var speedChanged = download.PublishedSpeed is not { } published
            ? download.Speed is not null
            : download.Speed is not { } current || Math.Abs(current - published) > Math.Max(1024, published * 0.1);
        if (download.Bytes != download.PublishedBytes || speedChanged)
        {
            progress.Add(ToEvent(download, DownloadState.InProgress));
            download.PublishedBytes = download.Bytes;
            download.PublishedSpeed = download.Speed;
        }
    }

    private void SampleCandidateLocked(Candidate candidate, DateTime now, List<DownloadProgressChanged> progress)
    {
        var info = new FileInfo(candidate.Path);
        if (!info.Exists)
        {
            _candidates.Remove(candidate.Path);
            return;
        }

        var size = info.Length;
        if (size != candidate.LastSize)
        {
            candidate.LastSize = size;
            candidate.LastChangeUtc = now;
        }

        if (size > candidate.BaselineSize && size >= candidate.MinBytes)
        {
            _candidates.Remove(candidate.Path);
            var download = new TrackedDownload(candidate.Path, candidate.Kind, now) { Bytes = size };
            _active[candidate.Path] = download;
            progress.Add(ToEvent(download, DownloadState.InProgress));
            download.PublishedBytes = size;
            return;
        }

        if (now - candidate.LastChangeUtc > StaleAfter)
        {
            _candidates.Remove(candidate.Path);
            if (candidate.Kind != TempKind.Tmp)
                RememberDormantLocked(new TrackedDownload(candidate.Path, candidate.Kind, now) { Bytes = size });
        }
    }

    private void RememberDormantLocked(TrackedDownload download)
    {
        if (_dormant.Count >= MaxDormant)
        {
            var oldest = _dormant.Values.OrderBy(d => d.LastChangeUtc).First();
            _dormant.Remove(oldest.TempPath);
        }
        _dormant[download.TempPath] = download;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private void PublishAll(List<DownloadProgressChanged>? progress, List<IslandNotification>? notifications)
    {
        if (progress is not null)
            foreach (var p in progress) _bus.Publish(p);
        if (notifications is not null)
            foreach (var n in notifications) _bus.Publish(new NotificationReceived(n));
    }

    private static DownloadProgressChanged ToEvent(TrackedDownload download, DownloadState state) => new()
    {
        Id = download.Id,
        FileName = DisplayNameFor(download.TempPath, download.Kind) ?? PendingName,
        State = state,
        BytesReceived = download.Bytes,
        TotalBytes = null,
        BytesPerSecond = state == DownloadState.InProgress ? download.Speed : null,
        Source = SourceFor(download.Kind),
        FullPath = download.TempPath,
    };

    private static TempKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return TempKind.None;
        if (extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)) return TempKind.Chromium;
        if (extension.Equals(".part", StringComparison.OrdinalIgnoreCase)) return TempKind.Firefox;
        if (extension.Equals(".partial", StringComparison.OrdinalIgnoreCase)) return TempKind.Partial;
        if (extension.Equals(".download", StringComparison.OrdinalIgnoreCase)) return TempKind.SafariLike;
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return TempKind.Tmp;
        return TempKind.None;
    }

    private static string StripTempExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? path : path.Substring(0, path.Length - extension.Length);
    }

    /// <summary>Final file name for display, or null while unknown (Chromium "Unconfirmed …", *.tmp).</summary>
    private static string? DisplayNameFor(string tempPath, TempKind kind)
    {
        if (kind == TempKind.Tmp) return null;
        var fileName = Path.GetFileName(tempPath);
        if (kind == TempKind.Chromium && UnconfirmedChromium.IsMatch(fileName)) return null;
        var finalName = Path.GetFileName(StripTempExtension(tempPath));
        return string.IsNullOrWhiteSpace(finalName) ? null : finalName;
    }

    private static string SourceFor(TempKind kind) => kind switch
    {
        TempKind.Chromium => "Браузер (Chromium)",
        TempKind.Firefox => "Firefox",
        _ => "Загрузки",
    };

    // ───────────────────────────── Deactivation ─────────────────────────────

    private void Deactivate()
    {
        FileSystemWatcher? watcher;
        System.Threading.Timer? sampler;
        var progress = new List<DownloadProgressChanged>();
        lock (_gate)
        {
            _running = false;
            watcher = _watcher;
            sampler = _sampler;
            _watcher = null;
            _sampler = null;
            _samplerArmed = false;
            // Do not leave a download "in progress" in the island once we stop watching it.
            foreach (var download in _active.Values) progress.Add(ToEvent(download, DownloadState.Failed));
            _active.Clear();
            _candidates.Clear();
            _dormant.Clear();
            _folder = null;
        }

        DisposeWatcher(watcher);
        sampler?.Dispose();
        PublishAll(progress, null);
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
