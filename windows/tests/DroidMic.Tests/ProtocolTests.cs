using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DroidMic.Protocol;
using Xunit;

namespace DroidMic.Tests;

public class ProtocolTests
{
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "test-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);
    private static byte[] B64(JsonElement e, string name) => Convert.FromBase64String(e.GetProperty(name).GetString()!);

    [Fact]
    public void KeySchedule_MatchesIndependentVectors()
    {
        var h = Vectors.GetProperty("handshake");
        byte[] th = HandshakeCrypto.TranscriptHash(h.GetProperty("deviceId").GetString()!, B64(h, "clientEph"), B64(h, "clientNonce"),
            h.GetProperty("serverId").GetString()!, B64(h, "identity"), B64(h, "serverEph"), B64(h, "serverNonce"));
        Assert.Equal(Hex(h, "transcriptHashHex"), th);

        var keys = HandshakeCrypto.DeriveKeys(Hex(h, "sharedSecretHex"), Encoding.UTF8.GetBytes(h.GetProperty("authSecretUtf8").GetString()!), th);
        Assert.Equal(Hex(h, "clientConfirmHex"), keys.ClientConfirm);
        Assert.Equal(Hex(h, "serverConfirmHex"), keys.ServerConfirm);
        Assert.Equal(Hex(h, "audioKeyHex"), keys.AudioKey);
        Assert.Equal(Hex(h, "pairingWrapHex"), keys.PairingWrap);
        Assert.Equal(Hex(h, "clientMacHex"), HandshakeCrypto.Mac(keys.ClientConfirm, th));
        Assert.Equal(h.GetProperty("fingerprintOfIdentity").GetString(), HandshakeCrypto.Fingerprint(B64(h, "identity")));
    }

    [Fact]
    public void AudioPacket_EncodeMatchesVector_AndDecodes()
    {
        var a = Vectors.GetProperty("audioPacket");
        using var codec = new AudioPacketCodec(Hex(a, "keyHex"), a.GetProperty("sessionId").GetUInt32());
        byte[] pcm = Hex(a, "pcmHex");
        byte[] buf = new byte[AudioPacketCodec.MaxDatagramLength];
        int len = codec.Encode(buf, a.GetProperty("seq").GetUInt32(), a.GetProperty("timestamp").GetUInt32(),
            a.GetProperty("sampleRate").GetInt32(), (byte)a.GetProperty("channels").GetInt32(),
            (AudioFrameFlags)a.GetProperty("flags").GetInt32(), pcm);
        Assert.Equal(Hex(a, "datagramHex"), buf[..len]);

        byte[] outPcm = new byte[ProtocolConstants.MaxPayloadBytes];
        Assert.Equal(PacketError.None, codec.TryDecode(buf.AsSpan(0, len), outPcm, out var header));
        Assert.Equal(pcm, outPcm[..pcm.Length]);
        Assert.Equal(7u, header.Sequence);
        Assert.Equal(48000, header.SampleRate);
        Assert.Equal(AudioFrameFlags.Clipped, header.Flags);
    }

    private static (AudioPacketCodec codec, byte[] datagram) MakePacket(uint session = 42, uint seq = 1)
    {
        var codec = new AudioPacketCodec(RandomNumberGenerator.GetBytes(16), session);
        byte[] buf = new byte[AudioPacketCodec.MaxDatagramLength];
        int len = codec.Encode(buf, seq, 0, 48000, 1, AudioFrameFlags.None, new byte[960]);
        return (codec, buf[..len]);
    }

    [Fact]
    public void AudioPacket_TamperedPayloadOrHeader_IsRejected()
    {
        var (codec, dg) = MakePacket();
        using var owned = codec;
        byte[] pcm = new byte[4000];

        var payloadFlip = (byte[])dg.Clone();
        payloadFlip[30] ^= 1;
        Assert.Equal(PacketError.AuthenticationFailed, codec.TryDecode(payloadFlip, pcm, out _));

        var headerFlip = (byte[])dg.Clone();
        headerFlip[12] ^= 1; // timestamp is authenticated as AAD
        Assert.Equal(PacketError.AuthenticationFailed, codec.TryDecode(headerFlip, pcm, out _));
    }

