using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.SystemIntegration.Native;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Watches the master volume / mute of the default audio endpoint for a (flow, role) pair and follows
/// default-device changes. Wraps NAudio's Core Audio API:
/// <c>MMDeviceEnumerator</c> → <c>GetDefaultAudioEndpoint</c> → <c>AudioEndpointVolume.OnVolumeNotification</c>,
/// plus an <c>IMMNotificationClient</c> for <c>OnDefaultDeviceChanged</c>.
///
/// Threading: all COM objects are created, rebound and released on thread-pool (MTA) threads.
/// Volume notifications arrive on a Core Audio thread and are forwarded as-is. A default-device change is
/// debounced (300 ms) and the rebind runs on a timer thread — never inside the COM callback.
/// When the endpoint is (re)bound the current values are reported once with <c>isBaseline = true</c>
/// so the owner can update its "last value" without showing an indicator.
/// </summary>
internal sealed class EndpointVolumeWatcher : IDisposable
{
    /// <summary>Callback: (scalar level 0..1, muted, isBaseline).</summary>
    private readonly Action<float, bool, bool> _handler;
    private readonly DataFlow _flow;
    private readonly Role _role;
    private readonly string _logName;
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private AudioEndpointNotificationClient? _client;
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;
    private System.Threading.Timer? _rebindTimer;
    private volatile bool _running;

    public EndpointVolumeWatcher(DataFlow flow, Role role, string logName, Action<float, bool, bool> handler)
    {
        _flow = flow;
        _role = role;
        _logName = logName;
        _handler = handler;
    }

    /// <summary>True while an endpoint is bound.</summary>
    public bool HasDevice
    {
        get { lock (_gate) return _volume is not null; }
    }

    /// <summary>
    /// Creates the enumerator, registers for default-device changes and binds the current default endpoint.
    /// Returns null on success, or a Russian explanation when Core Audio is unavailable.
    /// Call from a thread-pool thread.
    /// </summary>
    public string? Start()
    {
        lock (_gate)
        {
            if (_running) return null;
            try
            {
                _enumerator = new MMDeviceEnumerator();
            }
            catch (Exception ex)
            {
                Log.Warn($"{_logName}: MMDeviceEnumerator unavailable", ex);
                _enumerator = null;
                return "Звуковая подсистема Windows (Core Audio) недоступна: " + ex.Message;
            }

            _running = true;
            _rebindTimer = new System.Threading.Timer(_ => Rebind(), null, Timeout.Infinite, Timeout.Infinite);
            _client = new AudioEndpointNotificationClient(OnDefaultDeviceChanged);
            try
            {
                var hr = _enumerator.RegisterEndpointNotificationCallback(_client);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new COMException("RegisterEndpointNotificationCallback", hr);
            }
            catch (Exception ex)
            {
                // Still usable, just without following default-device switches.
                Log.Warn($"{_logName}: cannot follow default device changes", ex);
                _client = null;
            }

            BindCore();
            return null;
        }
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role)
    {
        if (!_running || flow != _flow || role != _role) return;
        try
        {
            _rebindTimer?.Change(300, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // stopping
        }
    }

    private void Rebind()
    {
        try
        {
            lock (_gate)
            {
                if (!_running) return;
                BindCore();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{_logName}: rebind failed", ex);
        }
    }

    /// <summary>Binds the current default endpoint. Caller holds the lock.</summary>
    private void BindCore()
    {
        UnbindCore();
        if (_enumerator is null) return;

        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(_flow, _role);
        }
        catch (COMException ex)
        {
            // E_NOTFOUND (0x80070490): no active endpoint of this kind; we wait for OnDefaultDeviceChanged.
            Log.Info($"{_logName}: no default endpoint (0x{ex.HResult:X8})");
            return;
        }

        try
        {
            var volume = device.AudioEndpointVolume;
            var level = volume.MasterVolumeLevelScalar;
            var muted = volume.Mute;
            volume.OnVolumeNotification += OnVolumeNotification;
            _device = device;
            _volume = volume;
            Log.Debug($"{_logName}: bound to endpoint '{SafeFriendlyName(device)}'");
            _handler(level, muted, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"{_logName}: cannot open endpoint volume", ex);
            _device = device;
            UnbindCore();
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try { return device.FriendlyName; } catch { return "?"; }
    }

    /// <summary>Unsubscribes and releases the bound endpoint. Caller holds the lock.</summary>
    private void UnbindCore()
    {
        var volume = _volume;
        var device = _device;
        _volume = null;
        _device = null;

        if (volume is not null)
        {
            try { volume.OnVolumeNotification -= OnVolumeNotification; } catch { /* ignore */ }
        }

        // MMDevice.Dispose disposes its AudioEndpointVolume (UnregisterControlChangeNotify + COM release).
        if (device is not null)
        {
            try { device.Dispose(); }
            catch (Exception ex) { Log.Warn($"{_logName}: endpoint dispose failed", ex); }
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (!_running) return;
        try
        {
            _handler(data.MasterVolume, data.Muted, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{_logName}: volume handler failed", ex);
        }
    }

    /// <summary>Unregisters every callback and releases all COM objects. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_running && _enumerator is null) return;
            _running = false;

            try { _rebindTimer?.Dispose(); } catch { /* ignore */ }
            _rebindTimer = null;

            UnbindCore();

            if (_enumerator is not null)
            {
                if (_client is not null)
                {
                    try { _enumerator.UnregisterEndpointNotificationCallback(_client); }
                    catch (Exception ex) { Log.Warn($"{_logName}: unregister endpoint callback failed", ex); }
                }
                try { _enumerator.Dispose(); }
                catch (Exception ex) { Log.Warn($"{_logName}: enumerator dispose failed", ex); }
            }
            _client = null;
            _enumerator = null;
        }
    }
}
