package com.droidmic.app.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.droidmic.app.R
import com.droidmic.app.ui.screens.ConnectScreen
import com.droidmic.app.ui.screens.DiagnosticsScreen
import com.droidmic.app.ui.screens.HomeScreen
import com.droidmic.app.ui.screens.SettingsScreen
import com.droidmic.app.ui.theme.DroidMicTheme
import com.droidmic.core.session.SessionState
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : ComponentActivity() {
    private val vm: MainViewModel by viewModels()

    /** Work to run once RECORD_AUDIO is granted (starting the microphone). */
    private var afterPermission: (() -> Unit)? = null
    private var showPermissionRationale by mutableStateOf(false)
    private var selectedTab by mutableIntStateOf(0)

    private val micPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val action = afterPermission
        afterPermission = null
        if (granted) action?.invoke() else showPermissionRationale = true
    }

    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* optional */ }

    private val scanQr = registerForActivityResult(ScanContract()) { result ->
        val text = result.contents ?: return@registerForActivityResult
        withMicPermission {
            if (!vm.pairWithUri(text)) Toast.makeText(this, "That is not a DroidMic pairing code", Toast.LENGTH_LONG).show()
            else selectedTab = 0
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            // The microphone notification still appears in the task manager without this; ask once, non-blocking.
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        handleDeepLink(intent)
        setContent { DroidMicTheme { App() } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleDeepLink(intent)
    }

    /** droidmic://pair?... from a QR code scanned with the system camera. Pairing still needs a tap. */
    private fun handleDeepLink(intent: Intent?) {
        val data = intent?.data ?: return
        if (data.scheme != "droidmic") return
        val text = data.toString()
        intent.data = null
        pendingDeepLink = text
    }

    private var pendingDeepLink by mutableStateOf<String?>(null)

    private fun withMicPermission(action: () -> Unit) {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            action()
        } else {
            afterPermission = action
            micPermission.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    @Composable
    private fun App() {
        val state by vm.state.collectAsStateWithLifecycle()
        val stats by vm.stats.collectAsStateWithLifecycle()
        val levels by vm.levels.collectAsStateWithLifecycle()
        val settings by vm.settings.collectAsStateWithLifecycle()
        val pairings by vm.pairings.collectAsStateWithLifecycle()
        val discovery by vm.discovery.collectAsStateWithLifecycle()
        val pinPrompt by vm.pinPrompt.collectAsStateWithLifecycle()
        val network by vm.network.collectAsStateWithLifecycle()
        val silenced by vm.micSilenced.collectAsStateWithLifecycle()
        val micOn = state is SessionState.Connecting || state is SessionState.Streaming || state is SessionState.Reconnecting

        Scaffold(
            containerColor = MaterialTheme.colorScheme.background,
            bottomBar = {
                NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                    val tabs = listOf(
                        Triple("Home", R.drawable.ic_home, 0),
                        Triple("Connect", R.drawable.ic_computer, 1),
                        Triple("Audio", R.drawable.ic_tune, 2),
                        Triple("Diagnostics", R.drawable.ic_chart, 3),
                    )
                    tabs.forEach { (label, icon, index) ->
                        NavigationBarItem(
                            selected = selectedTab == index,
                            onClick = { selectedTab = index },
                            icon = { Icon(painterResource(icon), contentDescription = null) },
                            label = { Text(label) },
                        )
                    }
                }
            },
        ) { padding ->
            Box(Modifier.fillMaxSize().padding(padding)) {
                when (selectedTab) {
                    0 -> HomeScreen(
                        state = state, stats = stats, levels = levels, network = network, settings = settings,
                        defaultPc = vm.defaultPc().takeIf { pairings.isNotEmpty() }, micSilenced = silenced,
                        onStart = { vm.defaultPc()?.let { pc -> withMicPermission { vm.startWith(pc) } } },
                        onStop = vm::stop,
                        onGoToConnect = { selectedTab = 1 },
                        onDismissError = vm::acknowledgeError,
                    )
                    1 -> ConnectScreen(
                        state = state, discovery = discovery, pairings = pairings, pinPrompt = pinPrompt,
                        onDiscover = vm::discover,
                        onConnectDiscovered = { pc -> withMicPermission { vm.connectTo(pc); selectedTab = 0 } },
                        onConnectPaired = { pc -> withMicPermission { vm.startWith(pc); selectedTab = 0 } },
                        onForget = vm::forget,
                        onManual = { host, port -> vm.promptManual(host, port) },
                        onScanQr = {
                            scanQr.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("Scan the QR code shown by DroidMic on your PC").setBeepEnabled(false).setOrientationLocked(false))
                        },
                        onSubmitPin = { prompt, pin -> withMicPermission { vm.pairWithPin(prompt, pin); selectedTab = 0 } },
                        onDismissPin = vm::dismissPinPrompt,
                    )
                    2 -> SettingsScreen(settings, micOn) { vm.updateSettings(it) }
                    3 -> DiagnosticsScreen(state, stats, network, settings.latencyMode.frameMs)
                }
            }
        }

        if (showPermissionRationale) {
            AlertDialog(
                onDismissRequest = { showPermissionRationale = false },
                title = { Text("Microphone permission needed") },
                text = {
                    Text("DroidMic needs the microphone to send your voice to your PC. Audio goes only to the PC you paired, encrypted, and is never recorded. You can grant the permission in the app settings.")
                },
                confirmButton = {
                    TextButton(onClick = {
                        showPermissionRationale = false
                        startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
                    }) { Text("Open settings") }
                },
                dismissButton = { TextButton(onClick = { showPermissionRationale = false }) { Text("Not now") } },
            )
        }

        pendingDeepLink?.let { link ->
            AlertDialog(
                onDismissRequest = { pendingDeepLink = null },
                title = { Text("Pair with this PC?") },
                text = { Column { Text("A DroidMic pairing code was opened. Pair and start the microphone?") } },
                confirmButton = {
                    TextButton(onClick = {
                        pendingDeepLink = null
                        withMicPermission {
                            if (!vm.pairWithUri(link)) Toast.makeText(this, "Invalid pairing code", Toast.LENGTH_LONG).show() else selectedTab = 0
                        }
                    }) { Text("Pair") }
                },
                dismissButton = { TextButton(onClick = { pendingDeepLink = null }) { Text("Cancel") } },
            )
        }
    }
}
