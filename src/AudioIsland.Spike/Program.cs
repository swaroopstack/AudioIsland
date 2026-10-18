using AudioIsland.Audio.Loopback;
using AudioIsland.Core.Sessions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wasapi;

// Session 1 spike: per-process loopback capture (the routing foundation).
//
// Modes:
//   dotnet run                     -> self-test: spawn a tone child, capture it
//   dotnet run -- step0            -> Session 2 gate: is the tap pre- or post-mute?
//   dotnet run -- tone <seconds>   -> child mode: play a 440Hz sine (used by self-test)
//   dotnet run -- <proc> <seconds> -> capture a real named process (e.g. brave 12)

if (args.Length > 0 && args[0] == "tone")
{
    PlayTone(args.Length > 1 ? int.Parse(args[1]) : 10);
    return;
}

if (args.Length > 0 && (args[0] == "step0" || args[0] == "--step0"))
{
    Environment.ExitCode = await RunStep0GateAsync();
    return;
}

if (args.Length > 0 && args[0] != "--selftest")
{
    await CaptureNamedProcessAsync(args[0], args.Length > 1 ? int.Parse(args[1]) : 12);
    return;
}

// --- Self-test: deterministic proof of process-loopback capture ---
Console.WriteLine("=== AudioIsland process-loopback self-test ===");
Console.WriteLine("Spawning a child that plays a 440Hz sine at -6 dBFS...");

var child = SpawnToneChild();

// Wait for the child to open a render stream (its session becomes active).
var sessions = new AudioIsland.Audio.Sessions.AudioSessionService();
bool active = WaitForChildSession(sessions, child.Id, TimeSpan.FromSeconds(10)) is not null;
Console.WriteLine(active
    ? "  child has an ACTIVE audio session — good."
    : "  WARNING: child session not detected (may still be starting).");

// CONTROL: device-level loopback (NAudio WasapiLoopbackCapture) — a fully
// independent, known-good code path. It proves the tone is really in the
// engine's mix, so any process-loopback failure is ours, not the machine's.
Console.WriteLine("\n--- CONTROL: device-level WasapiLoopbackCapture (3s) ---");
{
    long devFrames = 0; double devPeak = 0; int bytesPerFrame = 4;
    using var devCapture = new WasapiLoopbackCapture();
    bytesPerFrame = devCapture.WaveFormat.Channels * devCapture.WaveFormat.BitsPerSample / 8;
    devCapture.DataAvailable += (_, e) =>
    {
        devFrames += e.BytesRecorded / bytesPerFrame;
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
        {
            float s = BitConverter.ToSingle(e.Buffer, i);
            if (Math.Abs(s) > devPeak) devPeak = Math.Abs(s);
        }
    };
    devCapture.StartRecording();
    Thread.Sleep(3000);
    devCapture.StopRecording();
    Console.WriteLine($"  device loopback frames={devFrames} peak={devPeak:F4} fmt={devCapture.WaveFormat.SampleRate}Hz {devCapture.WaveFormat.Channels}ch");
    Console.WriteLine(devFrames > 100_000
        ? "  -> tone IS reaching the engine (device mix format above is what we hand the VAD)."
        : "  -> tone is NOT reaching the engine. (Spike/device problem, not our capture.)");
}

// Both PROCESS_LOOPBACK_MODE directions, so the feasibility report can state
// that per-app targeting (split) AND per-app exclusion both work.
var combos = new (string Name, CaptureOptions Opts, int TargetPid)[]
{
    ("INCLUDE child process tree (per-app split — the feature)",
        CaptureOptions.Default, child.Id),
    ("EXCLUDE System/pid 4 (captures every app incl. the tone)",
        CaptureOptions.Default with { LoopbackMode = 1 }, 4),
};

var results = new List<(string Name, CaptureResult Result)>();
foreach (var (name, opts, targetPid) in combos)
{
    Console.WriteLine($"\n--- combo: {name} [target pid {targetPid}] ---");
    results.Add((name, await CaptureAsync(targetPid, TimeSpan.FromSeconds(4), opts)));
}

try { child.Kill(); } catch { /* may have exited */ }
sessions.Dispose();

Console.WriteLine();
Console.WriteLine("=== VERDICT ===");
foreach (var (name, r) in results)
    Console.WriteLine($"  {(r.Frames > 50_000 ? "PASS" : "FAIL"),-4}  {r.Frames,7} frames  peak {r.Peak:F4} ({r.PeakDb:F1} dBFS)  {name}");
