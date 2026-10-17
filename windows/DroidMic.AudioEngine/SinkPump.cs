using System.Diagnostics;

namespace DroidMic.AudioEngine;

/// <summary>
/// Dedicated thread that moves audio from an <see cref="AudioPipeline"/> into one
/// or more sinks, keeping each sink's queue near a small target so that the sink
/// clock (not the network) paces playout.
/// </summary>
public sealed class SinkPump : IDisposable
{
    private readonly AudioPipeline _pipeline;
    private readonly List<IAudioSink> _sinks = new();
    private readonly object _sinkLock = new();
    private readonly short[] _chunk;
    private readonly int _targetQueue;
    private Thread? _thread;
    private volatile bool _running;

    public event Action<IAudioSink, Exception>? SinkFailed;

    /// <param name="chunkMs">Size of each pipeline read.</param>
    /// <param name="targetQueueMs">Desired samples queued in the primary sink.</param>
    public SinkPump(AudioPipeline pipeline, int chunkMs = 5, int targetQueueMs = 15)
    {
        _pipeline = pipeline;
        _chunk = new short[pipeline.OutputRate * chunkMs / 1000];
        _targetQueue = pipeline.OutputRate * targetQueueMs / 1000;
    }

    public long SamplesWritten { get; private set; }

    /// <summary>The first sink is the clock master; others receive the same samples.</summary>
    public void AddSink(IAudioSink sink)
    {
        sink.Open(_pipeline.OutputRate);
        lock (_sinkLock) _sinks.Add(sink);
    }

    public void RemoveSink(IAudioSink sink)
    {
        lock (_sinkLock) _sinks.Remove(sink);
        sink.Close();
    }

    public IReadOnlyList<IAudioSink> Sinks
    {
        get { lock (_sinkLock) return _sinks.ToArray(); }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DroidMic sink pump", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double chunkMs = _chunk.Length * 1000.0 / _pipeline.OutputRate;
        double fallbackDue = 0;
        while (_running)
        {
            IAudioSink? master;
            lock (_sinkLock) master = _sinks.Count > 0 ? _sinks[0] : null;

            bool wrote = false;
            if (master is not null)
            {
                int queued;
                try { queued = master.QueuedSamples; }
                catch (Exception ex) { Fail(master, ex); continue; }

                if (queued >= 0 ? queued < _targetQueue : sw.Elapsed.TotalMilliseconds >= fallbackDue)
                {
                    _pipeline.Read(_chunk);
                    WriteAll(_chunk);
                    fallbackDue = Math.Max(fallbackDue, sw.Elapsed.TotalMilliseconds - 50) + chunkMs;
                    wrote = true;
                }
            }
            if (!wrote) Thread.Sleep(1);
        }
    }

    private void WriteAll(short[] chunk)
    {
        IAudioSink[] sinks;
        lock (_sinkLock) sinks = _sinks.ToArray();
        foreach (var sink in sinks)
        {
            try { sink.Write(chunk); }
            catch (Exception ex) { Fail(sink, ex); }
        }
        SamplesWritten += chunk.Length;
    }

    private void Fail(IAudioSink sink, Exception ex)
    {
        lock (_sinkLock) _sinks.Remove(sink);
        try { sink.Close(); } catch { /* already failed */ }
        SinkFailed?.Invoke(sink, ex);
    }

    public void Dispose()
    {
        Stop();
        lock (_sinkLock)
        {
            foreach (var s in _sinks) s.Dispose();
            _sinks.Clear();
        }
    }
}
