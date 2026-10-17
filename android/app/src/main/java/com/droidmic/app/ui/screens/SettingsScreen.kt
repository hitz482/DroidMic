package com.droidmic.app.ui.screens

import android.content.Intent
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.droidmic.app.audio.MicCaptureSource
import com.droidmic.app.data.AppSettings
import com.droidmic.app.data.CaptureMode
import com.droidmic.app.data.LatencyMode
import com.droidmic.app.ui.components.Hint
import com.droidmic.app.ui.components.SectionCard
import kotlin.math.roundToInt

@Composable
fun SettingsScreen(settings: AppSettings, micOn: Boolean, onUpdate: ((AppSettings) -> AppSettings) -> Unit) {
    val context = LocalContext.current
    val inputs = remember { MicCaptureSource.availableInputs(context) }
    val unprocessed = remember { MicCaptureSource.supportsUnprocessed(checkNotNull(context.getSystemService(android.media.AudioManager::class.java))) }

    Column(Modifier.verticalScroll(rememberScrollState()).padding(20.dp)) {
        Text("Audio settings", style = MaterialTheme.typography.headlineMedium)
        if (micOn) {
            Spacer(Modifier.height(4.dp))
            Hint("Input, processing and latency changes apply the next time you start the microphone. Gain applies immediately.")
        }
        Spacer(Modifier.height(16.dp))

        SectionCard {
            Text("Input gain: ${"%+.0f".format(settings.gainDb)} dB", style = MaterialTheme.typography.titleMedium)
            Slider(
                value = settings.gainDb, onValueChange = { v -> onUpdate { it.copy(gainDb = v.roundToInt().toFloat()) } },
                valueRange = -12f..24f, steps = 35,
            )
            Hint("Raise it if the PC level meter stays low. Lower it if you see clipping.")
        }
        Spacer(Modifier.height(12.dp))

        SectionCard {
            Text("Processing", style = MaterialTheme.typography.titleMedium)
            CaptureMode.entries.forEach { mode ->
                val enabled = mode != CaptureMode.RAW || unprocessed
                Row(
                    Modifier.fillMaxWidth().selectable(selected = settings.captureMode == mode, enabled = enabled, role = Role.RadioButton) {
                        onUpdate { it.copy(captureMode = mode) }
                    }.padding(vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = settings.captureMode == mode, onClick = null, enabled = enabled)
                    Column(Modifier.padding(start = 8.dp)) {
                        Text(mode.label)
                        Hint(if (enabled) mode.description else "Not supported on this phone.")
                    }
                }
            }
            Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Noise suppression")
                    Hint("Uses the phone's built-in noise suppressor, where one exists. Off keeps the sound natural.")
                }
                Switch(checked = settings.noiseSuppression, onCheckedChange = { v -> onUpdate { it.copy(noiseSuppression = v) } })
            }
        }
        Spacer(Modifier.height(12.dp))

        SectionCard {
            Text("Microphone", style = MaterialTheme.typography.titleMedium)
            val options = listOf(0 to "Phone default") + inputs.map { it.id to MicCaptureSource.describe(it) }
            options.forEach { (id, label) ->
                Row(
                    Modifier.fillMaxWidth().selectable(selected = settings.inputDeviceId == id, role = Role.RadioButton) {
                        onUpdate { it.copy(inputDeviceId = id) }
                    }.padding(vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = settings.inputDeviceId == id, onClick = null)
                    Text(label, Modifier.padding(start = 8.dp))
                }
            }
            Hint("Bluetooth headset microphones are not offered: Android limits them to low-quality call audio.")
        }
        Spacer(Modifier.height(12.dp))

        SectionCard {
            Text("Latency", style = MaterialTheme.typography.titleMedium)
            LatencyMode.entries.forEach { mode ->
                Row(
                    Modifier.fillMaxWidth().selectable(selected = settings.latencyMode == mode, role = Role.RadioButton) {
                        onUpdate { it.copy(latencyMode = mode) }
                    }.padding(vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = settings.latencyMode == mode, onClick = null)
                    Text(mode.label, Modifier.padding(start = 8.dp))
                }
            }
            Hint("Format: 48 kHz, 16-bit, mono PCM, encrypted on your local network. The PC sets the buffer size under Settings → Latency.")
        }
        Spacer(Modifier.height(12.dp))

        SectionCard {
            Text("Battery", style = MaterialTheme.typography.titleMedium)
            val pm = checkNotNull(context.getSystemService(PowerManager::class.java))
            val exempt = pm.isIgnoringBatteryOptimizations(context.packageName)
            Hint(
                if (exempt) "Battery optimisation is off for DroidMic."
                else "Some phones stop background apps aggressively. If streaming stops with the screen off, turn off battery optimisation for DroidMic.",
            )
            if (!exempt) {
                Spacer(Modifier.height(8.dp))
                OutlinedButton(onClick = {
                    context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}")))
                }) { Text("Open app settings") }
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}
