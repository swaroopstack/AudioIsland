using AudioIsland.Audio.Devices;
using AudioIsland.Core.Devices;

// Session 1 spike: device enumeration + change notifications.
// Prints real endpoints from this machine and listens for changes.

using var service = new AudioDeviceService();

Console.WriteLine("=== Playback (render) devices ===");
foreach (var d in service.GetDevices(AudioDataFlow.Render))
    Console.WriteLine($"  {(d.IsDefaultForRender ? "[default] " : "          ")}{d.Name}  ({d.State})");

Console.WriteLine();
Console.WriteLine("=== Recording (capture) devices ===");
foreach (var d in service.GetDevices(AudioDataFlow.Capture))
    Console.WriteLine($"  {(d.IsDefaultForRender ? "[default] " : "          ")}{d.Name}  ({d.State})");

Console.WriteLine();
Console.WriteLine("Device-change notifications: plug/unplug a device or change default (15s listen)...");
var sw = 0;
service.DevicesChanged += (_, e) =>
{
    Interlocked.Increment(ref sw);
    Console.WriteLine($"  [{DateTimeOffset.Now:HH:mm:ss}] change for {e.Flow} flow");
};

Thread.Sleep(15_000);
Console.WriteLine($"Done. {sw} event(s) received.");
