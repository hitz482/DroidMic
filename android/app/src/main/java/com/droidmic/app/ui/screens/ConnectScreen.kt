package com.droidmic.app.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.droidmic.app.R
import com.droidmic.app.ui.DiscoveryState
import com.droidmic.app.ui.MainViewModel
import com.droidmic.app.ui.PinPrompt
import com.droidmic.app.ui.components.Hint
import com.droidmic.app.ui.components.SectionCard
import com.droidmic.app.ui.theme.DroidMicColors
import com.droidmic.core.net.DiscoveredPc
import com.droidmic.core.session.SessionState
import com.droidmic.core.session.StoredPairing

@Composable
fun ConnectScreen(
    state: SessionState,
    discovery: DiscoveryState,
    pairings: List<StoredPairing>,
    pinPrompt: PinPrompt?,
    onDiscover: () -> Unit,
    onConnectDiscovered: (DiscoveredPc) -> Unit,
    onConnectPaired: (StoredPairing) -> Unit,
    onForget: (StoredPairing) -> Unit,
    onManual: (String, Int) -> Unit,
    onScanQr: () -> Unit,
    onSubmitPin: (PinPrompt, String) -> Unit,
    onDismissPin: () -> Unit,
) {
    LaunchedEffect(Unit) { if (!discovery.searched) onDiscover() }
    val busy = state is SessionState.Connecting

    Column(Modifier.verticalScroll(rememberScrollState()).padding(20.dp)) {
        Text("Connect to PC", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(4.dp))
        Hint("Open DroidMic on your Windows PC. Phone and PC must be on the same Wi-Fi network, or connected with USB tethering.")
        Spacer(Modifier.height(16.dp))

        if (busy) {
            SectionCard {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(12.dp))
                    Text("Connecting to ${(state as SessionState.Connecting).host}…")
                }
            }
            Spacer(Modifier.height(12.dp))
        }
        if (state is SessionState.Failed) {
            SectionCard {
                Text("Could not connect", style = MaterialTheme.typography.titleMedium, color = DroidMicColors.Bad)
                Spacer(Modifier.height(4.dp))
                Hint(state.message)
            }
            Spacer(Modifier.height(12.dp))
        }

        SectionCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(painterResource(R.drawable.ic_qr), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(10.dp))
                Text("Pair with a QR code", style = MaterialTheme.typography.titleMedium)
            }
            Spacer(Modifier.height(6.dp))
            Hint("On the PC, choose “Pair phone” and scan the code. This is the most secure way to pair.")
            Spacer(Modifier.height(12.dp))
            Button(onClick = onScanQr, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Scan QR code") }
        }

        if (pairings.isNotEmpty()) {
            Spacer(Modifier.height(12.dp))
            SectionCard {
                Text("Paired PCs", style = MaterialTheme.typography.titleMedium)
                pairings.forEachIndexed { i, pc ->
                    if (i > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline)
                    Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(pc.serverName, style = MaterialTheme.typography.bodyLarge)
                            Hint("${pc.host} · identity ${pc.fingerprint}")
                        }
                        TextButton(onClick = { onForget(pc) }) { Text("Forget") }
                        Button(onClick = { onConnectPaired(pc) }, enabled = !busy) { Text("Connect") }
                    }
                }
            }
        }

        Spacer(Modifier.height(12.dp))
        SectionCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(painterResource(R.drawable.ic_computer), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(10.dp))
                Text("PCs on this network", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                if (discovery.searching) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                else TextButton(onClick = onDiscover) { Text("Refresh") }
            }
            if (discovery.found.isEmpty() && discovery.searched && !discovery.searching) {
                Spacer(Modifier.height(6.dp))
                Hint("No PCs found. Check that DroidMic is running on the PC and listening, that both devices are on the same network, and that the network allows device-to-device traffic (guest networks often don't). You can also enter the address manually below.")
            }
            discovery.found.forEach { pc ->
                Row(
                    Modifier.fillMaxWidth().clickable(enabled = !busy) { onConnectDiscovered(pc) }.padding(vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(pc.name, style = MaterialTheme.typography.bodyLarge)
                        Hint("${pc.host}:${pc.controlPort}")
                    }
                    Text(if (pairings.any { it.serverId == pc.serverId }) "Connect" else "Pair", color = MaterialTheme.colorScheme.primary)
                }
            }
        }

        Spacer(Modifier.height(12.dp))
        ManualEntry(enabled = !busy, onManual = onManual)
        Spacer(Modifier.height(24.dp))
    }

    if (pinPrompt != null) PinDialog(pinPrompt, onSubmitPin, onDismissPin)
}

@Composable
private fun ManualEntry(enabled: Boolean, onManual: (String, Int) -> Unit) {
    var host by rememberSaveable { mutableStateOf("") }
    var port by rememberSaveable { mutableStateOf(MainViewModel.DEFAULT_PORT.toString()) }
    val portNum = port.toIntOrNull() ?: -1
    val valid = MainViewModel.isValidHost(host) && MainViewModel.isValidPort(portNum)
    SectionCard {
        Text("Enter address manually", style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(4.dp))
        Hint("Shown on the PC under “Pair phone”, for example 192.168.1.20.")
        Spacer(Modifier.height(10.dp))
        OutlinedTextField(
            value = host, onValueChange = { host = it.trim() }, label = { Text("PC address") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(
            value = port, onValueChange = { port = it.filter(Char::isDigit).take(5) }, label = { Text("Port") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth(),
            isError = port.isNotEmpty() && !MainViewModel.isValidPort(portNum),
        )
        Spacer(Modifier.height(12.dp))
        OutlinedButton(onClick = { onManual(host, portNum) }, enabled = enabled && valid, modifier = Modifier.fillMaxWidth()) { Text("Continue") }
    }
}

@Composable
private fun PinDialog(prompt: PinPrompt, onSubmit: (PinPrompt, String) -> Unit, onDismiss: () -> Unit) {
    var pin by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pair with ${prompt.name}") },
        text = {
            Column {
                Text("On the PC, open DroidMic → Pair phone and enter the 6-digit PIN shown there.")
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = pin, onValueChange = { pin = it.filter(Char::isDigit).take(6) }, label = { Text("PIN") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                )
                prompt.expectedFingerprint?.let {
                    Spacer(Modifier.height(8.dp))
                    Hint("PC identity code: $it. It should match the code on the PC.")
                }
            }
        },
        confirmButton = { Button(onClick = { onSubmit(prompt, pin) }, enabled = pin.length == 6) { Text("Pair") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
