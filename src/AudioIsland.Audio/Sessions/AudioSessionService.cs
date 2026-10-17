using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioIsland.Core.Contracts;
using AudioIsland.Core.Sessions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioIsland.Audio.Sessions;

/// <summary>
/// Per-application audio sessions on the current default render endpoint.
/// Enumeration is snapshot-based; COM objects never leave GetSessions().
/// Session identity: IAudioSessionControl.GetSessionIdentifier is stable
/// across app restarts (per install+SID) — persistable with validation,
/// unlike PID. Note: NAudio 2.2.1 netstandard exposes AudioSessionControl
/// (not AudioSessionControl2); PID/SessionIdentifier are still reachable.
/// </summary>
public sealed class AudioSessionService : IAudioSessionService
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private readonly System.Timers.Timer _debounce = new(150) { AutoReset = false };

    private AudioSessionManager? _manager;

    public AudioSessionService()
    {
        _debounce.Elapsed += (_, _) => SessionsChanged?.Invoke(this, EventArgs.Empty);
        BindToDefault();
    }

    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        lock (_gate)
        {
            var result = new List<AudioSessionInfo>();
            if (_manager is null) return result;

            // Sessions come and go between calls; refresh before each snapshot.
            _manager.RefreshSessions();
            var sessions = _manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? control = null;
                try
                {
                    control = sessions[i];
                    result.Add(ToInfo(control));
                }
                catch (COMException)
                {
                    // Session vanished mid-enumeration — skip.
                }
                finally
                {
                    control?.Dispose();
                }
            }
            return result;
        }
    }

    public void SetSessionVolume(string sessionId, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        WithSession(sessionId, s => s.SimpleAudioVolume.Volume = level);
    }

    public void SetSessionMuted(string sessionId, bool muted) =>
        WithSession(sessionId, s => s.SimpleAudioVolume.Mute = muted);

    public event EventHandler? SessionsChanged;

    private void WithSession(string sessionId, Action<AudioSessionControl> action)
    {
        lock (_gate)
        {
            if (_manager is null) return;
            // Refresh so a session created since the last snapshot is reachable.
            _manager.RefreshSessions();
            var sessions = _manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? control = null;
                try
                {
                    control = sessions[i];
                    if (SafeSessionId(control) != sessionId) continue;
                    action(control);
                    return; // First match; duplicates share sessionId and are
                            // addressed together by the routing layer later.
                }
                catch (COMException)
                {
                    // Session exited; caller sees no change and can refresh.
                }
                finally
                {
                    control?.Dispose();
                }
            }
        }
    }

    private static string SafeSessionId(AudioSessionControl control)
    {
        try { return control.GetSessionIdentifier ?? ""; }
        catch (COMException) { return ""; }
    }

    private static AudioSessionInfo ToInfo(AudioSessionControl control)
    {
        int pid = 0;
        try { pid = (int)control.GetProcessID; } catch (COMException) { }

        string processName = "";
        bool isActive = false;
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                processName = p.ProcessName;
            }
            catch
            {
                // Exited between enumeration and lookup, or protected process.
            }
        }

        try { isActive = control.State == AudioSessionState.AudioSessionStateActive; }
        catch (COMException) { }

        float volume = 0f;
        bool muted = false;
        try
        {
            volume = control.SimpleAudioVolume.Volume;
            muted = control.SimpleAudioVolume.Mute;
        }
        catch (COMException) { }

        string display = "";
        try { display = control.DisplayName ?? ""; } catch (COMException) { }

        return new AudioSessionInfo(SafeSessionId(control), pid, display, processName, volume, muted, isActive);
    }

    private void OnSessionCreated(object sender, IAudioSessionControl newSession)
    {
        // The manager owns this control's lifetime; only the raw COM
        // interface arrives here — dispose nothing, just signal.
        RequestRaise();
    }

    private void RequestRaise()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void BindToDefault()
    {
        lock (_gate)
        {
            try
            {
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                // MMDevice lazily creates and caches its AudioSessionManager.
                var manager = device.AudioSessionManager;
                manager.RefreshSessions(); // Sessions is null until primed.
                manager.OnSessionCreated += OnSessionCreated;
                _manager = manager;
            }
            catch (COMException)
            {
                // No active render endpoint; sessions unavailable until one appears.
                _manager = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_manager is not null)
            {
                try { _manager.OnSessionCreated -= OnSessionCreated; } catch (COMException) { }
                _manager.Dispose();
                _manager = null;
            }
        }
        _debounce.Dispose();
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
