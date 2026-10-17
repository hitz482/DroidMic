using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidMic.Protocol;

/// <summary>JSON body of a discovery reply (protocol §1).</summary>
public sealed record DiscoveryReply(
    [property: JsonPropertyName("magic")] string Magic,
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ctl")] int ControlPort,
    [property: JsonPropertyName("fp")] string Fingerprint)
{
    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}

public static class DiscoveryCodec
{
    /// <summary>Returns the request nonce if <paramref name="datagram"/> is a valid discovery request.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> datagram, out byte[] nonce)
    {
        nonce = [];
        var magic = ProtocolConstants.DiscoveryRequestMagic;
        if (datagram.Length != magic.Length + ProtocolConstants.DiscoveryNonceLength) return false;
        if (!datagram[..magic.Length].SequenceEqual(magic)) return false;
        nonce = datagram[magic.Length..].ToArray();
        return true;
    }

    public static byte[] BuildRequest(ReadOnlySpan<byte> nonce)
    {
        var magic = ProtocolConstants.DiscoveryRequestMagic;
        byte[] buf = new byte[magic.Length + nonce.Length];
        magic.CopyTo(buf);
        nonce.CopyTo(buf.AsSpan(magic.Length));
        return buf;
    }

    /// <summary>Builds the pairing URI encoded in the QR code shown by the Windows app.</summary>
    public static string BuildPairingUri(string host, int port, string pin, string fingerprint, string serverName) =>
        $"droidmic://pair?h={Uri.EscapeDataString(host)}&p={port}&pin={pin}&fp={fingerprint}&n={Uri.EscapeDataString(serverName)}";
}
