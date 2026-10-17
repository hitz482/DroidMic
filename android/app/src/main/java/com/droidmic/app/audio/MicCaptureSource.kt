package com.droidmic.app.audio

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioFormat as AndroidAudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioRecordingConfiguration
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.util.Log
import com.droidmic.app.data.AppSettings
import com.droidmic.app.data.CaptureMode
import com.droidmic.core.audio.AudioFormat
import com.droidmic.core.audio.AudioSource
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The phone microphone, via [AudioRecord] at 48 kHz mono 16-bit. Nothing is
 * written to storage. Reads block on the capture thread created by StreamSession.
 */
class MicCaptureSource(
    private val context: Context,
    private val settings: AppSettings,
) : AudioSource {
    override val format = AudioFormat(SAMPLE_RATE, 1, settings.latencyMode.frameMs)
    override var description: String = "Microphone"
        private set

    private var record: AudioRecord? = null
    private var noiseSuppressor: NoiseSuppressor? = null
    private var agc: AutomaticGainControl? = null
    private var recordingCallback: AudioManager.AudioRecordingCallback? = null
    @Volatile private var priorityRaised = false

    @SuppressLint("MissingPermission") // Checked by the UI before the service starts; failure surfaces as an exception.
    override fun start() {
        val audioManager = context.audioManager()
        val mode = if (settings.captureMode == CaptureMode.RAW && !supportsUnprocessed(audioManager)) CaptureMode.NATURAL else settings.captureMode

        val minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AndroidAudioFormat.CHANNEL_IN_MONO, AndroidAudioFormat.ENCODING_PCM_16BIT)
        if (minBuf <= 0) throw IllegalStateException("This phone cannot record 48 kHz mono audio")
        val frameBytes = format.frameSamples * 2
        val r = AudioRecord.Builder()
            .setAudioSource(mode.audioSource)
            .setAudioFormat(
                AndroidAudioFormat.Builder()
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AndroidAudioFormat.CHANNEL_IN_MONO)
                    .setEncoding(AndroidAudioFormat.ENCODING_PCM_16BIT)
                    .build(),
            )
            .setBufferSizeInBytes(maxOf(minBuf, frameBytes * 8))
            .build()
        if (r.state != AudioRecord.STATE_INITIALIZED) {
            r.release()
            throw IllegalStateException("The microphone could not be opened (it may be in use by another app)")
        }

        val device = selectedDevice(audioManager)
        if (device != null) r.preferredDevice = device

        if (NoiseSuppressor.isAvailable()) {
            noiseSuppressor = NoiseSuppressor.create(r.audioSessionId)?.apply { enabled = settings.noiseSuppression }
        }
        if (mode != CaptureMode.VOICE_CALL && AutomaticGainControl.isAvailable()) {
            // Keep levels natural and predictable; the user controls gain in the app.
            agc = AutomaticGainControl.create(r.audioSessionId)?.apply { enabled = false }
        }

        r.startRecording()
        if (r.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            r.release()
            throw IllegalStateException("The microphone did not start (another app may be using it)")
        }
        record = r
        description = "${device?.let { describe(it) } ?: "Built-in microphone"} · ${mode.label}"
        watchSilencing(audioManager, r)
        Log.i(TAG, "Capture started: $description, buffer ${maxOf(minBuf, frameBytes * 8)} bytes")
    }

    override fun readFrame(buffer: ShortArray): Int {
        val r = record ?: return -1
        if (!priorityRaised) {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            priorityRaised = true
        }
        val n = format.frameSamples
        var read = 0
        while (read < n) {
            val got = r.read(buffer, read, n - read, AudioRecord.READ_BLOCKING)
            if (stopped) return -1
            if (got < 0) {
                Log.w(TAG, "AudioRecord.read failed: $got")
                return -1
            }
            read += got
        }
        return n
    }

    @Volatile private var stopped = false

    /** Unblocks a pending read. Native resources are released in [close], after the capture thread has exited. */
    override fun stop() {
        stopped = true
        try { record?.stop() } catch (_: IllegalStateException) { }
    }

    override fun close() {
        stop()
        val r = record ?: return
        record = null
        recordingCallback?.let { context.audioManager().unregisterAudioRecordingCallback(it) }
        recordingCallback = null
        noiseSuppressor?.release()
        agc?.release()
        noiseSuppressor = null
        agc = null
        r.release()
        _silenced.value = false
        Log.i(TAG, "Capture stopped")
    }

    /** Android silences our input when another app takes the microphone (e.g. a phone call). */
    private fun watchSilencing(audioManager: AudioManager, r: AudioRecord) {
        val cb = object : AudioManager.AudioRecordingCallback() {
            override fun onRecordingConfigChanged(configs: MutableList<AudioRecordingConfiguration>) {
                val mine = configs.firstOrNull { it.clientAudioSessionId == r.audioSessionId } ?: return
                _silenced.value = mine.isClientSilenced
            }
        }
        audioManager.registerAudioRecordingCallback(cb, Handler(Looper.getMainLooper()))
        recordingCallback = cb
    }

    private fun selectedDevice(audioManager: AudioManager): AudioDeviceInfo? =
        if (settings.inputDeviceId == 0) null
        else audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.id == settings.inputDeviceId }

    companion object {
        const val SAMPLE_RATE = 48000

        private fun Context.audioManager(): AudioManager = checkNotNull(getSystemService(AudioManager::class.java))
        private const val TAG = "DroidMicCapture"

        private val _silenced = MutableStateFlow(false)
        /** True while Android is feeding us silence because another app holds the microphone. */
        val silenced: StateFlow<Boolean> = _silenced.asStateFlow()

        fun supportsUnprocessed(am: AudioManager): Boolean =
            am.getProperty(AudioManager.PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED) == "true"

        /** Inputs the user can choose. Bluetooth SCO is excluded: it needs call-mode routing and is 16 kHz at best. */
        fun availableInputs(context: Context): List<AudioDeviceInfo> =
            context.audioManager().getDevices(AudioManager.GET_DEVICES_INPUTS).filter {
                it.type in setOf(
                    AudioDeviceInfo.TYPE_BUILTIN_MIC, AudioDeviceInfo.TYPE_WIRED_HEADSET,
                    AudioDeviceInfo.TYPE_USB_DEVICE, AudioDeviceInfo.TYPE_USB_HEADSET,
                )
            }

        fun describe(d: AudioDeviceInfo): String {
            val type = when (d.type) {
                AudioDeviceInfo.TYPE_BUILTIN_MIC -> "Built-in microphone"
                AudioDeviceInfo.TYPE_WIRED_HEADSET -> "Wired headset"
                AudioDeviceInfo.TYPE_USB_DEVICE, AudioDeviceInfo.TYPE_USB_HEADSET -> "USB microphone"
                else -> "Input ${d.type}"
            }
            val addr = d.address?.takeIf { it.isNotBlank() && d.type == AudioDeviceInfo.TYPE_BUILTIN_MIC }
            return if (addr != null) "$type ($addr)" else type
        }
    }
}
