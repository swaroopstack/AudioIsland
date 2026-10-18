using System.Runtime.InteropServices;

namespace AudioIsland.Audio.Policy;

/// <summary>
/// Windows' own per-application default endpoint override — the mechanism behind
/// Settings → Sound → Volume mixer ("App volume and device preferences") and
/// behind EarTrumpet's "Move to device".
///
/// <para><b>Why this exists.</b> Step 0 proved the process-loopback tap reads
/// AFTER the source app's session volume (see
/// <c>docs/feasibility/tap-vs-session-volume.md</c>), so an app cannot be moved
/// by "capture it and mute it" — muting silences the capture too. This API moves
/// the app at the Windows level instead: no capture, no mute, no extra audio
/// path. Capture remains reserved for mirroring, where leaving the app on its
/// own device is the intent.</para>
///
/// <para><b>This is internal API.</b> It is not in the SDK headers; the contract
/// below was recovered from EarTrumpet's shipped interop
/// (<c>DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs</c>). It is
/// what the OS's own Volume mixer UI calls, but treat every detail here as
/// unverified on our target OSes until the gate experiment has run.</para>
///
/// <para><b>Slot arithmetic.</b> The factory is an <c>IInspectable</c>, so its
/// vtable starts 3 slots of <c>IUnknown</c> + 3 slots of <c>IInspectable</c>
/// (<c>GetIids</c>, <c>GetRuntimeClassName</c>, <c>GetTrustLevel</c>). EarTrumpet
/// then declares 19 filler methods before the three real ones. We dispatch
/// through raw vtable slots rather than a <c>[ComImport]</c> declaration so the
/// arithmetic is explicit and checkable here instead of being implicit in how
/// the CLR expands <c>InterfaceIsIInspectable</c>.</para>
/// </summary>
public sealed class AudioPolicyConfig : IDisposable
{
    private const string ActivationClass = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>EarTrumpet's fillers, in order: 11 context/ringer/volume-group, 6 chat, 2 chat events.</summary>
    private const int FillerMethodCount = 19;

    private const int VtableSlotsUnknown = 3;
    private const int VtableSlotsInspectable = 3;
    private const int SlotSetPersisted = VtableSlotsUnknown + VtableSlotsInspectable + FillerMethodCount; // 25
    private const int SlotGetPersisted = SlotSetPersisted + 1;   // 26
    private const int SlotClearAll = SlotSetPersisted + 2;       // 27

    /// <summary>IID for 21H2 and later (Windows 11, and Win10 21H2 per EarTrumpet's OS check).</summary>
    public static readonly Guid Iid21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");

    /// <summary>IID for downlevel builds.</summary>
    public static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    // EDataFlow / ERole — shared with MMDevice, values are stable ABI.
    private const int FlowRender = 0;
    private const int RoleMultimedia = 0;
    private const int RoleConsole = 1;

    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";
    private const string DevInterfaceRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private IntPtr _factory;
    private bool _disposed;

    /// <summary>The IID that actually activated — either one may win, per OS build.</summary>
    public Guid ActivatedIid { get; }

    private AudioPolicyConfig(IntPtr factory, Guid iid)
    {
        _factory = factory;
        ActivatedIid = iid;
    }

