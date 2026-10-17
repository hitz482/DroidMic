package com.droidmic.app.ui.screens

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.droidmic.app.net.NetworkStatus
import com.droidmic.app.ui.components.Hint
import com.droidmic.app.ui.components.SectionCard
import com.droidmic.app.ui.components.StatRow
import com.droidmic.core.session.SessionState
import com.droidmic.core.session.SessionStats

@Composable
fun DiagnosticsScreen(state: SessionState, stats: SessionStats, network: NetworkStatus, frameMs: Int) {
    // Throughput from the change in bytes sent over each 1 s stats update.
    var lastBytes by remember { mutableLongStateOf(0L) }
    var lastTime by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var kbps by remember { mutableStateOf(0.0) }
    LaunchedEffect(stats.bytesSent) {
        val now = System.currentTimeMillis()
        val dt = (now - lastTime) / 1000.0
        if (dt > 0.2 && stats.bytesSent >= lastBytes) kbps = (stats.bytesSent - lastBytes) * 8 / 1000.0 / dt
        lastBytes = stats.bytesSent
        lastTime = now
    }
    val pc = stats.pc
    val streaming = state is SessionState.Streaming

    Column(Modifier.verticalScroll(rememberScrollState()).padding(20.dp)) {
        Text("Diagnostics", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(16.dp))
        SectionCard {
            Text("Stream", style = MaterialTheme.typography.titleMedium)
            StatRow("State", state::class.simpleName ?: "—")
            StatRow("Format", "48 kHz · 16-bit · mono · $frameMs ms frames")
            StatRow("Frames sent", stats.framesSent.toString())
            StatRow("Throughput", if (streaming) "%.0f kbit/s".format(kbps) else "—")
            StatRow("Send errors", stats.sendErrors.toString())
            StatRow("Clipped frames", stats.clippedFrames.toString())
            StatRow("Reconnects", stats.reconnects.toString())
        }
        Spacer(Modifier.height(12.dp))
        SectionCard {
            Text("Network", style = MaterialTheme.typography.titleMedium)
            StatRow("Link", network.description)
            StatRow("Phone address", network.localAddress ?: "—")
            StatRow("PC", stats.serverName ?: "—")
            StatRow("PC identity", stats.fingerprint ?: "—")
            StatRow("Round trip", if (stats.rttMs > 0) "%.1f ms".format(stats.rttMs) else "—")
        }
        Spacer(Modifier.height(12.dp))
        SectionCard {
            Text("Reported by the PC", style = MaterialTheme.typography.titleMedium)
            if (pc == null) {
                Hint("No statistics yet.")
            } else {
                val total = pc.received + pc.lost
                StatRow("Frames received", pc.received.toString())
                StatRow("Packet loss", if (total > 0) "%.2f %% (%d)".format(100.0 * pc.lost / total, pc.lost) else "0")
                StatRow("Late packets", pc.late.toString())
                StatRow("Buffer underruns", pc.underruns.toString())
                StatRow("Jitter buffer", "%.0f ms".format(pc.bufferMs))
                StatRow("Network jitter", "%.1f ms".format(pc.jitterMs))
                // Capture frame + one-way network + PC buffer + driver/engine (~25 ms).
                StatRow("Estimated latency", "≈ %.0f ms".format(frameMs + stats.rttMs / 2 + pc.bufferMs + 25))
                StatRow("PC output", pc.sinkName + if (pc.sinkOk) "" else " (other apps cannot hear it)")
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}
