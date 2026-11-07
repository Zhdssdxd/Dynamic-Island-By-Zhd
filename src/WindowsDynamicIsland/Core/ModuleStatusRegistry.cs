using System.Collections.Concurrent;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Collects <see cref="ModuleStatusChanged"/> events so the settings window can show a diagnostics page
/// (which features work on this PC and why some do not).
/// </summary>
public sealed class ModuleStatusRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, ModuleStatusChanged> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _sub;

    public ModuleStatusRegistry(IEventBus bus)
    {
        _sub = bus.Subscribe<ModuleStatusChanged>(e =>
        {
            _statuses[e.ModuleName] = e;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Raised on the publisher's thread whenever a status changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ModuleStatusChanged> Snapshot()
        => _statuses.Values.OrderBy(s => s.ModuleName, StringComparer.OrdinalIgnoreCase).ToList();

    public void Dispose() => _sub.Dispose();
}
