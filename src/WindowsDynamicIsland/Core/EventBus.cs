using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Windows.Threading;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Typed publish/subscribe bus. The single channel through which modules talk to each other.
/// Publish is thread-safe and may be called from any thread.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribes to events of exactly type <typeparamref name="T"/>.
    /// When <paramref name="onUiThread"/> is true, the handler is marshalled to the UI dispatcher
    /// (asynchronously, preserving publish order); otherwise it runs synchronously on the publisher's thread.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    IDisposable Subscribe<T>(Action<T> handler, bool onUiThread = false) where T : notnull;

    /// <summary>Publishes an event to all current subscribers. Handler exceptions are caught and logged.</summary>
    void Publish<T>(T evt) where T : notnull;

    /// <summary>Returns the last published event of type T (or default). Lets late subscribers get current state.</summary>
    T? GetLast<T>() where T : class;
}

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, ImmutableArray<Subscription>> _subscriptions = new();
    private readonly ConcurrentDictionary<Type, object> _last = new();
    private readonly Dispatcher? _uiDispatcher;

    public EventBus(Dispatcher? uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
    }

    public IDisposable Subscribe<T>(Action<T> handler, bool onUiThread = false) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        var sub = new Subscription(this, typeof(T), o => handler((T)o), onUiThread);
        _subscriptions.AddOrUpdate(typeof(T),
            _ => ImmutableArray.Create(sub),
            (_, list) => list.Add(sub));
        return sub;
    }

    public void Publish<T>(T evt) where T : notnull
    {
        if (evt is null) return;
        var type = typeof(T);
        _last[type] = evt;

        if (!_subscriptions.TryGetValue(type, out var subs) || subs.IsDefaultOrEmpty)
            return;

        foreach (var sub in subs)
        {
            if (sub.IsDisposed) continue;

            if (sub.OnUiThread && _uiDispatcher is not null && !_uiDispatcher.CheckAccess())
            {
                if (_uiDispatcher.HasShutdownStarted) continue;
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Invoke(sub, evt)));
            }
            else
            {
                Invoke(sub, evt);
            }
        }
    }

    public T? GetLast<T>() where T : class
        => _last.TryGetValue(typeof(T), out var v) ? v as T : null;

    private static void Invoke(Subscription sub, object evt)
    {
        if (sub.IsDisposed) return;
        try
        {
            sub.Handler(evt);
        }
        catch (Exception ex)
        {
            Log.Error($"EventBus handler for {evt.GetType().Name} failed", ex);
        }
    }

    private void Remove(Subscription sub)
    {
        _subscriptions.AddOrUpdate(sub.EventType,
            _ => ImmutableArray<Subscription>.Empty,
            (_, list) => list.Remove(sub));
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventBus _owner;
        private int _disposed;

        public Subscription(EventBus owner, Type eventType, Action<object> handler, bool onUiThread)
        {
            _owner = owner;
            EventType = eventType;
            Handler = handler;
            OnUiThread = onUiThread;
        }

        public Type EventType { get; }
        public Action<object> Handler { get; }
        public bool OnUiThread { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Remove(this);
        }
    }
}

/// <summary>Helper to collect several subscriptions and dispose them together.</summary>
public sealed class SubscriptionBag : IDisposable
{
    private readonly List<IDisposable> _items = new();
    private readonly object _gate = new();

    public void Add(IDisposable d)
    {
        lock (_gate) _items.Add(d);
    }

    public void Dispose()
    {
        IDisposable[] items;
        lock (_gate)
        {
            items = _items.ToArray();
            _items.Clear();
        }
        foreach (var d in items)
        {
            try { d.Dispose(); } catch (Exception ex) { Log.Warn("Subscription dispose failed: " + ex.Message); }
        }
    }
}
