using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Per-key debouncer for on/off state changes (device connected/disconnected) that suppresses flapping.
/// Every <see cref="Push"/> reports a change, so the state before the first push of a burst is assumed to be
/// the opposite of the pushed one.
///
/// * Trailing mode: the state is delivered once it has been stable for the window; a burst that ends in the
///   original state (plug + unplug within the window) delivers nothing.
/// * Leading mode: the first change is delivered at once; further changes within the (sliding) window are
///   coalesced and the final state is delivered at the end of the window only if it differs from the last delivered one.
///
/// The callback runs outside the lock on the pushing thread or a thread-pool timer thread.
/// </summary>
internal sealed class KeyedStateDebouncer<TKey> : IDisposable where TKey : notnull
{
    private sealed class Entry
    {
        public bool Delivered;
        public bool HasPending;
        public bool PendingState;
        public string PendingPayload = "";
        public System.Threading.Timer? Timer;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly TimeSpan _window;
    private readonly bool _leadingEdge;
    private readonly Action<TKey, bool, string> _deliver;
    private bool _disposed;

    /// <param name="window">Quiet period per key.</param>
    /// <param name="leadingEdge">Deliver the first change immediately.</param>
    /// <param name="deliver">Callback (key, state, payload).</param>
    /// <param name="comparer">Key comparer.</param>
    public KeyedStateDebouncer(TimeSpan window, bool leadingEdge, Action<TKey, bool, string> deliver, IEqualityComparer<TKey>? comparer = null)
    {
        _window = window;
        _leadingEdge = leadingEdge;
        _deliver = deliver;
        _entries = new Dictionary<TKey, Entry>(comparer);
    }

    /// <summary>Reports that <paramref name="key"/> changed to <paramref name="state"/>.</summary>
    public void Push(TKey key, bool state, string payload)
    {
        var deliverNow = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Delivered = !state };
                _entries[key] = entry;
                if (_leadingEdge)
                {
                    entry.Delivered = state;
                    deliverNow = true;
                }
                else
                {
                    entry.HasPending = true;
                    entry.PendingState = state;
                    entry.PendingPayload = payload;
                }
                entry.Timer = new System.Threading.Timer(OnTimer, key, _window, Timeout.InfiniteTimeSpan);
            }
            else
            {
                entry.HasPending = true;
                entry.PendingState = state;
                entry.PendingPayload = payload;
                try { entry.Timer?.Change(_window, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }

        if (deliverNow) Deliver(key, state, payload);
    }

    private void OnTimer(object? timerState)
    {
        if (timerState is not TKey key) return;
        var deliver = false;
        var state = false;
        var payload = "";
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out var entry)) return;
            _entries.Remove(key);
            entry.Timer?.Dispose();
            entry.Timer = null;
            if (entry.HasPending && entry.PendingState != entry.Delivered)
            {
                deliver = true;
                state = entry.PendingState;
                payload = entry.PendingPayload;
            }
        }

        if (deliver) Deliver(key, state, payload);
    }

    private void Deliver(TKey key, bool state, string payload)
    {
        try
        {
            _deliver(key, state, payload);
        }
        catch (Exception ex)
        {
            Log.Error("Debounced callback failed", ex);
        }
    }

    /// <summary>Drops every pending change.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Timer?.Dispose();
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Clear();
    }
}
