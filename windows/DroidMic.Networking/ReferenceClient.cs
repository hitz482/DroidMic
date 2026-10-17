using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DroidMic.Protocol;

namespace DroidMic.Networking;

/// <summary>
/// A minimal sender implementation of the phone side of the protocol, used by
/// automated tests and by <c>DroidMic.ReceiverCli --simulate-phone</c>. The real
/// sender is the Android app (android/core).
/// </summary>
public sealed class ReferenceClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Socket? _udp;
    private AudioPacketCodec? _codec;
    private IPEndPoint? _audioEndpoint;
    private uint _seq;
    private uint _timestamp;
    private readonly byte[] _datagram = new byte[AudioPacketCodec.MaxDatagramLength];

    public string DeviceId { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public string DeviceName { get; init; } = "Reference client";

    public byte[]? PairingKey { get; private set; }
    public string? ServerFingerprint { get; private set; }
    public uint SessionId { get; private set; }

    /// <summary>Connects and authenticates. Pass a PIN to pair, or a pairing key to reconnect.</summary>
    public async Task<ControlMessage> ConnectAsync(IPEndPoint control, string? pin, byte[]? pairingKey, CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(control, ct);
        _stream = _tcp.GetStream();

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] clientEph = eph.ExportSubjectPublicKeyInfo();
        byte[] clientNonce = RandomNumberGenerator.GetBytes(ProtocolConstants.HandshakeNonceLength);
        string mode = pin is not null ? HelloMessage.ModePin : HelloMessage.ModePaired;
        await ControlFraming.WriteAsync(_stream, new HelloMessage(ProtocolConstants.Version, DeviceId, DeviceName,
            Convert.ToBase64String(clientEph), Convert.ToBase64String(clientNonce), mode), ct);

        var reply = await ControlFraming.ReadAsync(_stream, ct);
        if (reply is not ServerHelloMessage sh) return reply ?? new ErrorMessage("closed");

        byte[] identity = Convert.FromBase64String(sh.Identity);
        byte[] serverEph = Convert.FromBase64String(sh.Ephemeral);
        byte[] serverNonce = Convert.FromBase64String(sh.Nonce);
        byte[] th = HandshakeCrypto.TranscriptHash(DeviceId, clientEph, clientNonce, sh.ServerId, identity, serverEph, serverNonce);
        if (!HandshakeCrypto.VerifySignature(identity, th, Convert.FromBase64String(sh.Signature)))
            throw new ProtocolException("Server signature invalid");
        ServerFingerprint = HandshakeCrypto.Fingerprint(identity);

        byte[] secret = pin is not null ? Encoding.UTF8.GetBytes(pin) : pairingKey ?? throw new ArgumentNullException(nameof(pairingKey));
        var keys = HandshakeCrypto.DeriveKeys(HandshakeCrypto.Agree(eph, serverEph), secret, th);
        await ControlFraming.WriteAsync(_stream, new AuthMessage(Convert.ToBase64String(HandshakeCrypto.Mac(keys.ClientConfirm, th))), ct);

        var result = await ControlFraming.ReadAsync(_stream, ct);
        if (result is not AuthOkMessage ok) return result ?? new ErrorMessage("closed");
        if (!HandshakeCrypto.VerifyMac(keys.ServerConfirm, th, Convert.FromBase64String(ok.Mac)))
            throw new ProtocolException("Server confirmation MAC invalid");
        if (ok.Pairing is not null)
            PairingKey = HandshakeCrypto.UnwrapPairingKey(keys.PairingWrap, Convert.FromBase64String(ok.Pairing), th);
        else
            PairingKey = pairingKey;

        SessionId = ok.SessionId;
        _codec = new AudioPacketCodec(keys.AudioKey, ok.SessionId);
        _audioEndpoint = new IPEndPoint(control.Address, ok.AudioPort);
        _udp = new Socket(control.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        return ok;
    }

    public async Task<StreamAckMessage> StartStreamAsync(int rate, int channels, int frameMs, CancellationToken ct)
    {
        await SendAsync(new StreamStartMessage(StreamStartMessage.CodecPcm16, rate, channels, frameMs), ct);
        while (true)
        {
            var msg = await ControlFraming.ReadAsync(_stream!, ct) ?? throw new IOException("Closed");
            if (msg is StreamAckMessage ack) return ack;
        }
    }

    public Task SendAsync(ControlMessage msg, CancellationToken ct) => ControlFraming.WriteAsync(_stream!, msg, ct);

    public Task<ControlMessage?> ReadAsync(CancellationToken ct) => ControlFraming.ReadAsync(_stream!, ct);

    /// <summary>Encrypts and sends one frame of little-endian PCM.</summary>
    public void SendFrame(ReadOnlySpan<byte> pcm, int rate, byte channels, AudioFrameFlags flags = AudioFrameFlags.None)
    {
        int len = _codec!.Encode(_datagram, _seq, _timestamp, rate, channels, flags, pcm);
        _udp!.SendTo(_datagram.AsSpan(0, len), SocketFlags.None, _audioEndpoint!);
        _seq++;
        _timestamp += (uint)(pcm.Length / (2 * channels));
    }

    /// <summary>Encodes a frame without sending it (for replay/tamper tests).</summary>
    public byte[] EncodeFrame(ReadOnlySpan<byte> pcm, int rate, byte channels, uint seq)
    {
        int len = _codec!.Encode(_datagram, seq, seq * (uint)(pcm.Length / 2), rate, channels, AudioFrameFlags.None, pcm);
        return _datagram.AsSpan(0, len).ToArray();
    }

    public void SendRaw(ReadOnlySpan<byte> datagram) => _udp!.SendTo(datagram, SocketFlags.None, _audioEndpoint!);

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try { await ControlFraming.WriteAsync(_stream, new ByeMessage("client_closing"), CancellationToken.None); }
            catch (Exception) { /* connection may already be gone */ }
        }
        _udp?.Dispose();
        _codec?.Dispose();
        _tcp?.Dispose();
    }
}
