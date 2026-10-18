# Feasibility: is the process-loopback tap before or after session volume?

**Status: VERIFIED — the tap is POST-volume (muted source => silent capture).**
Recorded from the Step 0 gate run on Windows 11 build 26200 (x64), .NET 8.

## Why this experiment existed

Process loopback is a **TAP, not a redirect**. When we capture app A, app A
*still plays on its normal device*. To actually *move* app A somewhere else,
the original plan was:

1. capture app A's stream → render it to device X, and
2. mute app A's original session so it does not play twice.

Step 2 only works if our tap reads the engine mix **before** the per-session
volume/mute node. If the tap is **after** it, muting the source silences us
too, and `capture + mute + render` produces silence.

## Method

`dotnet run --project src/AudioIsland.Spike -- step0`

Spawn a child process playing a 440 Hz sine at a known −6 dBFS (0.5000), then
capture it three times through `ProcessLoopbackCapture`:

| Phase | Source session | Purpose |
|---|---|---|
| A | unmuted | baseline — proves the tone is arriving |
| B | **muted** | the gate itself |
| C | unmuted again | recovery control — any drop was caused by the mute, not by the child exiting |

Mute is applied with `AudioSessionService.SetSessionMuted`, and the mute state
is read back **by `SessionId`** (see the caveat below) to prove it actually
stuck before the reading is trusted.

## Results

| Phase | Frames (4 s) | Peak | Session read-back |
|---|---|---|---|
| A unmuted | 192,960 | **0.5000 (−6.0 dBFS)** | `muted=False active=True` |
| B **muted** | 192,960 | **0.0000 (−180.0 dBFS)** | `muted=True active=True` |
| C unmuted | 192,960 | **0.5000 (−6.0 dBFS)** | `muted=False active=True` |

Frames are *identical across all three phases* — the packets keep flowing while
the source is muted. They are just **digital silence**. The tap therefore reads
the process's audio **after** its session volume node.

Phase C returning to exactly 0.5000 rules out the child dying as an explanation.

### Verdict

**GATE FAILED.** Mutating the source session's volume or mute **cannot** be
used to suppress the app's original output while we re-render it elsewhere.

## Implementation caveat found while running this

The first run reported `INCONCLUSIVE: the mute did not stick`, because the
confirmation read-back looked the session up **by pid**:

```csharp
sessions.GetSessions().FirstOrDefault(s => s.ProcessId == child.Id)   // WRONG
```

while the write had looked it up **by SessionId**. Those can be different
sessions. After switching the read-back to `s.SessionId == sessionId`, the same
experiment reported `muted=True` correctly and produced the clean result above.

**Rule:** any mute/volume confirmation must read back with the *same key* used
to write it. `WithSession` in `AudioSessionService` writes by `SessionId`; the
gate now reads by `SessionId` too. Related: `GetSessionIdentifier` does not
contain a pid, so two processes running the *same exe path* (the parent spike
and its re-exec'd child are both `AudioIsland.Spike.exe`) can share a session
identifier — always disambiguate with pid as well when selecting a target.

## What this breaks, and what it does not

**Broken:** split-by-mute. We cannot capture an app *and* silence its original
output by touching that app's session volume.

**Not affected:** mirroring. A mirror keeps the app playing on its own device on
purpose and renders an *extra* copy elsewhere — a post-volume tap is exactly
the right signal for that. The capture + `WasapiOut` render path stays valid.

## Candidate redesign: native per-app endpoint override

Windows can move an app to another endpoint *without* any of this. That is
precisely the feature behind Settings → Sound → **Volume mixer → App volume and
device preferences**, and behind EarTrumpet's "Move to device".

The API (found in [EarTrumpet's source](https://github.com/File-New-Project/EarTrumpet),
`DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs`):

```csharp
// Windows.Media.Internal.AudioPolicyConfig, activated via RoGetActivationFactory
HRESULT SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, HSTRING deviceId);
HRESULT GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out HSTRING deviceId);
HRESULT ClearAllPersistedApplicationDefaultEndpoints();
```

Details taken from EarTrumpet's interop:

| Item | Value |
|---|---|
| Activation class | `"Windows.Media.Internal.AudioPolicyConfig"` |
| IID (downlevel) | `2a59116d-6c4f-45e0-a74f-707e3fef9258` |
| IID (21H2+) | `ab3d4648-e242-459f-b02f-541c70306324` |
| Interface kind | `IInspectable` — 19 filler slots, then `Set/Get/Clear` |
| Endpoint id form | `\\?\SWD#MMDEVAPI#` + MMDevice id + `#{e6327cad-dcec-4949-ae8a-991e976a79d2}` (render) |

EarTrumpet calls it for `eMultimedia` **and** `eConsole`; a null HSTRING clears
the override back to the system default.

Both IIDs share an identical method layout, so only the IID differs between OS
versions — trying both at runtime and keeping whichever activates is the robust
approach for "Win10 22H2 + Win11".

### Why this would be a better architecture

- **Split** becomes `SetPersistedDefaultAudioEndpoint(pid, …, targetX)` — no
  capture, no mute, no extra CPU or latency, and no dependency on tap semantics.
- **Mirror** stays the proven capture → render pipeline (post-volume tap is
  correct here).
- Per-app route volume/mute for a *split* route is ordinary
  `ISimpleAudioVolume` on that session — already built and verified in Session 1.

### Open questions — this is the new gate, not yet run

1. Does the factory activate on Win11 build 26200 *and* Win10 22H2, and with
   which of the two IIDs?
2. Does `SetPersistedDefaultAudioEndpoint` accept an arbitrary Win32 desktop
   process pid, or only packaged apps?
3. Does it take effect on an **already-running** app, or only the next time the
   app opens its stream? (EarTrumpet's issue
   [#835](https://github.com/File-New-Project/EarTrumpet/issues/835) shows this
   path regressed on some Windows dev builds — playback stayed on the old device
   and a clone session appeared on the new one.)
4. Does the process-loopback tap **follow** the app across the move? Capture is
   still needed for mirroring, so this must hold.
5. Does it require elevation? (EarTrumpet is a normal Store app, so expected no.)

Until questions 1–4 are answered with a run, split mode has **no** working
mechanism and no routing code should be written against one.

## References

- `docs/feasibility/process-loopback.md` — the capture half (verified)
- EarTrumpet `AudioPolicyConfigService.cs`, `IAudioPolicyConfigFactory*.cs`
- Microsoft Learn: [Audio Sessions](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-sessions)
