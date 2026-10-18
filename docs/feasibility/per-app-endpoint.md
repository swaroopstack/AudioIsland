# Feasibility: native per-app endpoint override (the split redesign)

**Status: PARTIALLY VERIFIED.** The API works and is accepted on this machine
(Windows 11 build 26200, x64, .NET 8, **not elevated**). Whether an
already-running app physically relocates is **NOT yet tested — this machine has
only one render endpoint**.

Gate command: `dotnet run --project src/AudioIsland.Spike -- perapp`

## Why we are here

`tap-vs-session-volume.md` proved the process-loopback tap reads **after** the
source session's volume node, so the original split design — capture an app,
then mute its original session — captures silence. Split needs a different
mechanism entirely.

This gate tests the mechanism Windows itself uses for **Settings → Sound →
Volume mixer → App volume and device preferences**, and that EarTrumpet uses for
"Move to device": `IAudioPolicyConfigFactory.SetPersistedDefaultAudioEndpoint`.

## The contract, as implemented

Implementation: `src/AudioIsland.Audio/Policy/AudioPolicyConfig.cs`.

| Item | Value |
|---|---|
| Activation class | `Windows.Media.Internal.AudioPolicyConfig` via `RoGetActivationFactory` |
| IID tried first (21H2+) | `ab3d4648-e242-459f-b02f-541c70306324` — **wins on build 26200** |
| IID fallback (downlevel) | `2a59116d-6c4f-45e0-a74f-707e3fef9258` |
| Interface kind | `IInspectable`: 3 `IUnknown` + 3 `IInspectable` slots, then 19 fillers, then `Set/Get/Clear` |
| `SetPersistedDefaultAudioEndpoint` slot | **25** (3 + 3 + 19) |
| `GetPersistedDefaultAudioEndpoint` slot | **26** |
| `ClearAllPersistedApplicationDefaultEndpoints` slot | **27** |
| Endpoint id form | `\\?\SWD#MMDEVAPI#` + MMDevice id + `#{e6327cad-dcec-4949-ae8a-991e976a79d2}` |
| Roles | `eMultimedia` (0) **and** `eConsole` (1) — both, like the OS's own UI |
| HSTRING | hand P/Invoke (`WindowsCreateString` / `WindowsDeleteString` / `WindowsGetStringRawBuffer`) |
| Elevation | none required |

Both IIDs share one method layout, so only the IID differs by OS version — we
probe both rather than branch on build numbers, which keeps one code path for
Win10 22H2 and Win11.

**Slot arithmetic was the main risk** (a wrong slot means calling an unrelated
method). It is asserted correct by the read-back in Q2: the value written comes
back byte-for-byte from the slot we think is `GetPersistedDefaultAudioEndpoint`.
Dispatch is through raw vtable reads rather than a `[ComImport]` declaration, so
the arithmetic is visible in the source instead of implicit in how the CLR
expands `InterfaceIsIInspectable`.

## Results

### Q1 — does the factory activate? **PASS**

```
ab3d4648-e242-459f-b02f-541c70306324: hr=0x00000000
```

The 21H2+ IID resolves first try. No admin, no driver, no WinRT projection
package.

### Q2 — does it accept a plain Win32 desktop pid? **PASS for an Active endpoint**

Target = the machine's only active render endpoint:

```
set hr=0x00000000 (S_OK)
read-back: {0.0.0.00000000}.{1780c099-7450-439b-9a42-995ad1329ebd}   == what we set
cleanup: clear hr=0x00000000, read-back=(none)
```

It is not restricted to packaged apps, and clearing (null HSTRING) restores
"default".

### Q2 with a non-present endpoint — **E_INVALIDARG (0x80070057)**

Second run targeted `Headphones (2- TOR-227) [NotPresent]`:

```
set hr=0x80070057 FAILED
read-back: (none)
```

Useful: **Windows validates the endpoint.** We cannot point an app at a device
that is not present, which means AudioIsland must only ever offer Active
endpoints in its device list — a wrong-but-valid-looking choice is rejected by
the OS, not silently swallowed. It also means this experiment cannot be run
against a non-existent second output.

### Q3a / Q3b / Q4-after-move — **BLOCKED, no hardware**

The gate fell back to a non-active endpoint precisely because there is nothing
else to target:

```
Render endpoints: 23 total, 1 active.
    [default] Speakers (Realtek(R) Audio)
```

and the OS agrees there is no second device at all:

```
Get-PnpDevice -Class AudioEndpoint
  OK  Speakers (Realtek(R) Audio)   SWD\MMDEVAPI\{0.0.0.00000000}.{1780C099-...}
  OK  Microphone (Realtek(R) Audio) SWD\MMDEVAPI\{0.0.1.00000000}.{A570C409-...}
```

One render endpoint, one capture endpoint. The remaining questions all require
*choosing a different output than the one the app is on*, which is impossible
here:

- **Q3a** does Windows hand *that process* a different default?
- **Q3b** does an already-running session physically relocate?
- **Q4** does the process-loopback tap still follow it (mirror mode needs this)?

Exit code `5` marks this exact state: mechanism partly proven, end-to-end move
unverified.

## How the gate measures Q3a (for when a second output exists)

The child process resolves its own default every 250 ms and reports it, because
that is the actual boundary: Windows expresses a per-app endpoint change by
handing *that process* a different default, so an app that never re-queries
(games that cache their `IMMDevice`) will never move however often we ask.
Browsers and media players do re-query — that is the only reason split mode can
exist at all.

Two lines are printed and deliberately kept apart:

- `RESOLVED DEFAULT -> <id>` — what Windows told us our default is (**mechanism**)
- `OPEN on ...` / `open failed` — whether a stream could attach (**physical move**)

The child's stdout is captured by the gate, so Q3a is judged from the child's
own words rather than inferred across the process boundary. A resolution that
*worked before* the override and then *fails* after it also counts as proof the
answer changed because of us.

Note for future runs: sessions for a process are found per-device with
`IAudioSessionManager` on each endpoint (`DescribeSessionDevices`) — the
production `AudioSessionService` is bound to the *default* endpoint only, so it
stops seeing an app that has moved.

## Still open

1. Does an already-running, re-querying app actually relocate? **(needs a 2nd output)**
2. Does the loopback tap follow it across the move? **(needs a 2nd output)**
3. Do Win10 22H2 (build 19045) machines resolve the downlevel IID
   `2a59116d-…`? Unverified — no Win10 machine has been run.
4. Behavior on a *later* Windows build than 26200 (vtable could grow fillers).

Items 1–2 need any second output at all: wired headphones, a USB DAC, a
reconnected Bluetooth device, or monitor/HDMI speakers. Nothing exotic.

## References

- `docs/feasibility/tap-vs-session-volume.md` — why the capture+mute design died
- `docs/feasibility/process-loopback.md` — the capture half
- EarTrumpet `AudioPolicyConfigService.cs` + `IAudioPolicyConfigFactory*.cs`
  (the contract this was recovered from)
