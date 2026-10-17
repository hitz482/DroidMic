package com.droidmic.core.session

import com.droidmic.core.audio.AudioSource
import com.droidmic.core.audio.GainProcessor
import com.droidmic.core.audio.LevelMeter
import com.droidmic.core.net.AuthRejectedException
import com.droidmic.core.net.ControlListener
import com.droidmic.core.net.Credentials
import com.droidmic.core.net.DiscoveredPc
import com.droidmic.core.net.DroidMicClient
import com.droidmic.core.net.IdentityMismatchException
import com.droidmic.core.protocol.AuthFailed
import com.droidmic.core.protocol.FrameFlags
import com.droidmic.core.protocol.Protocol
import com.droidmic.core.protocol.ProtocolException
import com.droidmic.core.protocol.Stats
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.atomic.AtomicReference

/** Pairing remembered on the phone. [pairingKey] must be stored encrypted (Android Keystore). */
data class StoredPairing(
    val serverId: String,
    val serverName: String,
    val host: String,
    val port: Int,
    val fingerprint: String,
    val pairingKey: ByteArray,
)

interface PairingRepository {
    fun all(): List<StoredPairing>
    fun get(serverId: String): StoredPairing?
    fun save(pairing: StoredPairing)
    fun remove(serverId: String)
}

/** What the user asked to connect to. */
sealed class ConnectTarget {
    /** First-time pairing (manual entry or QR code). */
    data class PairWithPin(val host: String, val port: Int, val pin: String, val expectedFingerprint: String? = null) : ConnectTarget()

    /** A PC paired earlier. [host] overrides the stored address (e.g. from fresh discovery). */
    data class Paired(val serverId: String, val host: String? = null, val port: Int? = null) : ConnectTarget()
}

sealed class SessionState {
    data object Idle : SessionState()
    data class Connecting(val host: String) : SessionState()
    data class Streaming(val serverName: String, val host: String) : SessionState()
    data class Reconnecting(val serverName: String, val attempt: Int, val reason: String) : SessionState()
    data class Failed(val message: String, val code: String? = null, val needsRepair: Boolean = false) : SessionState()
}

data class SessionStats(
    val framesSent: Long = 0,
    val bytesSent: Long = 0,
    val sendErrors: Long = 0,
    val clippedFrames: Long = 0,
    val captureErrors: Long = 0,
    val reconnects: Int = 0,
    val rttMs: Double = 0.0,
    val pc: Stats? = null,
    val serverName: String? = null,
    val fingerprint: String? = null,
    val sourceDescription: String? = null,
)

data class StreamSettings(val gainDb: Float = 0f, val muted: Boolean = false)

/**
 * Owns one streaming session from the user's Start to Stop: connect, authenticate,
 * capture, send, keep-alive, and automatic reconnection. All network I/O runs on
 * [Dispatchers.IO]; capture runs on its own thread.
 */
