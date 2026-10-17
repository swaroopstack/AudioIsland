using AudioIsland.Audio.Devices;
using AudioIsland.Core.Devices;

// Session 1 spike: master volume two-way sync.
// 1. Reads current system volume.
// 2. Sets it to 40% (watch taskbar slider move).
// 3. Waits 10s — change volume externally (taskbar/keyboard) and it will echo.
// 4. Restores original level + mute.

using var devices = new AudioDeviceService();
using var master = new MasterVolumeService(devices);

var original = master.GetState();
Console.WriteLine($"Current: {original.Level:P0} muted={original.IsMuted}");

master.VolumeChanged += (_, s) => Console.WriteLine($"  [event] {s.Level:P0} muted={s.IsMuted}");
master.DefaultDeviceChanged += (_, id) => Console.WriteLine($"  [event] default device -> {id ?? "(none)"}");

Console.WriteLine("Setting volume to 40%...");
master.SetLevel(0.40f);
Console.WriteLine($"Verify taskbar shows 40%: {master.GetState().Level:P0}");

Console.WriteLine("10s window: change volume externally now...");
Thread.Sleep(10_000);

Console.WriteLine($"Restoring to {original.Level:P0} muted={original.IsMuted}");
master.SetLevel(original.Level);
master.SetMuted(original.IsMuted);
Console.WriteLine("Done.");
