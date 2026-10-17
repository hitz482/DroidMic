package com.droidmic.app.ui.screens

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.droidmic.app.R
import com.droidmic.app.data.AppSettings
import com.droidmic.app.net.NetworkStatus
import com.droidmic.app.ui.Levels
import com.droidmic.app.ui.components.Hint
import com.droidmic.app.ui.components.LevelMeter
import com.droidmic.app.ui.components.SectionCard
import com.droidmic.app.ui.components.StatRow
import com.droidmic.app.ui.components.StatusDot
import com.droidmic.app.ui.theme.DroidMicColors
import com.droidmic.core.session.SessionState
import com.droidmic.core.session.SessionStats
import com.droidmic.core.session.StoredPairing

@Composable
fun HomeScreen(
    state: SessionState,
    stats: SessionStats,
    levels: Levels,
    network: NetworkStatus,
    settings: AppSettings,
    defaultPc: StoredPairing?,
    micSilenced: Boolean,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onGoToConnect: () -> Unit,
    onDismissError: () -> Unit,
) {
    val micOn = state is SessionState.Connecting || state is SessionState.Streaming || state is SessionState.Reconnecting
    val streaming = state is SessionState.Streaming
    val (title, subtitle, color) = describe(state, defaultPc)
    val ringColor by animateColorAsState(if (streaming) DroidMicColors.Good else if (micOn) DroidMicColors.Warn else DroidMicColors.SurfaceHigh, label = "ring")

    Column(
        Modifier.verticalScroll(rememberScrollState()).padding(20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(12.dp))
        // Large microphone status indicator.
        Box(
            Modifier.size(156.dp).clip(CircleShape).background(ringColor.copy(alpha = 0.18f))
                .semantics { contentDescription = if (streaming) "Microphone is transmitting" else if (micOn) "Microphone is on, not yet transmitting" else "Microphone is off" },
            contentAlignment = Alignment.Center,
        ) {
            Box(Modifier.size(116.dp).clip(CircleShape).background(ringColor.copy(alpha = if (micOn) 0.9f else 1f)), contentAlignment = Alignment.Center) {
                Icon(
                    painterResource(if (micOn) R.drawable.ic_mic else R.drawable.ic_mic_off), contentDescription = null,
                    tint = if (micOn) DroidMicColors.Background else DroidMicColors.Muted, modifier = Modifier.size(56.dp),
                )
            }
        }
        Spacer(Modifier.height(18.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            StatusDot(color)
            Spacer(Modifier.width(8.dp))
            Text(title, style = MaterialTheme.typography.titleLarge)
        }
        Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center, modifier = Modifier.padding(top = 6.dp))

        if (micSilenced && micOn) {
            Spacer(Modifier.height(10.dp))
            Hint("Another app (or a call) is using the microphone. DroidMic is sending silence until it is released.", DroidMicColors.Warn)
        }

        Spacer(Modifier.height(22.dp))
        if (micOn) {
            Button(
                onClick = onStop,
                modifier = Modifier.fillMaxWidth().height(56.dp),
                colors = ButtonDefaults.buttonColors(containerColor = DroidMicColors.Bad, contentColor = DroidMicColors.Background),
            ) { Text("Stop Microphone", style = MaterialTheme.typography.titleMedium) }
        } else if (defaultPc != null) {
            Button(onClick = onStart, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                Text("Start Microphone", style = MaterialTheme.typography.titleMedium)
            }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = onGoToConnect, modifier = Modifier.fillMaxWidth()) { Text("Choose another PC") }
        } else {
            Button(onClick = onGoToConnect, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                Text("Connect to a PC", style = MaterialTheme.typography.titleMedium)
            }
        }
        if (state is SessionState.Failed) {
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = onDismissError, modifier = Modifier.fillMaxWidth()) { Text("Dismiss") }
        }

        Spacer(Modifier.height(20.dp))
        SectionCard {
            Text("Input level", style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(10.dp))
            LevelMeter(levels.peakDb, levels.rmsDb, active = micOn)
            Spacer(Modifier.height(6.dp))
            Hint(if (micOn && levels.peakDb > -89) "%.0f dBFS peak%s".format(levels.peakDb, if (levels.peakDb > -0.5) " · clipping, lower the gain" else "") else "Microphone off")
        }
        Spacer(Modifier.height(12.dp))
        SectionCard {
            StatRow("PC", stats.serverName ?: defaultPc?.serverName ?: "Not connected")
            StatRow("Connection", network.description + (network.localAddress?.let { " · $it" } ?: ""))
            StatRow("Input", stats.sourceDescription ?: "Microphone · ${settings.captureMode.label}")
            StatRow("Quality", quality(stats, streaming))
            StatRow("Round trip", if (streaming && stats.rttMs > 0) "%.0f ms".format(stats.rttMs) else "—")
        }
    }
}

private data class Described(val title: String, val subtitle: String, val color: androidx.compose.ui.graphics.Color)

private fun describe(state: SessionState, pc: StoredPairing?): Described = when (state) {
    SessionState.Idle -> Described("Microphone off", if (pc != null) "Ready to stream to ${pc.serverName}." else "Pair with your PC to get started.", DroidMicColors.Muted)
    is SessionState.Connecting -> Described("Connecting…", "Reaching ${state.host}", DroidMicColors.Warn)
    is SessionState.Streaming -> Described("Transmitting to ${state.serverName}", "Your PC can use the phone as “DroidMic Microphone”.", DroidMicColors.Good)
    is SessionState.Reconnecting -> Described("Reconnecting to ${state.serverName}…", "Attempt ${state.attempt}. ${state.reason}", DroidMicColors.Warn)
    is SessionState.Failed -> Described("Not connected", state.message, DroidMicColors.Bad)
}

private fun quality(stats: SessionStats, streaming: Boolean): String {
    val pc = stats.pc ?: return if (streaming) "Measuring…" else "—"
    val total = pc.received + pc.lost
    val loss = if (total > 0) 100.0 * pc.lost / total else 0.0
    val label = when {
        loss < 1 && pc.jitterMs < 10 -> "Excellent"
        loss < 3 && pc.jitterMs < 25 -> "Good"
        loss < 8 -> "Fair"
        else -> "Poor"
    }
    return "$label (%.1f%% loss)".format(loss) + if (!pc.sinkOk) " · PC has no virtual mic driver" else ""
}
