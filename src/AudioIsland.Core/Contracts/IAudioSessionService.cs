using AudioIsland.Core.Sessions;

namespace AudioIsland.Core.Contracts;

/// <summary>
/// Enumerates per-application audio sessions and exposes per-session
/// volume/mute. Sessions appear/disappear as apps start and stop audio —
/// consumers must treat the list as live, not static.
/// </summary>
public interface IAudioSessionService : IDisposable
{
    /// <summary>Snapshot of all sessions on the current default render endpoint.</summary>
    IReadOnlyList<AudioSessionInfo> GetSessions();

    /// <summary>Set one session's volume 0..1. No-op if session exited.</summary>
    void SetSessionVolume(string sessionId, float level);

    /// <summary>Mute/unmute one session. No-op if session exited.</summary>
    void SetSessionMuted(string sessionId, bool muted);

    /// <summary>
    /// Raised when sessions are created/removed or their state changes
    /// (volume, mute, active). Coalesced.
    /// </summary>
    event EventHandler? SessionsChanged;
}
