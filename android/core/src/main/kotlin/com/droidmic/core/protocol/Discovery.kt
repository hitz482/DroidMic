package com.droidmic.core.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.net.URI
import java.net.URLDecoder

@Serializable
data class DiscoveryReply(
    val magic: String,
    val v: Int,
    val nonce: String,
    val serverId: String,
    val name: String,
    val ctl: Int,
    val fp: String,
)

/** Information from a `droidmic://pair?...` QR code (shown by the Windows app). */
data class PairingUri(
    val host: String,
    val port: Int,
    val pin: String,
    val fingerprint: String,
    val serverName: String,
) {
    companion object {
        /** Parses and validates a pairing URI; returns null if it is not a valid DroidMic code. */
        fun parse(text: String): PairingUri? {
            val uri = try { URI(text.trim()) } catch (e: Exception) { return null }
            if (uri.scheme != "droidmic" || uri.host != "pair" && uri.authority != "pair") return null
            val params = (uri.rawQuery ?: return null).split('&').mapNotNull {
                val i = it.indexOf('=')
                if (i <= 0) null else it.substring(0, i) to URLDecoder.decode(it.substring(i + 1), "UTF-8")
            }.toMap()
            val host = params["h"]?.takeIf { isPlausibleHost(it) } ?: return null
            val port = params["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            val pin = params["pin"]?.takeIf(Protocol::isValidPin) ?: return null
            val fp = params["fp"]?.takeIf { it.length == 12 && it.all { c -> c in '0'..'9' || c in 'a'..'f' } } ?: return null
            val name = params["n"]?.take(64) ?: host
            return PairingUri(host, port, pin, fp, name)
        }

        private fun isPlausibleHost(h: String) = h.isNotEmpty() && h.length <= 253 &&
            h.all { it.isLetterOrDigit() || it == '.' || it == '-' || it == ':' }
    }
}

object DiscoveryCodec {
    private val json = Json { ignoreUnknownKeys = true }

    fun buildRequest(nonce: ByteArray): ByteArray {
        require(nonce.size == Protocol.DISCOVERY_NONCE_LENGTH)
        return Protocol.DISCOVERY_REQUEST_MAGIC + nonce
    }

    /** Parses a reply and checks it answers [nonce]; returns null for anything else. */
    fun parseReply(datagram: ByteArray, length: Int, nonce: ByteArray): DiscoveryReply? {
        if (length <= 0 || length > 2048) return null
        val reply = try {
            json.decodeFromString(DiscoveryReply.serializer(), String(datagram, 0, length, Charsets.UTF_8))
        } catch (e: Exception) {
            return null
        }
        if (reply.magic != Protocol.DISCOVERY_REPLY_MAGIC || reply.v != Protocol.VERSION) return null
        if (reply.nonce != HandshakeCrypto.b64(nonce)) return null
        if (reply.ctl !in 1..65535 || reply.name.length > 64) return null
        return reply
    }
}
