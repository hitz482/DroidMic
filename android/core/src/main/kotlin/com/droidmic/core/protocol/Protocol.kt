package com.droidmic.core.protocol

/** Constants shared with the Windows receiver. See docs/AUDIO_PROTOCOL.md. */
object Protocol {
    const val VERSION = 1

    const val DEFAULT_CONTROL_PORT = 48110
    const val DEFAULT_AUDIO_PORT = 48111
    const val DISCOVERY_PORT = 48112

    const val MAX_CONTROL_MESSAGE_BYTES = 65536
    const val MAX_FRAME_SAMPLES = 960
    const val MAX_PAYLOAD_BYTES = 3840

    const val KEEPALIVE_INTERVAL_MS = 1000L
    const val SESSION_TIMEOUT_MS = 5000L

    const val PIN_LENGTH = 6
    const val HANDSHAKE_NONCE_LENGTH = 16
    const val PAIRING_KEY_LENGTH = 32

    val DISCOVERY_REQUEST_MAGIC: ByteArray = "DMDISC1".toByteArray(Charsets.US_ASCII)
    const val DISCOVERY_REPLY_MAGIC = "DMSRV1"
    const val DISCOVERY_NONCE_LENGTH = 8

    const val TRANSCRIPT_LABEL = "DroidMic/v1"
    const val INFO_CLIENT_CONFIRM = "droidmic c2s confirm"
    const val INFO_SERVER_CONFIRM = "droidmic s2c confirm"
    const val INFO_AUDIO_KEY = "droidmic audio"
    const val INFO_PAIRING_WRAP = "droidmic pairing wrap"

    const val CODEC_PCM16 = "pcm16"

    fun sampleRateCode(rate: Int): Int = when (rate) {
        16000 -> 1
        24000 -> 2
        32000 -> 3
        44100 -> 4
        48000 -> 5
        else -> throw IllegalArgumentException("Unsupported sample rate $rate")
    }

    fun sampleRateFromCode(code: Int): Int = when (code) {
        1 -> 16000
        2 -> 24000
        3 -> 32000
        4 -> 44100
        5 -> 48000
        else -> 0
    }

    fun isValidPin(pin: String): Boolean = pin.length == PIN_LENGTH && pin.all { it in '0'..'9' }
}

class ProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)
