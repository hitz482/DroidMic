using System.Buffers.Binary;
using System.Diagnostics;

namespace DroidMic.AudioEngine;

/// <summary>
/// Writes received audio to a WAV file, consuming it in real time against the
/// system clock. Used by the headless receiver and integration tests; never used
/// by default (DroidMic does not record audio unless explicitly asked).
/// </summary>
public sealed class WavFileSink : IAudioSink
{
    private readonly string _path;
    private FileStream? _file;
    private int _rate;
    private long _dataBytes;
    private long _written;
    private readonly Stopwatch _clock = new();

    public WavFileSink(string path) => _path = path;

    public string Name => "WAV file " + Path.GetFileName(_path);

    public void Open(int sampleRate)
    {
        _rate = sampleRate;
        _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(new byte[44]);
        _clock.Restart();
    }

    public int QueuedSamples
    {
        get
        {
            long consumed = (long)(_clock.Elapsed.TotalSeconds * _rate);
            return (int)Math.Max(0, _written - consumed);
        }
    }

    public int Write(ReadOnlySpan<short> samples)
    {
        if (_file is null) return 0;
        Span<byte> buf = stackalloc byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(buf[(i * 2)..], samples[i]);
        _file.Write(buf);
        _dataBytes += buf.Length;
        _written += samples.Length;
        return samples.Length;
    }

    public void Close()
    {
        if (_file is null) return;
        _file.Position = 0;
        _file.Write(BuildHeader(_rate, 1, _dataBytes));
        _file.Dispose();
        _file = null;
    }

    public static byte[] BuildHeader(int rate, short channels, long dataBytes)
    {
        byte[] h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(rate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }

    public void Dispose() => Close();
}
