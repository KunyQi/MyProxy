package com.myproxy.android.data.storage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.security.KeyStoreException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * SecureStorage abstraction backed by Android Keystore + SharedPreferences.
 *
 * The AES/GCM key is generated in AndroidKeyStore with user authentication
 * disabled, so it works transparently for normal app sessions. This is a
 * replaceable implementation; callers should depend on save/read/delete.
 */
class SecureStorage(context: Context) : CredentialStorage {

    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    override fun save(key: String, value: String) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val cipherText = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        val iv = cipher.iv
        val encoded = Base64.encodeToString(iv, Base64.NO_WRAP) +
            ":" +
            Base64.encodeToString(cipherText, Base64.NO_WRAP)
        check(prefs.edit().putString(key, encoded).commit()) {
            "Unable to persist secure storage value"
        }
    }

    /**
     * Returns null when there is no value, or when the stored bytes can never
     * be decrypted (the tag does not verify, the value is malformed). Throws
     * [CredentialUnreadableException] when the key cannot be had right now --
     * including when the keystore says it has no key for a value we stored:
     * that is exactly what keystore answers while it is not ready yet (boot,
     * always-on start), and it is not "no credential".
     */
    override fun read(key: String): String? {
        val encoded = prefs.getString(key, null) ?: return null
        val loaded = try {
            loadKey()
        } catch (e: Exception) {
            throw CredentialUnreadableException(e)
        }
        val secretKey = loaded
            ?: throw CredentialUnreadableException(KeyStoreException("no key answered for a stored value"))
        return try {
            val parts = encoded.split(":", limit = 2)
            if (parts.size != 2) return null
            val iv = Base64.decode(parts[0], Base64.NO_WRAP)
            val cipherText = Base64.decode(parts[1], Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secretKey, GCMParameterSpec(128, iv))
            String(cipher.doFinal(cipherText), Charsets.UTF_8)
        } catch (e: Exception) {
            if (isPermanentReadFailure(e)) null else throw CredentialUnreadableException(e)
        }
    }

    override fun delete(key: String) {
        check(prefs.edit().remove(key).commit()) {
            "Unable to delete secure storage value"
        }
    }

    /**
     * The existing key, or null when keystore answers that there is none.
     *
     * That answer is not proof the key is gone: `containsAlias` swallows
     * keystore errors into "false" (AOSP getKeyMetadata), and older keystores
     * answered a RemoteException the same way, so a keystore that is not up
     * yet looks exactly like a missing key. `getKey` at least throws
     * (UnrecoverableKeyException) for most failures on keystore2; for the
     * rest, the callers below decide what a null may mean.
     */
    private fun loadKey(): SecretKey? {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        return keyStore.getKey(KEY_ALIAS, null) as? SecretKey
    }

    /**
     * Generates a key only when nothing is stored yet. With values present, a
     * missing key is far more likely a keystore that is not ready than a key
     * that is gone, and generating one over the alias would make every stored
     * value undecryptable for good -- reads used to go through a get-or-create
     * and did exactly that. (A key that really is gone, e.g. after the
     * keystore was wiped, leaves the values unreadable either way; clearing
     * the app's data is the way out.)
     */
    private fun getOrCreateKey(): SecretKey {
        val existing = try {
            loadKey()
        } catch (e: Exception) {
            throw CredentialUnreadableException(e)
        }
        if (existing != null) return existing
        if (prefs.all.isNotEmpty()) {
            throw CredentialUnreadableException(KeyStoreException("no key answered while values are stored"))
        }
        return generateKey()
    }

    private fun generateKey(): SecretKey {
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setUserAuthenticationRequired(false)
                .build()
        )
        return generator.generateKey()
    }

    private companion object {
        const val PREFS_NAME = "secure_storage"
        const val KEY_ALIAS = "myproxy_device_credential_key"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
