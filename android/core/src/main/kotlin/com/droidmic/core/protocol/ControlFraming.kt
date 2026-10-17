package com.droidmic.core.protocol

import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer

/** `u32 length || JSON` framing for the TCP control channel (protocol §2). */
object ControlFraming {
    fun write(out: OutputStream, message: ControlMessage) {
        val body = ControlCodec.encode(message)
        if (body.size > Protocol.MAX_CONTROL_MESSAGE_BYTES) throw ProtocolException("Control message too large")
        val frame = ByteBuffer.allocate(4 + body.size).putInt(body.size).put(body).array()
        synchronized(out) {
            out.write(frame)
            out.flush()
        }
    }

    /** Reads one message, or returns null on a clean end of stream. */
    fun read(input: InputStream): ControlMessage? {
        val din = if (input is DataInputStream) input else DataInputStream(input)
        val first = din.read()
        if (first < 0) return null
        val rest = ByteArray(3)
        try {
            din.readFully(rest)
        } catch (e: EOFException) {
            throw ProtocolException("Connection closed mid-frame", e)
        }
        val len = (first shl 24) or ((rest[0].toInt() and 0xFF) shl 16) or
            ((rest[1].toInt() and 0xFF) shl 8) or (rest[2].toInt() and 0xFF)
        if (len <= 0 || len > Protocol.MAX_CONTROL_MESSAGE_BYTES) throw ProtocolException("Invalid control frame length $len")
        val body = ByteArray(len)
        try {
            din.readFully(body)
        } catch (e: EOFException) {
            throw ProtocolException("Connection closed mid-frame", e)
        }
        return ControlCodec.decode(body)
    }
}
