using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DroidMic.AudioEngine;
using DroidMic.Protocol;
using Microsoft.Extensions.Logging;

namespace DroidMic.Networking;

/// <summary>Counters for datagrams rejected before reaching the pipeline.</summary>
public sealed class ReceiverCounters
{
    private long _accepted, _malformed, _wrongSession, _wrongSource, _replayed, _authFailed;
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Malformed => Interlocked.Read(ref _malformed);
    public long WrongSession => Interlocked.Read(ref _wrongSession);
    public long WrongSource => Interlocked.Read(ref _wrongSource);
    public long Replayed => Interlocked.Read(ref _replayed);
    public long AuthFailed => Interlocked.Read(ref _authFailed);
    public long Bytes;

    internal void IncAccepted() => Interlocked.Increment(ref _accepted);
    internal void IncMalformed() => Interlocked.Increment(ref _malformed);
    internal void IncWrongSession() => Interlocked.Increment(ref _wrongSession);
    internal void IncWrongSource() => Interlocked.Increment(ref _wrongSource);
    internal void IncReplayed() => Interlocked.Increment(ref _replayed);
    internal void IncAuthFailed() => Interlocked.Increment(ref _authFailed);
}

/// <summary>
/// UDP audio receiver (protocol §5). Exactly one session is active at a time; the
/// session's codec, expected source address and replay window live in
/// <see cref="ActiveAudioSession"/>.
/// </summary>
public sealed class AudioReceiver : IDisposable
{
    private readonly AudioPipeline _pipeline;
    private readonly ILogger _log;
    private readonly Socket _socket;
    private ActiveAudioSession? _session;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AudioReceiver(AudioPipeline pipeline, int port, ILogger log, IPAddress? bindAddress = null)
    {
        _pipeline = pipeline;
        _log = log;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.ReceiveBufferSize = 256 * 1024;
        _socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP port-unreachable resets (WSAECONNRESET) on the UDP socket.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;
    public ReceiverCounters Counters { get; } = new();
    public DateTime LastPacketUtc { get; private set; }

    public void SetSession(ActiveAudioSession? session)
    {
        var old = Interlocked.Exchange(ref _session, session);
        old?.Dispose();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => ReceiveLoop(_cts.Token), TaskCreationOptions.LongRunning).Unwrap();
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        byte[] buf = new byte[2048];
        byte[] pcm = new byte[ProtocolConstants.MaxPayloadBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.LogDebug("UDP receive error {Code}", ex.SocketErrorCode);
                continue;
            }
            Handle(buf.AsSpan(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint, pcm);
        }
    }

    internal void Handle(ReadOnlySpan<byte> datagram, IPEndPoint from, byte[] pcm)
    {
        var session = Volatile.Read(ref _session);
        if (session is null) { Counters.IncWrongSession(); return; }
        if (!from.Address.Equals(session.Peer)) { Counters.IncWrongSource(); return; }

        var err = AudioPacketCodec.TryParseHeader(datagram, out var header);
        if (err != PacketError.None) { Counters.IncMalformed(); return; }
        if (header.SessionId != session.SessionId) { Counters.IncWrongSession(); return; }

        lock (session)
        {
            if (session.Disposed) return;
            if (!session.Replay.Check(header.Sequence)) { Counters.IncReplayed(); return; }
            err = session.Codec.TryDecode(datagram, pcm, out header);
            if (err != PacketError.None) { Counters.IncAuthFailed(); return; }
            session.Replay.Commit(header.Sequence);
        }

        Counters.IncAccepted();
        Interlocked.Add(ref Counters.Bytes, datagram.Length);
        LastPacketUtc = DateTime.UtcNow;
        int payload = header.FrameSamples * header.Channels * 2;
        var samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, payload));
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < samples.Length; i++) samples[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(samples[i]);
        _pipeline.PushFrame(header.Sequence, header.Timestamp, samples, header.Channels,
            (header.Flags & AudioFrameFlags.Clipped) != 0);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket.Dispose();
        try { _loop?.Wait(1000); } catch (AggregateException) { }
        SetSession(null);
    }
}

/// <summary>Per-session audio decryption state.</summary>
public sealed class ActiveAudioSession : IDisposable
{
    public ActiveAudioSession(uint sessionId, byte[] audioKey, IPAddress peer)
    {
        SessionId = sessionId;
        Peer = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        Codec = new AudioPacketCodec(audioKey, sessionId);
    }

    public uint SessionId { get; }
    public IPAddress Peer { get; }
    public AudioPacketCodec Codec { get; }
    public ReplayWindow Replay { get; } = new();
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        lock (this)
        {
            if (Disposed) return;
            Disposed = true;
            Codec.Dispose();
        }
    }
}
