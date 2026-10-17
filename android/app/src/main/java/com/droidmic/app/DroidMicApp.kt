package com.droidmic.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import androidx.core.content.ContextCompat
import com.droidmic.app.audio.MicCaptureSource
import com.droidmic.app.data.KeystorePairingRepository
import com.droidmic.app.data.SettingsRepository
import com.droidmic.app.net.AndroidNetwork
import com.droidmic.app.service.MicService
import com.droidmic.core.session.ConnectTarget
import com.droidmic.core.session.StreamSession
import com.droidmic.core.session.StreamSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

/**
 * Process-wide objects. The [StreamSession] lives here so the UI and the
 * foreground service observe the same state; the service exists only while a
 * session is active, which is what keeps microphone access legal in the background.
 */
class DroidMicApp : Application() {
    lateinit var settings: SettingsRepository
        private set
    lateinit var pairings: KeystorePairingRepository
        private set
    lateinit var network: AndroidNetwork
        private set
    lateinit var session: StreamSession
        private set

    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()
        settings = SettingsRepository(this)
        pairings = KeystorePairingRepository(this)
        network = AndroidNetwork(this)
        session = StreamSession(
            deviceId = settings.deviceId,
            deviceName = settings.deviceName,
            pairings = pairings,
            sourceFactory = { MicCaptureSource(this, settings.settings.value) },
            scope = appScope,
            settings = { StreamSettings(gainDb = settings.settings.value.gainDb) },
            rediscover = { network.discover() },
        )
        val channel = NotificationChannel(MicService.CHANNEL_ID, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW)
            .apply { description = getString(R.string.notification_channel_desc); setShowBadge(false) }
        checkNotNull(getSystemService(NotificationManager::class.java)).createNotificationChannel(channel)
    }

    /** Starts streaming. Must be called from a visible activity (Android's while-in-use microphone rule). */
    fun startStreaming(target: ConnectTarget) {
        pendingTarget = target
        ContextCompat.startForegroundService(this, Intent(this, MicService::class.java).setAction(MicService.ACTION_START))
    }

    fun stopStreaming() {
        session.stop()
        startService(Intent(this, MicService::class.java).setAction(MicService.ACTION_STOP))
    }

    /** Handed from [startStreaming] to the service once it is in the foreground. */
    @Volatile var pendingTarget: ConnectTarget? = null
}
