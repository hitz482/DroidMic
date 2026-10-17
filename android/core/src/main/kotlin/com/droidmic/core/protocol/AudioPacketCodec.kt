package com.droidmic.core.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

object FrameFlags {
    const val CLIPPED = 1
    const val MUTED = 2
}

/**
 * Builds encrypted audio datagrams (protocol §5). One instance per session; not
 * thread-safe. [encode] reuses its internal buffers, so the returned array is only
 * valid until the next call.
 */
class AudioPacketCodec(audioKey: ByteArray, val sessionId: Int) {
    companion object {
        const val HEADER_LENGTH = 24
        const val TAG_LENGTH = 16
        const val MAX_DATAGRAM_LENGTH = HEADER_LENGTH + Protocol.MAX_PAYLOAD_BYTES + TAG_LENGTH
        private const val TYPE_AUDIO = 1
        private const val CODEC_PCM16 = 1
    }

    init {
        require(audioKey.size == 16) { "Audio key must be 16 bytes" }
    }

    private val key = SecretKeySpec(audioKey.copyOf(), "AES")
    private val cipher: Cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private val header = ByteArray(HEADER_LENGTH)
    private val nonce = ByteArray(12)
    private val pcmBytes = ByteArray(Protocol.MAX_PAYLOAD_BYTES)
    private val out = ByteArray(MAX_DATAGRAM_LENGTH)

    /**
     * Encrypts [frameSamples]×[channels] interleaved samples from [pcm] and returns the
     * datagram length; the datagram is in [buffer].
     */
    fun encode(
        seq: Int,
        timestamp: Int,
        sampleRate: Int,
        channels: Int,
        flags: Int,
        pcm: ShortArray,
        frameSamples: Int,
    ): Int {
        require(channels in 1..2) { "channels" }
        require(frameSamples in 1..Protocol.MAX_FRAME_SAMPLES) { "frameSamples" }
        val payload = frameSamples * channels * 2
        require(payload <= Protocol.MAX_PAYLOAD_BYTES && pcm.size >= frameSamples * channels)

        val h = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
        h.put('D'.code.toByte()).put('M'.code.toByte())
        h.put(Protocol.VERSION.toByte()).put(TYPE_AUDIO.toByte())
        h.putInt(sessionId).putInt(seq).putInt(timestamp)
        h.put(CODEC_PCM16.toByte()).put(channels.toByte())
        h.put(Protocol.sampleRateCode(sampleRate).toByte()).put(flags.toByte())
        h.putShort(frameSamples.toShort()).putShort(0)

        for (i in 0 until frameSamples * channels) {
            val s = pcm[i].toInt()
            pcmBytes[2 * i] = s.toByte()
            pcmBytes[2 * i + 1] = (s shr 8).toByte()
        }

        ByteBuffer.wrap(nonce).order(ByteOrder.BIG_ENDIAN).putInt(sessionId).putInt(0).putInt(seq)
        cipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(TAG_LENGTH * 8, nonce))
        cipher.updateAAD(header)
        System.arraycopy(header, 0, out, 0, HEADER_LENGTH)
        val written = cipher.doFinal(pcmBytes, 0, payload, out, HEADER_LENGTH)
        return HEADER_LENGTH + written
    }

    /** Datagram produced by the last [encode]. */
    val buffer: ByteArray get() = out
}
