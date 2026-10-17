package com.droidmic.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.droidmic.app.DroidMicApp
import com.droidmic.app.audio.MicCaptureSource
import com.droidmic.app.data.AppSettings
import com.droidmic.app.net.NetworkStatus
import com.droidmic.core.net.DiscoveredPc
import com.droidmic.core.protocol.PairingUri
import com.droidmic.core.protocol.Protocol
import com.droidmic.core.session.ConnectTarget
import com.droidmic.core.session.SessionState
import com.droidmic.core.session.SessionStats
import com.droidmic.core.session.StoredPairing
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class Levels(val peakDb: Double = -90.0, val rmsDb: Double = -90.0)

data class DiscoveryState(val searching: Boolean = false, val found: List<DiscoveredPc> = emptyList(), val searched: Boolean = false)

/** Pairing request awaiting PIN entry (from discovery or manual entry). */
data class PinPrompt(val host: String, val port: Int, val name: String, val expectedFingerprint: String?)

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as DroidMicApp
    private val session = app.session

    val state: StateFlow<SessionState> = session.state
    val stats: StateFlow<SessionStats> = session.stats
    val settings: StateFlow<AppSettings> = app.settings.settings
    val pairings: StateFlow<List<StoredPairing>> = app.pairings.pairings
    val micSilenced: StateFlow<Boolean> = MicCaptureSource.silenced
    val network: StateFlow<NetworkStatus> = app.network.statusFlow()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), app.network.currentStatus())

    private val _levels = MutableStateFlow(Levels())
    val levels: StateFlow<Levels> = _levels.asStateFlow()

    private val _discovery = MutableStateFlow(DiscoveryState())
    val discovery: StateFlow<DiscoveryState> = _discovery.asStateFlow()

    private val _pinPrompt = MutableStateFlow<PinPrompt?>(null)
    val pinPrompt: StateFlow<PinPrompt?> = _pinPrompt.asStateFlow()

    val deviceName: String get() = app.settings.deviceName

    init {
        // Meter refresh at ~20 Hz only while the screen is observed; cheap reads of volatile fields.
        viewModelScope.launch {
            while (isActive) {
                _levels.value = Levels(session.meter.peakDb, session.meter.rmsDb)
                delay(50)
            }
        }
    }

    /** The PC to use for one-tap Start: the last one used, else the only paired one. */
    fun defaultPc(): StoredPairing? {
        val all = pairings.value
        return all.firstOrNull { it.serverId == app.settings.lastServerId } ?: all.singleOrNull()
    }

    fun startWith(pc: StoredPairing) {
        app.settings.lastServerId = pc.serverId
        app.startStreaming(ConnectTarget.Paired(pc.serverId))
    }

    fun stop() = app.stopStreaming()

    fun acknowledgeError() = session.acknowledgeError()

    fun discover() {
        if (_discovery.value.searching) return
        _discovery.value = _discovery.value.copy(searching = true)
        viewModelScope.launch {
            val found = try { app.network.discover() } catch (e: Exception) { emptyList() }
            _discovery.value = DiscoveryState(searching = false, found = found, searched = true)
        }
    }

    /** User tapped a discovered PC: reconnect if paired, otherwise ask for the PIN. */
    fun connectTo(pc: DiscoveredPc) {
        val paired = app.pairings.get(pc.serverId)
        if (paired != null && paired.fingerprint == pc.fingerprint) {
            app.settings.lastServerId = pc.serverId
            app.startStreaming(ConnectTarget.Paired(pc.serverId, pc.host, pc.controlPort))
        } else {
            _pinPrompt.value = PinPrompt(pc.host, pc.controlPort, pc.name, expectedFingerprint = pc.fingerprint)
        }
    }

    fun promptManual(host: String, port: Int) {
        _pinPrompt.value = PinPrompt(host.trim(), port, host.trim(), expectedFingerprint = null)
    }

    fun dismissPinPrompt() {
        _pinPrompt.value = null
    }

    fun pairWithPin(prompt: PinPrompt, pin: String) {
        _pinPrompt.value = null
        app.startStreaming(ConnectTarget.PairWithPin(prompt.host, prompt.port, pin, prompt.expectedFingerprint))
    }

    /** From a scanned or deep-linked QR code. Returns false if the code is not a DroidMic code. */
    fun pairWithUri(text: String): Boolean {
        val uri = PairingUri.parse(text) ?: return false
        app.startStreaming(ConnectTarget.PairWithPin(uri.host, uri.port, uri.pin, uri.fingerprint))
        return true
    }

    fun forget(pc: StoredPairing) = app.pairings.remove(pc.serverId)

    fun updateSettings(transform: (AppSettings) -> AppSettings) = app.settings.update(transform)

    companion object {
        fun isValidHost(h: String): Boolean = h.isNotBlank() && h.length <= 253 && h.all { it.isLetterOrDigit() || it == '.' || it == '-' || it == ':' }
        fun isValidPort(p: Int): Boolean = p in 1..65535
        const val DEFAULT_PORT = Protocol.DEFAULT_CONTROL_PORT
    }
}
