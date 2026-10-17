using System.Diagnostics;

namespace DroidMic.AudioEngine;

public enum PipelineState
{
    Idle,
    Buffering,
    Playing,
}

/// <summary>Snapshot of pipeline statistics for UI and the stats message.</summary>
public readonly record struct PipelineStats(
    PipelineState State,
    long FramesReceived,
    long FramesLost,
    long FramesLate,
    long Underruns,
    long LatencyResets,
    double BufferMs,
    double TargetMs,
    double JitterMs,
    double DriftPpm,
    int InputRate);

/// <summary>
/// Receives decoded frames from the network thread (<see cref="PushFrame"/>) and
/// supplies a continuous mono 16-bit stream at <see cref="OutputRate"/> to the sink
/// thread (<see cref="Read"/>). Combines the jitter buffer, loss concealment,
/// adaptive target depth and drift compensation so latency stays bounded.
/// </summary>
public sealed class AudioPipeline
{
    public const int MaxFrameSamples = 960;
    private const int JitterCapacityFrames = 64;
    private const double MaxDriftCorrection = 0.005; // ±0.5 %
    private const double DriftGain = 0.01;

    private readonly object _lock = new();
    private readonly JitterBuffer _jitter = new(JitterCapacityFrames, MaxFrameSamples);
    private readonly DriftResampler _resampler = new(MaxFrameSamples * 4);
    private readonly short[] _frame = new short[MaxFrameSamples];
    private readonly short[] _lastFrame = new short[MaxFrameSamples];
    private readonly short[] _mono = new short[MaxFrameSamples];
    private readonly Func<double> _clockMs;

    private BufferBounds _bounds;
    private PipelineState _state = PipelineState.Idle;
    private int _inputRate;
    private int _frameSamples;
    private int _lastFrameLen;
    private int _consecutiveConcealed;

    private double _targetMs;
    private double _depthEmaMs;
    private double _jitterMs;
    private double _prevTransitMs;
    private bool _haveTransit;
    private double _ratio = 1.0;

    private long _received, _lost, _underruns, _latencyResets;

    public AudioPipeline(int outputRate = 48000, LatencyPreset preset = LatencyPreset.Balanced, Func<double>? clockMs = null)
    {
        OutputRate = outputRate;
        _bounds = BufferBounds.For(preset);
        _targetMs = _bounds.MinMs;
        var sw = Stopwatch.StartNew();
        _clockMs = clockMs ?? (() => sw.Elapsed.TotalMilliseconds);
    }

    public int OutputRate { get; }

    /// <summary>Meter of the received (pre-buffer) signal.</summary>
    public LevelMeter InputMeter { get; } = new();

    public void SetPreset(LatencyPreset preset)
    {
        lock (_lock)
        {
            _bounds = BufferBounds.For(preset);
            _targetMs = Math.Clamp(_targetMs, _bounds.MinMs, _bounds.MaxMs);
        }
    }

