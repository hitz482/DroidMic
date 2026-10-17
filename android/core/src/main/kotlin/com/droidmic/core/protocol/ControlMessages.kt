package com.droidmic.core.protocol

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** Control-channel messages (protocol §3–4). The JSON discriminator is `"t"`. */
@Serializable
sealed class ControlMessage

@Serializable
@SerialName("hello")
data class Hello(
    val v: Int,
    val deviceId: String,
    val deviceName: String,
    val eph: String,
    val nonce: String,
    val mode: String,
) : ControlMessage() {
    companion object {
        const val MODE_PIN = "pin"
        const val MODE_PAIRED = "paired"
    }
}

@Serializable
@SerialName("server_hello")
data class ServerHello(
    val v: Int,
    val serverId: String,
    val serverName: String,
    val identity: String,
    val eph: String,
    val nonce: String,
    val sig: String,
) : ControlMessage()

@Serializable
@SerialName("auth")
data class Auth(val mac: String) : ControlMessage()

@Serializable
@SerialName("auth_ok")
data class AuthOk(
    val mac: String,
    val sessionId: Long,
    val audioPort: Int,
    val pairing: String? = null,
) : ControlMessage()

@Serializable
@SerialName("auth_failed")
data class AuthFailed(val reason: String) : ControlMessage() {
    companion object {
        const val BAD_CREDENTIALS = "bad_credentials"
        const val UNKNOWN_DEVICE = "unknown_device"
        const val PAIRING_CLOSED = "pairing_closed"
        const val LOCKED_OUT = "locked_out"
    }
}

@Serializable
@SerialName("error")
data class ErrorMsg(val code: String, val message: String? = null) : ControlMessage() {
    companion object {
        const val BUSY = "busy"
        const val PAIRING_CLOSED = "pairing_closed"
        const val UNSUPPORTED_VERSION = "unsupported_version"
    }
}

@Serializable
@SerialName("stream_start")
data class StreamStart(val codec: String, val rate: Int, val ch: Int, val frameMs: Int) : ControlMessage()

@Serializable
@SerialName("stream_ack")
data class StreamAck(val ok: Boolean, val reason: String? = null) : ControlMessage()

@Serializable
@SerialName("stream_stop")
data object StreamStop : ControlMessage()

@Serializable
@SerialName("ping")
data class Ping(val ts: Long, val rtt: Double = 0.0) : ControlMessage()

@Serializable
@SerialName("pong")
data class Pong(val ts: Long) : ControlMessage()

@Serializable
@SerialName("stats")
data class Stats(
    val received: Long,
    val lost: Long,
    val late: Long,
    val underruns: Long,
    val bufferMs: Double,
    val jitterMs: Double,
    val sinkName: String,
    val sinkOk: Boolean,
) : ControlMessage()

@Serializable
@SerialName("bye")
data class Bye(val reason: String) : ControlMessage()

/** A message type this version does not know (forward compatibility). */
data class UnknownMessage(val type: String) : ControlMessage()

@OptIn(ExperimentalSerializationApi::class)
object ControlCodec {
    private val json = Json {
        classDiscriminator = "t"
        ignoreUnknownKeys = true
        explicitNulls = false
        encodeDefaults = true
    }

    private val knownTypes = setOf(
        "hello", "server_hello", "auth", "auth_ok", "auth_failed", "error", "stream_start",
        "stream_ack", "stream_stop", "ping", "pong", "stats", "bye",
    )

    fun encode(message: ControlMessage): ByteArray {
        require(message !is UnknownMessage)
        return json.encodeToString(ControlMessage.serializer(), message).toByteArray(Charsets.UTF_8)
    }

    fun decode(bytes: ByteArray): ControlMessage {
        val text = String(bytes, Charsets.UTF_8)
        try {
            val obj: JsonObject = json.parseToJsonElement(text).jsonObject
            val type = obj["t"]?.jsonPrimitive?.takeIf { it.isString }?.content
                ?: throw ProtocolException("Control message has no type")
            if (type !in knownTypes) return UnknownMessage(type)
            return json.decodeFromJsonElement(ControlMessage.serializer(), obj)
        } catch (e: SerializationException) {
            throw ProtocolException("Malformed control message: ${e.message}", e)
        } catch (e: IllegalArgumentException) {
            throw ProtocolException("Malformed control message: ${e.message}", e)
        }
    }
}
