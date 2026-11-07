namespace WindowsDynamicIsland.Core;

/// <summary>
/// Common contract for every background feature module (media, audio, system monitors, timer, …).
/// Lifecycle: constructed by AppManager → StartAsync once → StopAsync once on shutdown → Dispose.
///
/// Rules for implementers:
///  * Constructor must be cheap and must not touch OS APIs (no exceptions from ctor).
///  * StartAsync must not block the UI thread for long: do heavy work with Task.Run / async APIs.
///  * A module that cannot work on this machine (API missing, access denied) must NOT throw out of
///    StartAsync; it logs, publishes <see cref="Models.ModuleStatusChanged"/> with an explanation and stays idle.
///  * A module reacts to <see cref="Models.SettingsChanged"/> itself, releasing heavy resources
///    (capture, polling, watchers) when its feature is disabled, and re-acquiring them when enabled.
///  * StopAsync/Dispose must be idempotent and release every OS handle, event subscription and timer.
/// </summary>
public interface IIslandModule : IDisposable
{
    /// <summary>Stable, human-readable module name (used in logs and status).</summary>
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}
