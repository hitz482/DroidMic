using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DroidMic.Platform;

public sealed record SelfTestResult(bool Passed, string Message, double MeasuredHz, double MeasuredDb);

/// <summary>
/// Proves the endpoint works end to end without a phone: writes a 440 Hz tone into
/// the driver, records the "DroidMic Microphone" endpoint like any other app
/// would (WASAPI shared mode), and checks the recording contains the tone.
/// Must not run while a phone is streaming (the tone would mix with live audio).
/// </summary>
public static class EndpointSelfTest
{
    private const double ToneHz = 440;

    public static async Task<SelfTestResult> RunAsync(CancellationToken ct = default)
    {
        var endpoint = EndpointDetector.Detect();
        if (endpoint.Availability != EndpointAvailability.Active || endpoint.Id is null)
            return new SelfTestResult(false, $"DroidMic Microphone is not available ({endpoint.Availability}).", 0, -90);

        using var driver = DriverClient.TryOpen(out var error);
        if (driver is null) return new SelfTestResult(false, error ?? "Driver not available", 0, -90);

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpoint.Id);
        using var capture = new WasapiCapture(device, true, 20);
        var captured = new List<float>(48000 * 3);
        var format = capture.WaveFormat;
        capture.DataAvailable += (_, e) =>
        {
            lock (captured) AppendAsMonoFloat(captured, e.Buffer, e.BytesRecorded, format);
        };

        using var feederCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        driver.Reset();
        capture.StartRecording();
        var feeder = Task.Run(() => FeedTone(driver, feederCts.Token), feederCts.Token);
        try
        {
            await Task.Delay(2500, ct);
        }
        finally
        {
            feederCts.Cancel();
            capture.StopRecording();
            try { await feeder; } catch (OperationCanceledException) { }
            driver.Reset();
        }

        float[] samples;
        lock (captured) samples = captured.ToArray();
        return Analyse(samples, format.SampleRate);
    }

    /// <summary>Checks a recording for the test tone. Public for unit tests.</summary>
    public static SelfTestResult Analyse(float[] samples, int rate)
    {
        // Skip the first 500 ms (stream start-up).
        var seg = samples.AsSpan(Math.Min(samples.Length, rate / 2));
        if (seg.Length < rate) return new SelfTestResult(false, "Recorded too little audio from the endpoint.", 0, -90);

        double sumSq = 0;
        int crossings = 0;
        for (int i = 0; i < seg.Length; i++)
        {
            sumSq += seg[i] * seg[i];
            if (i > 0 && seg[i - 1] < 0 && seg[i] >= 0) crossings++;
        }
        double rmsDb = 20 * Math.Log10(Math.Sqrt(sumSq / seg.Length) + 1e-12);
        double hz = crossings / (seg.Length / (double)rate);
        bool ok = Math.Abs(hz - ToneHz) < 10 && rmsDb > -30;
        string msg = ok
            ? $"A Windows recording client captured the test tone from DroidMic Microphone ({hz:0} Hz, {rmsDb:0.0} dBFS)."
            : $"The recording did not contain the test tone (measured {hz:0} Hz at {rmsDb:0.0} dBFS). Check that the endpoint is not muted in Sound settings.";
        return new SelfTestResult(ok, msg, hz, rmsDb);
    }

    private static void FeedTone(DriverClient driver, CancellationToken ct)
    {
        const int chunk = 480; // 10 ms
        short[] buf = new short[chunk];
        long n = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long sent = 0;
        while (!ct.IsCancellationRequested)
        {
            // Keep ~30 ms ahead of real time.
            long due = (long)(sw.Elapsed.TotalSeconds * 48000) + 1440;
            while (sent < due)
            {
                for (int i = 0; i < chunk; i++, n++) buf[i] = (short)(0.25 * 32767 * Math.Sin(2 * Math.PI * ToneHz * n / 48000));
                driver.Write(buf);
                sent += chunk;
            }
            Thread.Sleep(5);
        }
    }

    private static void AppendAsMonoFloat(List<float> dst, byte[] buffer, int bytes, WaveFormat format)
    {
        int ch = format.Channels;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT))
        {
            for (int i = 0; i + 4 * ch <= bytes; i += 4 * ch) dst.Add(BitConverter.ToSingle(buffer, i));
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 * ch <= bytes; i += 2 * ch) dst.Add(BitConverter.ToInt16(buffer, i) / 32768f);
        }
        else if (format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 * ch <= bytes; i += 4 * ch) dst.Add(BitConverter.ToInt32(buffer, i) / 2147483648f);
        }
    }
}
