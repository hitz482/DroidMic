namespace DroidMic.AudioEngine;

/// <summary>
/// Linear-interpolating resampler with a continuously adjustable step. It converts
/// between nominal rates (e.g. 44.1 kHz → 48 kHz) and absorbs the small clock
/// drift between the phone and the PC audio clock. Input is appended in blocks;
/// output is pulled. Preallocated, allocation-free after construction.
/// </summary>
public sealed class DriftResampler
{
    private readonly float[] _buf;
    private int _count;     // valid samples in _buf
    private double _pos;    // read position relative to _buf[0]

    public DriftResampler(int capacitySamples)
    {
        _buf = new float[capacitySamples];
    }

    /// <summary>Input samples not yet consumed (fractional position rounded down).</summary>
    public int Available => Math.Max(0, _count - (int)_pos);

    public int FreeSpace => _buf.Length - _count;

    /// <summary>True when at least one more output sample can be produced at the given step.</summary>
    public bool CanProduce => _pos + 1 < _count;

    public void Append(ReadOnlySpan<short> samples)
    {
        Compact();
        if (samples.Length > FreeSpace) throw new InvalidOperationException("Resampler input overflow");
        for (int i = 0; i < samples.Length; i++) _buf[_count + i] = samples[i] * (1f / 32768f);
        _count += samples.Length;
    }

    public void AppendSilence(int samples)
    {
        Compact();
        samples = Math.Min(samples, FreeSpace);
        Array.Clear(_buf, _count, samples);
        _count += samples;
    }

    /// <summary>
    /// Produces up to <paramref name="output"/>.Length samples, advancing the input by
    /// <paramref name="step"/> per output sample. Returns the number written.
    /// </summary>
    public int Process(Span<short> output, double step)
    {
        int written = 0;
        while (written < output.Length && _pos + 1 < _count)
        {
            int i = (int)_pos;
            float frac = (float)(_pos - i);
            float v = _buf[i] + (_buf[i + 1] - _buf[i]) * frac;
            int s = (int)MathF.Round(v * 32768f);
            output[written++] = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
            _pos += step;
        }
        return written;
    }

    public void Reset()
    {
        _count = 0;
        _pos = 0;
    }

    // Keep one sample of history for interpolation across block boundaries.
    private void Compact()
    {
        int consumed = (int)_pos;
        if (consumed == 0) return;
        if (consumed >= _count)
        {
            _pos -= _count;
            _count = 0;
            if (_pos < 0) _pos = 0;
            return;
        }
        Array.Copy(_buf, consumed, _buf, 0, _count - consumed);
        _count -= consumed;
        _pos -= consumed;
    }
}
