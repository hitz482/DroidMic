package com.droidmic.app.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.droidmic.core.session.PairingRepository
import com.droidmic.core.session.StoredPairing
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

@Serializable
data class StoredPairingJson(
    val serverId: String,
    val serverName: String,
    val host: String,
    val port: Int,
    val fingerprint: String,
    val key: String,
)

/**
 * Pairings stored in SharedPreferences, encrypted with an AES-GCM key that never
 * leaves the Android Keystore. Uninstalling the app destroys the key.
 */
class KeystorePairingRepository(context: Context) : PairingRepository {
    private val prefs = context.getSharedPreferences("pairings", Context.MODE_PRIVATE)
    private val json = Json { ignoreUnknownKeys = true }
    private val lock = Any()
    private val _pairings = MutableStateFlow(loadAll())
    val pairings: StateFlow<List<StoredPairing>> = _pairings.asStateFlow()

    override fun all(): List<StoredPairing> = _pairings.value

    override fun get(serverId: String): StoredPairing? = _pairings.value.firstOrNull { it.serverId == serverId }

    override fun save(pairing: StoredPairing) = synchronized(lock) {
        persist(_pairings.value.filter { it.serverId != pairing.serverId } + pairing)
    }

    override fun remove(serverId: String) = synchronized(lock) {
        persist(_pairings.value.filter { it.serverId != serverId })
    }

    private fun persist(list: List<StoredPairing>) {
        val plain = json.encodeToString(ListSerializer(StoredPairingJson.serializer()), list.map {
            StoredPairingJson(it.serverId, it.serverName, it.host, it.port, it.fingerprint, Base64.encodeToString(it.pairingKey, Base64.NO_WRAP))
        }).toByteArray(Charsets.UTF_8)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val blob = cipher.iv + cipher.doFinal(plain)
        plain.fill(0)
        prefs.edit().putString(PREF, Base64.encodeToString(blob, Base64.NO_WRAP)).apply()
        _pairings.value = list
    }

    private fun loadAll(): List<StoredPairing> {
        val stored = prefs.getString(PREF, null) ?: return emptyList()
        return try {
            val blob = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, blob, 0, 12))
            val plain = cipher.doFinal(blob, 12, blob.size - 12)
            json.decodeFromString(ListSerializer(StoredPairingJson.serializer()), String(plain, Charsets.UTF_8)).map {
                StoredPairing(it.serverId, it.serverName, it.host, it.port, it.fingerprint, Base64.decode(it.key, Base64.NO_WRAP))
            }
        } catch (e: Exception) {
            // Key lost (e.g. restored backup on a new device): pairings are unusable; start clean.
            prefs.edit().remove(PREF).apply()
            emptyList()
        }
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private companion object {
        const val ALIAS = "droidmic-pairings"
        const val PREF = "blob"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
