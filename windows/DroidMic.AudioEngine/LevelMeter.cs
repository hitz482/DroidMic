namespace DroidMic.AudioEngine;

/// <summary>
/// Peak/RMS meter with simple ballistics. Written by the audio thread, read by
/// the UI thread; values are published as doubles via Volatile and tolerate tearing
/// between peak and RMS.
/// </summary>
public sealed class LevelMeter
{
    private const double FloorDb = -90.0;
    private double _peakDb = FloorDb;
    private double _rmsDb = FloorDb;
    private long _clippedFrames;

    public double PeakDb => Volatile.Read(ref _peakDb);
    public double RmsDb => Volatile.Read(ref _rmsDb);
    public long ClippedFrames => Interlocked.Read(ref _clippedFrames);

    public void Process(ReadOnlySpan<short> samples, bool clippedAtSource = false)
    {
        if (samples.IsEmpty) return;
        int peak = 0;
        double sumSq = 0;
        foreach (short s in samples)
        {
            int a = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (a > peak) peak = a;
            sumSq += (double)s * s;
        }
        double peakDb = ToDb(peak / 32768.0);
        double rmsDb = ToDb(Math.Sqrt(sumSq / samples.Length) / 32768.0);

        // Instant attack, ~300 ms release at 10 ms frames.
        double prevPeak = PeakDb;
        double prevRms = RmsDb;
        Volatile.Write(ref _peakDb, peakDb >= prevPeak ? peakDb : Math.Max(peakDb, prevPeak - 3.0));
        Volatile.Write(ref _rmsDb, rmsDb >= prevRms ? rmsDb : Math.Max(rmsDb, prevRms - 2.0));
        if (clippedAtSource || peak >= 32767) Interlocked.Increment(ref _clippedFrames);
    }

    public void Reset()
    {
        Volatile.Write(ref _peakDb, FloorDb);
        Volatile.Write(ref _rmsDb, FloorDb);
        Interlocked.Exchange(ref _clippedFrames, 0);
    }

    private static double ToDb(double linear) => linear <= 1e-9 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(linear));
}
