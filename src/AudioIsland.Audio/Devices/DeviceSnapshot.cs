using System.Runtime.InteropServices;
using AudioIsland.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioIsland.Audio.Devices;

/// <summary>
/// Wraps an IMMDevice as an immutable snapshot. COM objects are released
/// immediately after reading properties — never held beyond enumeration.
/// </summary>
internal static class DeviceSnapshot
{
    public static AudioDeviceInfo From(
        MMDevice device,
        bool isDefaultRender,
        bool isDefaultComm)
    {
        var state = device.State switch
        {
            DeviceState.Active => AudioDeviceState.Active,
            DeviceState.Unplugged => AudioDeviceState.NotPluggedIn,
            _ => AudioDeviceState.NotPresent,
        };

        // Friendly name can throw on ghosted devices; never fail enumeration.
        string name;
        try { name = device.FriendlyName; }
        catch (COMException) { name = "(unknown device)"; }

        return new AudioDeviceInfo(device.ID, name, MapFlow(device.DataFlow), state, isDefaultRender, isDefaultComm);
    }

    private static AudioDataFlow MapFlow(DataFlow flow) =>
        flow == DataFlow.Capture ? AudioDataFlow.Capture : AudioDataFlow.Render;
}
