using System.Net;
using DroidMic.AudioEngine;
using DroidMic.Networking;
using Microsoft.Extensions.Logging;

// Headless DroidMic receiver for development, CI interop tests and diagnostics.
// It does not need the virtual microphone driver: received audio goes to a WAV
// file (only when --wav is given) or is discarded after metering.

var opts = CliOptions.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine("""
        Usage: droidmic-receiver [options]
          --pair                 Open pairing and print the PIN
          --pin <6 digits>       Use a fixed pairing PIN (automated tests only)
          --wav <file>           Write received audio to a WAV file
          --data-dir <dir>       Where to keep identity and pairings (default: ./droidmic-data)
          --control-port <n>     TCP control port (default 48110)
          --audio-port <n>       UDP audio port (default 48111)
          --exit-after-session   Exit when the first streaming session ends
          --timeout <seconds>    Exit after this many seconds
        """);
    return 2;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }).SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("droidmic");

var store = new FileSecretStore(opts.DataDir);
using var identity = ServerIdentity.LoadOrCreate(store);
var pipeline = new AudioPipeline();
using var pump = new SinkPump(pipeline);
var sinkStatus = new CliSinkStatus();

if (opts.WavPath is not null)
{
    pump.AddSink(new WavFileSink(opts.WavPath));
    sinkStatus.Name = "WAV file";
}
else
{
    pump.AddSink(new ClockedNullSink());
}

await using var server = new DroidMicServer(new ServerOptions
{
    ControlPort = opts.ControlPort,
    AudioPort = opts.AudioPort,
    ServerName = Environment.MachineName + " (receiver)",
}, identity, new PairingStore(store), pipeline, sinkStatus, log);

var done = new TaskCompletionSource();
bool streamed = false;
ConnectionPhase? lastPhase = null;
server.StatusChanged += s =>
{
    if (s.Phase == lastPhase) return;
    lastPhase = s.Phase;
    Console.WriteLine($"STATUS {s.Phase} {s.DeviceName} {s.StreamFormat}");
    if (s.Phase == ConnectionPhase.Streaming) streamed = true;
    if (opts.ExitAfterSession && streamed && s.Phase is ConnectionPhase.Listening or ConnectionPhase.Connected) done.TrySetResult();
};

server.Start();
pump.Start();
Console.WriteLine($"FINGERPRINT {identity.Fingerprint}");
if (opts.Pin is not null || opts.Pair)
{
    var info = opts.Pin is not null ? server.Pairing.OpenWith(opts.Pin) : server.Pairing.Open();
    Console.WriteLine($"PIN {info.Pin}");
}
Console.WriteLine("READY");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
if (opts.TimeoutSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
cts.Token.Register(() => done.TrySetResult());

_ = Task.Run(async () =>
{
    using var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await t.WaitForNextTickAsync())
    {
        var st = pipeline.GetStats();
        if (st.State == PipelineState.Idle) continue;
        Console.WriteLine($"STATS state={st.State} rx={st.FramesReceived} lost={st.FramesLost} late={st.FramesLate} " +
                          $"underruns={st.Underruns} buffer={st.BufferMs:0.0}ms target={st.TargetMs:0.0}ms jitter={st.JitterMs:0.0}ms " +
                          $"drift={st.DriftPpm:0}ppm peak={pipeline.InputMeter.PeakDb:0.0}dBFS");
    }
});

await done.Task;
var final = pipeline.GetStats();
var c = server.Receiver!.Counters;
Console.WriteLine($"SUMMARY accepted={c.Accepted} malformed={c.Malformed} replayed={c.Replayed} authFailed={c.AuthFailed} " +
                  $"lost={final.FramesLost} underruns={final.Underruns}");
await server.StopAsync();
pump.Stop();
foreach (var s in pump.Sinks) s.Close();
return 0;

internal sealed class CliSinkStatus : ISinkStatusProvider
{
    public string Name = "Headless receiver (no virtual microphone)";
    public string SinkName => Name;
    public bool SinkOk => true;
}

internal sealed record CliOptions(bool Pair, string? Pin, string? WavPath, string DataDir, int ControlPort, int AudioPort,
    bool ExitAfterSession, int TimeoutSeconds)
{
    public static CliOptions? Parse(string[] args)
    {
        bool pair = false, exitAfter = false;
        string? pin = null, wav = null;
        string dataDir = Path.Combine(Environment.CurrentDirectory, "droidmic-data");
        int ctl = 48110, audio = 48111, timeout = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--pair": pair = true; break;
                    case "--pin": pin = Next(); break;
                    case "--wav": wav = Next(); break;
                    case "--data-dir": dataDir = Next(); break;
                    case "--control-port": ctl = int.Parse(Next()); break;
                    case "--audio-port": audio = int.Parse(Next()); break;
                    case "--exit-after-session": exitAfter = true; break;
                    case "--timeout": timeout = int.Parse(Next()); break;
                    default: return null;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                return null;
            }
        }
        return new CliOptions(pair, pin, wav, dataDir, ctl, audio, exitAfter, timeout);
    }
}
