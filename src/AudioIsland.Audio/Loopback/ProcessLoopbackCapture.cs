using System.Runtime.InteropServices;
using NAudio.CoreAudioApi; // AudioClientBufferFlags, AudioClientShareMode, AudioClientStreamFlags
using NAudio.Wave;

namespace AudioIsland.Audio.Loopback;

/// <summary>
/// Captures one application's audio via Windows process-loopback
/// (Win10 2004+, NTDDI_WIN10_VB). Uses the virtual VAD\Process_Loopback
/// device, so captured audio follows the app across device changes.
/// IAudioClient/IAudioCaptureClient are hand-declared against audioclient.h:
/// COM dispatches by vtable SLOT, so method order must match the SDK exactly.
/// One capture per target process; dispose to stop.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly IntPtr _propVariantPtr;
    private readonly TaskCompletionSource<object> _clientReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ActivateCallback _callback; // rooted: OS invokes it async

    private ProcessLoopbackInterop.IAudioClient? _client;
    private ProcessLoopbackInterop.IAudioCaptureClient? _capture;
    private bool _started;
    private ManualResetEvent? _sampleReady;
    private readonly CaptureOptions _options;

    /// <summary>WaveFormat of the captured audio (chosen format; engine converts).</summary>
    public WaveFormat MixFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    /// <summary>True when the capture client is initialized and ready to read.</summary>
    public bool IsReady => _capture is not null;

    /// <summary>
    /// Starts async activation for the given process (INCLUDE its whole
    /// process tree — catches browsers where audio happens in child procs).
    /// Throws InvalidOperationException if activation fails.
    /// </summary>
    public ProcessLoopbackCapture(int targetProcessId, CaptureOptions? options = null)
    {
        _options = options ?? CaptureOptions.Default;
        _propVariantPtr = ProcessLoopbackInterop.AllocActivationParamsBlob(
            targetProcessId, _options.LoopbackMode);

        _callback = new ActivateCallback(_clientReady);
        int hr = ProcessLoopbackInterop.ActivateAudioInterfaceAsync(
            ProcessLoopbackInterop.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            new Guid(ProcessLoopbackInterop.IID_IAudioClientStr),
            _propVariantPtr, _callback, out var asyncOp);
        if (hr != 0)
        {
            if (asyncOp is not null) Marshal.ReleaseComObject(asyncOp);
            throw new InvalidOperationException($"ActivateAudioInterfaceAsync failed: 0x{hr:X8}");
        }

        try
        {
            if (!_clientReady.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Process-loopback activation timed out.");

            object result = _clientReady.Task.Result;
            _client = AsAudioClient(result)
                ?? throw new InvalidOperationException("QI for IAudioClient failed on the activation result.");

            // Realize the client while the async operation is still alive, the
            // same shape as Microsoft's ApplicationLoopback sample and
            // win-capture-audio (both keep the operation alive across
            // Initialize/GetService before releasing it).
            InitializeCapture();
        }
        finally
        {
            // Release once we hold a fully-initialized client. The operation
            // holds an internal per-PID slot in the audio stack; leaking it
            // makes Windows refuse re-activation for this PID
            // (bozbez/win-capture-audio issue #14).
            Marshal.ReleaseComObject(asyncOp);
        }
    }

    private static ProcessLoopbackInterop.IAudioClient? AsAudioClient(object comObject)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(comObject);
        try
        {
            Guid iid = new(ProcessLoopbackInterop.IID_IAudioClientStr);
            int hr = Marshal.QueryInterface(unknown, ref iid, out IntPtr ppv);
            if (hr != 0 || ppv == IntPtr.Zero) return null;
            try
            {
                return (ProcessLoopbackInterop.IAudioClient)Marshal.GetObjectForIUnknown(ppv);
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private void InitializeCapture()
    {
        // The process-loopback VAD does NOT implement GetMixFormat or
        // IsFormatSupported (both return E_NOTIMPL — confirmed by Microsoft's
        // ApplicationLoopback sample issue #343 and Chromium's WASAPI code).
        // Like those implementations we pick a format ourselves and let the
        // engine convert via AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM.
        MixFormat = _options.Format;

        long bufferDuration = _options.BufferDuration; // in 100ns units
        var flags = _options.Flags;

        // AudioSessionGuid = NULL, exactly like Microsoft's ApplicationLoopback
        // sample and win-capture-audio. (NAudio's IAudioClient forces
        // `ref Guid`, which can only ever pass &GUID_NULL, not a true NULL.)
        int hr = _client!.Initialize(AudioClientShareMode.Shared, flags,
            bufferDuration, 0, MixFormat, IntPtr.Zero);
        if (hr != 0)
            throw new InvalidOperationException($"Initialize failed: 0x{hr:X8}");

        // Event-driven read support (the official sample's mode): bind an event
        // the engine signals when a buffer is ready.
        if ((flags & AudioClientStreamFlags.EventCallback) != 0)
        {
            _sampleReady = new ManualResetEvent(false);
            hr = _client.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0)
                throw new InvalidOperationException($"SetEventHandle failed: 0x{hr:X8}");
        }

        // IAudioCaptureClient via GetService (the 3-method COM interface is
        // declared in ProcessLoopbackInterop against audioclient.h's vtable).
        Guid captureIid = new(ProcessLoopbackInterop.IID_IAudioCaptureClientStr);
        hr = _client.GetService(captureIid, out IntPtr captureSvc);
        if (hr != 0 || captureSvc == IntPtr.Zero)
            throw new InvalidOperationException($"GetService(IAudioCaptureClient) failed: 0x{hr:X8}");
        try
        {
            _capture = (ProcessLoopbackInterop.IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureSvc);
        }
        finally
        {
            Marshal.Release(captureSvc);
        }
    }

    /// <summary>Begin capturing (mirrors IAudioClient::Start).</summary>
    public void Start()
    {
        if (_started) return;
        Marshal.ThrowExceptionForHR(_client!.Start());
        _started = true;
    }

    /// <summary>Stop capturing (mirrors IAudioClient::Stop).</summary>
    public void Stop()
    {
        if (!_started) return;
        try { _client?.Stop(); } catch (COMException) { /* device gone */ }
        _started = false;
    }

    /// <summary>
    /// Diagnostic: reports buffer size, and polls for raw packet data with
    /// HRESULTs, so we can see exactly where data stops flowing.
    /// </summary>
    public string Diagnose(int pollMs = 2000)
    {
        var sb = new System.Text.StringBuilder();
        if (_client is null || _capture is null) { sb.AppendLine("not initialized"); return sb.ToString(); }

        // Dump the activation params actually handed to the OS, so a layout
        // mismatch (wrong size/offsets => OS sees a different PID or mode)
        // is visible instead of silently capturing an empty process set.
        int paramSize = Marshal.SizeOf<ProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS>();
        int cbSize = Marshal.ReadInt32(_propVariantPtr, 8);
        IntPtr blobData = Marshal.ReadIntPtr(_propVariantPtr, 16);
        sb.AppendLine($"activation: vt={Marshal.ReadInt16(_propVariantPtr)} paramsSize={paramSize} cbSize={cbSize}");
        if (blobData != IntPtr.Zero && cbSize is > 0 and <= 64)
        {
            var blob = new byte[cbSize];
            Marshal.Copy(blobData, blob, 0, cbSize);
            sb.AppendLine($"blob[{cbSize}]: {BitConverter.ToString(blob)}");
        }

        _client.GetBufferSize(out uint bufFrames);
        sb.AppendLine($"GetBufferSize: {bufFrames} frames");

        // The VAD intentionally does not implement GetMixFormat/IsFormatSupported
        // (E_NOTIMPL — the same behavior Chromium's audio_low_latency_input_win.cc
        // documents). Seeing E_NOTIMPL here CONFIRMS we hold a real
        // process-loopback client rather than a normal endpoint client: if these
        // succeeded, our activation blob was ignored and we got a plain device.
        {
            string MixProbe()
            {
                int hr = _client.GetMixFormat(out IntPtr pFmt);
                return hr == 0 && pFmt != IntPtr.Zero
                    ? $"hr=0x{hr:X8} {Marshal.PtrToStructure<WaveFormat>(pFmt)}"
                    : $"hr=0x{hr:X8}";
            }
            string PadProbe()
            { int hr = _client.GetCurrentPadding(out uint pad); return $"hr=0x{hr:X8} pad={pad}"; }
            string LatProbe()
            { int hr = _client.GetStreamLatency(out long lat); return $"hr=0x{hr:X8} lat={lat}"; }
            string PerProbe()
            { int hr = _client.GetDevicePeriod(out long d, out long m); return $"hr=0x{hr:X8} default={d} min={m}"; }

            sb.AppendLine($"GetMixFormat: {MixProbe()}  (E_NOTIMPL => real VAD client)");
            sb.AppendLine($"GetCurrentPadding: {PadProbe()}");
            sb.AppendLine($"GetStreamLatency: {LatProbe()}");
            sb.AppendLine($"GetDevicePeriod: {PerProbe()}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int polls = 0, nonZeroPackets = 0;
        int lastSized = 0, firstHr = 0;
        uint lastPacket = 0;
        while (sw.ElapsedMilliseconds < pollMs)
        {
            int hr = _capture.GetNextPacketSize(out uint packetSize);
            polls++;
            if (firstHr == 0) firstHr = hr;
            lastPacket = packetSize;
            if (packetSize > 0) nonZeroPackets++;
            lastSized = (int)packetSize;
            Thread.Sleep(10);
        }
        sb.AppendLine($"GetNextPacketSize: {polls} polls, {nonZeroPackets} had data, firstHr=0x{firstHr:X8}, lastPacket={lastPacket}, lastSized={lastSized}");
        return sb.ToString();
    }

    /// <summary>
    /// Drains one packet of captured frames. Returns false when no data.
    /// data is float32 interleaved (length = frames * channels).
    /// The buffer is only valid until the next read — copy if kept.
    /// </summary>
    public bool TryRead(out float[] data, out uint frames, int waitMs = 0)
    {
        data = Array.Empty<float>();
        frames = 0;
        if (_capture is null) return false;

        // Event-driven mode (win-capture-audio / MS sample): block on the
        // engine's packet-ready signal instead of polling. waitMs<=0 polls.
        if (waitMs > 0 && _sampleReady is not null && !_sampleReady.WaitOne(waitMs))
            return false;

        int packetSizeResult = _capture.GetNextPacketSize(out uint packetSize);
        if (packetSizeResult != 0 || packetSize == 0) return false;

        int hr = _capture.GetBuffer(out IntPtr buffer, out int readFrames,
            out AudioClientBufferFlags flags, out _, out _);
        if (hr != 0 || readFrames <= 0) return false;

        // AUDCLNT_BUFFERFLAGS_SILENT: buffer pointer may be null/zero.
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        int sampleCount = checked(readFrames * MixFormat.Channels);
        data = new float[sampleCount];
        if (!silent && buffer != IntPtr.Zero)
            Marshal.Copy(buffer, data, 0, sampleCount);

        _capture.ReleaseBuffer(readFrames);
        frames = (uint)readFrames;
        return true;
    }

    public void Dispose()
    {
        Stop();
        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }
        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
        _sampleReady?.Dispose();
        _sampleReady = null;
        ProcessLoopbackInterop.FreeActivationParamsBlob(_propVariantPtr);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Completion handler per mmdeviceapi.h: the OS calls ActivateCompleted
    /// once; we pull the activated IUnknown via GetActivateResult.
    /// </summary>
    private sealed class ActivateCallback(TaskCompletionSource<object> ready)
        : ProcessLoopbackInterop.IActivateAudioInterfaceCompletionHandler
    {
        public int ActivateCompleted(ProcessLoopbackInterop.IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            int hr = activateOperation.GetActivateResult(out int activateResult, out object? activated);
            if (hr != 0)
            {
                ready.TrySetException(new InvalidOperationException($"GetActivateResult failed: 0x{hr:X8}"));
                return hr;
            }
            if (activateResult != 0)
            {
                ready.TrySetException(new InvalidOperationException($"Activation error 0x{activateResult:X8}"));
                return 0;
            }
            if (activated is null)
            {
                ready.TrySetException(new InvalidOperationException("Activation returned null interface."));
                return 0;
            }
            ready.TrySetResult(activated);
            return 0;
        }
    }
}

/// <summary>
/// Capture initialization knobs. Defaults are the configuration verified in
/// docs/feasibility/process-loopback.md.
/// </summary>
public sealed record CaptureOptions(
    WaveFormat Format,
    AudioClientStreamFlags Flags,
    long BufferDuration)
{
    public static CaptureOptions Default { get; } = new(
        WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
        AudioClientStreamFlags.Loopback
        | AudioClientStreamFlags.AutoConvertPcm
        | AudioClientStreamFlags.SrcDefaultQuality,
        100 * 10_000);

    /// <summary>
    /// PROCESS_LOOPBACK_MODE for the activation blob: 0 = include the target
    /// process tree (default), 1 = exclude it (capture every other app).
    /// </summary>
    public int LoopbackMode { get; init; } =
        ProcessLoopbackInterop.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
}
