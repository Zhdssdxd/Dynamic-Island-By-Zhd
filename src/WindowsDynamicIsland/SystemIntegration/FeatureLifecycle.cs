using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>Outcome of activating a feature; published as <see cref="ModuleStatusChanged"/>.</summary>
internal readonly record struct FeatureStatus(bool IsWorking, string? Message)
{
    public static FeatureStatus Ok(string? message = null) => new(true, message);

    public static FeatureStatus Unavailable(string message) => new(false, message);
}

/// <summary>
/// Shared lifecycle for the system modules: follows <see cref="SettingsChanged"/>, activates the feature
/// while its toggle is on (releasing every OS resource when it is turned off), restarts it when its
/// configuration key changes and publishes <see cref="ModuleStatusChanged"/>.
///
/// Threading: all transitions are serialized by a semaphore and executed on the thread pool, so the
/// activation/deactivation callbacks never run on the UI thread (unless <see cref="Dispose"/> is called
/// there while nothing else is running) and never run concurrently with each other.
/// A failed activation is not retried until the toggle or the configuration key changes.
/// </summary>
internal sealed class FeatureLifecycle : IDisposable
{
    internal const string DisabledMessage = "Выключено в настройках";

    private readonly string _name;
    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly Func<AppSettings, bool> _isEnabled;
    private readonly Func<AppSettings, string> _configurationKey;
    private readonly Func<AppSettings, CancellationToken, Task<FeatureStatus>> _activate;
    private readonly Action _deactivate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly object _statusGate = new();
    private IDisposable? _subscription;
    private bool _active;
    private string? _activeKey;
    private (bool IsWorking, string? Message)? _lastStatus;
    private volatile bool _stopped;
    private int _disposed;

    /// <param name="name">Module name used for status and logs.</param>
    /// <param name="bus">Event bus.</param>
    /// <param name="settings">Settings manager (the latest snapshot is always used).</param>
    /// <param name="isEnabled">Whether the feature toggle is on.</param>
    /// <param name="configurationKey">Settings that require a restart when they change (e.g. a folder path).</param>
    /// <param name="activate">Acquires OS resources. Must clean up after itself only through <paramref name="deactivate"/>.</param>
    /// <param name="deactivate">Releases every resource; must be idempotent and tolerate a partial activation.</param>
    public FeatureLifecycle(
        string name,
        IEventBus bus,
        SettingsManager settings,
        Func<AppSettings, bool> isEnabled,
        Func<AppSettings, string> configurationKey,
        Func<AppSettings, CancellationToken, Task<FeatureStatus>> activate,
        Action deactivate)
    {
        _name = name;
        _bus = bus;
        _settings = settings;
        _isEnabled = isEnabled;
        _configurationKey = configurationKey;
        _activate = activate;
        _deactivate = deactivate;
        _token = _cts.Token;
    }

    /// <summary>Token cancelled when the module stops; activation code may pass it to async work.</summary>
    public CancellationToken StoppingToken => _token;

    /// <summary>Subscribes to settings and applies the current snapshot. Never throws.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stopped || cancellationToken.IsCancellationRequested) return;
        try
        {
            _subscription ??= _bus.Subscribe<SettingsChanged>(OnSettingsChanged);
        }
        catch (Exception ex)
        {
            Log.Error($"{_name}: settings subscription failed", ex);
        }
        await ApplyAsync().ConfigureAwait(false);
    }

    /// <summary>Unsubscribes and releases resources. Idempotent, never throws.</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        _subscription?.Dispose();
        _subscription = null;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(DeactivateCore).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Publishes a status update (deduplicated). Modules call it when their health changes at runtime
    /// (e.g. access revoked, watcher recreated).
    /// </summary>
    public void PublishStatus(bool isWorking, string? message)
    {
        lock (_statusGate)
        {
            if (_lastStatus is { } last && last.IsWorking == isWorking && last.Message == message) return;
            _lastStatus = (isWorking, message);
        }
        if (isWorking && message is null) Log.Info($"{_name}: работает");
        else Log.Info($"{_name}: {(isWorking ? "работает" : "недоступно")} — {message}");
        _bus.Publish(new ModuleStatusChanged(_name, isWorking, message));
    }

    private void OnSettingsChanged(SettingsChanged e)
    {
        // Runs on the publisher's thread (usually UI): just queue the serialized transition.
        _ = ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        try
        {
            await _gate.WaitAsync(_token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await Task.Run(ApplyCoreAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"{_name}: applying settings failed", ex);
        }
        finally
        {
            // Dispose may have raced with this transition and skipped the cleanup because the gate was taken.
            if (_stopped) DeactivateCore();
            _gate.Release();
        }
    }

    private async Task ApplyCoreAsync()
    {
        if (_stopped)
        {
            DeactivateCore();
            return;
        }

        var settings = _settings.Current;
        var enabled = _isEnabled(settings);
        var key = enabled ? _configurationKey(settings) : null;

        if (_active && (!enabled || !string.Equals(key, _activeKey, StringComparison.Ordinal)))
            DeactivateCore();

        if (!enabled)
        {
            PublishStatus(true, DisabledMessage);
            return;
        }

        if (_active) return;

        _active = true;
        _activeKey = key;

        FeatureStatus status;
        try
        {
            status = await _activate(settings, _token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"{_name}: activation failed", ex);
            // Release whatever was acquired; _active stays true so a retry only happens on a settings change.
            SafeDeactivate();
            status = FeatureStatus.Unavailable("Не удалось запустить: " + ex.Message);
        }

        if (_stopped) return;
        PublishStatus(status.IsWorking, status.Message);
    }

    private void DeactivateCore()
    {
        if (!_active) return;
        _active = false;
        _activeKey = null;
        SafeDeactivate();
    }

    private void SafeDeactivate()
    {
        try
        {
            _deactivate();
        }
        catch (Exception ex)
        {
            Log.Warn($"{_name}: deactivation failed", ex);
        }
    }

    /// <summary>Synchronous best-effort cleanup; never blocks on an in-flight transition.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopped = true;
        _subscription?.Dispose();
        _subscription = null;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        if (_gate.Wait(0))
        {
            try
            {
                DeactivateCore();
            }
            finally
            {
                _gate.Release();
            }
            _cts.Dispose();
        }
        // Otherwise the running transition sees _stopped in its finally block and deactivates.
    }
}