Console.WriteLine(results.All(r => r.Result.Frames > 50_000)
    ? "VERDICT: process-loopback capture WORKS on this OS. Per-app split is feasible."
    : "VERDICT: at least one mode captured no audio.");

static async Task<int> CaptureNamedProcessAsync(string processName, int seconds)
{
    var procs = System.Diagnostics.Process.GetProcessesByName(processName);
    if (procs.Length == 0)
    {
        Console.WriteLine($"No running process named '{processName}'. Start it first.");
        return 0;
    }
    int pid = procs[0].Id;
    Console.WriteLine($"Target: {processName}.exe (pid {pid})");
    Console.WriteLine($"PLAY AUDIO IN {processName.ToUpper()} NOW — capturing {seconds}s of only that app...");
    var result = await CaptureAsync(pid, TimeSpan.FromSeconds(seconds));
    Console.WriteLine();
    Console.WriteLine("=== RESULT ===");
    Console.WriteLine(result.Frames > 0
        ? $"VERDICT: process-loopback capture WORKS ({result.Frames} frames, peak {result.Peak:F4} / {result.PeakDb:F1} dBFS)."
        : "VERDICT: no frames captured — play audio in the target app and retry.");
    return 0;
}

/// <summary>
/// Opens a process-loopback capture on <paramref name="pid"/> for
/// <paramref name="duration"/>, reading every packet it delivers, and
/// reports the total frame count plus the loudest sample observed.
/// </summary>
static async Task<CaptureResult> CaptureAsync(int pid, TimeSpan duration, CaptureOptions? options = null)
{
    long totalFrames = 0;
    int packets = 0;
    double peak = 0;

    await Task.Run(() =>
    {
        using var capture = new ProcessLoopbackCapture(pid, options);
        capture.Start();
        bool eventDriven = options is not null
            && (options.Flags & AudioClientStreamFlags.EventCallback) != 0;
        Console.WriteLine($"Capture ready. Format: {capture.MixFormat.SampleRate}Hz " +
            $"{capture.MixFormat.Channels}ch {capture.MixFormat.BitsPerSample}-bit {capture.MixFormat.Encoding}" +
            (eventDriven ? " [event-driven]" : " [polling]"));
        Console.WriteLine("  DIAG: " + capture.Diagnose(2000).Replace("\n", "\n  DIAG: "));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool announcedFirstFrame = false;
        while (sw.Elapsed < duration)
        {
            // Event-driven clients block on the engine's packet-ready signal;
            // polling clients ask directly. Both drain via the same TryRead.
            if (capture.TryRead(out var data, out uint frames, eventDriven ? 50 : 0))
            {
                packets++;
                totalFrames += frames;
                foreach (var s in data) peak = Math.Max(peak, Math.Abs(s));
                if (!announcedFirstFrame)
                {
                    announcedFirstFrame = true;
                    Console.WriteLine($"  first audio received after {sw.ElapsedMilliseconds}ms");
                }
            }
            else
            {
                Thread.Sleep(10);
            }
        }
        capture.Stop();
    });

    double peakDb = 20 * Math.Log10(Math.Max(peak, 1e-9));
    Console.WriteLine($"Packets read:    {packets}");
    Console.WriteLine($"Frames captured: {totalFrames}");
    Console.WriteLine($"Peak level:      {peak:F4} ({peakDb:F1} dBFS)");
    return new CaptureResult((int)Math.Min(int.MaxValue, totalFrames), peak);
}

/// <summary>
/// Re-execs this program as a child that plays a 440Hz sine for 60s, and
/// returns the live Process. Under `dotnet run` we are the dotnet host, so
/// the child has to be told which assembly to exec.
/// </summary>
static System.Diagnostics.Process SpawnToneChild()
{
    var exe = Environment.ProcessPath!;
    bool isDotnetHost = exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    var child = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = isDotnetHost
                ? $"exec \"{System.Reflection.Assembly.GetExecutingAssembly().Location}\" tone 60"
                : "tone 60",
            UseShellExecute = false,
        },
    };
    child.Start();
    Console.WriteLine($"  child pid {child.Id} ({(isDotnetHost ? "dotnet exec" : "apphost")})");
    return child;
}

