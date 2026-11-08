using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Tests;

/// <summary>An EventBus without a dispatcher (handlers run synchronously) + a SettingsManager on a temp file.</summary>
internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "DynamicIslandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        Bus = new EventBus(null);
        Settings = new SettingsManager(Bus, SettingsPath);
    }

    public string RootDirectory { get; }
    public string SettingsPath { get; }
    public EventBus Bus { get; }
    public SettingsManager Settings { get; }

    /// <summary>Records every event of type T published on the bus.</summary>
    public EventRecorder<T> Record<T>() where T : notnull => new(Bus);

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(RootDirectory, recursive: true); } catch { /* best effort */ }
    }
}

internal sealed class EventRecorder<T> : IDisposable where T : notnull
{
    private readonly List<T> _events = new();
    private readonly object _gate = new();
    private readonly IDisposable _sub;

    public EventRecorder(IEventBus bus)
    {
        _sub = bus.Subscribe<T>(e =>
        {
            lock (_gate) _events.Add(e);
        });
    }

    public IReadOnlyList<T> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public T Last
    {
        get { lock (_gate) return _events[^1]; }
    }

    public int Count
    {
        get { lock (_gate) return _events.Count; }
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }

    public void Dispose() => _sub.Dispose();
}

/// <summary>Manually advanced monotonic clock for TimerManager tests.</summary>
internal sealed class FakeClock
{
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    public TimeSpan Now => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    public void AdvanceSeconds(double seconds) => _now += TimeSpan.FromSeconds(seconds);
}
