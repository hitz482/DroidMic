using System.Diagnostics;

namespace DroidMic.AudioEngine;

/// <summary>
/// Discards audio while consuming it at real-time rate. Keeps the pipeline (meter,
/// statistics, latency control) running when no real output exists, e.g. when the
/// virtual microphone driver is not installed.
/// </summary>
public sealed class ClockedNullSink : IAudioSink
{
    private readonly Stopwatch _clock = new();
    private long _written;
    private int _rate;

    public ClockedNullSink(string name = "Discard") => Name = name;

    public string Name { get; }

    public void Open(int sampleRate)
    {
        _rate = sampleRate;
        _written = 0;
        _clock.Restart();
    }

    public int Write(ReadOnlySpan<short> samples)
    {
        _written += samples.Length;
        return samples.Length;
    }

    public int QueuedSamples => (int)Math.Max(0, _written - (long)(_clock.Elapsed.TotalSeconds * _rate));

    public void Close() => _clock.Stop();

    public void Dispose() => Close();
}
