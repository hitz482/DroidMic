using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DroidMic.Protocol;

[Flags]
public enum AudioFrameFlags : byte
{
    None = 0,
    Clipped = 1,
    Muted = 2,
}

/// <summary>Parsed, authenticated header of an audio datagram.</summary>
public readonly record struct AudioPacketHeader(
    uint SessionId,
    uint Sequence,
    uint Timestamp,
    AudioCodec Codec,
    byte Channels,
    int SampleRate,
    AudioFrameFlags Flags,
    int FrameSamples);

public enum PacketError
{
    None,
    TooShort,
    BadMagic,
    BadVersion,
    BadType,
    BadCodec,
    BadFormat,
    BadLength,
    WrongSession,
    AuthenticationFailed,
}

/// <summary>
/// Encodes and decodes encrypted audio datagrams (protocol §5). Instances are
/// bound to one session key; they are not thread-safe for concurrent use of the
/// same instance, but hold no per-call allocations on the hot path.
/// </summary>
public sealed class AudioPacketCodec : IDisposable
{
    public const int HeaderLength = 24;
    public const int TagLength = 16;
    public const int NonceLength = 12;
    public const int MaxDatagramLength = HeaderLength + ProtocolConstants.MaxPayloadBytes + TagLength;

    private const byte MagicD = 0x44;
    private const byte MagicM = 0x4D;
    private const byte TypeAudio = 1;

    private readonly AesGcm _aes;
    private readonly uint _sessionId;

    public AudioPacketCodec(ReadOnlySpan<byte> audioKey, uint sessionId)
    {
        if (audioKey.Length != 16) throw new ArgumentException("Audio key must be 16 bytes", nameof(audioKey));
        _aes = new AesGcm(audioKey, TagLength);
        _sessionId = sessionId;
    }

    public uint SessionId => _sessionId;

    public static int DatagramLength(int frameSamples, int channels) =>
        HeaderLength + frameSamples * channels * 2 + TagLength;

    /// <summary>Writes a datagram into <paramref name="destination"/> and returns its length.</summary>
    public int Encode(Span<byte> destination, uint sequence, uint timestamp, int sampleRate, byte channels,
        AudioFrameFlags flags, ReadOnlySpan<byte> pcm)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (pcm.Length % (2 * channels) != 0) throw new ArgumentException("PCM length is not a whole frame", nameof(pcm));
        int frameSamples = pcm.Length / (2 * channels);
        if (frameSamples is 0 or > ProtocolConstants.MaxFrameSamples) throw new ArgumentException("Frame too large", nameof(pcm));
        int total = HeaderLength + pcm.Length + TagLength;
        if (destination.Length < total) throw new ArgumentException("Destination too small", nameof(destination));

        Span<byte> header = destination[..HeaderLength];
        header[0] = MagicD;
        header[1] = MagicM;
        header[2] = ProtocolConstants.Version;
        header[3] = TypeAudio;
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], _sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], timestamp);
        header[16] = (byte)AudioCodec.Pcm16;
        header[17] = channels;
        header[18] = SampleRates.ToCode(sampleRate);
        header[19] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(header[20..], (ushort)frameSamples);
        BinaryPrimitives.WriteUInt16BigEndian(header[22..], 0);

        Span<byte> nonce = stackalloc byte[NonceLength];
        BuildNonce(nonce, _sessionId, sequence);
        _aes.Encrypt(nonce, pcm, destination.Slice(HeaderLength, pcm.Length),
            destination.Slice(HeaderLength + pcm.Length, TagLength), header);
        return total;
    }

    /// <summary>
    /// Parses only the cleartext header without authenticating. Used to route and
    /// sanity-check a datagram before spending time on decryption.
    /// </summary>
    public static PacketError TryParseHeader(ReadOnlySpan<byte> datagram, out AudioPacketHeader header)
    {
        header = default;
        if (datagram.Length < HeaderLength + TagLength) return PacketError.TooShort;
        if (datagram[0] != MagicD || datagram[1] != MagicM) return PacketError.BadMagic;
        if (datagram[2] != ProtocolConstants.Version) return PacketError.BadVersion;
        if (datagram[3] != TypeAudio) return PacketError.BadType;
        if (datagram[16] != (byte)AudioCodec.Pcm16) return PacketError.BadCodec;

        byte channels = datagram[17];
        int rate = SampleRates.FromCode(datagram[18]);
        int frameSamples = BinaryPrimitives.ReadUInt16BigEndian(datagram[20..]);
        if (channels is < 1 or > 2 || rate == 0 || frameSamples is 0 or > ProtocolConstants.MaxFrameSamples)
            return PacketError.BadFormat;

        int payload = frameSamples * channels * 2;
        if (payload > ProtocolConstants.MaxPayloadBytes || datagram.Length != HeaderLength + payload + TagLength)
            return PacketError.BadLength;

        header = new AudioPacketHeader(
            BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[12..]),
            AudioCodec.Pcm16,
            channels,
            rate,
            (AudioFrameFlags)datagram[19],
            frameSamples);
        return PacketError.None;
    }

    /// <summary>Validates, authenticates and decrypts a datagram into <paramref name="pcmOut"/>.</summary>
    public PacketError TryDecode(ReadOnlySpan<byte> datagram, Span<byte> pcmOut, out AudioPacketHeader header)
    {
        var err = TryParseHeader(datagram, out header);
        if (err != PacketError.None) return err;
        if (header.SessionId != _sessionId) return PacketError.WrongSession;

        int payload = header.FrameSamples * header.Channels * 2;
        if (pcmOut.Length < payload) throw new ArgumentException("Output buffer too small", nameof(pcmOut));

        Span<byte> nonce = stackalloc byte[NonceLength];
        BuildNonce(nonce, header.SessionId, header.Sequence);
        try
        {
            _aes.Decrypt(nonce, datagram.Slice(HeaderLength, payload), datagram.Slice(HeaderLength + payload, TagLength),
                pcmOut[..payload], datagram[..HeaderLength]);
        }
        catch (AuthenticationTagMismatchException)
        {
            pcmOut[..payload].Clear();
            return PacketError.AuthenticationFailed;
        }
        return PacketError.None;
    }

    private static void BuildNonce(Span<byte> nonce, uint sessionId, uint seq)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], seq);
    }

    public void Dispose() => _aes.Dispose();
}
