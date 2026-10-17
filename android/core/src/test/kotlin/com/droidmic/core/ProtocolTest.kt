package com.droidmic.core

import com.droidmic.core.audio.GainProcessor
import com.droidmic.core.audio.LevelMeter
import com.droidmic.core.protocol.AudioPacketCodec
import com.droidmic.core.protocol.AuthOk
import com.droidmic.core.protocol.ControlCodec
import com.droidmic.core.protocol.ControlFraming
import com.droidmic.core.protocol.DiscoveryCodec
import com.droidmic.core.protocol.HandshakeCrypto
import com.droidmic.core.protocol.Hello
import com.droidmic.core.protocol.PairingUri
import com.droidmic.core.protocol.Ping
import com.droidmic.core.protocol.ProtocolException
import com.droidmic.core.protocol.Stats
import com.droidmic.core.protocol.StreamStop
import com.droidmic.core.protocol.UnknownMessage
import com.droidmic.core.protocol.hexToBytes
import com.droidmic.core.protocol.toHex
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProtocolTest {
    private val vectors: JsonObject by lazy {
        Json.parseToJsonElement(File(System.getProperty("droidmic.vectors")).readText()).jsonObject
    }

    private fun JsonObject.s(k: String) = this[k]!!.jsonPrimitive.content
    private fun JsonObject.b64(k: String) = Base64.getDecoder().decode(s(k))

    @Test
    fun keyScheduleMatchesSharedVectors() {
        val h = vectors["handshake"]!!.jsonObject
        val th = HandshakeCrypto.transcriptHash(h.s("deviceId"), h.b64("clientEph"), h.b64("clientNonce"),
            h.s("serverId"), h.b64("identity"), h.b64("serverEph"), h.b64("serverNonce"))
        assertEquals(h.s("transcriptHashHex"), th.toHex())
        val keys = HandshakeCrypto.deriveKeys(h.s("sharedSecretHex").hexToBytes(), h.s("authSecretUtf8").toByteArray(), th)
        assertEquals(h.s("clientConfirmHex"), keys.clientConfirm.toHex())
        assertEquals(h.s("serverConfirmHex"), keys.serverConfirm.toHex())
        assertEquals(h.s("audioKeyHex"), keys.audioKey.toHex())
        assertEquals(h.s("pairingWrapHex"), keys.pairingWrap.toHex())
        assertEquals(h.s("clientMacHex"), HandshakeCrypto.hmac(keys.clientConfirm, th).toHex())
        assertEquals(h.s("fingerprintOfIdentity"), HandshakeCrypto.fingerprint(h.b64("identity")))
    }

    @Test
    fun audioPacketMatchesSharedVector() {
        val a = vectors["audioPacket"]!!.jsonObject
        val pcmBytes = a.s("pcmHex").hexToBytes()
        val pcm = ShortArray(pcmBytes.size / 2) { ((pcmBytes[2 * it].toInt() and 0xFF) or (pcmBytes[2 * it + 1].toInt() shl 8)).toShort() }
        val codec = AudioPacketCodec(a.s("keyHex").hexToBytes(), a["sessionId"]!!.jsonPrimitive.long.toInt())
        val len = codec.encode(a["seq"]!!.jsonPrimitive.int, a["timestamp"]!!.jsonPrimitive.int, a["sampleRate"]!!.jsonPrimitive.int,
            a["channels"]!!.jsonPrimitive.int, a["flags"]!!.jsonPrimitive.int, pcm, pcm.size)
        assertEquals(a.s("datagramHex"), codec.buffer.copyOf(len).toHex())
    }

    @Test
    fun ecdhAndSignatureWorkWithSpkiEncoding() {
        val a = HandshakeCrypto.newEphemeralKeyPair()
        val b = HandshakeCrypto.newEphemeralKeyPair()
        val z1 = HandshakeCrypto.agree(a, HandshakeCrypto.encodePublicKey(b.public))
        val z2 = HandshakeCrypto.agree(b, HandshakeCrypto.encodePublicKey(a.public))
        assertEquals(32, z1.size)
        assertContentEquals(z1, z2)

        val gen = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
        val id = gen.generateKeyPair()
        val th = HandshakeCrypto.sha256("x".toByteArray())
        val sig = Signature.getInstance("SHA256withECDSA").run { initSign(id.private); update(th); sign() }
        assertTrue(HandshakeCrypto.verifySignature(id.public.encoded, th, sig))
        th[0] = (th[0].toInt() xor 1).toByte()
        assertFalse(HandshakeCrypto.verifySignature(id.public.encoded, th, sig))
        assertFailsWith<ProtocolException> { HandshakeCrypto.decodePublicKey(ByteArray(10)) }
    }

    @Test
    fun controlMessagesUseTypeDiscriminatorAndRoundTrip() {
        val hello = Hello(1, "0123456789abcdef", "Pixel", "ZXBo", "bm9uY2U=", Hello.MODE_PIN)
        val json = String(ControlCodec.encode(hello))
        assertTrue(json.startsWith("{\"t\":\"hello\""), json)
        assertEquals(hello, ControlCodec.decode(json.toByteArray()))
        assertEquals("{\"t\":\"stream_stop\"}", String(ControlCodec.encode(StreamStop)))
        assertEquals(StreamStop, ControlCodec.decode("{\"t\":\"stream_stop\"}".toByteArray()))

        // Messages as produced by the C# serializer.
        val ok = ControlCodec.decode("""{"t":"auth_ok","mac":"bWFj","sessionId":4000000000,"audioPort":48111}""".toByteArray())
        assertIs<AuthOk>(ok)
        assertNull(ok.pairing)
        assertEquals(-294967296, ok.sessionId.toInt()) // u32 carried in the bits of an Int
        val stats = ControlCodec.decode("""{"t":"stats","received":1,"lost":2,"late":3,"underruns":4,"bufferMs":5.5,"jitterMs":6.5,"sinkName":"DroidMic Microphone","sinkOk":true}""".toByteArray())
        assertEquals(5.5, (stats as Stats).bufferMs)
        assertIs<UnknownMessage>(ControlCodec.decode("""{"t":"future","x":1}""".toByteArray()))
        assertIs<Ping>(ControlCodec.decode("""{"t":"ping","ts":5,"extra":true}""".toByteArray()))

        assertFailsWith<ProtocolException> { ControlCodec.decode("nope".toByteArray()) }
        assertFailsWith<ProtocolException> { ControlCodec.decode("{\"x\":1}".toByteArray()) }
        assertFailsWith<ProtocolException> { ControlCodec.decode("{\"t\":\"auth\"}".toByteArray()) }
    }

    @Test
    fun framingRejectsBadLengths() {
        val out = ByteArrayOutputStream()
        ControlFraming.write(out, Ping(7))
        assertEquals(Ping(7), ControlFraming.read(ByteArrayInputStream(out.toByteArray())))
        assertNull(ControlFraming.read(ByteArrayInputStream(ByteArray(0))))
        assertFailsWith<ProtocolException> { ControlFraming.read(ByteArrayInputStream(byteArrayOf(0, 2, 0, 1))) }
        assertFailsWith<ProtocolException> { ControlFraming.read(ByteArrayInputStream(byteArrayOf(0, 0, 0, 0))) }
        assertFailsWith<ProtocolException> { ControlFraming.read(ByteArrayInputStream(byteArrayOf(0, 0, 0, 9, '{'.code.toByte()))) }
    }

    @Test
    fun pairingUriParsing() {
        val p = assertNotNull(PairingUri.parse("droidmic://pair?h=192.168.1.20&p=48110&pin=012345&fp=a1b2c3d4e5f6&n=Gaming%20PC"))
        assertEquals("192.168.1.20", p.host)
        assertEquals(48110, p.port)
        assertEquals("012345", p.pin)
        assertEquals("Gaming PC", p.serverName)
        assertNull(PairingUri.parse("https://example.com/pair?h=1.2.3.4&p=1&pin=123456&fp=a1b2c3d4e5f6"))
        assertNull(PairingUri.parse("droidmic://pair?h=1.2.3.4&p=99999&pin=123456&fp=a1b2c3d4e5f6"))
        assertNull(PairingUri.parse("droidmic://pair?h=1.2.3.4&p=1&pin=12a456&fp=a1b2c3d4e5f6"))
        assertNull(PairingUri.parse("droidmic://pair?h=1.2.3.4&p=1&pin=123456&fp=XYZ"))
        assertNull(PairingUri.parse("droidmic://pair?h=bad%20host&p=1&pin=123456&fp=a1b2c3d4e5f6"))
    }

    @Test
    fun discoveryReplyMustEchoNonce() {
        val nonce = ByteArray(8) { it.toByte() }
        assertEquals(15, DiscoveryCodec.buildRequest(nonce).size)
        val reply = """{"magic":"DMSRV1","v":1,"nonce":"${HandshakeCrypto.b64(nonce)}","serverId":"aa","name":"PC","ctl":48110,"fp":"abc"}""".toByteArray()
        assertNotNull(DiscoveryCodec.parseReply(reply, reply.size, nonce))
        assertNull(DiscoveryCodec.parseReply(reply, reply.size, ByteArray(8)))
        assertNull(DiscoveryCodec.parseReply("garbage".toByteArray(), 7, nonce))
    }

    @Test
    fun gainAndMeter() {
        val s = ShortArray(480) { 16384 }
        assertFalse(GainProcessor.apply(s, s.size, 0f))
        assertTrue(GainProcessor.apply(s, s.size, 6.1f))
        assertEquals(32767, s[0].toInt())
        val m = LevelMeter()
        m.process(ShortArray(480) { 16384 }, 480)
        assertEquals(-6.02, m.peakDb, 0.05)
        m.process(ShortArray(480), 480)
        assertEquals(-9.02, m.peakDb, 0.05)
    }
}
