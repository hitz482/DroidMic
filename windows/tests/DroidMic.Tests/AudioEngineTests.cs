using DroidMic.AudioEngine;
using Xunit;

namespace DroidMic.Tests;

public class AudioEngineTests
{
    private static short[] Frame(int n, short value) => Enumerable.Repeat(value, n).ToArray();

    [Fact]
    public void JitterBuffer_ReordersAndDetectsLoss()
    {
        var jb = new JitterBuffer(16, 480);
        var outBuf = new short[480];
        jb.Insert(10, Frame(480, 1));
        jb.Insert(12, Frame(480, 3));
        jb.Insert(11, Frame(480, 2));
        jb.Insert(14, Frame(480, 5)); // 13 is lost

        Assert.Equal(480, jb.TakeNext(outBuf)); Assert.Equal(1, outBuf[0]);
        Assert.Equal(480, jb.TakeNext(outBuf)); Assert.Equal(2, outBuf[0]);
        Assert.Equal(480, jb.TakeNext(outBuf)); Assert.Equal(3, outBuf[0]);
        Assert.Equal(0, jb.TakeNext(outBuf));   // 13 missing
        Assert.Equal(480, jb.TakeNext(outBuf)); Assert.Equal(5, outBuf[0]);
        Assert.Equal(-1, jb.TakeNext(outBuf));  // empty
    }

    [Fact]
    public void JitterBuffer_LateDuplicateAndOverflow()
    {
        var jb = new JitterBuffer(8, 480);
        var outBuf = new short[480];
        jb.Insert(0, Frame(480, 1));
        jb.Insert(1, Frame(480, 1));
        jb.TakeNext(outBuf);
        Assert.Equal(JitterBuffer.InsertResult.Late, jb.Insert(0, Frame(480, 9)));
        Assert.Equal(JitterBuffer.InsertResult.Duplicate, jb.Insert(1, Frame(480, 9)));
        Assert.Equal(JitterBuffer.InsertResult.OverflowDroppedOldest, jb.Insert(20, Frame(480, 7)));
        Assert.True(jb.Count <= jb.Capacity);
        Assert.Equal(13u, jb.NextSequence);
        Assert.Equal(1, jb.Count);
    }

    [Fact]
    public void Resampler_UnityStep_IsTransparent()
    {
        var r = new DriftResampler(4096);
        short[] input = Enumerable.Range(0, 480).Select(i => (short)(i * 50)).ToArray();
        r.Append(input);
        var output = new short[479];
        Assert.Equal(479, r.Process(output, 1.0));
        Assert.Equal(input[..479], output);
    }

    [Fact]
    public void Resampler_RateConversion_ProducesExpectedCount()
    {
        var r = new DriftResampler(8192);
        int produced = 0;
        var output = new short[480];
        for (int block = 0; block < 100; block++)
        {
            r.Append(new short[441]);
            int n;
            while ((n = r.Process(output, 44100.0 / 48000.0)) > 0) produced += n;
        }
        Assert.InRange(produced, 48000 - 5, 48000 + 5);
    }

    /// <summary>
    /// Simulates a sender running fast/slow relative to the PC clock with network
    /// jitter, and checks the buffer stays bounded (no latency creep) with no underruns.
    /// </summary>
    [Theory]
    [InlineData(1.003, LatencyPreset.Balanced)]
    [InlineData(0.997, LatencyPreset.Balanced)]
    [InlineData(1.0, LatencyPreset.Low)]
    public void Pipeline_DriftAndJitter_KeepLatencyBounded(double senderClockRatio, LatencyPreset preset)
    {
        double now = 0;
        var pipe = new AudioPipeline(48000, preset, () => now);
        pipe.Start(48000, 480);
        var rng = new Random(1);
        var frame = Frame(480, 1000);
        var output = new short[240];

        double senderPeriod = 10.0 / senderClockRatio;
        double nextSend = 0;
        uint seq = 0;
        var pending = new List<(double arrive, uint seq)>();
        double maxBufferAfterWarmup = 0;
        long underrunsAfterWarmup = -1;

        for (int step = 0; step < 120_000; step++) // 600 s at 5 ms
        {
            now = step * 5.0;
            while (nextSend <= now)
            {
                pending.Add((nextSend + 2 + rng.NextDouble() * 8, seq++)); // 2–10 ms network delay
                nextSend += senderPeriod;
            }
            foreach (var p in pending.Where(p => p.arrive <= now).OrderBy(p => p.arrive).ToList())
            {
                pipe.PushFrame(p.seq, p.seq * 480, frame, 1, false);
                pending.Remove(p);
            }
            pipe.Read(output);

            if (step == 12_000) underrunsAfterWarmup = pipe.GetStats().Underruns; // after 60 s
            if (step > 12_000) maxBufferAfterWarmup = Math.Max(maxBufferAfterWarmup, pipe.GetStats().BufferMs);
        }

        var stats = pipe.GetStats();
        var bounds = BufferBounds.For(preset);
        Assert.Equal(PipelineState.Playing, stats.State);
        Assert.InRange(maxBufferAfterWarmup, 0, bounds.MaxMs + 30);
        Assert.Equal(underrunsAfterWarmup, stats.Underruns);
        Assert.Equal(0, stats.LatencyResets);
        Assert.InRange(Math.Abs(stats.DriftPpm), 0, 5000);
    }

