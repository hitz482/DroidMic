package com.droidmic.app.service

import android.app.Notification
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import com.droidmic.app.DroidMicApp
import com.droidmic.app.R
import com.droidmic.app.ui.MainActivity
import com.droidmic.core.session.SessionState
import kotlinx.coroutines.launch

/**
 * Foreground service of type "microphone". It is started only by an explicit user
 * action in the activity, shows a persistent notification with a Stop action for
 * as long as the microphone is open, and stops itself when the session ends.
 */
class MicService : LifecycleService() {
    private val app get() = application as DroidMicApp
    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var started = false
    private var lastStartId = 0

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        lastStartId = startId
        when (intent?.action) {
            ACTION_STOP -> {
                app.session.stop()
                shutdown()
                return START_NOT_STICKY
            }
            ACTION_START -> {
                val target = app.pendingTarget
                app.pendingTarget = null
                if (target == null) {
                    shutdown()
                    return START_NOT_STICKY
                }
                try {
                    ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(getString(R.string.notification_title_connecting)),
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0)
                } catch (e: Exception) {
                    // E.g. ForegroundServiceStartNotAllowedException or missing RECORD_AUDIO.
                    Log.e(TAG, "Could not start the microphone service", e)
                    shutdown()
                    return START_NOT_STICKY
                }
                acquireLocks()
                app.session.start(target)
                if (!started) {
                    started = true
                    lifecycleScope.launch { app.session.state.collect(::onState) }
                }
            }
        }
        // Never restart automatically: microphone use must always follow a user action.
        return START_NOT_STICKY
    }

    private fun onState(state: SessionState) {
        when (state) {
            is SessionState.Connecting -> notify(getString(R.string.notification_title_connecting))
            is SessionState.Streaming -> notify(getString(R.string.notification_title_streaming, state.serverName))
            is SessionState.Reconnecting -> notify(getString(R.string.notification_title_reconnecting, state.serverName))
            is SessionState.Failed -> shutdown()
            SessionState.Idle -> if (!app.session.isActive) shutdown()
        }
    }

    private fun buildNotification(title: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, MicService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_mic)
            .setContentTitle(title)
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(open)
            .addAction(R.drawable.ic_mic_off, getString(R.string.action_stop), stop)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    private fun notify(title: String) {
        if (!started) return
        getSystemService(android.app.NotificationManager::class.java)?.notify(NOTIFICATION_ID, buildNotification(title))
    }

    /** Keeps Wi-Fi out of power-save (which adds 100+ ms bursts) and the CPU awake while streaming. */
    private fun acquireLocks() {
        if (wifiLock == null) {
            val wm = applicationContext.getSystemService(WifiManager::class.java)
            @Suppress("DEPRECATION")
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = wm?.createWifiLock(mode, "droidmic:stream")?.apply { setReferenceCounted(false); acquire() }
        }
        if (wakeLock == null) {
            val pm = checkNotNull(getSystemService(PowerManager::class.java))
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "droidmic:stream").apply { setReferenceCounted(false); acquire(12 * 60 * 60 * 1000L) }
        }
    }

    private fun shutdown() {
        wifiLock?.release()
        wifiLock = null
        wakeLock?.release()
        wakeLock = null
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        // Only stops if no newer start request arrived meanwhile.
        stopSelf(lastStartId)
    }

    override fun onDestroy() {
        // If the system kills the service, make sure the microphone is released too.
        app.session.stop()
        wifiLock?.release()
        wakeLock?.release()
        super.onDestroy()
    }

    companion object {
        const val CHANNEL_ID = "streaming"
        const val NOTIFICATION_ID = 1
        const val ACTION_START = "com.droidmic.app.START"
        const val ACTION_STOP = "com.droidmic.app.STOP"
        private const val TAG = "DroidMicService"
    }
}
