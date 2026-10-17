using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DroidMic.Protocol;

/// <summary>Keys derived from one authenticated handshake (protocol §3.3).</summary>
public sealed class SessionKeys
{
    public required byte[] ClientConfirm { get; init; }
    public required byte[] ServerConfirm { get; init; }
    public required byte[] AudioKey { get; init; }
    public required byte[] PairingWrap { get; init; }
}

/// <summary>Pure functions implementing the handshake math. Shared by server and tests.</summary>
public static class HandshakeCrypto
{
    public static byte[] TranscriptHash(
        string deviceId, ReadOnlySpan<byte> clientEph, ReadOnlySpan<byte> clientNonce,
        string serverId, ReadOnlySpan<byte> identity, ReadOnlySpan<byte> serverEph, ReadOnlySpan<byte> serverNonce)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(ProtocolConstants.TranscriptLabel));
        WriteLp(ms, Encoding.UTF8.GetBytes(deviceId));
        WriteLp(ms, clientEph);
        WriteLp(ms, clientNonce);
        WriteLp(ms, Encoding.UTF8.GetBytes(serverId));
        WriteLp(ms, identity);
        WriteLp(ms, serverEph);
        WriteLp(ms, serverNonce);
        return SHA256.HashData(ms.ToArray());
    }

    private static void WriteLp(Stream s, ReadOnlySpan<byte> data)
    {
        if (data.Length > ushort.MaxValue) throw new ProtocolException("Field too long");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)data.Length);
        s.Write(len);
        s.Write(data);
    }

    public static SessionKeys DeriveKeys(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> authSecret, ReadOnlySpan<byte> transcriptHash)
    {
        byte[] ikm = new byte[sharedSecret.Length + authSecret.Length];
        sharedSecret.CopyTo(ikm);
        authSecret.CopyTo(ikm.AsSpan(sharedSecret.Length));
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, transcriptHash.ToArray());
        CryptographicOperations.ZeroMemory(ikm);
        try
        {
            return new SessionKeys
            {
                ClientConfirm = Expand(prk, ProtocolConstants.InfoClientConfirm, 32),
                ServerConfirm = Expand(prk, ProtocolConstants.InfoServerConfirm, 32),
                AudioKey = Expand(prk, ProtocolConstants.InfoAudioKey, 16),
                PairingWrap = Expand(prk, ProtocolConstants.InfoPairingWrap, 16),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private static byte[] Expand(byte[] prk, string info, int len) =>
        HKDF.Expand(HashAlgorithmName.SHA256, prk, len, Encoding.UTF8.GetBytes(info));

    public static byte[] Mac(byte[] key, ReadOnlySpan<byte> transcriptHash) => HMACSHA256.HashData(key, transcriptHash);

    public static bool VerifyMac(byte[] key, ReadOnlySpan<byte> transcriptHash, ReadOnlySpan<byte> mac) =>
        mac.Length == 32 && CryptographicOperations.FixedTimeEquals(Mac(key, transcriptHash), mac);

    /// <summary>ECDH on P-256 returning the raw 32-byte x-coordinate, matching Java's KeyAgreement("ECDH").</summary>
    public static byte[] Agree(ECDiffieHellman local, ReadOnlySpan<byte> peerSpki)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out int read);
        if (read != peerSpki.Length) throw new ProtocolException("Trailing data after public key");
        if (peer.KeySize != 256) throw new ProtocolException("Peer key is not P-256");
        return local.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public static byte[] WrapPairingKey(byte[] wrapKey, ReadOnlySpan<byte> pairingKey, ReadOnlySpan<byte> transcriptHash)
    {
        byte[] output = new byte[pairingKey.Length + 16];
        using var aes = new AesGcm(wrapKey, 16);
        aes.Encrypt(new byte[12], pairingKey, output.AsSpan(0, pairingKey.Length), output.AsSpan(pairingKey.Length), transcriptHash);
        return output;
    }

    public static byte[] UnwrapPairingKey(byte[] wrapKey, ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> transcriptHash)
    {
        if (wrapped.Length != ProtocolConstants.PairingKeyLength + 16) throw new ProtocolException("Bad wrapped key length");
        byte[] output = new byte[ProtocolConstants.PairingKeyLength];
        using var aes = new AesGcm(wrapKey, 16);
        aes.Decrypt(new byte[12], wrapped[..^16], wrapped[^16..], output, transcriptHash);
        return output;
    }

    public static string Fingerprint(ReadOnlySpan<byte> identitySpki) =>
        Convert.ToHexString(SHA256.HashData(identitySpki)[..6]).ToLowerInvariant();

    public static byte[] Sign(ECDsa identity, ReadOnlySpan<byte> transcriptHash) =>
        identity.SignData(transcriptHash, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool VerifySignature(ReadOnlySpan<byte> identitySpki, ReadOnlySpan<byte> transcriptHash, ReadOnlySpan<byte> signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(identitySpki, out _);
        return ecdsa.VerifyData(transcriptHash, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public static byte[] DecodeBase64(string? value, int? expectedLength, string field)
    {
        if (string.IsNullOrEmpty(value)) throw new ProtocolException($"Missing {field}");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw new ProtocolException($"{field} is not base64"); }
        if (expectedLength is int n && bytes.Length != n) throw new ProtocolException($"{field} has wrong length");
        if (bytes.Length > 512) throw new ProtocolException($"{field} is too long");
        return bytes;
    }
}
