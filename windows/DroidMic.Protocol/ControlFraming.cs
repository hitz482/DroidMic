using System.Buffers.Binary;

namespace DroidMic.Protocol;

/// <summary>Length-prefixed framing for the TCP control channel (protocol §2).</summary>
public static class ControlFraming
{
    public static async Task WriteAsync(Stream stream, ControlMessage message, CancellationToken ct)
    {
        byte[] body = ControlMessageSerializer.Serialize(message);
        if (body.Length > ProtocolConstants.MaxControlMessageBytes)
            throw new ProtocolException("Control message too large");
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message, or returns null on a clean end of stream.</summary>
    public static async Task<ControlMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, lenBuf, ct).ConfigureAwait(false)) return null;
        uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (len == 0 || len > ProtocolConstants.MaxControlMessageBytes)
            throw new ProtocolException($"Invalid control frame length {len}");
        byte[] body = new byte[len];
        if (!await ReadExactlyOrEofAsync(stream, body, ct).ConfigureAwait(false))
            throw new ProtocolException("Connection closed mid-frame");
        return ControlMessageSerializer.Deserialize(body);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new ProtocolException("Connection closed mid-frame");
            }
            read += n;
        }
        return true;
    }
}
