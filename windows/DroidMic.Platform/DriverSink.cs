using System.Diagnostics;
using DroidMic.AudioEngine;

namespace DroidMic.Platform;

/// <summary>
/// Feeds the pipeline into the DroidMic virtual microphone driver. The driver's
/// capture streams consume the ring at the Windows audio clock, so this sink is
/// the pipeline's clock master while a recording app is running. With no
/// recording app the driver discards old audio itself and the pump falls back to
/// wall-clock pacing (QueuedSamples = -1).
/// </summary>
public sealed class DriverSink : IAudioSink
{
    private DriverClient? _client;
    private readonly Stopwatch _sinceStatus = new();
    private DriverStatus _status;
    private readonly object _lock = new();

    public string Name => DriverInterface.EndpointName;

    public DriverStatus LastStatus
    {
        get { lock (_lock) return _status; }
    }

    public bool IsOpen => _client is not null;

    public void Open(int sampleRate)
    {
        if (sampleRate != DriverInterface.SampleRate)
            throw new NotSupportedException($"The driver runs at {DriverInterface.SampleRate} Hz");
        _client = DriverClient.TryOpen(out var error) ?? throw new InvalidOperationException(error);
        _client.Reset();
        lock (_lock) _status = _client.GetStatus();
        _sinceStatus.Restart();
    }

    public int QueuedSamples
    {
        get
        {
            var client = _client ?? throw new ObjectDisposedException(nameof(DriverSink));
            DriverStatus s;
            double elapsedMs;
            lock (_lock)
            {
                if (_sinceStatus.ElapsedMilliseconds >= 20)
                {
                    _status = client.GetStatus();
                    _sinceStatus.Restart();
                }
                s = _status;
                elapsedMs = _sinceStatus.Elapsed.TotalMilliseconds;
            }
            if (s.ActiveCaptureStreams == 0) return -1;
            int consumed = (int)(elapsedMs * DriverInterface.SampleRate / 1000);
            return Math.Max(0, (int)s.RingFillSamples - consumed);
        }
    }

    public int Write(ReadOnlySpan<short> samples)
    {
        var client = _client ?? throw new ObjectDisposedException(nameof(DriverSink));
        var status = client.Write(samples);
        lock (_lock)
        {
            _status = status;
            _sinceStatus.Restart();
        }
        return samples.Length;
    }

    public void Close()
    {
        try { _client?.Reset(); } catch (Exception) { /* driver may already be gone */ }
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Close();
}
