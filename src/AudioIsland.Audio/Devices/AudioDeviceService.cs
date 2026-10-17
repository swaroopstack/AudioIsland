using System.Runtime.InteropServices;
using AudioIsland.Core.Contracts;
using AudioIsland.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioIsland.Audio.Devices;

/// <summary>
/// Production device service backed by IMMDeviceEnumerator + IMMNotificationClient.
/// Enumeration is snapshot-based: COM objects are only alive inside GetDevices().
/// </summary>
public sealed class AudioDeviceService : IAudioDeviceService
{
    /// <summary>Convenience alias used by notification routing.</summary>
    internal static AudioDataFlow RenderFlow => AudioDataFlow.Render;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notification;
    private readonly object _gate = new();

    public AudioDeviceService()
    {
        _notification = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notification);
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDataFlow flow)
    {
        lock (_gate)
        {
            var result = new List<AudioDeviceInfo>();
            var dataFlow = flow == AudioDataFlow.Capture ? DataFlow.Capture : DataFlow.Render;

            string? defaultRender = SafeDefaultId(dataFlow, Role.Multimedia);
            string? defaultComm = SafeDefaultId(dataFlow, Role.Communications);

            var collection = _enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.All);
            for (int i = 0; i < collection.Count; i++)
            {
                using var device = collection[i];
                result.Add(DeviceSnapshot.From(device,
                    isDefaultRender: device.ID == defaultRender,
                    isDefaultComm: device.ID == defaultComm));
            }

            // Active defaults first, then active, then inactive — UI shows useful order.
            return result
                .OrderByDescending(d => d.IsDefaultForRender && d.IsActive)
                .ThenByDescending(d => d.IsActive)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public string? GetDefaultDeviceId(AudioDataFlow flow, bool communicationsRole = false)
    {
        lock (_gate)
        {
            var dataFlow = flow == AudioDataFlow.Capture ? DataFlow.Capture : DataFlow.Render;
            return SafeDefaultId(dataFlow, communicationsRole ? Role.Communications : Role.Multimedia);
        }
    }

    /// <summary>
    /// COM callbacks arrive on an MTA thread; re-entrancy into the enumerator
    /// from inside the callback deadlocks on some builds, so raise the event
    /// outside any enumerator call. Consumers must marshal to the UI thread.
    /// </summary>
    private void RaiseDevicesChanged(AudioDataFlow flow) =>
        DevicesChanged?.Invoke(this, new AudioDevicesChangedEventArgs(flow));

    public event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;

    private string? SafeDefaultId(DataFlow flow, Role role)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (COMException)
        {
            // No active device of this flow (e.g. no mic attached).
            return null;
        }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notification); }
        catch (COMException) { /* COM already torn down — nothing to do */ }
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Thin IMMNotificationClient. All six callbacks funnel into one event.
    /// The COM thread must not block: no enumerator calls here.
    /// </summary>
    private sealed class NotificationClient(AudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState) =>
            owner.RaiseDevicesChanged(FlowOf(deviceId));

        public void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId) =>
            owner.RaiseDevicesChanged(FlowOf(deviceId));

        public void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId) =>
            owner.RaiseDevicesChanged(FlowOf(deviceId));

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId) =>
            owner.RaiseDevicesChanged(flow == DataFlow.Capture ? AudioDataFlow.Capture : AudioDeviceService.RenderFlow);

        public void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key)
        {
            // Property changes (name, format) are frequent enough to ignore for UI.
        }

        /// <summary>Flow cannot be read from an ID string without a COM round-trip; raise for render by convention — consumers re-enumerate both flows anyway.</summary>
        private static AudioDataFlow FlowOf(string deviceId) => AudioDeviceService.RenderFlow;
    }
}
