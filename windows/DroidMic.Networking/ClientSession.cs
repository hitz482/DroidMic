using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DroidMic.Protocol;
using Microsoft.Extensions.Logging;

namespace DroidMic.Networking;

/// <summary>One TCP control connection: handshake, then session control (protocol §3–4).</summary>
internal sealed class ClientSession : IDisposable
{
    private readonly DroidMicServer _server;
    private readonly TcpClient _client;
    private readonly IPEndPoint _remote;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Stopwatch _sinceRx = Stopwatch.StartNew();

    private string _deviceId = "";
    private string _deviceName = "";
    private uint _sessionId;
    private string? _format;
    private double _rttMs;
    private bool _streaming;
    private bool _claimed;

    public ClientSession(DroidMicServer server, TcpClient client, IPEndPoint remote, CancellationToken serverCt)
    {
        _server = server;
        _client = client;
        _remote = remote;
        _stream = client.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
    }

    private ILogger Log => _server.Log;

    public void Abort(string reason)
    {
        try
        {
            if (_writeLock.Wait(200))
            {
                try { ControlFraming.WriteAsync(_stream, new ByeMessage(reason), CancellationToken.None).Wait(500); }
                finally { _writeLock.Release(); }
            }
        }
        catch (Exception) { /* best effort */ }
        _cts.Cancel();
        _client.Close();
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        using (var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hsTimeout.CancelAfter(_server.Options.HandshakeTimeout);
            bool ok = await HandshakeAsync(hsTimeout.Token).ConfigureAwait(false);
            if (!ok) return;
        }

        var stats = Task.Run(() => StatsLoopAsync(ct), ct);
        try
        {
            await ControlLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _cts.Cancel();
            try { await stats.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Log.LogInformation("Device '{Name}' disconnected", _deviceName);
        }
    }

    // ---- Handshake ---------------------------------------------------------

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        _server.SetStatus(_server.Status with { Phase = ConnectionPhase.Authenticating, DeviceAddress = _remote.Address });
        var first = await ControlFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (first is not HelloMessage hello) throw new ProtocolException("Expected hello");
        if (hello.Version != ProtocolConstants.Version)
        {
            await SendAsync(new ErrorMessage(ErrorMessage.UnsupportedVersion, $"Server speaks v{ProtocolConstants.Version}"), ct);
            return false;
        }
        if (!IsValidDeviceId(hello.DeviceId)) throw new ProtocolException("Bad deviceId");
        _deviceId = hello.DeviceId;
        _deviceName = SanitizeName(hello.DeviceName);

        byte[] clientEph = HandshakeCrypto.DecodeBase64(hello.Ephemeral, null, "eph");
        byte[] clientNonce = HandshakeCrypto.DecodeBase64(hello.Nonce, ProtocolConstants.HandshakeNonceLength, "nonce");

        byte[] authSecret;
        bool pinMode;
        if (hello.Mode == HelloMessage.ModePin)
        {
            string? pin = _server.Pairing.CurrentPin;
            if (pin is null)
            {
                await SendAsync(new ErrorMessage(ErrorMessage.PairingClosed, "Open 'Pair new phone' on the PC first"), ct);
                return false;
            }
            authSecret = Encoding.UTF8.GetBytes(pin);
            pinMode = true;
        }
        else if (hello.Mode == HelloMessage.ModePaired)
        {
            // Unknown devices still get a full handshake with a random secret so the
            // response does not reveal whether the device id is known.
            authSecret = _server.Pairings.GetKey(_deviceId) ?? RandomNumberGenerator.GetBytes(32);
            pinMode = false;
        }
        else
        {
            throw new ProtocolException("Unknown mode");
        }

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] serverEph = eph.ExportSubjectPublicKeyInfo();
        byte[] serverNonce = RandomNumberGenerator.GetBytes(ProtocolConstants.HandshakeNonceLength);
        byte[] shared;
        try { shared = HandshakeCrypto.Agree(eph, clientEph); }
        catch (CryptographicException) { throw new ProtocolException("Invalid client key"); }

        var identity = _server.IdentityKey;
        byte[] th = HandshakeCrypto.TranscriptHash(_deviceId, clientEph, clientNonce,
            identity.ServerId, identity.PublicKeySpki, serverEph, serverNonce);
        var keys = HandshakeCrypto.DeriveKeys(shared, authSecret, th);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(authSecret);

        await SendAsync(new ServerHelloMessage(ProtocolConstants.Version, identity.ServerId, _server.Options.ServerName,
            Convert.ToBase64String(identity.PublicKeySpki), Convert.ToBase64String(serverEph),
            Convert.ToBase64String(serverNonce), Convert.ToBase64String(HandshakeCrypto.Sign(identity.Key, th))), ct);

        var authMsg = await ControlFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (authMsg is not AuthMessage auth) throw new ProtocolException("Expected auth");
        byte[] mac = HandshakeCrypto.DecodeBase64(auth.Mac, 32, "mac");

        if (!HandshakeCrypto.VerifyMac(keys.ClientConfirm, th, mac))
        {
            string reason;
            if (pinMode)
            {
                bool locked = _server.Pairing.RecordFailure();
                reason = locked ? AuthFailedMessage.LockedOut : AuthFailedMessage.BadCredentials;
                Log.LogWarning("Wrong pairing PIN from {Remote}{Locked}", _remote.Address, locked ? " (pairing locked)" : "");
            }
            else
            {
                reason = _server.Pairings.GetKey(_deviceId) is null ? AuthFailedMessage.UnknownDevice : AuthFailedMessage.BadCredentials;
                Log.LogWarning("Rejected device {Remote}: {Reason}", _remote.Address, reason);
            }
            await SendAsync(new AuthFailedMessage(reason), ct);
            return false;
        }

