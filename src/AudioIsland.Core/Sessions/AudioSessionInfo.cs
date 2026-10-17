namespace AudioIsland.Core.Sessions;

/// <summary>
/// Immutable snapshot of one application audio session.
/// </summary>
public sealed record AudioSessionInfo(
    /// <summary>IAudioSessionControl2.SessionIdentifier — stable per app install+sid+session, reusable for persistence cautiously.</summary>
    string SessionId,
    /// <summary>Process ID owning the session; 0 if unknown.</summary>
    int ProcessId,
    /// <summary>Display name Windows provides (may be empty — consumer falls back to process name).</summary>
    string DisplayName,
    /// <summary>Process name without extension, e.g. "chrome". Empty if process exited.</summary>
    string ProcessName,
    /// <summary>Normalized session volume 0..1.</summary>
    float Volume,
    bool IsMuted,
    /// <summary>True when the session is actively rendering audio.</summary>
    bool IsActive)
{
    /// <summary>Best display name: session display name, else process name, else PID.</summary>
    public string DisplayNameOrFallback =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName
        : $"PID {ProcessId}";
}
