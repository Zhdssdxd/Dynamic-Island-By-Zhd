using System.Management;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Shows a brightness HUD when the display brightness changes, using WMI (<c>root\WMI</c>):
/// <c>WmiMonitorBrightness</c> for the initial value and a <see cref="ManagementEventWatcher"/> on
/// <c>WmiMonitorBrightnessEvent</c> for changes. Windows only exposes these classes for internal laptop
/// panels (and a few all-in-ones); on desktop PCs with external monitors (DDC/CI is not exposed via WMI)
/// the module reports itself unavailable and stays idle. Active only while <c>Indicators.Brightness</c> is on.
/// </summary>
public sealed class BrightnessMonitor : IIslandModule
{
    private const string UnsupportedMessage =
        "Яркость: поддерживается только для встроенных дисплеев (WMI WmiMonitorBrightness). " +
        "Для внешних мониторов Windows не сообщает об изменении яркости.";

    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private Throttler<int>? _throttler;
    private int _lastValue = -1;

    public BrightnessMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Indicators.Brightness,
            _ => "",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Яркость";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        var scope = new ManagementScope(@"\\.\root\WMI");
        int? current = null;
        try
        {
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                using (mo)
                {
                    if (current is not null) continue;
                    if (mo["Active"] is bool active && !active) continue;
                    if (mo["CurrentBrightness"] is { } value) current = Convert.ToInt32(value);
                }
            }
        }
        catch (ManagementException ex)
        {
            // "Not supported" / "Invalid class" on machines without a WMI brightness provider.
            Log.Info($"Brightness: WMI provider unavailable ({ex.ErrorCode})");
            return Task.FromResult(FeatureStatus.Unavailable(UnsupportedMessage));
        }
        catch (Exception ex)
        {
            Log.Warn("Brightness: WMI query failed", ex);
            return Task.FromResult(FeatureStatus.Unavailable(UnsupportedMessage + " (" + ex.Message + ")"));
        }

        if (current is null)
            return Task.FromResult(FeatureStatus.Unavailable(UnsupportedMessage));

        ct.ThrowIfCancellationRequested();

        var throttler = new Throttler<int>(TimeSpan.FromMilliseconds(40), PublishValue);
        var watcher = new ManagementEventWatcher(scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
        lock (_gate)
        {
            _lastValue = current.Value;
            _throttler = throttler;
            _watcher = watcher;
        }

        watcher.EventArrived += OnEventArrived;
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Brightness: event watcher failed to start", ex);
            return Task.FromResult(FeatureStatus.Unavailable("Яркость: не удалось подписаться на события WMI — " + ex.Message));
        }

        return Task.FromResult(FeatureStatus.Ok());
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var evt = e.NewEvent;
            if (evt["Brightness"] is not { } raw) return;
            var value = Math.Clamp(Convert.ToInt32(raw), 0, 100);
            Throttler<int>? throttler;
            lock (_gate) throttler = _throttler;
            throttler?.Post(value);
        }
        catch (Exception ex)
        {
            Log.Warn("Brightness: bad WMI event", ex);
        }
    }

    private void PublishValue(int value)
    {
        lock (_gate)
        {
            if (_watcher is null || value == _lastValue) return;
            _lastValue = value;
        }

        _bus.Publish(new SystemIndicatorEvent
        {
            Kind = IndicatorKind.Brightness,
            Glyph = Glyphs.Brightness,
            Title = "Яркость",
            Subtitle = $"{value}%",
            Level = value / 100.0,
            Tone = IndicatorTone.Neutral,
        });
    }

    private void Deactivate()
    {
        ManagementEventWatcher? watcher;
        Throttler<int>? throttler;
        lock (_gate)
        {
            watcher = _watcher;
            throttler = _throttler;
            _watcher = null;
            _throttler = null;
        }

        if (watcher is not null)
        {
            watcher.EventArrived -= OnEventArrived;
            try { watcher.Stop(); } catch (Exception ex) { Log.Warn("Brightness: watcher stop failed", ex); }
            try { watcher.Dispose(); } catch (Exception ex) { Log.Warn("Brightness: watcher dispose failed", ex); }
        }
        throttler?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
