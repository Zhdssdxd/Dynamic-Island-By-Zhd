using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Media;

/// <summary>
/// Turns GSMTC thumbnails (<see cref="IRandomAccessStreamReference"/>) into small, frozen WPF bitmaps.
/// <list type="bullet">
/// <item>Bytes are read asynchronously with a timeout (a hung source app can never block anything).</item>
/// <item>Decoding happens on one dedicated background thread (one WPF dispatcher instead of one per pool thread),
/// at ~<see cref="DecodePixelSize"/> px with <see cref="BitmapCacheOption.OnLoad"/>, then the bitmap is frozen.</item>
/// <item>A small LRU cache keyed by session + title + artist stores the image together with a hash of its bytes,
/// so re-reading an unchanged thumbnail returns the very same instance (no UI churn) while a changed one replaces it.</item>
/// <item>Failures return null; nothing throws.</item>
/// </list>
/// </summary>
internal sealed class ThumbnailLoader : IDisposable
{
    /// <summary>Target decode width in pixels (album art is shown at most ~96 DIP; 2× for high-DPI).</summary>
    public const int DecodePixelSize = 192;

    private const int MaxCacheEntries = 48;
    private const long MaxThumbnailBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    private readonly object _threadLock = new();
    private readonly BlockingCollection<DecodeRequest> _decodeQueue = new(new ConcurrentQueue<DecodeRequest>());
    private Thread? _decodeThread;
    private volatile bool _disposed;

    /// <summary>Builds the cache key for a track of a session.</summary>
    public static string MakeKey(string sessionId, string title, string artist)
        => string.Concat(sessionId, "\u001F", title, "\u001F", artist);

    /// <summary>Returns a cached image for <paramref name="key"/> without any I/O, or null.</summary>
    public BitmapSource? TryGetCached(string key)
    {
        lock (_cacheLock)
        {
            if (!_index.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>
    /// Reads and decodes the thumbnail. If the bytes equal the cached entry for <paramref name="key"/>, the cached
    /// instance is returned. Returns null when the source provides no readable image. Never throws.
    /// </summary>
    public async Task<BitmapSource?> LoadAsync(string key, IRandomAccessStreamReference reference)
    {
        if (_disposed) return null;
        try
        {
            var bytes = await ReadAllBytesAsync(reference).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            lock (_cacheLock)
            {
                if (_index.TryGetValue(key, out var existing) && existing.Value.Hash == hash)
                {
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                    return existing.Value.Image;
                }
            }

            var image = await DecodeAsync(bytes).ConfigureAwait(false);
            if (image is null) return null;

            lock (_cacheLock)
            {
                if (_disposed) return image;
                if (_index.TryGetValue(key, out var old))
                {
                    _lru.Remove(old);
                    _index.Remove(key);
                }
                _index[key] = _lru.AddFirst(new CacheEntry(key, hash, image));
                while (_index.Count > MaxCacheEntries && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _index.Remove(last.Value.Key);
                }
            }
            return image;
        }
        catch (Exception ex)
        {
            // COMException ("device not ready", RPC disconnected), timeout, corrupt image…
            Log.Debug($"Media: thumbnail load failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops all cached images.</summary>
    public void Clear()
    {
        lock (_cacheLock)
        {
            _index.Clear();
            _lru.Clear();
        }
    }

    private static async Task<byte[]?> ReadAllBytesAsync(IRandomAccessStreamReference reference)
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        using IRandomAccessStreamWithContentType winStream = await reference.OpenReadAsync().AsTask(cts.Token).ConfigureAwait(false);
        if (winStream is null) return null;

        ulong size = 0;
        try { size = winStream.Size; } catch (Exception) { /* unknown size: read until the end */ }
        if (size > (ulong)MaxThumbnailBytes) return null;

        // AsStreamForRead: System.IO.WindowsRuntimeStreamExtensions (CsWinRT projection of this TFM).
        using var stream = winStream.AsStreamForRead();
        using var buffer = new MemoryStream(size > 0 ? (int)size : 64 * 1024);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxThumbnailBytes) return null;
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private Task<BitmapSource?> DecodeAsync(byte[] bytes)
    {
        if (_disposed) return Task.FromResult<BitmapSource?>(null);
        var request = new DecodeRequest(bytes, new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously));
        EnsureDecodeThread();
        try
        {
            _decodeQueue.Add(request);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding was called (disposed).
            return Task.FromResult<BitmapSource?>(null);
        }
        return request.Completion.Task;
    }

    private void EnsureDecodeThread()
    {
        if (Volatile.Read(ref _decodeThread) is not null) return;
        lock (_threadLock)
        {
            if (_decodeThread is not null || _disposed) return;
            var thread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "MediaThumbnailDecoder",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
            Volatile.Write(ref _decodeThread, thread);
        }
    }

    private void DecodeLoop()
    {
        try
        {
            foreach (var request in _decodeQueue.GetConsumingEnumerable())
            {
                BitmapSource? image = null;
                try
                {
                    image = Decode(request.Bytes);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Media: thumbnail decode failed: {ex.GetType().Name}: {ex.Message}");
                }
                request.Completion.TrySetResult(image);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Media: thumbnail decoder thread stopped", ex);
        }
    }

    /// <summary>Decodes to a frozen bitmap ~<see cref="DecodePixelSize"/> px wide (never upscales). Throws on bad data.</summary>
    internal static BitmapSource? Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);

        // Peek at the native size so small images are not upscaled.
        var nativeWidth = 0;
        try
        {
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0) nativeWidth = decoder.Frames[0].PixelWidth;
        }
        catch (Exception)
        {
            // Let BitmapImage try anyway (it reports its own error).
        }
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (nativeWidth <= 0 || nativeWidth > DecodePixelSize) bitmap.DecodePixelWidth = DecodePixelSize;
        bitmap.StreamSource = ms;
        bitmap.EndInit();

        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Stops the decoder thread (pending requests complete with null) and clears the cache. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _decodeQueue.CompleteAdding(); } catch (ObjectDisposedException) { }

        lock (_threadLock)
        {
            if (_decodeThread is null)
            {
                // No consumer will ever run: fail anything that slipped in.
                while (_decodeQueue.TryTake(out var orphan)) orphan.Completion.TrySetResult(null);
            }
        }
        Clear();
        // The BlockingCollection itself is not disposed on purpose: the decoder thread may still be draining it;
        // it holds no OS handles once completed and is collected with this object.
    }

    private sealed record CacheEntry(string Key, string Hash, BitmapSource Image);

    private sealed record DecodeRequest(byte[] Bytes, TaskCompletionSource<BitmapSource?> Completion);
}
