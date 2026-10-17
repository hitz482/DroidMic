package com.droidmic.app.data

import android.content.Context
import android.media.MediaRecorder
import android.os.Build
import android.provider.Settings as SystemSettings
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.security.SecureRandom

/** How the microphone is opened. See docs/ARCHITECTURE.md §Android capture. */
enum class CaptureMode(val audioSource: Int, val label: String, val description: String) {
    NATURAL(
        MediaRecorder.AudioSource.VOICE_RECOGNITION, "Natural",
        "Minimal processing (no echo cancellation or automatic gain). Best for streaming and recording.",
    ),
    RAW(
        MediaRecorder.AudioSource.UNPROCESSED, "Raw",
        "Unprocessed microphone signal, if the phone supports it. Quieter; raise the gain.",
    ),
    VOICE_CALL(
        MediaRecorder.AudioSource.VOICE_COMMUNICATION, "Voice chat",
        "The phone's echo cancellation and noise reduction. Useful for calls without headphones.",
    ),
}

enum class LatencyMode(val frameMs: Int, val label: String) {
    LOW(10, "Low latency (10 ms frames)"),
    EFFICIENT(20, "Battery saver (20 ms frames)"),
}

data class AppSettings(
    val gainDb: Float = 0f,
    val captureMode: CaptureMode = CaptureMode.NATURAL,
    val noiseSuppression: Boolean = false,
    val latencyMode: LatencyMode = LatencyMode.LOW,
    val inputDeviceId: Int = 0, // 0 = system default
)

/** SharedPreferences-backed settings with an observable snapshot. */
class SettingsRepository(context: Context) {
    private val prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val appContext = context.applicationContext

    private val _settings = MutableStateFlow(load())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()

    val deviceId: String = prefs.getString("deviceId", null) ?: newDeviceId().also { prefs.edit().putString("deviceId", it).apply() }

    val deviceName: String
        get() = SystemSettings.Global.getString(appContext.contentResolver, SystemSettings.Global.DEVICE_NAME)
            ?.takeIf { it.isNotBlank() } ?: "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    var lastServerId: String?
        get() = prefs.getString("lastServerId", null)
        set(value) = prefs.edit().putString("lastServerId", value).apply()

    fun update(transform: (AppSettings) -> AppSettings) {
        val s = transform(_settings.value)
        _settings.value = s
        prefs.edit()
            .putFloat("gainDb", s.gainDb)
            .putString("captureMode", s.captureMode.name)
            .putBoolean("noiseSuppression", s.noiseSuppression)
            .putString("latencyMode", s.latencyMode.name)
            .putInt("inputDeviceId", s.inputDeviceId)
            .apply()
    }

    private fun load() = AppSettings(
        gainDb = prefs.getFloat("gainDb", 0f).coerceIn(-12f, 24f),
        captureMode = runCatching { CaptureMode.valueOf(prefs.getString("captureMode", null)!!) }.getOrDefault(CaptureMode.NATURAL),
        noiseSuppression = prefs.getBoolean("noiseSuppression", false),
        latencyMode = runCatching { LatencyMode.valueOf(prefs.getString("latencyMode", null)!!) }.getOrDefault(LatencyMode.LOW),
        inputDeviceId = prefs.getInt("inputDeviceId", 0),
    )

    private fun newDeviceId(): String {
        val b = ByteArray(8).also { SecureRandom().nextBytes(it) }
        return b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }
}