/// <summary>
/// Waits for the child's audio session to exist and become active (i.e. it
/// opened a render stream), falling back to any session on that pid. Null
/// when the child never registers one within <paramref name="timeout"/>.
/// </summary>
static AudioSessionInfo? WaitForChildSession(
    AudioIsland.Audio.Sessions.AudioSessionService sessions, int pid, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var active = sessions.GetSessions().FirstOrDefault(s => s.ProcessId == pid && s.IsActive);
        if (active is not null) return active;
        Thread.Sleep(250);
    }
    return sessions.GetSessions().FirstOrDefault(s => s.ProcessId == pid);
}

/// <summary>
/// Prints every audio session whose pid (or SessionId tail) matches, plus the
/// total session count. A gate reading is worthless if the mute landed on a
/// different session than the one being read back, so make both visible.
/// </summary>
static void DumpSessionsForPid(
    AudioIsland.Audio.Sessions.AudioSessionService sessions, int pid, string label)
{
    var all = sessions.GetSessions();
    var hits = all.Where(s => s.ProcessId == pid).ToList();
    Console.WriteLine($"  [{label}] {all.Count} sessions total, {hits.Count} on pid {pid}");
    foreach (var s in hits)
        Console.WriteLine($"    pid={s.ProcessId} vol={s.Volume:F2} muted={s.IsMuted} active={s.IsActive} " +
            $"proc={s.ProcessName} id={Tail(s.SessionId, 44)}");
}

/// <summary>Long identifiers shortened for logging; never throws on short input.</summary>
static string Tail(string value, int length) =>
    value.Length > length ? "..." + value[^length..] : value;

