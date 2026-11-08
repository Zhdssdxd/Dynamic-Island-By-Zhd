using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsDynamicIsland.SystemIntegration.Native;

/// <summary>
/// COM callback (<c>IMMNotificationClient</c>) that forwards default-endpoint changes.
/// Windows invokes it on an internal Core Audio thread; per the API contract the callback must return quickly
/// and must not call back into the device enumerator, so the handler only schedules work elsewhere.
/// Public because the runtime exposes it to COM through a CCW.
/// </summary>
[ComVisible(true)]
public sealed class AudioEndpointNotificationClient : IMMNotificationClient
{
    private readonly Action<DataFlow, Role> _defaultDeviceChanged;

    internal AudioEndpointNotificationClient(Action<DataFlow, Role> defaultDeviceChanged)
    {
        _defaultDeviceChanged = defaultDeviceChanged;
    }

    /// <inheritdoc />
    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
    }

    /// <inheritdoc />
    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    /// <inheritdoc />
    public void OnDeviceRemoved(string deviceId)
    {
    }

    /// <inheritdoc />
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        try
        {
            _defaultDeviceChanged(flow, role);
        }
        catch
        {
            // Never let an exception travel back into the COM caller.
        }
    }

    /// <inheritdoc />
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