    [Fact]
    public void Pipeline_ConcealsLossAndRecoversFromStarvation()
    {
        double now = 0;
        var pipe = new AudioPipeline(48000, LatencyPreset.Low, () => now);
        pipe.Start(48000, 480);
        var output = new short[480];
        var frame = Frame(480, 8000);

        // Paced delivery: one frame per 10 ms, seq 6 lost.
        for (uint s = 0; s < 10; s++)
        {
            now = s * 10;
            if (s != 6) pipe.PushFrame(s, s * 480, frame, 1, false);
            if (s >= 2) pipe.Read(output);
        }
        pipe.Read(output);

        var st = pipe.GetStats();
        Assert.Equal(1, st.FramesLost);
        Assert.Equal(0, st.Underruns);

        // Now starve it.
        for (int i = 0; i < 5; i++) pipe.Read(output);
        Assert.True(pipe.GetStats().Underruns >= 1);
        Assert.Equal(PipelineState.Buffering, pipe.GetStats().State);
        Assert.All(output, s => Assert.Equal(0, s));

        // Resume: it rebuffers then plays again.
        // The 200 ms stall raises the jitter estimate, so the target is now larger.
        for (uint s = 10; s < 19; s++) { now = 200 + s * 10; pipe.PushFrame(s, s * 480, frame, 1, false); }
        Assert.True(pipe.Read(output));
        Assert.Equal(PipelineState.Playing, pipe.GetStats().State);
    }

    [Fact]
    public void Pipeline_BurstAfterStall_SkipsAheadInsteadOfAccumulatingLatency()
    {
        double now = 0;
        var pipe = new AudioPipeline(48000, LatencyPreset.Low, () => now);
        pipe.Start(48000, 480);
        var frame = Frame(480, 100);
        // 500 ms of audio arrives in one burst (e.g. Wi-Fi power save flush).
        for (uint s = 0; s < 50; s++) pipe.PushFrame(s, s * 480, frame, 1, false);
        var st = pipe.GetStats();
        Assert.True(st.LatencyResets > 0);
        // Never more than the hard cap (2 × the largest Low target) instead of 500 ms.
        Assert.InRange(st.BufferMs, 0, 2 * BufferBounds.For(LatencyPreset.Low).MaxMs);
    }

    [Fact]
    public void Pipeline_StereoIsDownmixed_AndIdleIsSilent()
    {
        double now = 0;
        var pipe = new AudioPipeline(48000, LatencyPreset.Balanced, () => now);
        var output = new short[480];
        Assert.False(pipe.Read(output));
        Assert.All(output, s => Assert.Equal(0, s));

        pipe.Start(48000, 480);
        short[] stereo = new short[960];
        for (int i = 0; i < 480; i++) { stereo[2 * i] = 2000; stereo[2 * i + 1] = 0; }
        for (uint s = 0; s < 4; s++) { now = s * 10; pipe.PushFrame(s, s * 480, stereo, 2, false); }
        Assert.True(pipe.Read(output));
        Assert.Equal(1000, output[100]);
    }

    [Fact]
    public void LevelMeter_ReportsPeakAndClipping()
    {
        var m = new LevelMeter();
        m.Process(Frame(480, 16384));
        Assert.InRange(m.PeakDb, -6.1, -5.9);
        Assert.InRange(m.RmsDb, -6.1, -5.9);
        m.Process(Frame(480, short.MaxValue));
        Assert.Equal(1, m.ClippedFrames);
        m.Process(new short[480]);
        Assert.InRange(m.PeakDb, -3.1, -2.9); // 3 dB release per frame
    }
}