/// <summary>
/// SESSION 2 GATE — is the process-loopback tap read before or after the
/// source app's own session volume/mute?
///
/// Process loopback is a TAP, not a redirect: the app keeps playing on its
/// normal device while we also copy its audio. To truly MOVE an app to
/// another endpoint we must mute the original session (else it plays twice,
/// once on each device). That only works if our tap reads the engine mix
/// BEFORE the per-session volume is applied — otherwise mutting the source
/// would silence us too, leaving nothing to render.
///
/// Design: spawn a child playing a known -6 dBFS tone, then capture it in
/// three phases — unmuted, muted, unmuted again. Phase C proves any drop was
/// caused by the mute and not by the child exiting.
///
/// Exit codes: 0 = gate passed (tap is pre-mute, split as designed works),
/// 1 = inconclusive, 2 = gate failed (tap is post-mute, redesign needed).
/// </summary>
static async Task<int> RunStep0GateAsync()
{
    const double Loud = 0.05;   // tone is 0.5000; anything >= this means it still arrives
    const double Quiet = 0.01;  // at or below this, the tap is effectively silent

    Console.WriteLine("=== STEP 0 GATE: session-mute x loopback-tap semantics ===");
    Console.WriteLine("Loopback is a TAP, so moving an app means capturing it AND muting");
    Console.WriteLine("its original session. This asks whether that mute kills our tap too.");
    Console.WriteLine();
    Console.WriteLine($"  phase B still >= {Quiet:F2} -> PRE-mute  -> capture+mute+render WORKS.  PASS");
    Console.WriteLine($"  phase B silent            -> POST-mute -> capture+mute+render = silence. FAIL");
    Console.WriteLine();

    var sessions = new AudioIsland.Audio.Sessions.AudioSessionService();
    System.Diagnostics.Process? child = null;
    try
    {
        Console.WriteLine("Spawning a child that plays a 440Hz sine at -6 dBFS...");
        child = SpawnToneChild();

        var childSession = WaitForChildSession(sessions, child.Id, TimeSpan.FromSeconds(10));
        if (childSession is null)
        {
            Console.WriteLine("FATAL: the child never registered an audio session — gate cannot run.");
            return 1;
        }
        Console.WriteLine($"  session id: {childSession.SessionId}");
        Console.WriteLine($"  state: volume={childSession.Volume:F2} muted={childSession.IsMuted} active={childSession.IsActive}");
        string sessionId = childSession.SessionId;

        Console.WriteLine("\n--- phase A: session UNMUTED (baseline) ---");
        DumpSessionsForPid(sessions, child.Id, "before phase A");
        var a = await CaptureAsync(child.Id, TimeSpan.FromSeconds(4));

        Console.WriteLine("\n--- phase B: session MUTED (the gate) ---");
        sessions.SetSessionMuted(sessionId, true);
        Thread.Sleep(400);
        // Read back by SessionId — the same key SetSessionMuted used — because a
        // pid lookup can land on a different session if the pid owns more than one.
        var bState = sessions.GetSessions().FirstOrDefault(s => s.SessionId == sessionId);
        Console.WriteLine($"  read-back by SessionId: muted={bState?.IsMuted} active={bState?.IsActive} " +
            $"volume={bState?.Volume:F2} found={bState is not null}");
        DumpSessionsForPid(sessions, child.Id, "after muting");
        var b = await CaptureAsync(child.Id, TimeSpan.FromSeconds(4));

        Console.WriteLine("\n--- phase C: session UNMUTED again (recovery control) ---");
        sessions.SetSessionMuted(sessionId, false);
        Thread.Sleep(400);
        var cState = sessions.GetSessions().FirstOrDefault(s => s.SessionId == sessionId);
        Console.WriteLine($"  read-back by SessionId: muted={cState?.IsMuted} active={cState?.IsActive} " +
            $"volume={cState?.Volume:F2} found={cState is not null}");
        DumpSessionsForPid(sessions, child.Id, "after unmuting");
        var c = await CaptureAsync(child.Id, TimeSpan.FromSeconds(4));

        Console.WriteLine();
        Console.WriteLine("=== STEP 0 VERDICT ===");
        Console.WriteLine($"  A  unmuted  {a.Frames,7} frames  peak {a.Peak:F4} ({a.PeakDb:F1} dBFS)");
        Console.WriteLine($"  B   MUTED   {b.Frames,7} frames  peak {b.Peak:F4} ({b.PeakDb:F1} dBFS)");
        Console.WriteLine($"  C  unmuted  {c.Frames,7} frames  peak {c.Peak:F4} ({c.PeakDb:F1} dBFS)");

        if (a.Peak < Loud)
        {
            Console.WriteLine($"\nINCONCLUSIVE: baseline phase A only reached {a.Peak:F4} — the tone never");
            Console.WriteLine("arrived, so the muted phase proves nothing. Re-run the gate.");
            return 1;
        }
        if (bState?.IsMuted != true)
        {
            Console.WriteLine("\nINCONCLUSIVE: the mute did not stick on the child's session. Re-run the gate.");
            return 1;
        }
        if (b.Peak >= Loud)
        {
            Console.WriteLine("\nGATE PASSED: muting the source session did NOT silence the tap.");
            Console.WriteLine("  -> the process-loopback tap is PRE-mute: it reads the engine mix");
            Console.WriteLine("     before per-session volume/mute is applied.");
            Console.WriteLine("  -> capture + mute source + render is the correct split design.");
            Console.WriteLine("  -> split mode builds as planned. Proceeding to Session 2 commits.");
            return 0;
        }
        if (b.Peak <= Quiet && c.Peak >= Loud)
        {
            Console.WriteLine("\nGATE FAILED: muting the source session silenced the tap, and the tone");
            Console.WriteLine("came back the moment it was unmuted.");
            Console.WriteLine("  -> the process-loopback tap is POST-mute: it reads after per-session");
            Console.WriteLine("     volume, so capture+mute+render would produce silence.");
            Console.WriteLine("  -> split must be redesigned before any routing code is written.");
            return 2;
        }
        Console.WriteLine($"\nINCONCLUSIVE: phase B peak {b.Peak:F4} fell in the ambiguous band");
        Console.WriteLine($"(between {Quiet:F2} and {Loud:F2}), or phase C failed to recover. Re-run the gate.");
        return 1;
    }
    finally
    {
        if (child is not null)
        {
            try { if (!child.HasExited) child.Kill(); } catch { /* already gone */ }
            child.Dispose();
        }
        sessions.Dispose();
    }
}

static void PlayTone(int seconds)
{
    var sine = new SineProvider();
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
    output.Init(sine);
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    output.Stop();
}

/// <summary>48kHz stereo float32, 440Hz sine at 0.5 amplitude (-6 dBFS).</summary>
sealed class SineProvider : ISampleProvider
{
    private double _phase;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i += 2)
        {
            float v = (float)(0.5 * Math.Sin(_phase));
            _phase += 2 * Math.PI * 440 / WaveFormat.SampleRate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            buffer[offset + i] = v;
            buffer[offset + i + 1] = v;
        }
        return count;
    }
}

/// <summary>What one capture run produced: total frames and the loudest sample.</summary>
/// <param name="Frames">Frames read across every packet; 0 means nothing arrived.</param>
/// <param name="Peak">Absolute peak sample value (0.5 == -6.0 dBFS).</param>
record CaptureResult(int Frames, double Peak)
{
    public double PeakDb => 20 * Math.Log10(Math.Max(Peak, 1e-9));
}
