namespace AudioIsland.Core.Devices;

/// <summary>Data flow direction of an audio endpoint.</summary>
public enum AudioDataFlow
{
    Render,
    Capture,
}

/// <summary>Availability state of an audio endpoint.</summary>
public enum AudioDeviceState
{
    /// <summary>Device is present and usable.</summary>
    Active,
    /// <summary>Device is present but not usable (e.g. unplugged jack).</summary>
    NotPluggedIn,
    /// <summary>Device is not present on the system.</summary>
    NotPresent,
}

/// <summary>
/// Immutable snapshot of a Windows audio endpoint.
/// Id is the IMMDevice endpoint ID string — stable across sessions for
/// physical devices, so safe to persist (unlike session GUIDs).
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDataFlow Flow,
    AudioDeviceState State,
    bool IsDefaultForRender,
    bool IsDefaultForCommunications)
{
    public bool IsActive => State == AudioDeviceState.Active;

    public override string ToString() => $"{Name} [{Flow}/{State}]";
}