    [Fact]
    public void AudioPacket_WrongKeyOrSession_IsRejected()
    {
        var (codec, dg) = MakePacket(session: 42);
        codec.Dispose();
        using var other = new AudioPacketCodec(RandomNumberGenerator.GetBytes(16), 42);
        Assert.Equal(PacketError.AuthenticationFailed, other.TryDecode(dg, new byte[4000], out _));
        using var otherSession = new AudioPacketCodec(RandomNumberGenerator.GetBytes(16), 43);
        Assert.Equal(PacketError.WrongSession, otherSession.TryDecode(dg, new byte[4000], out _));
    }

    [Theory]
    [InlineData(0, PacketError.TooShort)]
    [InlineData(39, PacketError.TooShort)]
    public void AudioPacket_ShortDatagrams_AreRejected(int length, PacketError expected)
    {
        Assert.Equal(expected, AudioPacketCodec.TryParseHeader(new byte[length], out _));
    }

    [Fact]
    public void AudioPacket_MalformedHeaders_AreRejected()
    {
        var (codec, dg) = MakePacket();
        codec.Dispose();

        PacketError Mutate(Action<byte[]> m) { var c = (byte[])dg.Clone(); m(c); return AudioPacketCodec.TryParseHeader(c, out _); }

        Assert.Equal(PacketError.BadMagic, Mutate(d => d[0] = 0));
        Assert.Equal(PacketError.BadVersion, Mutate(d => d[2] = 9));
        Assert.Equal(PacketError.BadType, Mutate(d => d[3] = 2));
        Assert.Equal(PacketError.BadCodec, Mutate(d => d[16] = 7));
        Assert.Equal(PacketError.BadFormat, Mutate(d => d[17] = 3));
        Assert.Equal(PacketError.BadFormat, Mutate(d => d[18] = 99));
        Assert.Equal(PacketError.BadFormat, Mutate(d => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(20), 961)));
        Assert.Equal(PacketError.BadLength, Mutate(d => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(20), 479)));
        Assert.Equal(PacketError.BadLength, AudioPacketCodec.TryParseHeader(dg.Concat(new byte[1]).ToArray(), out _));
    }

    [Fact]
    public void ReplayWindow_RejectsDuplicatesAndOldPackets()
    {
        var w = new ReplayWindow();
        Assert.True(w.Check(10)); w.Commit(10);
        Assert.False(w.Check(10));
        Assert.True(w.Check(8)); w.Commit(8);
        Assert.False(w.Check(8));
        Assert.True(w.Check(100)); w.Commit(100);
        Assert.False(w.Check(36)); // 100 - 36 = 64, outside window
        Assert.True(w.Check(37)); w.Commit(37);
        Assert.False(w.Check(37));
        Assert.True(w.Check(1000)); w.Commit(1000);
        Assert.False(w.Check(100));
    }

    [Fact]
    public void ControlMessages_RoundTrip()
    {
        ControlMessage[] all =
        [
            new HelloMessage(1, "0123456789abcdef", "Pixel", "ZXBo", "bm9uY2U=", HelloMessage.ModePin),
            new AuthOkMessage("bWFj", 99, 48111, null),
            new StreamStartMessage("pcm16", 48000, 1, 10),
            new PingMessage(123456789, 12.5),
            new StatsMessage(1, 2, 3, 4, 5.5, 6.5, "DroidMic Microphone", true),
            new StreamStopMessage(),
        ];
        foreach (var m in all)
        {
            byte[] json = ControlMessageSerializer.Serialize(m);
            Assert.StartsWith("{\"t\":", Encoding.UTF8.GetString(json));
            Assert.Equal(m, ControlMessageSerializer.Deserialize(json));
        }
        Assert.DoesNotContain("pairing", Encoding.UTF8.GetString(ControlMessageSerializer.Serialize(all[1])));
    }

    [Fact]
    public void ControlMessages_RejectInvalid()
    {
        Assert.Throws<ProtocolException>(() => ControlMessageSerializer.Deserialize("not json"u8));
        Assert.Throws<ProtocolException>(() => ControlMessageSerializer.Deserialize("[1,2]"u8));
        Assert.Throws<ProtocolException>(() => ControlMessageSerializer.Deserialize("{\"x\":1}"u8));
        Assert.Throws<ProtocolException>(() => ControlMessageSerializer.Deserialize("{\"t\":\"auth\"}"u8));
        Assert.Throws<ProtocolException>(() => ControlMessageSerializer.Deserialize("{\"t\":\"ping\",\"ts\":\"abc\"}"u8));
        Assert.IsType<UnknownMessage>(ControlMessageSerializer.Deserialize("{\"t\":\"future\",\"x\":1}"u8));
        // Unknown fields are ignored for forward compatibility.
        Assert.IsType<PingMessage>(ControlMessageSerializer.Deserialize("{\"t\":\"ping\",\"ts\":5,\"extra\":true}"u8));
    }

    [Fact]
    public async Task Framing_RejectsOversizedAndTruncatedFrames()
    {
        var big = new MemoryStream([0x00, 0x02, 0x00, 0x01]);
        await Assert.ThrowsAsync<ProtocolException>(() => ControlFraming.ReadAsync(big, default));
        var zero = new MemoryStream([0, 0, 0, 0]);
        await Assert.ThrowsAsync<ProtocolException>(() => ControlFraming.ReadAsync(zero, default));
        var truncated = new MemoryStream([0, 0, 0, 10, (byte)'{']);
        await Assert.ThrowsAsync<ProtocolException>(() => ControlFraming.ReadAsync(truncated, default));
        Assert.Null(await ControlFraming.ReadAsync(new MemoryStream(), default));

        var ms = new MemoryStream();
        await ControlFraming.WriteAsync(ms, new PingMessage(7), default);
        ms.Position = 0;
        Assert.Equal(new PingMessage(7), await ControlFraming.ReadAsync(ms, default));
    }

    [Fact]
    public void Handshake_EcdhAndSignatures_Interoperate()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] z1 = HandshakeCrypto.Agree(a, b.ExportSubjectPublicKeyInfo());
        byte[] z2 = HandshakeCrypto.Agree(b, a.ExportSubjectPublicKeyInfo());
        Assert.Equal(32, z1.Length);
        Assert.Equal(z1, z2);

        using var id = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] th = SHA256.HashData("x"u8);
        byte[] sig = HandshakeCrypto.Sign(id, th);
        Assert.True(HandshakeCrypto.VerifySignature(id.ExportSubjectPublicKeyInfo(), th, sig));
        th[0] ^= 1;
        Assert.False(HandshakeCrypto.VerifySignature(id.ExportSubjectPublicKeyInfo(), th, sig));
    }

    [Fact]
    public void PairingKeyWrap_RoundTripsAndBindsTranscript()
    {
        byte[] k = RandomNumberGenerator.GetBytes(16);
        byte[] th = RandomNumberGenerator.GetBytes(32);
        byte[] pk = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = HandshakeCrypto.WrapPairingKey(k, pk, th);
        Assert.Equal(pk, HandshakeCrypto.UnwrapPairingKey(k, wrapped, th));
        th[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => HandshakeCrypto.UnwrapPairingKey(k, wrapped, th));
    }

    [Fact]
    public void Discovery_ParsesOnlyValidRequests()
    {
        byte[] nonce = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.True(DiscoveryCodec.TryParseRequest(DiscoveryCodec.BuildRequest(nonce), out var n));
        Assert.Equal(nonce, n);
        Assert.False(DiscoveryCodec.TryParseRequest("DMDISC1"u8, out _));
        Assert.False(DiscoveryCodec.TryParseRequest("XXDISC112345678"u8, out _));
        Assert.Contains("pin=123456", DiscoveryCodec.BuildPairingUri("192.168.1.5", 48110, "123456", "abcdef012345", "My PC"));
    }
}
