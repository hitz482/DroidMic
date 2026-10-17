using System.Net;
using System.Net.Sockets;
using DroidMic.AudioEngine;
using DroidMic.Protocol;
using Microsoft.Extensions.Logging;

namespace DroidMic.Networking;

public sealed class ServerOptions
{
    public int ControlPort { get; init; } = ProtocolConstants.DefaultControlPort;
    public int AudioPort { get; init; } = ProtocolConstants.DefaultAudioPort;
    public int DiscoveryPort { get; init; } = ProtocolConstants.DiscoveryPort;
    public bool EnableDiscovery { get; init; } = true;
    public string ServerName { get; init; } = Environment.MachineName;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public enum ConnectionPhase
{
    Listening,
    Authenticating,
    Connected,
    Streaming,
}

public sealed record ConnectionStatus(
    ConnectionPhase Phase,
    string? DeviceName,
    string? DeviceId,
    IPAddress? DeviceAddress,
    double RoundTripMs,
    string? StreamFormat);

/// <summary>Live status of the current sink, reported to the phone in stats.</summary>
public interface ISinkStatusProvider
{
    string SinkName { get; }
    bool SinkOk { get; }
}

/// <summary>
/// The PC side of DroidMic: control server, authentication, audio receiver and
/// discovery. One phone may stream at a time.
/// </summary>
public sealed class DroidMicServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly ServerIdentity _identity;
    private readonly PairingStore _pairings;
    private readonly AudioPipeline _pipeline;
    private readonly ISinkStatusProvider _sinkStatus;
    private readonly ILogger _log;

    private TcpListener? _listener;
    private AudioReceiver? _receiver;
    private DiscoveryResponder? _discovery;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private ClientSession? _active;
    private readonly object _activeLock = new();
    private ConnectionStatus _status = new(ConnectionPhase.Listening, null, null, null, 0, null);

    public DroidMicServer(ServerOptions options, ServerIdentity identity, PairingStore pairings,
        AudioPipeline pipeline, ISinkStatusProvider sinkStatus, ILogger log)
    {
        _options = options;
        _identity = identity;
        _pairings = pairings;
        _pipeline = pipeline;
        _sinkStatus = sinkStatus;
        _log = log;
    }

    public PairingWindow Pairing { get; } = new();
    public ServerIdentity Identity => _identity;
    public ServerOptions Options => _options;
    public AudioReceiver? Receiver => _receiver;
    public bool IsRunning => _cts is not null;
    public int ControlPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _options.ControlPort;

    public ConnectionStatus Status
    {
        get { lock (_activeLock) return _status; }
    }

    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<string, string>? DevicePaired;

    /// <summary>Binds sockets. Throws <see cref="SocketException"/> if a port is in use.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _listener = new TcpListener(_options.BindAddress, _options.ControlPort);
        _listener.Start();
        try
        {
            _receiver = new AudioReceiver(_pipeline, _options.AudioPort, _log, _options.BindAddress);
            _receiver.Start();
            if (_options.EnableDiscovery)
            {
                try
                {
                    _discovery = new DiscoveryResponder(_options.DiscoveryPort, BuildDiscoveryReply, _log);
                    _discovery.Start();
                }
                catch (SocketException ex)
                {
                    _log.LogWarning("Discovery disabled: port {Port} unavailable ({Error})", _options.DiscoveryPort, ex.SocketErrorCode);
                }
            }
        }
        catch
        {
            _listener.Stop();
            _receiver?.Dispose();
            throw;
        }
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        _log.LogInformation("Listening: control TCP {Ctl}, audio UDP {Audio}, identity {Fp}", ControlPort, _receiver.Port, _identity.Fingerprint);
        SetStatus(new ConnectionStatus(ConnectionPhase.Listening, null, null, null, 0, null));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _listener?.Stop();
        ClientSession? active;
        lock (_activeLock) active = _active;
        active?.Abort("server_stopping");
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        _discovery?.Dispose();
        _receiver?.Dispose();
        _discovery = null;
        _receiver = null;
        _listener = null;
        _cts.Dispose();
        _cts = null;
        _pipeline.Stop();
    }

    /// <summary>Disconnects the current phone, if any.</summary>
    public void DisconnectActive(string reason = "disconnected_by_pc")
    {
        ClientSession? active;
        lock (_activeLock) active = _active;
        active?.Abort(reason);
    }

    public DiscoveryReply BuildDiscoveryReply() =>
        new(ProtocolConstants.DiscoveryReplyMagic, ProtocolConstants.Version, "", _identity.ServerId,
            _options.ServerName, ControlPort, _identity.Fingerprint);

    private async Task AcceptLoop(CancellationToken ct)
    {
        var tasks = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.LogWarning("Accept failed: {Error}", ex.SocketErrorCode);
                continue;
            }
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Task.Run(() => HandleClientAsync(client, ct)));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        client.NoDelay = true;
        using var session = new ClientSession(this, client, remote, serverCt);
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or OperationCanceledException or TimeoutException)
        {
            _log.LogInformation("Connection from {Remote} ended: {Message}", remote.Address, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error in session from {Remote}", remote.Address);
        }
        finally
        {
            ReleaseActive(session);
        }
    }

    // ---- Session coordination -------------------------------------------------

    internal bool TryClaimActive(ClientSession session)
    {
        lock (_activeLock)
        {
            if (_active is not null && _active != session) return false;
            _active = session;
            return true;
        }
    }

    private void ReleaseActive(ClientSession session)
    {
        bool wasActive;
        lock (_activeLock)
        {
            wasActive = _active == session;
            if (wasActive) _active = null;
        }
        if (!wasActive) return;
        _receiver?.SetSession(null);
        _pipeline.Stop();
        SetStatus(new ConnectionStatus(ConnectionPhase.Listening, null, null, null, 0, null));
    }

    internal void SetStatus(ConnectionStatus status)
    {
        lock (_activeLock) _status = status;
        StatusChanged?.Invoke(status);
    }

    internal ServerIdentity IdentityKey => _identity;
    internal PairingStore Pairings => _pairings;
    internal AudioPipeline Pipeline => _pipeline;
    internal ISinkStatusProvider SinkStatus => _sinkStatus;
    internal ILogger Log => _log;
    internal void RaiseDevicePaired(string id, string name) => DevicePaired?.Invoke(id, name);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
