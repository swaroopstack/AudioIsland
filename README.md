# AudioIsland

Lightweight Windows 11 audio utility with a floating Dynamic Island-style pill.
Split per-application audio to different output devices (e.g. movie → headset A,
music → headset B) and mirror one app to multiple outputs for movie night.

## Status

In development — Stage 1 (feasibility).

## Stack

- C# / .NET 8 LTS
- Windows Core Audio APIs (WASAPI, IMMDeviceEnumerator, IAudioSessionManager2)
- NAudio for session/endpoint wrappers
- WinUI 3 (unpackaged) — pill UI planned for Session 4

## Layout

- `src/AudioIsland.Core` — domain models + service contracts
- `src/AudioIsland.Audio` — Core Audio backend (devices, sessions, routing, mixer)
- `src/AudioIsland.Sys` — Windows integration (tray, startup, fullscreen, hotkeys)
- `src/AudioIsland.Spike` — feasibility spike console (Session 1)
- `src/AudioIsland.App` — WinUI 3 pill + panel (Session 4)

## Build

```
dotnet build AudioIsland.sln -c Release
```

Requires .NET 8 SDK on Windows 10 22H2+ or Windows 11.

## License

MIT (pending).
