using System.Runtime.InteropServices;
using NAudio.CoreAudioApi; // AudioClientBufferFlags

namespace AudioIsland.Audio.Loopback;

/// <summary>
/// Raw interop for per-process audio capture (Win10 2004+, NTDDI_WIN10_VB).
/// Contract taken verbatim from SDK headers audioclientactivationparams.h,
/// audioclient.h and mmdeviceapi.h. IAudioClient/IAudioCaptureClient are
/// hand-declared here because COM dispatches by vtable SLOT, not by name —
/// the declared method order must match the SDK header exactly.
/// </summary>
internal static class ProcessLoopbackInterop
{
    /// <summary>Virtual device for process-based loopback (SDK header constant).</summary>
    internal const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

    internal const string IID_IAudioCaptureClientStr = "C8ADBD64-E71E-48A0-A4DE-185C395CD317";

    // AUDIOCLIENT_ACTIVATION_TYPE (audioclientactivationparams.h)
    internal const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;

    // PROCESS_LOOPBACK_MODE (audioclientactivationparams.h)
    internal const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    internal const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;

    // AUDCLNT_STREAMFLAGS_* (audioclient.h)
    internal const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    internal const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    internal const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    internal const short VT_BLOB = 65;

    internal const string IID_IAudioClientStr = "1CB9AD4C-DBFA-4c32-B178-C2F568A703B2";

    /// <summary>
    /// IAudioClient, hand-declared so we control marshaling exactly.
    /// NAudio forces `ref Guid` for AudioSessionGuid, which means we can only
    /// ever pass &amp;GUID_NULL — but the MS sample and win-capture-audio both
    /// pass a true NULL. This declaration lets us pass IntPtr.Zero (= NULL).
    /// Full vtable in audioclient.h order (IUnknown + 12 methods).
    /// </summary>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags, long hnsBufferDuration,
            long hnsPeriodicity, [In] NAudio.Wave.WaveFormat pFormat,
            IntPtr audioSessionGuid);   // NULL = default session
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long streamLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode,
            [In] NAudio.Wave.WaveFormat format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod,
            out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(in Guid serviceGuid, out IntPtr serviceInterface);
    }

    // Exported from MMDevAPI.dll (verified via export-table scan on Win11 build 26200).
    // Signature per mmdeviceapi.h — the 5th parameter is a required out-param;
    // the returned operation holds an internal per-PID slot and MUST be released
    // after the client is acquired or Windows refuses re-activation for that PID
    // (confirmed by bozbez/win-capture-audio issue #14).
    [DllImport("MMDevAPI.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int ActivateAudioInterfaceAsync(
        string deviceInterfaceId,
        in Guid iid,
        IntPtr activationParams,   // PROPVARIANT*
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activateOperation);

    /// <summary>
    /// Callback passed to ActivateAudioInterfaceAsync (mmdeviceapi.h).
    /// Vtable: IUnknown + ActivateCompleted(IActivateAudioInterfaceAsyncOperation*).
    /// Declared without [ComImport] so our managed callback implements it (CCW).
    /// </summary>
    [ComVisible(true)]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(
            [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>
    /// Async operation handed to the completion handler (mmdeviceapi.h).
    /// Vtable: IUnknown + GetActivateResult(HRESULT*, IUnknown**).
    /// </summary>
    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.Interface)] out object? activatedInterface);
    }

    /// <summary>
    /// IAudioCaptureClient — the only hand-declared client interface
    /// (NAudio's copy is internal). CRITICAL: COM dispatches by vtable SLOT,
    /// not by name, so these must appear in the exact order of audioclient.h
    /// (IUnknown + GetBuffer, ReleaseBuffer, GetNextPacketSize). Getting this
    /// order wrong silently maps GetNextPacketSize onto ReleaseBuffer, which
    /// returns S_OK but never writes the packet size — so capture appears to
    /// work while always reporting zero packets.
    /// </summary>
    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr buffer, out int numFramesToRead,
            out AudioClientBufferFlags flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesToRead);
        [PreserveSig] int GetNextPacketSize(out uint nextPacketSize);
    }

    // --- structs from audioclientactivationparams.h (x64 layout) ---

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // PROCESS_LOOPBACK_MODE
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType; // AUDIOCLIENT_ACTIVATION_TYPE
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    /// <summary>
    /// Allocates a PROPVARIANT(VT_BLOB) wrapping AUDIOCLIENT_ACTIVATION_PARAMS.
    /// PROPVARIANT x64 layout: vt:u16 @0, reserved @2..8,
    /// blob.cbSize:u32 @8, blob.pBlobData:ptr @16. Total 24 bytes.
    /// Free with FreeActivationParamsBlob.
    /// </summary>
    internal static IntPtr AllocActivationParamsBlob(int targetProcessId, int loopbackMode)
    {
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)targetProcessId,
                ProcessLoopbackMode = loopbackMode,
            },
        };

        int blobSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blobData = Marshal.AllocHGlobal(blobSize);
        Marshal.StructureToPtr(activation, blobData, false);

        IntPtr pv = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
        Marshal.WriteInt16(pv, VT_BLOB);        // vt
        Marshal.WriteInt32(pv, 8, blobSize);    // blob.cbSize
        Marshal.WriteIntPtr(pv, 16, blobData);  // blob.pBlobData
        return pv;
    }

    internal static void FreeActivationParamsBlob(IntPtr propVariant)
    {
        if (propVariant == IntPtr.Zero) return;
        IntPtr blobData = Marshal.ReadIntPtr(propVariant, 16);
        if (blobData != IntPtr.Zero) Marshal.FreeHGlobal(blobData);
        Marshal.FreeHGlobal(propVariant);
    }
}