class StreamSession(
    private val deviceId: String,
    private val deviceName: String,
    private val pairings: PairingRepository,
    private val sourceFactory: () -> AudioSource,
    private val scope: CoroutineScope,
    private val settings: () -> StreamSettings = { StreamSettings() },
    private val rediscover: (suspend () -> List<DiscoveredPc>)? = null,
    private val maxReconnectWindowMs: Long = 60_000,
) {
    private val _state = MutableStateFlow<SessionState>(SessionState.Idle)
    val state: StateFlow<SessionState> = _state.asStateFlow()

    private val _stats = MutableStateFlow(SessionStats())
    val stats: StateFlow<SessionStats> = _stats.asStateFlow()

    val meter = LevelMeter()

    private var job: Job? = null
    private val currentClient = AtomicReference<DroidMicClient?>(null)
    @Volatile private var source: AudioSource? = null
    @Volatile private var captureThread: Thread? = null
    @Volatile private var capturing = false

    val isActive: Boolean get() = job?.isActive == true

    fun start(target: ConnectTarget) {
        stop()
        _state.value = SessionState.Idle
        _stats.value = SessionStats()
        meter.reset()
        job = scope.launch(Dispatchers.IO) { run(target) }
    }

    /** Stops immediately: closes the socket and releases the microphone. A shown error is kept. */
    fun stop() {
        val j = job
        job = null
        j?.cancel()
        currentClient.getAndSet(null)?.let { c ->
            try { c.stopStream() } catch (_: Exception) { }
            c.close()
        }
        stopCapture()
        if (_state.value !is SessionState.Failed) _state.value = SessionState.Idle
    }

    private suspend fun run(initial: ConnectTarget) {
        var target = initial
        var attempt = 0
        var lostSince = 0L
        var serverName = (initial as? ConnectTarget.Paired)?.let { pairings.get(it.serverId)?.serverName } ?: "PC"

        while (currentCoroutineContext().isActive) {
            val (host, port, creds) = try {
                resolve(target)
            } catch (e: IllegalStateException) {
                fail(e.message ?: "Not paired", needsRepair = true)
                return
            }
            if (attempt == 0) _state.value = SessionState.Connecting(host)

            val client = DroidMicClient(deviceId, deviceName)
            currentClient.set(client)
            val closed = CompletableDeferred<String>()
            try {
                val result = client.connect(host, port, creds)
                serverName = result.serverName
                if (result.newPairingKey != null) {
                    pairings.save(StoredPairing(result.serverId, result.serverName, host, port, result.fingerprint, result.newPairingKey))
                } else {
                    pairings.get(result.serverId)?.let { if (it.host != host || it.port != port) pairings.save(it.copy(host = host, port = port, serverName = result.serverName)) }
                }
                // Later reconnects use the stored pairing key: the PIN is single-use.
                target = ConnectTarget.Paired(result.serverId, host, port)

                val src = ensureCapture()
                client.startStream(src.format.sampleRate, src.format.channels, src.format.frameMs)
                var lastHeard = System.currentTimeMillis()
                client.startReader(object : ControlListener {
                    override fun onStats(stats: Stats) {
                        lastHeard = System.currentTimeMillis()
                        _stats.update { it.copy(pc = stats) }
                    }
                    override fun onRoundTrip(rttMs: Double) {
                        lastHeard = System.currentTimeMillis()
                        _stats.update { it.copy(rttMs = rttMs) }
                    }
                    override fun onClosed(reason: String, error: Throwable?) { closed.complete(reason) }
                })
                _stats.update { it.copy(serverName = result.serverName, fingerprint = result.fingerprint, pc = client.lastStats ?: it.pc) }
                _state.value = SessionState.Streaming(result.serverName, host)
                attempt = 0
                lostSince = 0

                // Keep-alive until the connection drops or we are cancelled.
                while (!closed.isCompleted) {
                    // TCP may not notice a vanished network for minutes; the PC answers every ping.
                    if (System.currentTimeMillis() - lastHeard > Protocol.SESSION_TIMEOUT_MS) throw IOException("No response from the PC")
                    client.sendPing(_stats.value.rttMs)
                    _stats.update { it.copy(framesSent = client.framesSent.get(), bytesSent = client.bytesSent.get(), sendErrors = client.sendErrors.get()) }
                    withTimeoutOrNull(Protocol.KEEPALIVE_INTERVAL_MS) { closed.await() }
                }
                throw IOException(describeClose(closed.await()))
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: AuthRejectedException) {
                val repair = e.code == AuthFailed.UNKNOWN_DEVICE
                if (repair) (target as? ConnectTarget.Paired)?.let { pairings.remove(it.serverId) }
                fail(DroidMicClient.describe(e.code), e.code, repair)
                return
            } catch (e: IdentityMismatchException) {
                fail("Security warning: this PC's identity does not match the paired PC (expected ${e.expected}, got ${e.actual}). Re-pair only if you reinstalled DroidMic on the PC.", "identity_mismatch", true)
                return
            } catch (e: ProtocolException) {
                fail("Protocol error: ${e.message}", "protocol")
                return
            } catch (e: IOException) {
                currentClient.compareAndSet(client, null)
                client.close()
                if (initial is ConnectTarget.PairWithPin && target is ConnectTarget.PairWithPin) {
                    // Never paired: don't retry a PIN in the background.
                    fail(describeNetworkError(e, host, port), "network")
                    return
                }
                if (lostSince == 0L) lostSince = System.currentTimeMillis()
                if (System.currentTimeMillis() - lostSince > maxReconnectWindowMs) {
                    fail("Lost connection to $serverName. ${describeNetworkError(e, host, port)}", "network")
                    return
                }
                attempt++
                _stats.update { it.copy(reconnects = it.reconnects + 1) }
                _state.value = SessionState.Reconnecting(serverName, attempt, describeNetworkError(e, host, port))
                delay(backoffMs(attempt))
                if (attempt >= 2) target = rediscoverTarget(target)
            } finally {
                currentClient.compareAndSet(client, null)
                client.close()
            }
        }
    }

    /** Clears a [SessionState.Failed] after the user has seen it. */
    fun acknowledgeError() {
        if (_state.value is SessionState.Failed && !isActive) _state.value = SessionState.Idle
    }

    private fun resolve(target: ConnectTarget): Triple<String, Int, Credentials> = when (target) {
        is ConnectTarget.PairWithPin -> Triple(target.host, target.port, Credentials.Pin(target.pin, target.expectedFingerprint))
        is ConnectTarget.Paired -> {
            val p = pairings.get(target.serverId) ?: throw IllegalStateException("This PC is not paired any more. Pair again.")
            Triple(target.host ?: p.host, target.port ?: p.port, Credentials.Paired(p.pairingKey, p.fingerprint))
        }
    }

    // DHCP may have moved the PC: look it up by its server id.
    private suspend fun rediscoverTarget(target: ConnectTarget): ConnectTarget {
        val paired = target as? ConnectTarget.Paired ?: return target
        val found = try { rediscover?.invoke() } catch (_: Exception) { null } ?: return target
        val pc = found.firstOrNull { it.serverId == paired.serverId } ?: return target
        return ConnectTarget.Paired(paired.serverId, pc.host, pc.controlPort)
    }

    private fun fail(message: String, code: String? = null, needsRepair: Boolean = false) {
        stopCapture()
        _state.value = SessionState.Failed(message, code, needsRepair)
    }

    // ---- Capture ---------------------------------------------------------------

    private fun ensureCapture(): AudioSource {
        source?.let { return it }
        val src = sourceFactory()
        src.start()
        source = src
        capturing = true
        _stats.update { it.copy(sourceDescription = src.description) }
        captureThread = Thread({ captureLoop(src) }, "droidmic-capture").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
        return src
    }

    private fun captureLoop(src: AudioSource) {
        val buf = ShortArray(Protocol.MAX_FRAME_SAMPLES * 2)
        val frameSamples = src.format.frameSamples
        var clipped = 0L
        var errors = 0L
        while (capturing) {
            val n = src.readFrame(buf)
            if (n < 0) {
                if (!capturing) break
                errors++
                _stats.update { it.copy(captureErrors = errors) }
                fail("The microphone stopped delivering audio (it may be in use by another app, or permission was revoked).", "capture")
                currentClient.getAndSet(null)?.close()
                job?.cancel()
                break
            }
            val s = settings()
            if (s.muted) buf.fill(0, 0, n)
            val clip = !s.muted && GainProcessor.apply(buf, n, s.gainDb)
            if (clip) clipped++
            meter.process(buf, n)
            val flags = (if (clip) FrameFlags.CLIPPED else 0) or (if (s.muted) FrameFlags.MUTED else 0)
            currentClient.get()?.takeIf { _state.value is SessionState.Streaming }?.sendFrame(buf, frameSamples, flags)
            if (clip) _stats.update { it.copy(clippedFrames = clipped) }
        }
    }

    private fun stopCapture() {
        capturing = false
        source?.stop()
        val t = captureThread
        if (t != null && t != Thread.currentThread()) t.join(500)
        captureThread = null
        source?.close()
        source = null
        meter.reset()
    }

    companion object {
        fun backoffMs(attempt: Int): Long = minOf(10_000L, 500L shl minOf(attempt - 1, 5))

        fun describeClose(reason: String): String = when (reason) {
            "pc_closed", "server_stopping" -> "The PC stopped listening"
            "disconnected_by_pc" -> "Disconnected from the PC"
            "connection_lost" -> "Connection lost"
            else -> reason
        }

        fun describeNetworkError(e: IOException, host: String, port: Int): String = when (e) {
            is ConnectException -> "Could not reach $host:$port. Is DroidMic running on the PC, and is it allowed through Windows Firewall?"
            is SocketTimeoutException -> "Timed out connecting to $host:$port. Check both devices are on the same network (guest Wi-Fi and 'AP/client isolation' block device-to-device traffic)."
            is NoRouteToHostException -> "No route to $host. Check the phone is on the same network as the PC."
            is UnknownHostException -> "Unknown host $host."
            else -> e.message ?: "Network error"
        }
    }
}
