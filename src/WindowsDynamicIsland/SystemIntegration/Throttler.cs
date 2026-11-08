using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Leading + trailing throttle that keeps only the latest value: the first value after a quiet period is
/// delivered immediately, later values inside the interval are coalesced and the last one is delivered when
/// the interval ends. Used to cap bursty OS notifications (volume slider, brightness keys) at ~30/s.
/// Thread-safe; the callback runs on the posting thread or on a thread-pool timer thread.
/// </summary>
internal sealed class Throttler<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly long _intervalMs;
    private readonly Action<T> _action;
    private readonly System.Threading.Timer _timer;
    private long _lastFireMs = long.MinValue / 2;
    private bool _timerArmed;
    private bool _hasPending;
    private T _pending = default!;
    private bool _disposed;

    public Throttler(TimeSpan interval, Action<T> action)
    {
        _intervalMs = Math.Max(1, (long)interval.TotalMilliseconds);
        _action = action;
        _timer = new System.Threading.Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Submits a value.</summary>
    public void Post(T value)
    {
        var fireNow = false;
        lock (_gate)
        {
            if (_disposed) return;
            var now = Environment.TickCount64;
            var elapsed = now - _lastFireMs;
            if (!_timerArmed && elapsed >= _intervalMs)
            {
                _lastFireMs = now;
                fireNow = true;
            }
            else
            {
                _pending = value;
                _hasPending = true;
                if (!_timerArmed)
                {
                    _timerArmed = true;
                    _timer.Change(Math.Max(1, _intervalMs - elapsed), Timeout.Infinite);
                }
            }
        }

        if (fireNow) Invoke(value);
    }

    private void OnTimer(object? state)
    {
        T value;
        lock (_gate)
        {
            _timerArmed = false;
            if (_disposed || !_hasPending) return;
            value = _pending;
            _pending = default!;
            _hasPending = false;
            _lastFireMs = Environment.TickCount64;
        }
        Invoke(value);
    }

    private void Invoke(T value)
    {
        try
        {
            _action(value);
        }
        catch (Exception ex)
        {
            // Timer callbacks must never throw (an unhandled exception would terminate the process).
            Log.Error("Throttled callback failed", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPending = false;
            _pending = default!;
        }
        _timer.Dispose();
    }
}
