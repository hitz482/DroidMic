using DroidMic.AudioEngine;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DroidMic.Platform;

public sealed record OutputDevice(string Id, string Name);

/// <summary>
/// Plays the phone audio on a Windows output device: the speakers (to listen), or
/// the input side of an installed virtual audio cable (e.g. VB-CABLE's "CABLE Input"),
/// whose other end apps can then select as a microphone. That route needs no
/// DroidMic driver, so it works with Secure Boot on.
/// </summary>
public sealed class MonitorSink : IAudioSink
{
    private readonly string? _deviceId;
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private byte[] _bytes = new byte[4096];

    /// <param name="deviceId">Core Audio render endpoint id, or null for the default output.</param>
    public MonitorSink(string? deviceId = null) => _deviceId = deviceId;

    public string Name { get; private set; } = "Default output";

    /// <summary>Active playback devices, for the output picker.</summary>
    public static IReadOnlyList<OutputDevice> ListOutputs()
    {
        using var e = new MMDeviceEnumerator();
        var list = new List<OutputDevice>();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try { list.Add(new OutputDevice(d.ID, d.FriendlyName)); }
            catch (Exception) { /* device going away */ }
        }
        return list;
    }

    /// <summary>True for well-known virtual cable playback endpoints.</summary>
    public static bool IsVirtualCable(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase);

    public void Open(int sampleRate)
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true,
        };
        if (_deviceId is null)
        {
            _out = new WasapiOut(AudioClientShareMode.Shared, true, 30);
        }
        else
        {
            using var e = new MMDeviceEnumerator();
            var device = e.GetDevice(_deviceId);
            Name = device.FriendlyName;
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        }
        _out.Init(_buffer);
        _out.Play();
    }

    public int QueuedSamples => _buffer is null ? 0 : _buffer.BufferedBytes / 2;

    public int Write(ReadOnlySpan<short> samples)
    {
        if (_buffer is null) return 0;
        int n = samples.Length * 2;
        if (_bytes.Length < n) _bytes = new byte[n];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(_bytes);
        _buffer.AddSamples(_bytes, 0, n);
        return samples.Length;
    }

    public void Close()
    {
        _out?.Stop();
        _out?.Dispose();
        _out = null;
        _buffer = null;
    }

    public void Dispose() => Close();
}
