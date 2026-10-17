using DroidMic.AudioEngine;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DroidMic.Platform;

/// <summary>
/// Optional "listen on this PC" output to the default speakers, for checking the
/// phone audio by ear. This is a monitor only; other apps get the audio through
/// the DroidMic Microphone endpoint, not through this.
/// </summary>
public sealed class MonitorSink : IAudioSink
{
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private byte[] _bytes = new byte[4096];

    public string Name => "PC speakers (monitor)";

    public void Open(int sampleRate)
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true,
        };
        _out = new WasapiOut(AudioClientShareMode.Shared, true, 30);
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
