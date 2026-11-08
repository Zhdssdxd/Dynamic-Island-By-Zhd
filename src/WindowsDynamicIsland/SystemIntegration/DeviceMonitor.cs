using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// USB and Bluetooth connect/disconnect indicators.
///  * USB (<c>Indicators.Usb</c>): <see cref="UsbDeviceWatcher"/> — message-only window on the UI dispatcher +
///    <c>RegisterDeviceNotification(GUID_DEVINTERFACE_USB_DEVICE)</c>, WMI for friendly names.
///  * Bluetooth (<c>Indicators.Bluetooth</c>): <see cref="BluetoothConnectionWatcher"/> — WinRT DeviceWatchers on
///    paired Classic/LE association endpoints, <c>System.Devices.Aep.IsConnected</c>.
/// Each part runs only while its toggle is on; toggling either restarts the module with the new combination.
/// Devices present at start are not announced.
/// </summary>
public sealed class DeviceMonitor : IIslandModule
{
    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private UsbDeviceWatcher? _usb;
    private BluetoothConnectionWatcher? _bluetooth;

    public DeviceMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Indicators.Usb || s.Indicators.Bluetooth,
            s => $"usb={s.Indicators.Usb};bt={s.Indicators.Bluetooth}",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Устройства";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private async Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        string? usbError = null;
        string? btError = null;
        var usbOn = settings.Indicators.Usb;
        var btOn = settings.Indicators.Bluetooth;

        if (usbOn)
        {
            var usb = new UsbDeviceWatcher(_bus);
            lock (_gate) _usb = usb;
            usbError = await usb.StartAsync(ct).ConfigureAwait(false);
        }

        if (btOn)
        {
            var bluetooth = new BluetoothConnectionWatcher(_bus);
            lock (_gate) _bluetooth = bluetooth;
            btError = bluetooth.Start();
        }

        var failedAll = (!usbOn || usbError is not null) && (!btOn || btError is not null);
        var parts = new List<string>(3);
        if (usbError is not null) parts.Add(usbError);
        if (btError is not null) parts.Add(btError);
        if (!usbOn) parts.Add("USB выключено в настройках");
        if (!btOn) parts.Add("Bluetooth выключен в настройках");

        if (failedAll) return FeatureStatus.Unavailable(string.Join("; ", parts));
        return FeatureStatus.Ok(parts.Count == 0 ? null : string.Join("; ", parts));
    }

    private void Deactivate()
    {
        UsbDeviceWatcher? usb;
        BluetoothConnectionWatcher? bluetooth;
        lock (_gate)
        {
            usb = _usb;
            bluetooth = _bluetooth;
            _usb = null;
            _bluetooth = null;
        }
        usb?.Dispose();
        bluetooth?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
