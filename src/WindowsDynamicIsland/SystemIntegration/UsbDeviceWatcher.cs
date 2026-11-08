using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.SystemIntegration.Native;
using WpfApplication = System.Windows.Application;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// USB plug/unplug indicator for <see cref="DeviceMonitor"/>.
///
/// Design: a hidden message-only window (<see cref="HwndSource"/> parented to HWND_MESSAGE) is created on the
/// application's UI dispatcher — no extra thread is needed because device messages are rare and the window
/// procedure only parses the message and hands it to a debouncer. The window registers with
/// <c>RegisterDeviceNotification</c> for <c>GUID_DEVINTERFACE_USB_DEVICE</c>, so it receives
/// DBT_DEVICEARRIVAL / DBT_DEVICEREMOVECOMPLETE with the device-interface path of every USB device
/// (one per physical device, not per function).
///
/// The path "\\?\USB#VID_xxxx&amp;PID_yyyy#serial#{guid}" is mapped to the PnP instance ID
/// "USB\VID_xxxx&amp;PID_yyyy\serial". Events are debounced for 700 ms per device (re-enumeration noise
/// cancels out); the friendly name is then resolved with WMI <c>Win32_PnPEntity</c> on a thread-pool thread
/// (bus-reported product string → driver name → name of a child function → "USB-устройство").
/// Names are cached (and pre-seeded for already connected devices at start), because a removed device can no
/// longer be queried. Hubs and Bluetooth radios are skipped.
/// </summary>
internal sealed class UsbDeviceWatcher : IDisposable
{
    private static readonly Guid GuidDevInterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(700);
    private const string FallbackName = "USB-устройство";

    /// <summary>Generic driver names that say nothing about the actual product.</summary>
    private static readonly string[] GenericNameFragments =
    {
        "composite", "составное", "mass storage", "запоминающее устройство", "usb input device",
        "устройство ввода usb", "usb-устройство ввода", "hid-compliant", "hid-совместим", "unknown usb device",
        "неизвестное usb", "generic usb", "usb device", "usb-устройство", "winusb", "usb serial device",
    };

    private readonly IEventBus _bus;
    private readonly KeyedStateDebouncer<string> _debouncer;
    private readonly ConcurrentDictionary<string, UsbDeviceInfo> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private IntPtr _notificationHandle;
    private volatile bool _running;

    private sealed record UsbDeviceInfo(string Name, bool IsIgnored);

    public UsbDeviceWatcher(IEventBus bus)
    {
        _bus = bus;
        _debouncer = new KeyedStateDebouncer<string>(DebounceWindow, leadingEdge: false, Deliver, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates the message window and registers for notifications. Returns null or a Russian error.</summary>
    public async Task<string?> StartAsync(CancellationToken ct)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return "USB: нет UI-потока приложения для приёма системных уведомлений";

        _running = true;
        _dispatcher = dispatcher;

        // Seed names of devices that are already connected so that their removal can be named.
        _ = Task.Run(SeedKnownDevices, ct);

        try
        {
            return await dispatcher.InvokeAsync(CreateSink, DispatcherPriority.Normal, ct).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("USB: message window creation failed", ex);
            return "USB: не удалось создать окно уведомлений — " + ex.Message;
        }
    }

    /// <summary>Runs on the UI thread.</summary>
    private string? CreateSink()
    {
        if (!_running) return null;

        var parameters = new HwndSourceParameters("DynamicIsland.UsbNotifications")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        var source = new HwndSource(parameters);
        source.AddHook(WndProc);

        var filter = new NativeMethods.DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<NativeMethods.DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GuidDevInterfaceUsbDevice,
        };
        var buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        IntPtr handle;
        int lastError;
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            handle = NativeMethods.RegisterDeviceNotification(source.Handle, buffer, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            lastError = Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (handle == IntPtr.Zero)
        {
            source.RemoveHook(WndProc);
            source.Dispose();
            Log.Warn($"USB: RegisterDeviceNotification failed (Win32 error {lastError})");
            return $"USB: Windows отклонила подписку на уведомления об устройствах (код {lastError})";
        }

        lock (_gate)
        {
            _source = source;
            _notificationHandle = handle;
        }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_DEVICECHANGE || lParam == IntPtr.Zero || !_running) return IntPtr.Zero;
        try
        {
            var eventType = (int)(wParam.ToInt64() & 0xFFFF);
            if (eventType != NativeMethods.DBT_DEVICEARRIVAL && eventType != NativeMethods.DBT_DEVICEREMOVECOMPLETE) return IntPtr.Zero;
            if (Marshal.ReadInt32(lParam, NativeMethods.BroadcastHeaderDeviceTypeOffset) != NativeMethods.DBT_DEVTYP_DEVICEINTERFACE) return IntPtr.Zero;

            var path = Marshal.PtrToStringUni(IntPtr.Add(lParam, NativeMethods.DeviceInterfaceNameOffset));
            var instanceId = DevicePathToInstanceId(path);
            if (instanceId is null) return IntPtr.Zero;

            _debouncer.Push(instanceId, eventType == NativeMethods.DBT_DEVICEARRIVAL, instanceId);
        }
        catch (Exception ex)
        {
            Log.Warn("USB: bad WM_DEVICECHANGE payload", ex);
        }
        return IntPtr.Zero;
    }

