using NAudio.CoreAudioApi;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Shows a volume HUD when the master volume or mute state of the default playback device
/// (Render / Multimedia) changes — whether by keyboard keys, the tray flyout or another app.
/// Bursts (dragging a slider) are throttled to ~30 updates/s; the island updates the HUD in place.
/// Switching the default device silently rebinds (no HUD). Active only while <c>Indicators.Volume</c> is on.
/// </summary>
public sealed class VolumeMonitor : IIslandModule
{
    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private EndpointVolumeWatcher? _watcher;
    private Throttler<VolumeSample>? _throttler;
    private int _lastPercent = -1;
    private bool _lastMuted;

    private readonly record struct VolumeSample(float Level, bool Muted);

    public VolumeMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Indicators.Volume,
            _ => "",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Громкость";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        var throttler = new Throttler<VolumeSample>(TimeSpan.FromMilliseconds(33), PublishSample);
        var watcher = new EndpointVolumeWatcher(DataFlow.Render, Role.Multimedia, "Volume", OnVolume);
        lock (_gate)
        {
            _throttler = throttler;
            _watcher = watcher;
            _lastPercent = -1;
        }

        var error = watcher.Start();
        if (error is not null) return Task.FromResult(FeatureStatus.Unavailable(error));

        return Task.FromResult(watcher.HasDevice
            ? FeatureStatus.Ok()
            : FeatureStatus.Ok("Устройство воспроизведения не найдено — индикатор появится после подключения динамиков или наушников"));
    }

    private void OnVolume(float level, bool muted, bool isBaseline)
    {
        if (isBaseline)
        {
            // New endpoint bound: remember its state, do not show anything.
            lock (_gate)
            {
                _lastPercent = ToPercent(level);
                _lastMuted = muted;
            }
            return;
        }

        Throttler<VolumeSample>? throttler;
        lock (_gate) throttler = _throttler;
        throttler?.Post(new VolumeSample(level, muted));
    }

    private void PublishSample(VolumeSample sample)
    {
        var percent = ToPercent(sample.Level);
        lock (_gate)
        {
            if (_watcher is null) return; // deactivated meanwhile
            // Balance / per-channel changes also raise notifications — ignore if nothing visible changed.
            if (percent == _lastPercent && sample.Muted == _lastMuted) return;
            _lastPercent = percent;
            _lastMuted = sample.Muted;
        }

        var level = Math.Clamp((double)sample.Level, 0, 1);
        _bus.Publish(new SystemIndicatorEvent
        {
            Kind = IndicatorKind.Volume,
            Glyph = Glyphs.VolumeForLevel(level, sample.Muted),
            Title = "Громкость",
            Subtitle = sample.Muted ? "Без звука" : $"{percent}%",
            Level = level,
            Flag = sample.Muted,
            Tone = IndicatorTone.Neutral,
        });
    }

    private static int ToPercent(float level) => (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);

    private void Deactivate()
    {
        EndpointVolumeWatcher? watcher;
        Throttler<VolumeSample>? throttler;
        lock (_gate)
        {
            watcher = _watcher;
            throttler = _throttler;
            _watcher = null;
            _throttler = null;
        }
        watcher?.Dispose();
        throttler?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
