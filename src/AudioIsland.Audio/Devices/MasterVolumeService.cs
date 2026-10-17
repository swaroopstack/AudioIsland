using System.Runtime.InteropServices;
using AudioIsland.Core.Contracts;
using AudioIsland.Core.Devices;
using NAudio.CoreAudioApi;

namespace AudioIsland.Audio.Devices;

/// <summary>
/// Master volume/mute bound to the current default render endpoint.
/// Rebinds automatically when Windows changes the default device.
/// Change events come from IAudioEndpointVolume notifications — no polling.
/// </summary>
public sealed class MasterVolumeService : IMasterVolumeService
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AudioDeviceService? _deviceService;
    private readonly object _gate = new();

    // The MMDevice must stay alive: its AudioEndpointVolume is the control surface.
    private MMDevice? _device;
    private string? _boundDeviceId;

    public MasterVolumeService(AudioDeviceService? deviceService = null)
    {
        _deviceService = deviceService;
        BindToDefault();
        if (_deviceService is not null)
            _deviceService.DevicesChanged += OnDevicesChanged;
    }

    public VolumeState GetState()
    {
        lock (_gate)
        {
            if (_device?.AudioEndpointVolume is not { } vol) return VolumeState.Silent;
            // MasterVolumeLevelScalar is normalized 0..1; MasterVolumeLevel is dB.
            return new VolumeState(vol.MasterVolumeLevelScalar, vol.Mute);
        }
    }

    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        lock (_gate)
        {
            if (_device?.AudioEndpointVolume is not { } vol) return;
            vol.MasterVolumeLevelScalar = level;
        }
        // IAudioEndpointVolume also notifies us back; raising here gives the UI
        // instant feedback without waiting for the COM round-trip. UI handlers
        // must be idempotent — see interface contract.
        VolumeChanged?.Invoke(this, GetState());
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            if (_device?.AudioEndpointVolume is not { } vol) return;
            vol.Mute = muted;
        }
        VolumeChanged?.Invoke(this, GetState());
    }

    public event EventHandler<VolumeState>? VolumeChanged;
    public event EventHandler<string?>? DefaultDeviceChanged;

    private void BindToDefault()
    {
        lock (_gate)
        {
            try
            {
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device.ID == _boundDeviceId) return;

                _device?.Dispose();
                // Re-fetch: MMDevice cannot be transferred across using scopes.
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _boundDeviceId = _device.ID;
                _device.AudioEndpointVolume.OnVolumeNotification += data =>
                {
                    // Fires for Windows-slider/media-key changes and our own sets.
                    VolumeChanged?.Invoke(this, new VolumeState(data.MasterVolume, data.Muted));
                };
            }
            catch (COMException)
            {
                // No active render device (all unplugged) — unbind.
                _device?.Dispose();
                _device = null;
                _boundDeviceId = null;
            }
        }
    }

    private void OnDevicesChanged(object? sender, AudioDevicesChangedEventArgs e)
    {
        if (e.Flow != AudioDataFlow.Render) return;
        var currentDefault = _deviceService?.GetDefaultDeviceId(AudioDataFlow.Render);
        if (currentDefault == _boundDeviceId) return;
        BindToDefault();
        DefaultDeviceChanged?.Invoke(this, currentDefault);
    }

    public void Dispose()
    {
        if (_deviceService is not null) _deviceService.DevicesChanged -= OnDevicesChanged;
        lock (_gate) { _device?.Dispose(); _device = null; }
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
