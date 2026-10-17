using AudioIsland.Audio.Sessions;

// Session 1 spike: per-app session enumeration + volume/mute.
// Play any audio (YouTube/music app) while this runs.

using var sessions = new AudioSessionService();

Console.WriteLine("=== Audio sessions (play some audio while this runs) ===");
var list = sessions.GetSessions();
if (list.Count == 0)
{
    Console.WriteLine("  No sessions. Start audio playback and re-run.");
    return;
}

foreach (var s in list)
{
    Console.WriteLine($"  {s.DisplayNameOrFallback,-40} pid={s.ProcessId,-6} vol={s.Volume,6:P0} muted={s.IsMuted,-5} active={s.IsActive}");
}

// Pick first non-system, non-muted session and demonstrate volume control.
var target = list.FirstOrDefault(s => s.ProcessId > 0 && s.ProcessName is not ("AudioIsland.Spike" or "AudioIsland.App"));
if (target is null)
{
    Console.WriteLine("No controllable session found.");
    return;
}

Console.WriteLine();
Console.WriteLine($"Demo: muting '{target.DisplayNameOrFallback}'...");
sessions.SetSessionMuted(target.SessionId, true);
var after = sessions.GetSessions().First(x => x.SessionId == target.SessionId);
Console.WriteLine($"  -> muted={after.IsMuted}  (sound from that app should stop)");
Thread.Sleep(2000);
Console.WriteLine($"Demo: unmuting...");
sessions.SetSessionMuted(target.SessionId, false);
after = sessions.GetSessions().First(x => x.SessionId == target.SessionId);
Console.WriteLine($"  -> muted={after.IsMuted}  (sound should resume)");

Console.WriteLine();
Console.WriteLine("Demo: setting volume to 25% then back...");
sessions.SetSessionVolume(target.SessionId, 0.25f);
Thread.Sleep(1500);
sessions.SetSessionVolume(target.SessionId, target.Volume);
Console.WriteLine("Done.");