        if (!_server.TryClaimActive(this))
        {
            await SendAsync(new ErrorMessage(ErrorMessage.Busy, "Another phone is connected"), ct);
            return false;
        }
        _claimed = true;

        string? wrapped = null;
        if (pinMode)
        {
            byte[] pairingKey = RandomNumberGenerator.GetBytes(ProtocolConstants.PairingKeyLength);
            _server.Pairings.Add(_deviceId, _deviceName, pairingKey);
            wrapped = Convert.ToBase64String(HandshakeCrypto.WrapPairingKey(keys.PairingWrap, pairingKey, th));
            CryptographicOperations.ZeroMemory(pairingKey);
            _server.Pairing.Consume();
            _server.RaiseDevicePaired(_deviceId, _deviceName);
            Log.LogInformation("Paired new device '{Name}'", _deviceName);
        }
        else
        {
            _server.Pairings.Touch(_deviceId, _deviceName);
        }

        _sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _server.Receiver!.SetSession(new ActiveAudioSession(_sessionId, keys.AudioKey, _remote.Address));
        CryptographicOperations.ZeroMemory(keys.AudioKey);

        await SendAsync(new AuthOkMessage(Convert.ToBase64String(HandshakeCrypto.Mac(keys.ServerConfirm, th)),
            _sessionId, _server.Receiver.Port, wrapped), ct);
        _server.SetStatus(new ConnectionStatus(ConnectionPhase.Connected, _deviceName, _deviceId, _remote.Address, 0, null));
        Log.LogInformation("Device '{Name}' authenticated from {Remote}", _deviceName, _remote.Address);
        return true;
    }

    // ---- Session control ---------------------------------------------------

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        using var timeout = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            while (await timeout.WaitForNextTickAsync(watchdogCts.Token).ConfigureAwait(false))
            {
                if (_sinceRx.ElapsedMilliseconds > ProtocolConstants.SessionTimeoutMs)
                {
                    Log.LogWarning("Session with '{Name}' timed out", _deviceName);
                    _client.Close();
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ControlFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
                if (msg is null) return;
                _sinceRx.Restart();
                switch (msg)
                {
                    case PingMessage ping:
                        await SendAsync(new PongMessage(ping.Timestamp), ct);
                        if (ping.RoundTripMs is > 0 and < 10_000)
                        {
                            _rttMs = ping.RoundTripMs;
                            _server.SetStatus(_server.Status with { RoundTripMs = _rttMs });
                        }
                        break;
                    case StreamStartMessage start:
                        await HandleStreamStartAsync(start, ct);
                        break;
                    case StreamStopMessage:
                        StopStreaming();
                        break;
                    case ByeMessage bye:
                        Log.LogInformation("Device said bye: {Reason}", bye.Reason);
                        return;
                    case UnknownMessage u:
                        await SendAsync(new ErrorMessage(ErrorMessage.UnknownType, u.RawType), ct);
                        break;
                    default:
                        throw new ProtocolException($"Unexpected '{msg.Type}' after authentication");
                }
            }
        }
        finally
        {
            StopStreaming();
            watchdogCts.Cancel();
            try { await watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task HandleStreamStartAsync(StreamStartMessage start, CancellationToken ct)
    {
        string? problem = null;
        if (start.Codec != StreamStartMessage.CodecPcm16) problem = "unsupported_codec";
        else if (start.Channels is < 1 or > 2) problem = "unsupported_channels";
        else if (start.FrameMs is not (10 or 20)) problem = "unsupported_frame";
        else
        {
            try { SampleRates.ToCode(start.Rate); }
            catch (ArgumentOutOfRangeException) { problem = "unsupported_rate"; }
        }
        if (problem is not null)
        {
            await SendAsync(new StreamAckMessage(false, problem), ct);
            return;
        }

        int frameSamples = start.Rate * start.FrameMs / 1000;
        _server.Pipeline.Start(start.Rate, frameSamples);
        _streaming = true;
        _format = $"PCM {start.Rate / 1000.0:0.#} kHz {(start.Channels == 1 ? "mono" : "stereo")}, {start.FrameMs} ms frames";
        _server.SetStatus(new ConnectionStatus(ConnectionPhase.Streaming, _deviceName, _deviceId, _remote.Address, _rttMs, _format));
        await SendAsync(new StreamAckMessage(true), ct);
        Log.LogInformation("Streaming started: {Format}", _format);
    }

    private void StopStreaming()
    {
        if (!_streaming) return;
        _streaming = false;
        _server.Pipeline.Stop();
        if (_claimed && !_cts.IsCancellationRequested)
            _server.SetStatus(new ConnectionStatus(ConnectionPhase.Connected, _deviceName, _deviceId, _remote.Address, _rttMs, null));
        Log.LogInformation("Streaming stopped");
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var s = _server.Pipeline.GetStats();
                await SendAsync(new StatsMessage(s.FramesReceived, s.FramesLost, s.FramesLate, s.Underruns,
                    Math.Round(s.BufferMs, 1), Math.Round(s.JitterMs, 1), _server.SinkStatus.SinkName, _server.SinkStatus.SinkOk), ct);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }

    private async Task SendAsync(ControlMessage msg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ControlFraming.WriteAsync(_stream, msg, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    internal static bool IsValidDeviceId(string id) =>
        id.Length == 16 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string SanitizeName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return clean.Length == 0 ? "Android device" : clean;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
