using AudioIsland.Audio.Loopback;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wasapi;

// Session 1 spike: per-process loopback capture (the routing foundation).
//
// Modes:
//   dotnet run                     -> self-test: spawn a tone child, capture it
//   dotnet run -- tone <seconds>   -> child mode: play a 440Hz sine (used by self-test)
//   dotnet run -- <proc> <seconds> -> capture a real named process (e.g. brave 12)

if (args.Length > 0 && args[0] == "tone")
{
    PlayTone(args.Length > 1 ? int.Parse(args[1]) : 10);
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

var exe = Environment.ProcessPath!;
bool isDotnetHost = exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);
var child = new System.Diagnostics.Process
{
    StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        // Under `dotnet run`, ProcessPath is dotnet.exe — re-exec the assembly.
        FileName = exe,
        Arguments = isDotnetHost
            ? $"exec \"{System.Reflection.Assembly.GetExecutingAssembly().Location}\" tone 60"
            : "tone 60",
        UseShellExecute = false,
    },
};
child.Start();
Console.WriteLine($"  child pid {child.Id} ({(isDotnetHost ? "dotnet exec" : "apphost")})");

// Wait for the child to open a render stream (its session becomes active).
var sessions = new AudioIsland.Audio.Sessions.AudioSessionService();
bool active = false;
for (int i = 0; i < 40 && !active; i++)
{
    Thread.Sleep(250);
    active = sessions.GetSessions().Any(s => s.ProcessId == child.Id && s.IsActive);
}
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

var results = new List<(string Name, int Frames)>();
foreach (var (name, opts, targetPid) in combos)
{
    Console.WriteLine($"\n--- combo: {name} [target pid {targetPid}] ---");
    results.Add((name, await CaptureAsync(targetPid, TimeSpan.FromSeconds(4), opts)));
}

try { child.Kill(); } catch { /* may have exited */ }
sessions.Dispose();

Console.WriteLine();
Console.WriteLine("=== VERDICT ===");
foreach (var (name, frames) in results)
    Console.WriteLine($"  {(frames > 50_000 ? "PASS" : "FAIL"),-4}  {frames,7} frames  {name}");
Console.WriteLine(results.All(r => r.Frames > 50_000)
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
    int frames = await CaptureAsync(pid, TimeSpan.FromSeconds(seconds));
    Console.WriteLine();
    Console.WriteLine("=== RESULT ===");
    Console.WriteLine(frames > 0
        ? $"VERDICT: process-loopback capture WORKS ({frames} frames captured)."
        : "VERDICT: no frames captured — play audio in the target app and retry.");
    return 0;
}

static async Task<int> CaptureAsync(int pid, TimeSpan duration, CaptureOptions? options = null)
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
    return (int)Math.Min(int.MaxValue, totalFrames);
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
