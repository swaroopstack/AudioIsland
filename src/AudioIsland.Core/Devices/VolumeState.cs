namespace AudioIsland.Core.Devices;

/// <summary>
/// Snapshot of an endpoint's volume/mute state. Normalized 0..1.
/// </summary>
public sealed record VolumeState(float Level, bool IsMuted)
{
    public static readonly VolumeState Silent = new(0f, false);
}
