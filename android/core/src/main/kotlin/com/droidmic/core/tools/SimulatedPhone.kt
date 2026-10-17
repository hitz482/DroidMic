package com.droidmic.core.tools

import com.droidmic.core.audio.AudioFormat
import com.droidmic.core.audio.SineSource
import com.droidmic.core.net.DiscoveryClient
import com.droidmic.core.protocol.PairingUri
import com.droidmic.core.protocol.Protocol
import com.droidmic.core.session.ConnectTarget
import com.droidmic.core.session.PairingRepository
import com.droidmic.core.session.SessionState
import com.droidmic.core.session.StoredPairing
import com.droidmic.core.session.StreamSession
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlin.system.exitProcess

/**
 * JVM stand-in for the phone: runs the real Android protocol/session code with a
 * test-tone source. Used by scripts/interop-test.sh against the Windows receiver.
 *
 *   --host <ip> --pin <pin> [--port 48110] [--seconds 5] [--tone 1000] [--rate 48000] [--frame-ms 10]
 *   --discover            list PCs found by broadcast and exit
 *   --uri <droidmic://pair?...>   pair using a QR-code URI
 */
fun main(args: Array<String>) {
    val opts = args.toList().windowed(2, 1, partialWindows = true)
        .filter { it[0].startsWith("--") }
        .associate { it[0].removePrefix("--") to it.getOrNull(1)?.takeUnless { v -> v.startsWith("--") } }

    if ("discover" in opts) {
        val pcs = DiscoveryClient().discover()
        pcs.forEach { println("FOUND ${it.name} ${it.host}:${it.controlPort} fp=${it.fingerprint} id=${it.serverId}") }
        if (pcs.isEmpty()) println("FOUND none")
        return
    }

    val uri = opts["uri"]?.let { PairingUri.parse(it) ?: error("Invalid pairing URI") }
    val host = uri?.host ?: opts["host"] ?: error("--host required")
    val port = uri?.port ?: opts["port"]?.toInt() ?: Protocol.DEFAULT_CONTROL_PORT
    val pin = uri?.pin ?: opts["pin"] ?: error("--pin required")
    val seconds = opts["seconds"]?.toInt() ?: 5
    val format = AudioFormat(opts["rate"]?.toInt() ?: 48000, 1, opts["frame-ms"]?.toInt() ?: 10)
    val tone = opts["tone"]?.toDouble() ?: 1000.0

    val repo = object : PairingRepository {
        val map = mutableMapOf<String, StoredPairing>()
        override fun all() = map.values.toList()
        override fun get(serverId: String) = map[serverId]
        override fun save(pairing: StoredPairing) { map[pairing.serverId] = pairing }
        override fun remove(serverId: String) { map.remove(serverId) }
    }
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val session = StreamSession("00112233445566aa", "JVM simulated phone", repo, { SineSource(format, tone) }, scope)

    val ok = runBlocking {
        session.start(ConnectTarget.PairWithPin(host, port, pin, uri?.fingerprint))
        val deadline = System.currentTimeMillis() + 10_000
        while (session.state.value !is SessionState.Streaming) {
            val st = session.state.value
            if (st is SessionState.Failed) { println("FAILED ${st.code} ${st.message}"); return@runBlocking false }
            if (System.currentTimeMillis() > deadline) { println("FAILED timeout $st"); return@runBlocking false }
            delay(20)
        }
        println("STREAMING to ${session.stats.value.serverName} fp=${session.stats.value.fingerprint}")
        repeat(seconds) {
            delay(1000)
            val s = session.stats.value
            println("SENT frames=${s.framesSent} rtt=${"%.1f".format(s.rttMs)}ms pc.received=${s.pc?.received} pc.lost=${s.pc?.lost} pc.buffer=${s.pc?.bufferMs}ms peak=${"%.1f".format(session.meter.peakDb)}dBFS")
        }
        session.stop()
        delay(300)
        true
    }
    println(if (ok) "DONE" else "DONE-WITH-ERRORS")
    exitProcess(if (ok) 0 else 1)
}
