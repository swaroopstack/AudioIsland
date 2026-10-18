using AudioIsland.Audio.Devices;
using AudioIsland.Audio.Loopback;
using AudioIsland.Audio.Policy;
using AudioIsland.Audio.Sessions;
using AudioIsland.Core.Devices;
using AudioIsland.Core.Sessions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wasapi;

// Session 1 spike: per-process loopback capture (the routing foundation).
//
// Modes:
//   dotnet run                     -> self-test: spawn a tone child, capture it
//   dotnet run -- step0            -> Session 2 gate: is the tap pre- or post-mute?
//   dotnet run -- perapp           -> Session 2 gate: can Windows move an app itself?
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

if (args.Length > 0 && (args[0] == "perapp" || args[0] == "--perapp"))
{
    Environment.ExitCode = await RunPerAppGateAsync();
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
static System.Diagnostics.Process SpawnToneChild(Action<string>? onLine = null)
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
            // When the caller wants to *read* what the child decided (the per-app
            // gate needs the child's own view of its default endpoint), capture
            // stdout instead of letting it interleave with ours.
            RedirectStandardOutput = onLine is not null,
        },
    };
    if (onLine is not null)
        child.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };

    child.Start();
    if (onLine is not null) child.BeginOutputReadLine();
    Console.WriteLine($"  child pid {child.Id} ({(isDotnetHost ? "dotnet exec" : "apphost")})");
    return child;
}

/// <summary>Blocks until the child printed a line containing <paramref name="needle"/> (case-insensitive).</summary>
static bool WaitForChildLine(List<string> lines, object gate, string needle, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        lock (gate)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        Thread.Sleep(150);
    }
    return false;
}

/// <summary>
/// Did Windows hand this process a different default? Two acceptable proofs,
/// because a target endpoint may not be present on the machine:
/// the child resolved the target id itself, or resolution that worked before we
/// set the override now fails — either way its answer changed because of us.
/// Requires that it did resolve the old default first, so a startup failure
/// cannot be mistaken for success.
/// </summary>
static bool ChildSawOverride(List<string> lines, object gate, string targetId, string defaultId)
{
    lock (gate)
    {
        bool sawOriginal = lines.Any(l =>
            l.Contains($"resolved default -> {defaultId}", StringComparison.OrdinalIgnoreCase));
        bool sawTarget = lines.Any(l =>
            l.Contains($"resolved default -> {targetId}", StringComparison.OrdinalIgnoreCase));
        bool sawBreak = sawOriginal && lines.Any(l =>
            l.Contains("resolve failed", StringComparison.OrdinalIgnoreCase));
        return sawTarget || sawBreak;
    }
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

/// <summary>
/// Which active render endpoints currently hold an audio session owned by
/// <paramref name="pid"/>. A move is only real if the *session* relocates —
/// Windows echoing our setting back proves the API accepted it, nothing more.
/// </summary>
static string DescribeSessionDevices(MMDeviceEnumerator enumerator, int pid)
{
    var hits = new List<string>();
    // MMDeviceCollection is not IDisposable in NAudio 2.2.1 — AudioDeviceService
    // treats it the same way: enumerate, read, let the enumerator own lifetime.
    var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    for (int i = 0; i < collection.Count; i++)
    {
        using var device = collection[i];
        bool found = false;
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int j = 0; j < sessions.Count && !found; j++)
            {
                AudioSessionControl? session = null;
                try { session = sessions[j]; found = (int)session.GetProcessID == pid; }
                catch (System.Runtime.InteropServices.COMException) { /* session vanished */ }
                finally { session?.Dispose(); }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            continue; // endpoint went away mid-scan
        }
        if (found) hits.Add(device.FriendlyName);
    }
    return hits.Count == 0 ? "(no session on any active render device)" : string.Join(" + ", hits);
}

