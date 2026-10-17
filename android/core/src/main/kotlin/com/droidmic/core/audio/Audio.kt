package com.droidmic.core.audio

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

/** Format of the captured stream. Only PCM 16-bit is implemented. */
data class AudioFormat(val sampleRate: Int = 48000, val channels: Int = 1, val frameMs: Int = 10) {
    init {
        require(channels in 1..2)
        require(frameMs == 10 || frameMs == 20)
    }

    val frameSamples: Int get() = sampleRate * frameMs / 1000
}

/** A blocking source of PCM frames (the Android microphone, or a test tone). */
interface AudioSource : AutoCloseable {
    val format: AudioFormat
    val description: String

    fun start()

    /** Fills [buffer] with one frame (frameSamples × channels samples). Returns samples read, or -1 when stopped/failed. */
    fun readFrame(buffer: ShortArray): Int

    fun stop()

    override fun close() = stop()
}

/**
 * Peak/RMS meter with simple ballistics, matching the Windows meter.
 * Written by the capture thread, read by the UI thread.
 */
class LevelMeter {
    @Volatile var peakDb: Double = FLOOR_DB
        private set
    @Volatile var rmsDb: Double = FLOOR_DB
        private set

    fun process(samples: ShortArray, count: Int) {
        if (count <= 0) return
        var peak = 0
        var sumSq = 0.0
        for (i in 0 until count) {
            val s = samples[i].toInt()
            val a = abs(s)
            if (a > peak) peak = a
            sumSq += (s * s).toDouble()
        }
        val p = toDb(peak / 32768.0)
        val r = toDb(sqrt(sumSq / count) / 32768.0)
        peakDb = if (p >= peakDb) p else max(p, peakDb - 3.0)
        rmsDb = if (r >= rmsDb) r else max(r, rmsDb - 2.0)
    }

    fun reset() {
        peakDb = FLOOR_DB
        rmsDb = FLOOR_DB
    }

    companion object {
        const val FLOOR_DB = -90.0
        fun toDb(linear: Double): Double = if (linear <= 1e-9) FLOOR_DB else max(FLOOR_DB, 20 * log10(linear))
    }
}

/** Applies digital gain in place and reports whether the frame clipped. Allocation-free. */
object GainProcessor {
    /** Returns true if any sample hit full scale (before or after gain). */
    fun apply(samples: ShortArray, count: Int, gainDb: Float): Boolean {
        var clipped = false
        if (gainDb == 0f) {
            for (i in 0 until count) {
                val s = samples[i].toInt()
                if (s >= 32767 || s <= -32768) { clipped = true; break }
            }
            return clipped
        }
        val g = Math.pow(10.0, gainDb / 20.0).toFloat()
        for (i in 0 until count) {
            val v = (samples[i] * g).roundToInt()
            val c = when {
                v > 32767 -> { clipped = true; 32767 }
                v < -32768 -> { clipped = true; -32768 }
                else -> v
            }
            samples[i] = c.toShort()
        }
        return clipped
    }
}

/**
 * Real-time-paced sine generator; stands in for the microphone in tests and in the
 * JVM phone simulator.
 */
class SineSource(
    override val format: AudioFormat = AudioFormat(),
    private val frequencyHz: Double = 1000.0,
    private val amplitude: Double = 0.5,
    private val paced: Boolean = true,
) : AudioSource {
    override val description = "Test tone ${frequencyHz.toInt()} Hz"
    @Volatile private var running = false
    private var phase = 0L
    private var startNanos = 0L
    private var frames = 0L

    override fun start() {
        running = true
        startNanos = System.nanoTime()
        frames = 0
    }

    override fun readFrame(buffer: ShortArray): Int {
        if (!running) return -1
        val n = format.frameSamples
        if (paced) {
            val due = startNanos + frames * format.frameMs * 1_000_000L
            val wait = due - System.nanoTime()
            if (wait > 0) Thread.sleep(wait / 1_000_000L, (wait % 1_000_000L).toInt())
        }
        for (i in 0 until n) {
            val v = (amplitude * 32767 * sin(2 * PI * frequencyHz * phase / format.sampleRate)).roundToInt().toShort()
            for (c in 0 until format.channels) buffer[i * format.channels + c] = v
            phase++
        }
        frames++
        return n * format.channels
    }

    override fun stop() {
        running = false
    }
}
