using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Owns the lifecycle of all <see cref="IIslandModule"/>s. One failing module never takes the app down:
/// start/stop errors are isolated, logged and reported via <see cref="ModuleStatusChanged"/>.
/// </summary>
public sealed class ModuleManager : IDisposable
{
    private readonly IEventBus _bus;
    private readonly List<IIslandModule> _modules = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _disposed;

    public ModuleManager(IEventBus bus)
    {
        _bus = bus;
    }

    public IReadOnlyList<IIslandModule> Modules => _modules;

    public void Register(IIslandModule module)
    {
        if (_started) throw new InvalidOperationException("Register modules before StartAllAsync.");
        _modules.Add(module);
    }

    /// <summary>Starts all modules concurrently. Never throws.</summary>
    public async Task StartAllAsync()
    {
        _started = true;
        var tasks = _modules.Select(StartOneAsync).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task StartOneAsync(IIslandModule module)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await module.StartAsync(_cts.Token).ConfigureAwait(true);
            Log.Info($"Module {module.Name} started in {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Log.Error($"Module {module.Name} failed to start", ex);
            _bus.Publish(new ModuleStatusChanged(module.Name, false, ex.Message));
        }
    }

    /// <summary>Stops all modules in reverse order, each bounded by a timeout. Never throws.</summary>
    public async Task StopAllAsync(TimeSpan perModuleTimeout)
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            var module = _modules[i];
            try
            {
                var stop = module.StopAsync();
                var done = await Task.WhenAny(stop, Task.Delay(perModuleTimeout)).ConfigureAwait(true);
                if (done != stop) Log.Warn($"Module {module.Name} did not stop within {perModuleTimeout.TotalMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Warn($"Module {module.Name} stop failed", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            try { _modules[i].Dispose(); }
            catch (Exception ex) { Log.Warn($"Module {_modules[i].Name} dispose failed", ex); }
        }
        _modules.Clear();
        _cts.Dispose();
    }
}
