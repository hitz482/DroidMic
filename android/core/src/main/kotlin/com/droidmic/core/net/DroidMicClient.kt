package com.droidmic.core.net

import com.droidmic.core.protocol.Auth
import com.droidmic.core.protocol.AuthFailed
import com.droidmic.core.protocol.AuthOk
import com.droidmic.core.protocol.AudioPacketCodec
import com.droidmic.core.protocol.Bye
import com.droidmic.core.protocol.ControlFraming
import com.droidmic.core.protocol.ControlMessage
import com.droidmic.core.protocol.ErrorMsg
import com.droidmic.core.protocol.HandshakeCrypto
import com.droidmic.core.protocol.Hello
import com.droidmic.core.protocol.Ping
import com.droidmic.core.protocol.Pong
import com.droidmic.core.protocol.Protocol
import com.droidmic.core.protocol.ProtocolException
import com.droidmic.core.protocol.ServerHello
import com.droidmic.core.protocol.Stats
import com.droidmic.core.protocol.StreamAck
import com.droidmic.core.protocol.StreamStart
import com.droidmic.core.protocol.StreamStop
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicLong

/** How to authenticate to the PC. */
sealed class Credentials {
    /** First-time pairing with the PIN shown on the PC. [expectedFingerprint] comes from a QR code, if used. */
    data class Pin(val pin: String, val expectedFingerprint: String? = null) : Credentials()

    /** Reconnect using the key from an earlier pairing; the PC identity must match [pinnedFingerprint]. */
    class Paired(val pairingKey: ByteArray, val pinnedFingerprint: String) : Credentials()
}

data class HandshakeResult(
    val serverId: String,
    val serverName: String,
    val fingerprint: String,
    val sessionId: Int,
    val audioPort: Int,
    /** Present only after PIN pairing: persist it to reconnect later. */
    val newPairingKey: ByteArray?,
)

/** The PC refused us. [code] is an `auth_failed` reason or `error` code from the protocol. */
class AuthRejectedException(val code: String, message: String) : IOException(message)

/** The PC's identity key is not the one we expected. Possible impersonation; never auto-accept. */
class IdentityMismatchException(val expected: String, val actual: String) :
    IOException("PC identity changed (expected $expected, got $actual)")

interface ControlListener {
    fun onStats(stats: Stats) {}
    fun onRoundTrip(rttMs: Double) {}
    fun onClosed(reason: String, error: Throwable?) {}
}

/**
 * Phone-side protocol client: TCP control connection plus UDP audio sender.
 * [connect] and [startStream] block and must run off the main thread.
 * [sendFrame] is called from the capture thread.
 */