/// <summary>
/// SESSION 2 GATE (redesign) — can Windows move an app to another endpoint for
/// us?
///
/// Step 0 proved the process-loopback tap is read AFTER the source session's
/// volume node, so "capture it and mute it" yields silence. This gate asks
/// whether <c>IAudioPolicyConfigFactory.SetPersistedDefaultAudioEndpoint</c> —
/// the API behind the Volume mixer and EarTrumpet's "Move to device" — can do
/// the move instead, and whether our capture still follows afterwards (mirror
/// mode depends on that).
///
/// Q3 is split in two on purpose: *the process's default changed* is the
/// mechanism, and *the session relocated* is the physical fact. They are only
/// the same observation when a second active output exists to move onto, which
/// this machine does not have — so a run can legitimately prove Q3a and be
/// unable to say anything about Q3b.
///
/// Exit codes: 0 all four questions pass; 1 could not run / factory inactive;
/// 2 API rejected the call; 3 accepted but the process never saw it; 4 it moved
/// but the loopback tap lost the audio; 5 mechanism proven but the end-to-end
/// move needs a second output that this machine does not have.
/// </summary>
static async Task<int> RunPerAppGateAsync()
{
    const int MinFrames = 50_000; // 4s of 48k stereo is ~192k; below this the app went quiet

    Console.WriteLine("=== PER-APP ENDPOINT GATE ===");
    Console.WriteLine("Step 0 showed we cannot move an app by capturing and muting it.");
    Console.WriteLine("This asks whether Windows can move it for us instead:");
    Console.WriteLine("  SetPersistedDefaultAudioEndpoint(pid, render, role, endpoint)");
    Console.WriteLine();
    Console.WriteLine("  Q1 does Windows.Media.Internal.AudioPolicyConfig activate, and with which IID?");
    Console.WriteLine("  Q2 does it accept a plain Win32 desktop pid? (read back what we wrote)");
    Console.WriteLine("  Q3a does Windows hand THAT process a different default? (the mechanism)");
    Console.WriteLine("  Q3b does the running session physically relocate?  (needs 2nd output)");
    Console.WriteLine("  Q4 does the loopback tap follow it? (mirroring depends on this)");
    Console.WriteLine();

    var deviceService = new AudioDeviceService();
    using var enumerator = new MMDeviceEnumerator();
    using var logger = new DefaultChangeLogger();
    var sessions = new AudioSessionService();
    AudioPolicyConfig? policy = null;
    System.Diagnostics.Process? child = null;

    try
    {
        // --- pick a target endpoint -----------------------------------------
        var renderDevices = deviceService.GetDevices(AudioDataFlow.Render);
        string defaultId = deviceService.GetDefaultDeviceId(AudioDataFlow.Render) ?? "";
        var active = renderDevices.Where(d => d.IsActive).ToList();
        Console.WriteLine($"Render endpoints: {renderDevices.Count} total, {active.Count} active.");
        foreach (var d in active)
            Console.WriteLine($"    [{(d.Id == defaultId ? "default" : "      ")}] {d.Name}");

        // Prefer a real second output so the whole chain is observable. This
        // machine has only one active render endpoint, so fall back to any other
        // registered render endpoint: that still proves whether Windows hands
        // *this process* a different default — the mechanism split depends on —
        // even though it cannot prove audio physically lands there.
        var target = active.FirstOrDefault(d => d.Id != defaultId);
        bool realMovePossible = target is not null;
        if (!realMovePossible)
            target = renderDevices.FirstOrDefault(d => d.Id != defaultId);

        if (target is null)
            Console.WriteLine("\nNOTE: no second render endpoint exists at all — nothing to target.");
        else if (realMovePossible)
            Console.WriteLine($"\nTarget endpoint: {target.Name} (active — full move observable)");
        else
            Console.WriteLine($"\nOnly one active output here. Using {target.Name} [{target.State}] " +
                "as a MECHANISM target only.");

        // --- spawn the app ---------------------------------------------------
        Console.WriteLine("\n--- spawn tone child (stdout captured) ---");
        var childLines = new List<string>();
        object linesGate = new();
        child = SpawnToneChild(line => { lock (linesGate) childLines.Add(line); });
        if (WaitForChildSession(sessions, child.Id, TimeSpan.FromSeconds(10)) is null)
        {
            Console.WriteLine("FATAL: child has no audio session on the default endpoint.");
            return 1;
        }
        Console.WriteLine($"  child pid {child.Id}");
        string targetId = target?.Id ?? defaultId;
        if (string.IsNullOrEmpty(targetId))
        {
            Console.WriteLine("FATAL: no render endpoint to target.");
            return 1;
        }
        if (string.Equals(targetId, defaultId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FATAL: target is the current default, so any result would be a tautology.");
            return 1;
        }

        // --- Q1 --------------------------------------------------------------
        Console.WriteLine("\n--- Q1: activate the policy-config factory ---");
        policy = AudioPolicyConfig.TryCreate(out string activationDetail);
        Console.WriteLine($"  {activationDetail}");
        if (policy is null)
        {
            Console.WriteLine("  Q1 FAIL: factory did not activate on this OS.");
            return 1;
        }
        Console.WriteLine($"  Q1: activated with IID {policy.ActivatedIid}");

        // --- Q2 --------------------------------------------------------------
        Console.WriteLine("\n--- Q2: SetPersistedDefaultAudioEndpoint ---");
        int hr = policy.SetRenderOverride(child.Id, targetId);
        Console.WriteLine($"  set hr=0x{hr:X8}{(hr < 0 ? " FAILED" : " (S_OK)")}");

        string? readBack = null;
        if (hr >= 0)
        {
            try { readBack = policy.GetRenderOverride(child.Id); }
            catch (Exception ex) { Console.WriteLine($"  get threw: {ex.GetType().Name}: {ex.Message}"); }
        }
        bool roundTrip = readBack is not null
            && string.Equals(readBack, targetId, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  read-back: {readBack ?? "(none)"}");
        Console.WriteLine(roundTrip
            ? "  Q2 PASS: Windows stored the override for this Win32 desktop pid."
            : "  Q2 FAIL: read-back did not match what we set.");

        // --- Q3a: the mechanism ---------------------------------------------
        // Windows expresses a per-app endpoint change by handing THAT process a
        // different default. The child polls GetDefaultAudioEndpoint itself, so
        // waiting on its own words is the direct measurement — not something we
        // can infer from our side of the boundary.
        Console.WriteLine("\n--- Q3a: does Windows hand THIS process a different default? ---");
        WaitForChildLine(childLines, linesGate,
            $"resolved default -> {targetId}", TimeSpan.FromSeconds(6));
        bool overrideHonored = ChildSawOverride(childLines, linesGate, targetId, defaultId);
        Console.WriteLine($"  child reported: {(overrideHonored ? "yes" : "not within 6s")}");
        Console.WriteLine($"  default-device callbacks seen: {logger.Describe()}");
        Console.WriteLine(overrideHonored
            ? "  Q3a PASS: the process's own default became the override — the mechanism holds."
            : "  Q3a FAIL: the process still resolves its old default, so nothing would move.");

        // The child's own words are the evidence, so show them rather than trust a bool.
        lock (linesGate)
            foreach (var line in childLines) Console.WriteLine($"  child| {line}");

        // --- Q3b: the physical move -----------------------------------------
        // Only meaningful when a second endpoint actually exists to move onto.
        Console.WriteLine("\n--- Q3b: does the running session relocate? ---");
        string before = DescribeSessionDevices(enumerator, child.Id);
        Console.WriteLine($"  before: {before}");
        string after = before;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            Thread.Sleep(300);
            after = DescribeSessionDevices(enumerator, child.Id);
            if (!string.Equals(after, before, StringComparison.Ordinal)) break;
        }
        Console.WriteLine($"  after:  {after}");

        bool sessionMoved = target is not null
            && !string.Equals(after, before, StringComparison.Ordinal)
            && after.Contains(target.Name, StringComparison.OrdinalIgnoreCase);
        if (realMovePossible)
            Console.WriteLine(sessionMoved
                ? "  Q3b PASS: the session relocated to the target endpoint."
                : "  Q3b FAIL: the session never left where it was.");
        else
            Console.WriteLine("  Q3b n/a: no second active output on this machine — not observable.");

        // --- Q4 --------------------------------------------------------------
        Console.WriteLine("\n--- Q4: does the process-loopback tap follow? ---");
        var tap = await CaptureAsync(child.Id, TimeSpan.FromSeconds(4));
        bool tapSurvived = tap.Frames >= MinFrames;
        Console.WriteLine(!sessionMoved
            ? "  (not yet meaningful: no observed move, so this re-confirms baseline capture)"
            : tapSurvived
                ? "  Q4 PASS: capture still follows the app after the move — mirroring holds."
                : "  Q4 FAIL: the tap lost the app after the move — mirroring would break.");

        // --- verdict ---------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== PER-APP GATE VERDICT ===");
        Console.WriteLine($"  Q1 activate              {(policy is null ? "FAIL" : "pass")}  {policy?.ActivatedIid.ToString() ?? activationDetail}");
        Console.WriteLine($"  Q2 accept Win32 pid      {(roundTrip ? "pass" : "FAIL")}  hr=0x{hr:X8} read-back={readBack ?? "(none)"}");
        Console.WriteLine($"  Q3a process default moves{(overrideHonored ? "pass" : "FAIL")}  target={targetId}");
        Console.WriteLine($"  Q3b session relocates    {(realMovePossible ? (sessionMoved ? "pass" : "FAIL") : "n/a ")}  {before}  ->  {after}");
        Console.WriteLine($"  Q4 tap follows           {(sessionMoved ? (tapSurvived ? "pass" : "FAIL") : "n/a ")}  {tap.Frames} frames peak {tap.Peak:F4} ({tap.PeakDb:F1} dBFS)");

        if (policy is null) return 1;
        if (!roundTrip) { Console.WriteLine("\n-> The API refused the call. Split mode has no mechanism."); return 2; }
        if (!overrideHonored)
        {
            Console.WriteLine("\n-> Windows stored the setting but this process never saw it.");
            Console.WriteLine("   Split would only work for apps that re-query, or after a restart.");
            return 3;
        }
        if (realMovePossible && !sessionMoved)
        {
            Console.WriteLine("\n-> The process resolved the new default but the session never relocated.");
            return 3;
        }
        if (sessionMoved && !tapSurvived)
        {
            Console.WriteLine("\n-> The app moved but our capture lost it; mirror mode would break.");
            return 4;
        }
        if (!realMovePossible)
        {
            Console.WriteLine("\n-> MECHANISM PROVEN: Windows honours the per-app override and tells the");
            Console.WriteLine("   process about it, so a re-querying app will move.");
            Console.WriteLine("-> END-TO-END MOVE UNVERIFIED: this machine has one active render");
            Console.WriteLine("   endpoint (Speakers); a second output is required to hear it land.");
            return 5;
        }
        Console.WriteLine("\n-> Split (native per-app endpoint) and mirror (tap + render) both hold.");
        return 0;
    }
    finally
    {
        // Always hand the endpoint back — parent and child are the same exe path,
        // so a leaked override would follow the next run of this spike.
        if (policy is not null && child is not null)
        {
            try
            {
                int hr = policy.ClearRenderOverride(child.Id);
                string? after = policy.GetRenderOverride(child.Id);
                Console.WriteLine($"\ncleanup: clear hr=0x{hr:X8}, read-back={(after ?? "(none)")}");
            }
            catch (Exception ex) { Console.WriteLine($"\ncleanup: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (child is not null)
        {
            try { if (!child.HasExited) child.Kill(); } catch { /* already gone */ }
            child.Dispose();
        }
        policy?.Dispose();
        sessions.Dispose();
        deviceService.Dispose();
    }
}

/// <summary>
/// Plays a 440Hz sine for <paramref name="seconds"/>, resolving the default
/// render endpoint instead of pinning one: every 250ms it asks Windows what its
/// default is now, and re-opens when that answer changes.
///
/// <para>Not cosmetic — this is how the per-app gate models a well-behaved app.
/// Windows expresses a per-app endpoint change by handing *that process* a
/// different default, so an app that never re-queries (games that cache their
/// <c>IMMDevice</c> forever) will never move however often we ask. Browsers and
/// media players do re-query, which is the only reason split mode can exist.</para>
///
/// <para>It prints two lines the gate needs kept apart: <c>RESOLVED DEFAULT
/// -&gt;</c> is what Windows told us our default is (the mechanism), while
/// <c>OPEN on</c> / <c>open failed</c> is whether a stream could actually be
/// attached to it (the physical move). When the chosen target endpoint is not
/// present on this machine, those deliberately disagree.</para>
/// </summary>
static void PlayTone(int seconds)
{
    using var enumerator = new MMDeviceEnumerator();

    object gate = new();
    MMDevice? device = null;
    WasapiOut? output = null;
    bool alive = true;
    string resolvedId = "";   // last default Windows handed this process
    string attemptedId = "";  // last default we already tried (don't hammer a failing one)

    void Open()
    {
        lock (gate)
        {
            if (!alive) return;
            MMDevice? staleDevice = null;
            WasapiOut? staleOutput = null;
            try
            {
                var next = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var nextOutput = new WasapiOut(next, AudioClientShareMode.Shared, false, 100);
                nextOutput.Init(new SineProvider());
                nextOutput.Play();
                staleDevice = device;
                staleOutput = output;
                device = next;
                output = nextOutput;
                Console.WriteLine($"[child] OPEN on {next.FriendlyName} id={next.ID}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[child] open failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Only released after a successful swap, so a failed open leaves
                // the still-audible stream untouched.
                try { staleOutput?.Stop(); staleOutput?.Dispose(); } catch { /* already gone */ }
                try { staleDevice?.Dispose(); } catch { /* already gone */ }
            }
        }
    }

    void Inspect()
    {
        string nowId;
        try
        {
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            nowId = current.ID;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // "No default endpoint" and "our override points at one that is not
            // present" must not look alike to the gate — if resolution now fails
            // where it used to succeed, the override was honoured after all.
            string error = $"RESOLVED DEFAULT -> <resolve failed 0x{ex.HResult:X8}>";
            bool firstSighting;
            lock (gate)
            {
                firstSighting = !string.Equals(error, resolvedId, StringComparison.Ordinal);
                if (firstSighting) resolvedId = error;
            }
            if (firstSighting) Console.WriteLine($"[child] {error}");
            return;
        }

        bool resolvedChanged, needsOpen;
        lock (gate)
        {
            resolvedChanged = !string.Equals(nowId, resolvedId, StringComparison.OrdinalIgnoreCase);
            needsOpen = !string.Equals(nowId, attemptedId, StringComparison.OrdinalIgnoreCase);
            if (resolvedChanged) resolvedId = nowId;
            if (needsOpen) attemptedId = nowId;
        }

        if (resolvedChanged) Console.WriteLine($"[child] RESOLVED DEFAULT -> {nowId}");
        if (needsOpen) Open();
    }

    Inspect();

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        Thread.Sleep(250);
        Inspect();
    }

    lock (gate)
    {
        alive = false;
        try { output?.Stop(); output?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
    }
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

/// <summary>
/// Records default-device callbacks so the per-app gate can report whether
/// Windows *tells* anyone about a per-app override change. An app that
/// re-queries the default but is never notified still will not move.
/// </summary>
sealed class DefaultChangeLogger : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<string> _events = new();
    private readonly object _gate = new();

    public DefaultChangeLogger() => _enumerator.RegisterEndpointNotificationCallback(this);

    public string Describe()
    {
        lock (_gate) return _events.Count == 0 ? "none" : string.Join(" | ", _events);
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render) return;
        string id = string.IsNullOrEmpty(defaultDeviceId)
            ? "(null)"
            : defaultDeviceId.Length > 46 ? "..." + defaultDeviceId[^46..] : defaultDeviceId;
        lock (_gate) _events.Add($"{role} -> {id}");
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string deviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (System.Runtime.InteropServices.COMException) { /* COM already torn down */ }
        _enumerator.Dispose();
    }
}
