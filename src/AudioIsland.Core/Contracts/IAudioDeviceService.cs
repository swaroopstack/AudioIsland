using AudioIsland.Core.Devices;

namespace AudioIsland.Core.Contracts;

/// <summary>
/// Enumerates audio endpoints and raises change notifications.
/// Implementations must be safe to call from any thread; events are raised
/// on a background (COM notification) thread and consumers must marshal.
/// </summary>
public interface IAudioDeviceService : IDisposable
{
    /// <summary>Snapshot of endpoints for the given flow. Devices are sorted: active defaults first, then active, then others.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDataFlow flow);

    /// <summary>Returns the endpoint ID Windows currently uses as default for the flow, or null if none.</summary>
    string? GetDefaultDeviceId(AudioDataFlow flow, bool communicationsRole = false);

    /// <summary>
    /// Raised when a device arrives, is removed, its state changes,
    /// or the default device changes. Full re-enumeration is cheap;
    /// consumers may just refresh their snapshot.
    /// </summary>
    event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;
}

/// <summary>Event payload for <see cref="IAudioDeviceService.DevicesChanged"/>.</summary>
public sealed class AudioDevicesChangedEventArgs(AudioDataFlow flow) : EventArgs
{
    public AudioDataFlow Flow { get; } = flow;
}