    /// <summary>Starts a new stream; discards any previous state.</summary>
    public void Start(int inputRate, int frameSamples)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (frameSamples is <= 0 or > MaxFrameSamples) throw new ArgumentOutOfRangeException(nameof(frameSamples));
        lock (_lock)
        {
            ResetLocked();
            _inputRate = inputRate;
            _frameSamples = frameSamples;
            _targetMs = Math.Clamp(2.0 * FrameMs, _bounds.MinMs, _bounds.MaxMs);
            _depthEmaMs = 0;
            _state = PipelineState.Buffering;
        }
        InputMeter.Reset();
    }

    public void Stop()
    {
        lock (_lock)
        {
            ResetLocked();
            _state = PipelineState.Idle;
        }
    }

    public PipelineState State
    {
        get { lock (_lock) return _state; }
    }

    private double FrameMs => _frameSamples * 1000.0 / _inputRate;

    /// <summary>
    /// Adds one received frame. <paramref name="samples"/> is interleaved PCM with
    /// <paramref name="channels"/> channels; stereo is downmixed to mono.
    /// </summary>
    public void PushFrame(uint seq, uint timestamp, ReadOnlySpan<short> samples, int channels, bool clippedAtSource)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        int frames = samples.Length / channels;
        if (frames is 0 or > MaxFrameSamples) return;

        ReadOnlySpan<short> mono;
        lock (_lock)
        {
            if (_state == PipelineState.Idle) return;
            if (channels == 2)
            {
                for (int i = 0; i < frames; i++) _mono[i] = (short)((samples[2 * i] + samples[2 * i + 1]) >> 1);
                mono = _mono.AsSpan(0, frames);
            }
            else
            {
                mono = samples[..frames];
            }

            UpdateJitter(timestamp);
            _received++;
            _jitter.Insert(seq, mono);
            EnforceUpperBoundLocked();
        }
        InputMeter.Process(mono, clippedAtSource);
    }

    /// <summary>
    /// Fills <paramref name="output"/> with mono PCM at <see cref="OutputRate"/>.
    /// Always fills the whole span (silence when idle or starved). Returns true if
    /// any real signal (not silence/concealment) was produced.
    /// </summary>
    public bool Read(Span<short> output)
    {
        lock (_lock)
        {
            if (_state == PipelineState.Idle)
            {
                output.Clear();
                return false;
            }

            double depth = DepthMsLocked();
            _depthEmaMs = _depthEmaMs == 0 ? depth : _depthEmaMs + 0.05 * (depth - _depthEmaMs);

            if (_state == PipelineState.Buffering)
            {
                if (depth < _targetMs)
                {
                    output.Clear();
                    return false;
                }
                _state = PipelineState.Playing;
            }

            double error = (_depthEmaMs - _targetMs) / Math.Max(_targetMs, 1);
            _ratio = 1.0 + Math.Clamp(error * DriftGain, -MaxDriftCorrection, MaxDriftCorrection);
            double step = (double)_inputRate / OutputRate * _ratio;

            int written = 0;
            bool real = false;
            while (written < output.Length)
            {
                written += _resampler.Process(output[written..], step);
                if (written >= output.Length) break;

                int len = _jitter.TakeNext(_frame);
                if (len > 0)
                {
                    _resampler.Append(_frame.AsSpan(0, len));
                    _frame.AsSpan(0, len).CopyTo(_lastFrame);
                    _lastFrameLen = len;
                    _consecutiveConcealed = 0;
                    real = true;
                }
                else if (len == 0)
                {
                    _lost++;
                    AppendConcealmentLocked();
                }
                else
                {
                    // Starved: conceal the remainder and rebuild the cushion.
                    _underruns++;
                    _state = PipelineState.Buffering;
                    AppendConcealmentLocked();
                    written += _resampler.Process(output[written..], step);
                    output[written..].Clear();
                    _resampler.Reset();
                    break;
                }
            }
            return real;
        }
    }

    public PipelineStats GetStats()
    {
        lock (_lock)
        {
            return new PipelineStats(_state, _received, _lost, _jitter.Late, _underruns, _latencyResets,
                DepthMsLocked(), _targetMs, _jitterMs, (_ratio - 1.0) * 1e6, _inputRate);
        }
    }

    private double DepthMsLocked() =>
        _inputRate == 0 ? 0 : (_jitter.BufferedSamples + _resampler.Available) * 1000.0 / _inputRate;

    // RFC 3550 interarrival jitter, then target = 2 frames + 3 × jitter within the preset bounds.
    private void UpdateJitter(uint timestamp)
    {
        double transit = _clockMs() - timestamp * 1000.0 / _inputRate;
        if (_haveTransit)
        {
            double d = Math.Abs(transit - _prevTransitMs);
            if (d < 1000) _jitterMs += (d - _jitterMs) / 16.0;
        }
        _prevTransitMs = transit;
        _haveTransit = true;

        double desired = Math.Clamp(2.0 * FrameMs + 3.0 * _jitterMs, _bounds.MinMs, _bounds.MaxMs);
        // Rise quickly, fall slowly.
        _targetMs = desired > _targetMs ? desired : _targetMs + 0.01 * (desired - _targetMs);
    }

    // Hard cap: if depth gets far above target (e.g. after a Wi-Fi stall burst), skip ahead.
    private void EnforceUpperBoundLocked()
    {
        double limit = Math.Max(_targetMs * 2, _targetMs + 60);
        if (DepthMsLocked() <= limit) return;
        while (DepthMsLocked() > _targetMs && _jitter.DropOldest()) { }
        _depthEmaMs = DepthMsLocked();
        _latencyResets++;
    }

    private void AppendConcealmentLocked()
    {
        int n = _lastFrameLen > 0 ? _lastFrameLen : _frameSamples;
        if (_consecutiveConcealed == 0 && _lastFrameLen > 0)
        {
            // Repeat the last frame once with a linear fade-out.
            for (int i = 0; i < n; i++)
                _frame[i] = (short)(_lastFrame[i] * (1.0 - (double)i / n));
            _resampler.Append(_frame.AsSpan(0, n));
        }
        else
        {
            _resampler.AppendSilence(n);
        }
        _consecutiveConcealed++;
    }

    private void ResetLocked()
    {
        _jitter.Reset();
        _resampler.Reset();
        _lastFrameLen = 0;
        _consecutiveConcealed = 0;
        _haveTransit = false;
        _jitterMs = 0;
        _ratio = 1.0;
        _received = _lost = _underruns = _latencyResets = 0;
    }
}
