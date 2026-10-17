using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DroidMic.AudioEngine;
using DroidMic.Networking;
using DroidMic.Platform;

namespace DroidMic.Desktop.Services;

public enum SinkMode
{
    /// <summary>Audio goes to the DroidMic Microphone endpoint.</summary>
    VirtualMicrophone,
    /// <summary>Driver unavailable: audio is received and metered but no app can use it.</summary>
    NoDriver,
}

/// <summary>
/// Composition root for the receiving side: server, pipeline, sink pump and the
/// virtual-microphone sink. Owns their lifetimes and the fallback behaviour when
/// the driver is missing or disappears.
/// </summary>
public sealed class AudioHost : ISinkStatusProvider, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly UiLogger _log;
    private readonly ServerIdentity _identity;
    private SinkPump? _pump;
    private DriverSink? _driverSink;
    private MonitorSink? _monitor;
    private DateTime _nextDriverRetry = DateTime.MinValue;

    public AudioHost(AppSettings settings, UiLogger log)
    {
        _settings = settings;
        _log = log;
        var store = new DpapiSecretStore(System.IO.Path.Combine(AppSettings.Directory, "secrets"));
        _identity = ServerIdentity.LoadOrCreate(store);
        Pairings = new PairingStore(store);
        Pipeline = new AudioPipeline(48000, settings.LatencyPreset);
    }

    public AudioPipeline Pipeline { get; }
    public PairingStore Pairings { get; }
    public ServerIdentity Identity => _identity;
    public DroidMicServer? Server { get; private set; }
    public SinkMode Mode { get; private set; } = SinkMode.NoDriver;
    public string? LastStartError { get; private set; }
    public DriverSink? DriverSink => _driverSink;

    public bool IsListening => Server?.IsRunning == true;

    public string SinkName => Mode == SinkMode.VirtualMicrophone ? DriverInterface.EndpointName : "No virtual microphone (driver not installed)";
    public bool SinkOk => Mode == SinkMode.VirtualMicrophone;

    public event Action? StateChanged;

    public async Task<bool> StartListeningAsync()
    {
        if (IsListening) return true;
        LastStartError = null;
        var server = new DroidMicServer(new ServerOptions
        {
            ControlPort = _settings.ControlPort,
            AudioPort = _settings.AudioPort,
            EnableDiscovery = _settings.EnableDiscovery,
            ServerName = string.IsNullOrWhiteSpace(_settings.ServerName) ? Environment.MachineName : _settings.ServerName!,
        }, _identity, Pairings, Pipeline, this, _log);
        try
        {
            server.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            LastStartError = $"Port {_settings.ControlPort} or {_settings.AudioPort} is already in use. Is another copy of DroidMic running? You can change the ports in Settings.";
            _log.Error(LastStartError);
            await server.DisposeAsync();
            StateChanged?.Invoke();
            return false;
        }
        catch (SocketException ex)
        {
            LastStartError = "Could not open network ports: " + ex.Message;
            _log.Error(LastStartError);
            await server.DisposeAsync();
            StateChanged?.Invoke();
            return false;
        }
        Server = server;
        StartPump();
        StateChanged?.Invoke();
        return true;
    }

    public async Task StopListeningAsync()
    {
        var server = Server;
        Server = null;
        if (server is not null) await server.DisposeAsync();
        StopPump();
        StateChanged?.Invoke();
    }

    private void StartPump()
    {
        _pump = new SinkPump(Pipeline);
        _pump.SinkFailed += (sink, ex) =>
        {
            _log.Warn($"Audio output '{sink.Name}' failed: {ex.Message}");
            if (sink == _driverSink)
            {
                _driverSink = null;
                Mode = SinkMode.NoDriver;
                EnsureMasterSink();
                StateChanged?.Invoke();
            }
        };
        EnsureMasterSink();
        SetMonitor(_settings.MonitorOnSpeakers);
        _pump.Start();
    }

    private void StopPump()
    {
        _pump?.Dispose();
        _pump = null;
        _driverSink = null;
        _monitor = null;
        Mode = SinkMode.NoDriver;
    }

    /// <summary>Makes the driver the clock master if possible, else a clocked null sink.</summary>
    private void EnsureMasterSink()
    {
        if (_pump is null) return;
        if (TryOpenDriver(out var sink))
        {
            foreach (var s in _pump.Sinks) _pump.RemoveSink(s);
            _driverSink = sink;
            _pump.AddSink(sink!);
            Mode = SinkMode.VirtualMicrophone;
            _log.Info("Feeding audio to the DroidMic Microphone endpoint");
        }
        else if (_pump.Sinks.Count == 0)
        {
            _pump.AddSink(new ClockedNullSink("No driver"));
            Mode = SinkMode.NoDriver;
        }
        if (_monitor is not null)
        {
            _pump.RemoveSink(_monitor);
            _monitor = new MonitorSink();
            _pump.AddSink(_monitor);
        }
    }

    private bool TryOpenDriver(out DriverSink? sink)
    {
        sink = null;
        if (DriverClient.FindInterfaces().Count == 0) return false;
        var s = new DriverSink();
        try
        {
            s.Open(Pipeline.OutputRate);
            sink = s;
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open the virtual microphone driver: " + ex.Message);
            s.Dispose();
            return false;
        }
    }

    /// <summary>Called periodically: re-attaches the driver if it was installed after start.</summary>
    public void Tick()
    {
        if (_pump is null || Mode == SinkMode.VirtualMicrophone || DateTime.UtcNow < _nextDriverRetry) return;
        _nextDriverRetry = DateTime.UtcNow.AddSeconds(5);
        if (DriverClient.FindInterfaces().Count > 0)
        {
            EnsureMasterSink();
            StateChanged?.Invoke();
        }
    }

    public void SetMonitor(bool enabled)
    {
        if (_pump is null) return;
        if (enabled && _monitor is null)
        {
            try
            {
                _monitor = new MonitorSink();
                _pump.AddSink(_monitor);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not open the speakers for monitoring: " + ex.Message);
                _monitor = null;
            }
        }
        else if (!enabled && _monitor is not null)
        {
            _pump.RemoveSink(_monitor);
            _monitor = null;
        }
    }

    /// <summary>
    /// Temporarily releases the driver so <see cref="EndpointSelfTest"/> can feed
    /// its own test tone, then re-attaches.
    /// </summary>
    public async Task<SelfTestResult> RunSelfTestAsync()
    {
        if (Server?.Status.Phase == ConnectionPhase.Streaming)
            return new SelfTestResult(false, "Stop streaming from the phone before running the self-test.", 0, -90);
        if (_driverSink is not null && _pump is not null)
        {
            _pump.RemoveSink(_driverSink);
            _driverSink = null;
            _pump.AddSink(new ClockedNullSink("Self-test"));
            Mode = SinkMode.NoDriver;
        }
        try
        {
            return await EndpointSelfTest.RunAsync();
        }
        finally
        {
            if (_pump is not null)
            {
                foreach (var s in _pump.Sinks) if (s is ClockedNullSink) _pump.RemoveSink(s);
                EnsureMasterSink();
            }
            StateChanged?.Invoke();
        }
    }

    public IPAddress? PreferredAddress()
    {
        var list = NetworkInfo.GetLocalAddresses();
        return list.Count > 0 ? list[0].Address : null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopListeningAsync();
        _identity.Dispose();
    }
}
