using Windows.Foundation;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using BluetoothDevice = Windows.Devices.Bluetooth.BluetoothDevice;
using BluetoothLEDevice = Windows.Devices.Bluetooth.BluetoothLEDevice;
using DeviceInformation = Windows.Devices.Enumeration.DeviceInformation;
using DeviceInformationKind = Windows.Devices.Enumeration.DeviceInformationKind;
using DeviceInformationUpdate = Windows.Devices.Enumeration.DeviceInformationUpdate;
using DeviceWatcher = Windows.Devices.Enumeration.DeviceWatcher;
using DeviceWatcherStatus = Windows.Devices.Enumeration.DeviceWatcherStatus;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Bluetooth connect/disconnect indicator for <see cref="DeviceMonitor"/>.
/// Two <see cref="DeviceWatcher"/>s (Association Endpoint kind) watch paired Classic and LE devices with the
/// <c>System.Devices.Aep.IsConnected</c> property; an indicator is published when that property flips.
/// The initial enumeration of each watcher is recorded silently (gate: EnumerationCompleted).
/// Flapping is suppressed per device name (Classic and LE endpoints of one headset share it) with a 1.5 s
/// leading-edge debounce. Without a Bluetooth adapter the watchers simply find nothing.
/// WinRT events arrive on thread-pool threads.
/// </summary>
internal sealed class BluetoothConnectionWatcher : IDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string NameProperty = "System.ItemNameDisplay";
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(1500);

    private sealed class Slot
    {
        public Slot(DeviceWatcher watcher) => Watcher = watcher;
        public DeviceWatcher Watcher { get; }
        public bool EnumerationCompleted;
        public TypedEventHandler<DeviceWatcher, DeviceInformation>? Added;
        public TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Updated;
        public TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? Removed;
        public TypedEventHandler<DeviceWatcher, object>? Completed;
    }

    private sealed class TrackedDevice
    {
        public string Name = "";
        public bool IsConnected;
    }

    private readonly IEventBus _bus;
    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly Dictionary<string, TrackedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly KeyedStateDebouncer<string> _debouncer;
    private volatile bool _running;

    public BluetoothConnectionWatcher(IEventBus bus)
    {
        _bus = bus;
        _debouncer = new KeyedStateDebouncer<string>(DebounceWindow, leadingEdge: true, Deliver, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Starts both watchers. Returns null or a Russian error. Call from a thread-pool thread.</summary>
    public string? Start()
    {
        _running = true;
        var selectors = new List<string>(2);
        try
        {
            selectors.Add(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            selectors.Add(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            Log.Warn("Bluetooth: device selectors unavailable", ex);
            return "Bluetooth: API Windows.Devices.Bluetooth недоступно — " + ex.Message;
        }

        var started = 0;
        Exception? lastError = null;
        foreach (var selector in selectors)
        {
            try
            {
                var watcher = DeviceInformation.CreateWatcher(selector, new[] { IsConnectedProperty, NameProperty }, DeviceInformationKind.AssociationEndpoint);
                var slot = new Slot(watcher);
                slot.Added = (_, info) => OnAdded(slot, info);
                slot.Updated = (_, update) => OnUpdated(slot, update);
                slot.Removed = (_, update) => OnRemoved(update);
                slot.Completed = (_, _) => { lock (_gate) slot.EnumerationCompleted = true; };
                watcher.Added += slot.Added;
                watcher.Updated += slot.Updated;
                watcher.Removed += slot.Removed;
                watcher.EnumerationCompleted += slot.Completed;
                lock (_gate) _slots.Add(slot);
                watcher.Start();
                started++;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn("Bluetooth: watcher failed to start", ex);
            }
        }

        return started > 0 ? null : "Bluetooth: не удалось запустить отслеживание устройств — " + lastError?.Message;
    }

    private void OnAdded(Slot slot, DeviceInformation info)
    {
        try
        {
            var connected = info.Properties.TryGetValue(IsConnectedProperty, out var value) && value is bool b && b;
            string name;
            bool announce;
            lock (_gate)
            {
                if (!_running) return;
                name = info.Name ?? "";
                _devices[info.Id] = new TrackedDevice { Name = name, IsConnected = connected };
                // A device paired after start that connects right away.
                announce = slot.EnumerationCompleted && connected;
            }
            if (announce) Push(name, info.Id, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Bluetooth: Added handler failed", ex);
        }
    }

    private void OnUpdated(Slot slot, DeviceInformationUpdate update)
    {
        try
        {
            string name;
            bool connected;
            lock (_gate)
            {
                if (!_running || !_devices.TryGetValue(update.Id, out var device)) return;
                if (update.Properties.TryGetValue(NameProperty, out var n) && n is string newName && newName.Length > 0)
                    device.Name = newName;
                if (!update.Properties.TryGetValue(IsConnectedProperty, out var value) || value is not bool isConnected) return;
                if (isConnected == device.IsConnected) return;
                device.IsConnected = isConnected;
                if (!slot.EnumerationCompleted) return;
                name = device.Name;
                connected = isConnected;
            }
            Push(name, update.Id, connected);
        }
        catch (Exception ex)
        {
            Log.Warn("Bluetooth: Updated handler failed", ex);
        }
    }

    private void OnRemoved(DeviceInformationUpdate update)
    {
        try
        {
            // Unpaired: forget it; no indicator.
            lock (_gate) _devices.Remove(update.Id);
        }
        catch (Exception ex)
        {
            Log.Warn("Bluetooth: Removed handler failed", ex);
        }
    }

    private void Push(string name, string id, bool connected)
    {
        if (string.IsNullOrWhiteSpace(name)) return; // nameless LE endpoints carry no useful information
        var key = name.Trim();
        _debouncer.Push(key.Length > 0 ? key : id, connected, key);
    }

    private void Deliver(string key, bool connected, string name)
    {
        if (!_running) return;
        _bus.Publish(new SystemIndicatorEvent
        {
            Kind = IndicatorKind.Bluetooth,
            Glyph = Glyphs.Bluetooth,
            Title = name,
            Subtitle = connected ? "Подключено" : "Отключено",
            Flag = connected,
            Tone = connected ? IndicatorTone.Positive : IndicatorTone.Neutral,
        });
    }

    /// <summary>Stops the watchers and unsubscribes. Idempotent.</summary>
    public void Dispose()
    {
        _running = false;
        _debouncer.Dispose();

        Slot[] slots;
        lock (_gate)
        {
            slots = _slots.ToArray();
            _slots.Clear();
            _devices.Clear();
        }

        foreach (var slot in slots)
        {
            var watcher = slot.Watcher;
            try
            {
                if (slot.Added is not null) watcher.Added -= slot.Added;
                if (slot.Updated is not null) watcher.Updated -= slot.Updated;
                if (slot.Removed is not null) watcher.Removed -= slot.Removed;
                if (slot.Completed is not null) watcher.EnumerationCompleted -= slot.Completed;
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    watcher.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn("Bluetooth: watcher stop failed", ex);
            }
        }
    }
}
