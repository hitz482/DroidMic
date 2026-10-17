using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidMic.Protocol;

/// <summary>Base type of control-channel messages (protocol §3, §4).</summary>
public abstract record ControlMessage
{
    [JsonPropertyName("t")]
    [JsonPropertyOrder(-1)]
    public abstract string Type { get; }
}

public sealed record HelloMessage(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("eph")] string Ephemeral,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("mode")] string Mode) : ControlMessage
{
    public const string ModePin = "pin";
    public const string ModePaired = "paired";
    public override string Type => "hello";
}

public sealed record ServerHelloMessage(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("eph")] string Ephemeral,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("sig")] string Signature) : ControlMessage
{
    public override string Type => "server_hello";
}

public sealed record AuthMessage([property: JsonPropertyName("mac")] string Mac) : ControlMessage
{
    public override string Type => "auth";
}

public sealed record AuthOkMessage(
    [property: JsonPropertyName("mac")] string Mac,
    [property: JsonPropertyName("sessionId")] uint SessionId,
    [property: JsonPropertyName("audioPort")] int AudioPort,
    [property: JsonPropertyName("pairing")] string? Pairing) : ControlMessage
{
    public override string Type => "auth_ok";
}

public sealed record AuthFailedMessage([property: JsonPropertyName("reason")] string Reason) : ControlMessage
{
    public const string BadCredentials = "bad_credentials";
    public const string UnknownDevice = "unknown_device";
    public const string PairingClosed = "pairing_closed";
    public const string LockedOut = "locked_out";
    public override string Type => "auth_failed";
}

public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message = null) : ControlMessage
{
    public const string Busy = "busy";
    public const string PairingClosed = "pairing_closed";
    public const string UnsupportedVersion = "unsupported_version";
    public const string Protocol = "protocol";
    public const string UnknownType = "unknown_type";
    public override string Type => "error";
}

public sealed record StreamStartMessage(
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("rate")] int Rate,
    [property: JsonPropertyName("ch")] int Channels,
    [property: JsonPropertyName("frameMs")] int FrameMs) : ControlMessage
{
    public const string CodecPcm16 = "pcm16";
    public override string Type => "stream_start";
}

public sealed record StreamAckMessage(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason = null) : ControlMessage
{
    public override string Type => "stream_ack";
}

public sealed record StreamStopMessage : ControlMessage
{
    public override string Type => "stream_stop";
}

public sealed record PingMessage(
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("rtt")] double RoundTripMs = 0) : ControlMessage
{
    public override string Type => "ping";
}

public sealed record PongMessage([property: JsonPropertyName("ts")] long Timestamp) : ControlMessage
{
    public override string Type => "pong";
}

public sealed record StatsMessage(
    [property: JsonPropertyName("received")] long Received,
    [property: JsonPropertyName("lost")] long Lost,
    [property: JsonPropertyName("late")] long Late,
    [property: JsonPropertyName("underruns")] long Underruns,
    [property: JsonPropertyName("bufferMs")] double BufferMs,
    [property: JsonPropertyName("jitterMs")] double JitterMs,
    [property: JsonPropertyName("sinkName")] string SinkName,
    [property: JsonPropertyName("sinkOk")] bool SinkOk) : ControlMessage
{
    public override string Type => "stats";
}

public sealed record ByeMessage([property: JsonPropertyName("reason")] string Reason) : ControlMessage
{
    public override string Type => "bye";
}

/// <summary>A well-formed message with a type this implementation does not know.</summary>
public sealed record UnknownMessage(string RawType) : ControlMessage
{
    public override string Type => RawType;
}

public static class ControlMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly Dictionary<string, Type> Types = new()
    {
        ["hello"] = typeof(HelloMessage),
        ["server_hello"] = typeof(ServerHelloMessage),
        ["auth"] = typeof(AuthMessage),
        ["auth_ok"] = typeof(AuthOkMessage),
        ["auth_failed"] = typeof(AuthFailedMessage),
        ["error"] = typeof(ErrorMessage),
        ["stream_start"] = typeof(StreamStartMessage),
        ["stream_ack"] = typeof(StreamAckMessage),
        ["stream_stop"] = typeof(StreamStopMessage),
        ["ping"] = typeof(PingMessage),
        ["pong"] = typeof(PongMessage),
        ["stats"] = typeof(StatsMessage),
        ["bye"] = typeof(ByeMessage),
    };

    public static byte[] Serialize(ControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);

    /// <exception cref="ProtocolException">The payload is not a valid control message.</exception>
    public static ControlMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
                throw new ProtocolException("Control message has no type");
            string type = t.GetString()!;
            if (!Types.TryGetValue(type, out var clr)) return new UnknownMessage(type);
            var msg = (ControlMessage?)doc.RootElement.Deserialize(clr, Options)
                      ?? throw new ProtocolException($"Empty '{type}' message");
            ValidateRequiredStrings(msg);
            return msg;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Malformed control message: " + ex.Message, ex);
        }
    }

    // System.Text.Json leaves missing constructor parameters as null even for
    // non-nullable strings; reject those so handlers can trust the record.
    private static void ValidateRequiredStrings(ControlMessage msg)
    {
        foreach (var p in msg.GetType().GetProperties())
        {
            if (p.PropertyType != typeof(string) || p.Name == nameof(ControlMessage.Type)) continue;
            var nullability = new System.Reflection.NullabilityInfoContext().Create(p);
            if (nullability.ReadState == System.Reflection.NullabilityState.NotNull && p.GetValue(msg) is null)
                throw new ProtocolException($"'{msg.Type}' is missing field '{p.Name}'");
        }
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
