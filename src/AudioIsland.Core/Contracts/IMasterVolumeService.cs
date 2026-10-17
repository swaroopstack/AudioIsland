using AudioIsland.Core.Devices;

namespace AudioIsland.Core.Contracts;

/// <summary>
/// Controls the system master volume + mute for a specific endpoint.
/// Two-way sync: <see cref="VolumeChanged"/> fires when Windows volume
/// changes externally (taskbar slider, media keys, other apps).
/// </summary>
public interface IMasterVolumeService : IDisposable
{
    /// <summary>Current level 0..1 and mute state of the default render endpoint.</summary>
    VolumeState GetState();

    /// <summary>Set master level 0..1. Clamped. Applied to Windows immediately.</summary>
    void SetLevel(float level);

    /// <summary>Set master mute. Applied to Windows immediately.</summary>
    void SetMuted(bool muted);

    /// <summary>
    /// Fired on any change (internal or external). Handlers must be idempotent:
    /// a set may echo back through this event via the COM notification.
    /// </summary>
    event EventHandler<VolumeState>? VolumeChanged;

    /// <summary>
    /// Fired when the underlying default endpoint changes, so the UI knows
    /// the service is now bound to a different device.
    /// </summary>
    event EventHandler<string?>? DefaultDeviceChanged;
}