    /// <summary>"\\?\USB#VID_046D&amp;PID_C52B#5&amp;2b7f0c1&amp;0&amp;3#{a5dc…}" → "USB\VID_046D&amp;PID_C52B\5&amp;2B7F0C1&amp;0&amp;3".</summary>
    internal static string? DevicePathToInstanceId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Trim();
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal)) s = s.Substring(4);
        var guidStart = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidStart > 0) s = s.Substring(0, guidStart);
        if (s.Length == 0) return null;
        return s.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>Debounced, settled change — runs on a thread-pool timer thread.</summary>
    private void Deliver(string instanceId, bool connected, string payload)
    {
        if (!_running) return;

        UsbDeviceInfo? info;
        if (connected)
        {
            info = QueryDevice(instanceId);
            if (info is not null) _known[instanceId] = info;
        }
        else
        {
            _known.TryGetValue(instanceId, out info);
        }

        if (info is { IsIgnored: true } || !_running) return;

        _bus.Publish(new SystemIndicatorEvent
        {
            Kind = IndicatorKind.UsbDevice,
            Glyph = Glyphs.Usb,
            Title = info?.Name ?? FallbackName,
            Subtitle = connected ? "Подключено" : "Отключено",
            Flag = connected,
            Tone = connected ? IndicatorTone.Positive : IndicatorTone.Neutral,
        });
    }

    // ───────────────────────────── WMI name resolution ─────────────────────────────

    private sealed record PnpEntry(string InstanceId, string? Name, string? PnpClass, string? Service);

    private void SeedKnownDevices()
    {
        try
        {
            var entries = QueryEntities("SELECT Name, PNPDeviceID, PNPClass, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'", withBusDescription: false);
            foreach (var parent in entries.Where(e => e.Entry.InstanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && !IsFunction(e.Entry.InstanceId)))
            {
                if (!_running) return;
                var children = entries.Where(c => IsChildOf(c.Entry.InstanceId, parent.Entry.InstanceId)).Select(c => c.Entry).ToList();
                _known.TryAdd(parent.Entry.InstanceId, Describe(parent.Entry, null, children));
            }
            Log.Debug($"USB: seeded {_known.Count} device names");
        }
        catch (Exception ex)
        {
            Log.Warn("USB: seeding device names failed", ex);
        }
    }

    private static UsbDeviceInfo? QueryDevice(string instanceId)
    {
        try
        {
            var self = QueryEntities(
                $"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{EscapeWql(instanceId)}'", withBusDescription: true)
                .FirstOrDefault();
            if (self.Entry is null) return null;

            List<PnpEntry> children = new();
            var vidPid = VidPidPrefix(instanceId);
            if (vidPid is not null && IsGeneric(self.Entry.Name))
            {
                children = QueryEntities(
                    $"SELECT Name, PNPDeviceID, PNPClass, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '{EscapeWql(vidPid)}&MI%'",
                    withBusDescription: false)
                    .Select(c => c.Entry).ToList();
            }
            return Describe(self.Entry, self.BusDescription, children);
        }
        catch (Exception ex)
        {
            Log.Warn($"USB: WMI lookup failed for {instanceId}", ex);
            return null;
        }
    }

    private static List<(PnpEntry Entry, string? BusDescription)> QueryEntities(string wql, bool withBusDescription)
    {
        var list = new List<(PnpEntry, string?)>();
        using var searcher = new ManagementObjectSearcher(@"root\CIMV2", wql);
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            using (mo)
            {
                if (mo["PNPDeviceID"] is not string id) continue;
                var entry = new PnpEntry(id.ToUpperInvariant(), mo["Name"] as string, mo["PNPClass"] as string, mo["Service"] as string);
                var bus = withBusDescription && mo is ManagementObject obj ? TryGetBusReportedDescription(obj) : null;
                list.Add((entry, bus));
            }
        }
        return list;
    }

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc (the product string reported by the device). Windows 10+.</summary>
    private static string? TryGetBusReportedDescription(ManagementObject device)
    {
        try
        {
            using var input = device.GetMethodParameters("GetDeviceProperties");
            input["devicePropertyKeys"] = new[] { "DEVPKEY_Device_BusReportedDeviceDesc" };
            using var output = device.InvokeMethod("GetDeviceProperties", input, null);
            if (output?["deviceProperties"] is ManagementBaseObject[] props && props.Length > 0)
            {
                try
                {
                    return (props[0]["Data"] as string)?.Trim();
                }
                finally
                {
                    foreach (var p in props) p.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Method not available (older Windows) or property missing — fall back to other names.
        }
        return null;
    }

    private static UsbDeviceInfo Describe(PnpEntry self, string? busDescription, IReadOnlyList<PnpEntry> children)
    {
        var ignored = IsHub(self) || IsBluetoothRadio(self) || children.Any(IsBluetoothRadio);

        string? name = null;
        if (!IsGeneric(self.Name)) name = self.Name;
        if (name is null && !string.IsNullOrWhiteSpace(busDescription) && !IsGeneric(busDescription)) name = busDescription;
        name ??= children.Select(c => c.Name).FirstOrDefault(n => !IsGeneric(n));
        if (name is null && !string.IsNullOrWhiteSpace(busDescription)) name = busDescription;

        return new UsbDeviceInfo(string.IsNullOrWhiteSpace(name) ? FallbackName : name!.Trim(), ignored);
    }

    private static bool IsGeneric(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var lower = name.ToLowerInvariant();
        return GenericNameFragments.Any(lower.Contains);
    }

    private static bool IsHub(PnpEntry e)
        => (e.Service?.StartsWith("usbhub", StringComparison.OrdinalIgnoreCase) ?? false)
           || (e.Name?.Contains("hub", StringComparison.OrdinalIgnoreCase) ?? false)
           || (e.Name?.Contains("концентратор", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsBluetoothRadio(PnpEntry e)
        => string.Equals(e.PnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase);

    /// <summary>"USB\VID_1&amp;PID_2&amp;MI_00\…" is a function of a composite device.</summary>
    private static bool IsFunction(string instanceId) => instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    private static bool IsChildOf(string candidate, string parentInstanceId)
    {
        var prefix = VidPidPrefix(parentInstanceId);
        return prefix is not null && IsFunction(candidate) && candidate.StartsWith(prefix + "&MI_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"USB\VID_046D&amp;PID_C52B\serial" → "USB\VID_046D&amp;PID_C52B".</summary>
    private static string? VidPidPrefix(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length >= 2 && parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase) ? parts[0] + "\\" + parts[1] : null;
    }

    /// <summary>Escapes a WQL string literal (backslash and quote).</summary>
    private static string EscapeWql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    // ───────────────────────────── Shutdown ─────────────────────────────

    public void Dispose()
    {
        _running = false;
        _debouncer.Dispose();

        HwndSource? source;
        IntPtr handle;
        lock (_gate)
        {
            source = _source;
            handle = _notificationHandle;
            _source = null;
            _notificationHandle = IntPtr.Zero;
        }

        if (source is null && handle == IntPtr.Zero) return;

        void Release()
        {
            if (handle != IntPtr.Zero && !NativeMethods.UnregisterDeviceNotification(handle))
                Log.Warn($"USB: UnregisterDeviceNotification failed ({Marshal.GetLastWin32Error()})");
            if (source is not null)
            {
                source.RemoveHook(WndProc);
                source.Dispose();
            }
        }

        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                Release();
            else if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Release));
            // If the dispatcher is shutting down, the window is destroyed with the UI thread.
        }
        catch (Exception ex)
        {
            Log.Warn("USB: release failed", ex);
        }
    }
}
