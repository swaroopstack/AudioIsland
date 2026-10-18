# Feasibility: per-app audio capture via process loopback

**Status: VERIFIED WORKING** on Windows 11 build 26200 (x64), .NET 8, no admin,
no driver install. This was the Session-1 gate for AudioIsland.

## Goal

AudioIsland splits one app's audio to a chosen output device. That requires
capturing a *single process's* audio — not the whole device mix — then
re-rendering it to any endpoint. This report proves the capture half.

## What we used

Windows' documented **process loopback** API (Win10 2004+/NTDDI_WIN10_VB):

- A virtual audio endpoint `"VAD\Process_Loopback"`, activated through
  `ActivateAudioInterfaceAsync` (MMDevAPI.dll).
- An `AUDIOCLIENT_ACTIVATION_PARAMS` blob (`VT_BLOB`) selecting the target PID
  and a `PROCESS_LOOPBACK_MODE` (include / exclude the whole process tree).
- The returned `IAudioClient` is a normal WASAPI capture client: `Initialize`
  → `GetService(IAudioCaptureClient)` → `Start` → read packets.

Because the audio engine attributes the mix per-process, the capture follows
the app across device changes (headphones → speakers) automatically. This is
the same API OBS' "Application Audio Output Capture" and Chromium's WebRTC
audio input use. No virtual audio driver is required — the V1 requirement of
"no admin, no driver install" holds.

## Verified results

Deterministic self-test: spawn a child process playing a 440 Hz sine at
−6 dBFS (0.5000), then capture it. Run with:

```
dotnet run --project src/AudioIsland.Spike -- --selftest
```

| Mode | Frames captured (4 s) | Peak |
|---|---|---|
| `INCLUDE` child process tree | 192,480 | **0.5000 (−6.0 dBFS)** |
| `EXCLUDE` System (captures all apps) | 193,440 | **0.5000 (−6.0 dBFS)** |
| Device-level control (NAudio `WasapiLoopbackCapture`) | 128,160 | 0.7611 |

The peak of the `INCLUDE` capture is *exactly* the tone's −6.0 dBFS, while the
device-level control peaks at 0.76 because other apps (browser, etc.) are also
playing. That is the whole point of the feature: process loopback isolates one
app out of a busy mix.

## Interop contract (verified against SDK headers)

Authoritative source: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.19041.0\um\`

| Item | Value |
|---|---|
| Virtual device | `"VAD\Process_Loopback"` |
| `IID_IAudioClient` | `1CB9AD4C-DBFA-4c32-B178-C2F568A703B2` |
| `IID_IAudioCaptureClient` | `C8ADBD64-E71E-48A0-A4DE-185C395CD317` |
| `IID_IActivateAudioInterfaceCompletionHandler` | `41D949AB-9862-444A-80F6-C261334DA5EB` |
| `IID_IActivateAudioInterfaceAsyncOperation` | `72A22D78-CDE4-431D-B8CC-843A71199B6D` |
| `AUDIOCLIENT_ACTIVATION_PARAMS` | 12 bytes: `ActivationType` (1 = `PROCESS_LOOPBACK`) + `ProcessLoopbackParams` |
| `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` | `TargetProcessId` (DWORD @0), `ProcessLoopbackMode` (enum @4); mode 0 = INCLUDE tree, 1 = EXCLUDE tree |
| `PROPVARIANT` | 24 bytes: `vt=65` (`VT_BLOB`) @0, `cbSize` @8, `pBlobData` @16 |

Runtime-verified blob for `INCLUDE` pid 0x25C8 (9672):
`01-00-00-00-C8-25-00-00-00-00-00-00` — and for `EXCLUDE` pid 4:
`01-00-00-00-04-00-00-00-01-00-00-00`.

## The gotcha that cost us a session (and how to not repeat it)

COM dispatches method calls by **vtable slot index, not by name**. A
`[ComImport]` interface in C# is only correct if its methods are declared in
the *exact* order of the SDK header.

We initially declared `IAudioCaptureClient` as:

```
GetBuffer, GetNextPacketSize, ReleaseBuffer      // WRONG
```

while `audioclient.h` defines:

```
GetBuffer, ReleaseBuffer, GetNextPacketSize      // CORRECT
```

Symptom: everything "worked" — activation, `Initialize`, `GetService` and
`Start` all returned `S_OK` — but `GetNextPacketSize` always reported **0
packets**. That's because slot 4 was really `ReleaseBuffer`, which takes a
`UINT32` by value, ignores the `out uint*` we passed, never writes it (so it
stayed 0), and returns `S_OK`. The swapped `ReleaseBuffer` (now pointing at
`GetNextPacketSize`) was never reached, because reads bail out when the packet
size is 0 — so it also never crashed on the bogus pointer it would have written
through.

**Rule:** when hand-declaring a WASAPI/COM interface in C#, copy the method
order straight from the SDK header and add a comment naming the header line it
came from. Never trust memory or an alphabetical/natural grouping.

## Other findings worth keeping

- The process-loopback VAD **does not implement** `GetMixFormat`,
  `IsFormatSupported`, `GetCurrentPadding`, `GetStreamLatency` or
  `GetDevicePeriod` — all return `E_NOTIMPL` (`0x80004001`). This matches
  Chromium's `audio_low_latency_input_win.cc`. It is also a useful health
  check: if those calls *succeed*, you're talking to a normal endpoint, not
  the loopback VAD, and your activation blob was ignored.
- `GetBufferSize` returns 480 frames (10 ms) regardless of the requested
  buffer duration. The VAD clamps to the engine period; this is normal.
- `ActivateAudioInterfaceAsync` takes **5** parameters; the 5th is a required
  `out IActivateAudioInterfaceAsyncOperation`. Release that operation after
  you hold a fully-initialized client, or Windows keeps an internal per-PID
  slot and refuses to re-activate that PID later (bozbez/win-capture-audio
  issue #14).
- Pass a true `NULL` for `AudioSessionGuid` in `Initialize`, like both
  Microsoft's sample and win-capture-audio. (NAudio's `IAudioClient` forces
  `ref Guid`, which can only ever pass `&GUID_NULL` — one reason we declare
  the interface ourselves.)
- The capture format can be anything; use `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM`
  so the engine resamples to the device mix format. We default to float32 /
  48 kHz / stereo.

## Implications for AudioIsland V1

- **Capture is proven**, but see `tap-vs-session-volume.md`: the tap reads
  **after** the source session's volume node, so the obvious way to *move* an
  app (capture it, then mute its original session) produces silence. Split mode
  must not be designed that way.
- **Same-browser-tab splitting is not possible** (accepted V1 limit): the
  browser renders all tabs through one audio session owned by one PID, so
  process loopback cannot separate them.
- **Mirroring** (one app → many devices) is a render-side concern: NAudio can
  open multiple `WasapiOut` instances on the same captured stream. A
  post-volume tap is exactly right for mirroring, because a mirror deliberately
  leaves the app playing on its own device.
- **Split mode** is under redesign, most likely onto Windows' own per-app
  endpoint override (`IAudioPolicyConfigFactory`). Nothing is verified there
  yet — see `tap-vs-session-volume.md` before building routing.
- Bluetooth and wired/USB outputs are all ordinary WASAPI endpoints and are
  addressed by device ID, already covered by the device enumeration work.

## References

- Microsoft ApplicationLoopback sample (Windows-classic-samples)
- bozbez/win-capture-audio (OBS plugin; the activation + capture sequence)
- Chromium `media/audio/win/audio_low_latency_input_win.cc`
- SDK headers: `audioclient.h`, `audioclientactivationparams.h`, `mmdeviceapi.h`
