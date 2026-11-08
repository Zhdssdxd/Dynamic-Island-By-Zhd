using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Core Audio endpoint notification sink. Windows invokes it on its own MTA worker threads; every callback
/// only forwards to the supplied delegates (which must merely set flags / schedule work — calling back into
/// <see cref="MMDeviceEnumerator"/> from inside a notification can deadlock). Exceptions never cross the COM boundary.
/// Public only because COM needs to see the implementing type; not part of the module contract.
/// </summary>
public sealed class DefaultDeviceWatcher : IMMNotificationClient
{
    private readonly Action _defaultRenderDeviceChanged;
    private readonly Action _deviceAvailabilityChanged;

    /// <summary>Creates the sink.</summary>
    /// <param name="defaultRenderDeviceChanged">Called when the default multimedia/console render endpoint changes.</param>
    /// <param name="deviceAvailabilityChanged">Called when an endpoint is added, removed or becomes active.</param>
    public DefaultDeviceWatcher(Action defaultRenderDeviceChanged, Action deviceAvailabilityChanged)
    {
        _defaultRenderDeviceChanged = defaultRenderDeviceChanged ?? throw new ArgumentNullException(nameof(defaultRenderDeviceChanged));
        _deviceAvailabilityChanged = deviceAvailabilityChanged ?? throw new ArgumentNullException(nameof(deviceAvailabilityChanged));
    }

    /// <inheritdoc />
    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState == DeviceState.Active) Forward(_deviceAvailabilityChanged);
    }

    /// <inheritdoc />
    public void OnDeviceAdded(string pwstrDeviceId) => Forward(_deviceAvailabilityChanged);

    /// <inheritdoc />
    public void OnDeviceRemoved(string deviceId) => Forward(_deviceAvailabilityChanged);

    /// <inheritdoc />
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Windows reports each role separately; the capture follows the Multimedia endpoint (Console is usually the same).
        if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            Forward(_defaultRenderDeviceChanged);
    }

    /// <inheritdoc />
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Property changes (names, formats, …) are irrelevant here; a format change invalidates the stream and is
        // handled through RecordingStopped.
    }

    private static void Forward(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Audio device notification handler failed", ex);
        }
    }
}
