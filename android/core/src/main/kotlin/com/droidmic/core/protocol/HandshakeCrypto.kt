package com.droidmic.core.protocol

import java.io.ByteArrayOutputStream
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PublicKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class SessionKeys(
    val clientConfirm: ByteArray,
    val serverConfirm: ByteArray,
    val audioKey: ByteArray,
    val pairingWrap: ByteArray,
) {
    fun wipe() {
        clientConfirm.fill(0); serverConfirm.fill(0); audioKey.fill(0); pairingWrap.fill(0)
    }
}

/** Handshake math (protocol §3). Mirrors windows/DroidMic.Protocol/HandshakeCrypto.cs. */
object HandshakeCrypto {
    fun transcriptHash(
        deviceId: String,
        clientEph: ByteArray,
        clientNonce: ByteArray,
        serverId: String,
        identity: ByteArray,
        serverEph: ByteArray,
        serverNonce: ByteArray,
    ): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(Protocol.TRANSCRIPT_LABEL.toByteArray(Charsets.UTF_8))
        for (field in listOf(
            deviceId.toByteArray(Charsets.UTF_8), clientEph, clientNonce,
            serverId.toByteArray(Charsets.UTF_8), identity, serverEph, serverNonce,
        )) {
            if (field.size > 0xFFFF) throw ProtocolException("Field too long")
            out.write(field.size shr 8)
            out.write(field.size and 0xFF)
            out.write(field)
        }
        return sha256(out.toByteArray())
    }

    fun deriveKeys(sharedSecret: ByteArray, authSecret: ByteArray, transcriptHash: ByteArray): SessionKeys {
        val prk = hmac(transcriptHash, sharedSecret + authSecret)
        try {
            return SessionKeys(
                clientConfirm = hkdfExpand(prk, Protocol.INFO_CLIENT_CONFIRM, 32),
                serverConfirm = hkdfExpand(prk, Protocol.INFO_SERVER_CONFIRM, 32),
                audioKey = hkdfExpand(prk, Protocol.INFO_AUDIO_KEY, 16),
                pairingWrap = hkdfExpand(prk, Protocol.INFO_PAIRING_WRAP, 16),
            )
        } finally {
            prk.fill(0)
        }
    }

    /** RFC 5869 HKDF-Expand with SHA-256. */
    fun hkdfExpand(prk: ByteArray, info: String, length: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(prk, "HmacSHA256"))
        val result = ByteArray(length)
        var t = ByteArray(0)
        var offset = 0
        var counter = 1
        while (offset < length) {
            mac.update(t)
            mac.update(info.toByteArray(Charsets.UTF_8))
            mac.update(counter.toByte())
            t = mac.doFinal()
            val n = minOf(t.size, length - offset)
            System.arraycopy(t, 0, result, offset, n)
            offset += n
            counter++
        }
        return result
    }

    fun hmac(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    fun verifyMac(key: ByteArray, transcriptHash: ByteArray, mac: ByteArray): Boolean =
        MessageDigest.isEqual(hmac(key, transcriptHash), mac)

    fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)

    fun newEphemeralKeyPair(): KeyPair {
        val gen = KeyPairGenerator.getInstance("EC")
        gen.initialize(ECGenParameterSpec("secp256r1"))
        return gen.generateKeyPair()
    }

    /** X.509 SubjectPublicKeyInfo DER, as used on the wire. */
    fun encodePublicKey(key: PublicKey): ByteArray = key.encoded

    fun decodePublicKey(spki: ByteArray): ECPublicKey {
        val key = try {
            KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki))
        } catch (e: Exception) {
            throw ProtocolException("Invalid public key", e)
        }
        if (key !is ECPublicKey || key.params.curve.field.fieldSize != 256) throw ProtocolException("Key is not P-256")
        return key
    }

    /** ECDH returning the raw 32-byte shared x-coordinate. */
    fun agree(local: KeyPair, peerSpki: ByteArray): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(local.private)
        ka.doPhase(decodePublicKey(peerSpki), true)
        return ka.generateSecret()
    }

    fun verifySignature(identitySpki: ByteArray, transcriptHash: ByteArray, derSignature: ByteArray): Boolean = try {
        val sig = Signature.getInstance("SHA256withECDSA")
        sig.initVerify(decodePublicKey(identitySpki))
        sig.update(transcriptHash)
        sig.verify(derSignature)
    } catch (e: java.security.SignatureException) {
        false
    }

    fun unwrapPairingKey(wrapKey: ByteArray, wrapped: ByteArray, transcriptHash: ByteArray): ByteArray {
        if (wrapped.size != Protocol.PAIRING_KEY_LENGTH + 16) throw ProtocolException("Bad wrapped key length")
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(wrapKey, "AES"), GCMParameterSpec(128, ByteArray(12)))
        cipher.updateAAD(transcriptHash)
        return try {
            cipher.doFinal(wrapped)
        } catch (e: javax.crypto.AEADBadTagException) {
            throw ProtocolException("Pairing key authentication failed", e)
        }
    }

    fun fingerprint(identitySpki: ByteArray): String = sha256(identitySpki).copyOf(6).toHex()

    fun b64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    fun fromB64(value: String, expectedLength: Int? = null, field: String): ByteArray {
        val bytes = try {
            Base64.getDecoder().decode(value)
        } catch (e: IllegalArgumentException) {
            throw ProtocolException("$field is not base64", e)
        }
        if (expectedLength != null && bytes.size != expectedLength) throw ProtocolException("$field has wrong length")
        if (bytes.size > 512) throw ProtocolException("$field is too long")
        return bytes
    }
}

fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it.toInt() and 0xFF) }

fun String.hexToBytes(): ByteArray {
    require(length % 2 == 0)
    return ByteArray(length / 2) { substring(2 * it, 2 * it + 2).toInt(16).toByte() }
}
