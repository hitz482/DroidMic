namespace DroidMic.Protocol;

/// <summary>Constants shared with the Android implementation. See docs/AUDIO_PROTOCOL.md.</summary>
public static class ProtocolConstants
{
    public const int Version = 1;

    public const int DefaultControlPort = 48110;
    public const int DefaultAudioPort = 48111;
    public const int DiscoveryPort = 48112;

    public const int MaxControlMessageBytes = 65536;
    public const int MaxFrameSamples = 960;
    public const int MaxPayloadBytes = 3840;

    public const int KeepAliveIntervalMs = 1000;
    public const int SessionTimeoutMs = 5000;

    public const int MaxPinAttempts = 5;
    public const int PinLength = 6;

    public static ReadOnlySpan<byte> DiscoveryRequestMagic => "DMDISC1"u8;
    public const string DiscoveryReplyMagic = "DMSRV1";
    public const int DiscoveryNonceLength = 8;

    public const string TranscriptLabel = "DroidMic/v1";
    public const string InfoClientConfirm = "droidmic c2s confirm";
    public const string InfoServerConfirm = "droidmic s2c confirm";
    public const string InfoAudioKey = "droidmic audio";
    public const string InfoPairingWrap = "droidmic pairing wrap";

    public const int HandshakeNonceLength = 16;
    public const int PairingKeyLength = 32;
}

public enum AudioCodec : byte
{
    Pcm16 = 1,
}

public static class SampleRates
{
    public static int FromCode(byte code) => code switch
    {
        1 => 16000,
        2 => 24000,
        3 => 32000,
        4 => 44100,
        5 => 48000,
        _ => 0,
    };

    public static byte ToCode(int rate) => rate switch
    {
        16000 => 1,
        24000 => 2,
        32000 => 3,
        44100 => 4,
        48000 => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, "Unsupported sample rate"),
    };
}