    /// <summary>
    /// Activates the factory, trying every candidate IID. Both IIDs share one
    /// method layout, so which one resolves is purely an OS-version question —
    /// probing instead of branching on build numbers keeps this correct on
    /// Windows 10 22H2 and Windows 11 alike.
    /// </summary>
    public static AudioPolicyConfig? TryCreate(out string detail)
    {
        var log = new List<string>();
        IntPtr className = CreateHString(ActivationClass);
        try
        {
            foreach (var iid in new[] { Iid21H2, IidDownlevel })
            {
                Guid g = iid;
                int hr = RoGetActivationFactory(className, ref g, out IntPtr factory);
                log.Add($"{iid}: hr=0x{hr:X8}");
                if (hr >= 0 && factory != IntPtr.Zero)
                {
                    detail = string.Join("; ", log);
                    return new AudioPolicyConfig(factory, iid);
                }
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }
        finally
        {
            FreeHString(className);
        }
        detail = string.Join("; ", log);
        return null;
    }

    /// <summary>
    /// Points <paramref name="processId"/>'s default render endpoint at
    /// <paramref name="mmDeviceId"/> (an <c>IMMDevice.ID</c>, e.g.
    /// <c>{0.0.0.00000000}.{guid}</c>). The setting is persisted by Windows,
    /// keyed off the process at the time of the call.
    /// </summary>
    /// <returns>Win32 HRESULT; 0 (S_OK) means accepted.</returns>
    public int SetRenderOverride(int processId, string mmDeviceId)
    {
        IntPtr h = CreateHString(PackDeviceId(mmDeviceId));
        try { return SetBothRoles(processId, h); }
        finally { FreeHString(h); }
    }

    /// <summary>
    /// Clears the override for <paramref name="processId"/>, returning it to the
    /// system default. A null HSTRING is how the OS's own Volume mixer "reset"
    /// path expresses "no override".
    /// </summary>
    /// <returns>Win32 HRESULT.</returns>
    public int ClearRenderOverride(int processId) => SetBothRoles(processId, IntPtr.Zero);

    /// <summary>
    /// Reads back the override Windows currently holds for
    /// <paramref name="processId"/>, unpacked to an <c>IMMDevice.ID</c>.
    /// Null when nothing is set. Reading back is the only way to prove a Set
    /// was actually accepted, so callers should always do it.
    /// </summary>
    public string? GetRenderOverride(int processId)
    {
        ThrowIfDisposed();
        int hr = GetPersisted(processId, RoleMultimedia, out IntPtr packedHstring);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        if (packedHstring == IntPtr.Zero) return null;
        try
        {
            string packed = HStringToString(packedHstring);
            return string.IsNullOrWhiteSpace(packed) ? null : UnpackDeviceId(packed);
        }
        finally
        {
            FreeHString(packedHstring);
        }
    }

    /// <summary>
    /// The OS's Volume mixer sets both the multimedia (default media) and
    /// console (games/system sounds) roles for one app; doing only one would
    /// leave the app split across endpoints depending on which role it
    /// requested. First failing HRESULT wins so callers see a real error.
    /// </summary>
    private int SetBothRoles(int processId, IntPtr hstringDeviceId)
    {
        int hr = SetPersisted(processId, hstringDeviceId, RoleMultimedia);
        return hr < 0 ? hr : SetPersisted(processId, hstringDeviceId, RoleConsole);
    }

    private int SetPersisted(int processId, IntPtr hstringDeviceId, int role)
    {
        ThrowIfDisposed();
        var fn = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpoint>(
            VtableEntry(_factory, SlotSetPersisted));
        return fn(_factory, (uint)processId, FlowRender, role, hstringDeviceId);
    }

    private int GetPersisted(int processId, int role, out IntPtr hstringDeviceId)
    {
        ThrowIfDisposed();
        var fn = Marshal.GetDelegateForFunctionPointer<GetPersistedDefaultAudioEndpoint>(
            VtableEntry(_factory, SlotGetPersisted));
        return fn(_factory, (uint)processId, FlowRender, role, out hstringDeviceId);
    }

    /// <summary>Reads slot <paramref name="slot"/> out of the object's vtable pointer.</summary>
    private static IntPtr VtableEntry(IntPtr comObject, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    /// <summary>
    /// MMDevice ids (<c>{0.0.0.00000000}.{guid}</c>) are not what this API
    /// wants; it wants the device *instance* id form that the OS's own UI passes:
    /// token + MMDevice id + device-interface class GUID.
    /// </summary>
    public static string PackDeviceId(string mmDeviceId) =>
        string.IsNullOrWhiteSpace(mmDeviceId)
            ? mmDeviceId
            : $"{MmdevapiToken}{mmDeviceId}{DevInterfaceRender}";

    /// <summary>Inverse of <see cref="PackDeviceId"/>; tolerant of already-unpacked input.</summary>
    public static string UnpackDeviceId(string packed)
    {
        if (string.IsNullOrEmpty(packed)) return packed;
        if (packed.StartsWith(MmdevapiToken, StringComparison.OrdinalIgnoreCase))
            packed = packed[MmdevapiToken.Length..];
        if (packed.EndsWith(DevInterfaceRender, StringComparison.OrdinalIgnoreCase))
            packed = packed[..^DevInterfaceRender.Length];
        return packed;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _factory == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_factory != IntPtr.Zero)
        {
            Marshal.Release(_factory);
            _factory = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpoint(
        IntPtr thisPtr, uint processId, int flow, int role, IntPtr deviceIdHstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpoint(
        IntPtr thisPtr, uint processId, int flow, int role, out IntPtr deviceIdHstring);

    // HSTRING is handled by hand rather than through Marshal.StringToHString so
    // that exactly which WinRT primitive we depend on is visible here: this API
    // is internal, and a future OS could break on a marshaling assumption as
    // easily as on a vtable slot.
    private static IntPtr CreateHString(string value)
    {
        int hr = WindowsCreateString(value, (uint)value.Length, out IntPtr hstring);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        return hstring;
    }

    private static void FreeHString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
    }

    private static string HStringToString(IntPtr hstring)
    {
        IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
        return buffer == IntPtr.Zero || length == 0
            ? ""
            : Marshal.PtrToStringUni(buffer, (int)length) ?? "";
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid riid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