class DroidMicClient(
    private val deviceId: String,
    private val deviceName: String,
) : Closeable {
    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: OutputStream? = null
    private var udp: DatagramSocket? = null
    private var codec: AudioPacketCodec? = null
    private var audioTarget: InetSocketAddress? = null
    private var reader: Thread? = null
    @Volatile private var closed = false

    private var seq = 0
    private var timestamp = 0
    private var streamRate = 48000
    private var streamChannels = 1
    private val packet = DatagramPacket(ByteArray(0), 0)

    val framesSent = AtomicLong()
    val bytesSent = AtomicLong()
    val sendErrors = AtomicLong()

    /** Last statistics frame received during [startStream], before the reader thread started. */
    @Volatile var lastStats: Stats? = null
        private set

    fun connect(host: String, port: Int, credentials: Credentials, timeoutMs: Int = 5000): HandshakeResult {
        val address = InetAddress.getByName(host)
        val s = Socket()
        socket = s
        s.tcpNoDelay = true
        s.connect(InetSocketAddress(address, port), timeoutMs)
        s.soTimeout = timeoutMs
        val inp = DataInputStream(BufferedInputStream(s.getInputStream()))
        val out = BufferedOutputStream(s.getOutputStream())
        input = inp
        output = out

        val eph = HandshakeCrypto.newEphemeralKeyPair()
        val clientEph = HandshakeCrypto.encodePublicKey(eph.public)
        val clientNonce = ByteArray(Protocol.HANDSHAKE_NONCE_LENGTH).also { SecureRandom().nextBytes(it) }
        val mode = if (credentials is Credentials.Pin) Hello.MODE_PIN else Hello.MODE_PAIRED
        ControlFraming.write(out, Hello(Protocol.VERSION, deviceId, deviceName, HandshakeCrypto.b64(clientEph), HandshakeCrypto.b64(clientNonce), mode))

        val sh = when (val reply = readMessage()) {
            is ServerHello -> reply
            is ErrorMsg -> throw AuthRejectedException(reply.code, reply.message ?: reply.code)
            else -> throw ProtocolException("Expected server_hello, got $reply")
        }
        if (sh.v != Protocol.VERSION) throw ProtocolException("PC speaks protocol v${sh.v}")
        val identity = HandshakeCrypto.fromB64(sh.identity, null, "identity")
        val serverEph = HandshakeCrypto.fromB64(sh.eph, null, "eph")
        val serverNonce = HandshakeCrypto.fromB64(sh.nonce, Protocol.HANDSHAKE_NONCE_LENGTH, "nonce")
        val th = HandshakeCrypto.transcriptHash(deviceId, clientEph, clientNonce, sh.serverId, identity, serverEph, serverNonce)
        if (!HandshakeCrypto.verifySignature(identity, th, HandshakeCrypto.fromB64(sh.sig, null, "sig"))) {
            throw ProtocolException("PC signature is invalid")
        }
        val fingerprint = HandshakeCrypto.fingerprint(identity)
        val expected = when (credentials) {
            is Credentials.Pin -> credentials.expectedFingerprint
            is Credentials.Paired -> credentials.pinnedFingerprint
        }
        // Check before revealing anything derived from the secret.
        if (expected != null && expected != fingerprint) throw IdentityMismatchException(expected, fingerprint)

        val secret = when (credentials) {
            is Credentials.Pin -> credentials.pin.toByteArray(Charsets.UTF_8)
            is Credentials.Paired -> credentials.pairingKey.copyOf()
        }
        val shared = HandshakeCrypto.agree(eph, serverEph)
        val keys = HandshakeCrypto.deriveKeys(shared, secret, th)
        shared.fill(0)
        secret.fill(0)
        try {
            ControlFraming.write(out, Auth(HandshakeCrypto.b64(HandshakeCrypto.hmac(keys.clientConfirm, th))))
            val ok = when (val r = readMessage()) {
                is AuthOk -> r
                is AuthFailed -> throw AuthRejectedException(r.reason, describe(r.reason))
                is ErrorMsg -> throw AuthRejectedException(r.code, r.message ?: r.code)
                else -> throw ProtocolException("Expected auth_ok, got $r")
            }
            if (!HandshakeCrypto.verifyMac(keys.serverConfirm, th, HandshakeCrypto.fromB64(ok.mac, 32, "mac"))) {
                throw ProtocolException("PC failed to prove knowledge of the credentials")
            }
            val newKey = ok.pairing?.let { HandshakeCrypto.unwrapPairingKey(keys.pairingWrap, HandshakeCrypto.fromB64(it, null, "pairing"), th) }
            if (credentials is Credentials.Pin && newKey == null) throw ProtocolException("PC did not issue a pairing key")
            if (ok.audioPort !in 1..65535) throw ProtocolException("Bad audio port")

            val sessionId = ok.sessionId.toInt()
            codec = AudioPacketCodec(keys.audioKey, sessionId)
            audioTarget = InetSocketAddress(address, ok.audioPort)
            udp = DatagramSocket().apply { trafficClass = 0xB8 } // DSCP EF: hint for low-latency queuing
            s.soTimeout = 0
            return HandshakeResult(sh.serverId, sh.serverName, fingerprint, sessionId, ok.audioPort, newKey)
        } finally {
            keys.wipe()
        }
    }

    /** Requests streaming and waits for the PC's acknowledgement. */
    fun startStream(sampleRate: Int, channels: Int, frameMs: Int, timeoutMs: Int = 5000) {
        val s = socket ?: throw IllegalStateException("Not connected")
        streamRate = sampleRate
        streamChannels = channels
        ControlFraming.write(output!!, StreamStart(Protocol.CODEC_PCM16, sampleRate, channels, frameMs))
        s.soTimeout = timeoutMs
        try {
            while (true) {
                when (val m = readMessage()) {
                    is StreamAck -> if (m.ok) return else throw AuthRejectedException(m.reason ?: "refused", "PC refused the stream: ${m.reason}")
                    is Stats -> lastStats = m
                    is Pong -> Unit
                    is Bye -> throw IOException("PC closed the session: ${m.reason}")
                    else -> Unit
                }
            }
        } finally {
            s.soTimeout = 0
        }
    }

    /** Starts the background reader that delivers stats, pongs and disconnects. */
    fun startReader(listener: ControlListener) {
        val inp = input ?: throw IllegalStateException("Not connected")
        reader = Thread({
            var reason = "closed"
            var error: Throwable? = null
            try {
                while (!closed) {
                    when (val m = ControlFraming.read(inp)) {
                        null -> { reason = "pc_closed"; break }
                        is Stats -> listener.onStats(m)
                        is Pong -> listener.onRoundTrip((System.nanoTime() / 1_000_000 - m.ts).toDouble())
                        is Bye -> { reason = m.reason; break }
                        else -> Unit
                    }
                }
            } catch (e: Exception) {
                if (!closed) { reason = "connection_lost"; error = e }
            }
            if (!closed) listener.onClosed(reason, error)
        }, "droidmic-control-reader").apply { isDaemon = true; start() }
    }

    fun sendPing(lastRttMs: Double) {
        ControlFraming.write(output ?: return, Ping(System.nanoTime() / 1_000_000, lastRttMs))
    }

    /** Encrypts and sends one frame. Returns false if the datagram could not be sent. */
    fun sendFrame(pcm: ShortArray, frameSamples: Int, flags: Int): Boolean {
        val c = codec ?: return false
        val sock = udp ?: return false
        val len = c.encode(seq, timestamp, streamRate, streamChannels, flags, pcm, frameSamples)
        seq++
        timestamp += frameSamples
        return try {
            packet.setData(c.buffer, 0, len)
            packet.socketAddress = audioTarget
            sock.send(packet)
            framesSent.incrementAndGet()
            bytesSent.addAndGet(len.toLong())
            true
        } catch (e: IOException) {
            sendErrors.incrementAndGet()
            false
        }
    }

    fun stopStream() {
        try { ControlFraming.write(output ?: return, StreamStop) } catch (_: IOException) { }
    }

    private fun readMessage(): ControlMessage =
        ControlFraming.read(input!!) ?: throw IOException("PC closed the connection")

    override fun close() {
        if (closed) return
        closed = true
        try { output?.let { ControlFraming.write(it, Bye("phone_closing")) } } catch (_: Exception) { }
        try { socket?.close() } catch (_: IOException) { }
        udp?.close()
        reader?.interrupt()
    }

    companion object {
        fun describe(code: String): String = when (code) {
            AuthFailed.BAD_CREDENTIALS -> "Wrong PIN"
            AuthFailed.UNKNOWN_DEVICE -> "This phone is no longer paired with the PC"
            AuthFailed.PAIRING_CLOSED, ErrorMsg.PAIRING_CLOSED -> "Pairing is not open on the PC. Click 'Pair new phone' in DroidMic for Windows."
            AuthFailed.LOCKED_OUT -> "Too many wrong PINs. Open pairing again on the PC for a new PIN."
            ErrorMsg.BUSY -> "Another phone is already connected to this PC"
            ErrorMsg.UNSUPPORTED_VERSION -> "DroidMic versions on the phone and PC are incompatible"
            else -> code
        }
    }
}
