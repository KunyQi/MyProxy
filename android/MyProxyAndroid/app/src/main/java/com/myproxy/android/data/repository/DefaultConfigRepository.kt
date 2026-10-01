package com.myproxy.android.data.repository

import com.myproxy.android.data.api.ApiException
import com.myproxy.android.data.api.ConfigSyncResponse
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.api.ConfigApi
import com.myproxy.android.data.storage.ConfigCacheStore
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.config.ConfigRepository
import com.myproxy.android.domain.config.ConfigResult
import com.myproxy.android.domain.config.HeartbeatResult
import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.model.ServerProfile
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.Json

class DefaultConfigRepository(
    private val apiClient: ConfigApi,
    private val secureStorage: CredentialStorage,
    private val configCacheStore: ConfigCacheStore,
) : ConfigRepository {

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    override suspend fun getConfig(): ConfigResult {
        val credential = readCredential() ?: return ConfigResult.NoCredential
        val cache = configCacheStore.read()

        val online = try {
            val response = apiClient.getDeviceConfig(credential.deviceToken)
            buildOnlineEntry(response)
        } catch (e: ApiException) {
            if (e.code == ErrorCode.ServerNotConfigured) throw e
            if (e.code == ErrorCode.TokenInvalid || e.code == ErrorCode.DeviceNotFound || e.status == 401) {
                throw tokenRejected(e, credential.deviceToken)
            }
            return fallback(cache)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return fallback(cache)
        } ?: return fallback(cache)

        val usableCache = cache?.takeIf { isUsableCacheEntry(it) }
        val cacheVersion = usableCache?.configVersion
        val onlineVersion = online.configVersion

        // A delayed/rolled-back server response must never replace a newer
        // local snapshot. Only a verified LKG may satisfy that downgrade;
        // an unverified cache is intentionally unusable on its own.
        if (usableCache != null &&
            cacheVersion != null &&
            onlineVersion != null &&
            onlineVersion < cacheVersion
        ) {
            return if (usableCache.verified) {
                ConfigResult.Success(usableCache, fromCache = true)
            } else {
                ConfigResult.NoValidConfig
            }
        }

        if (usableCache?.verified == true &&
            cacheVersion != null &&
            onlineVersion != null &&
            onlineVersion == cacheVersion
        ) {
            return ConfigResult.Success(usableCache, fromCache = true)
        }

        return ConfigResult.Success(online, fromCache = false)
    }

    override suspend fun heartbeat(): HeartbeatResult {
        val credential = readCredential() ?: return HeartbeatResult.NoCredential
        return try {
            val response = apiClient.heartbeat(credential.deviceToken)
            HeartbeatResult.Success(response.configVersion)
        } catch (e: ApiException) {
            if (e.code == ErrorCode.TokenInvalid || e.code == ErrorCode.DeviceNotFound || e.status == 401) {
                throw tokenRejected(e, credential.deviceToken)
            }
            throw e
        }
    }

    override suspend fun lastKnownGood(): ConfigCacheEntry? =
        configCacheStore.read()?.takeIf { it.verified && isUsableCacheEntry(it) }

    override suspend fun promoteCurrentConfig(profile: ServerProfile, configVersion: Int) {
        require(configVersion > 0) { "config version must be positive" }
        require(profile.isValid()) { "cannot promote an invalid server profile" }
        val now = System.currentTimeMillis().toString()
        val current = configCacheStore.read()
        val updated = current?.copy(
            configVersion = configVersion,
            fetchedAt = now,
            verified = true,
            profile = profile,
        ) ?: ConfigCacheEntry(
            configVersion = configVersion,
            fetchedAt = now,
            verified = true,
            profile = profile,
        )
        configCacheStore.write(updated)
    }

    override suspend fun isCurrentCredential(expectedToken: String): Boolean =
        readCredential()?.deviceToken == expectedToken

    override suspend fun clearBinding(expectedToken: String?): Boolean {
        if (expectedToken != null && readCredential()?.deviceToken != expectedToken) {
            return false
        }
        secureStorage.delete(StorageKeys.DEVICE_CREDENTIAL)
        configCacheStore.clear()
        return true
    }

    private fun buildOnlineEntry(response: ConfigSyncResponse): ConfigCacheEntry? {
        val profile = response.config
        if (response.configVersion <= 0 || !profile.isValid()) return null
        return ConfigCacheEntry(
            schemaVersion = response.schemaVersion,
            configVersion = response.configVersion,
            fetchedAt = System.currentTimeMillis().toString(),
            verified = false,
            profile = profile,
            capabilities = response.capabilities,
            featureFlags = response.featureFlags,
        )
    }

    private fun fallback(cache: ConfigCacheEntry?): ConfigResult =
        cache
            ?.takeIf { it.verified && isUsableCacheEntry(it) }
            ?.let { ConfigResult.Success(it, fromCache = true) }
            ?: ConfigResult.NoValidConfig

    private fun isUsableCacheEntry(entry: ConfigCacheEntry): Boolean {
        val version = entry.configVersion ?: return false
        return version > 0 && entry.profile?.isValid() == true
    }

    private fun tokenRejected(cause: ApiException, token: String): ApiException = ApiException(
        code = ErrorCode.TokenInvalid,
        status = cause.status,
        message = cause.message ?: "invalid token",
        rejectedCredentialToken = token,
    )

    /**
     * Null means there is no usable credential. A keystore that cannot answer
     * right now throws [com.myproxy.android.data.storage.CredentialUnreadableException]
     * instead, and that must propagate: mapping it to NoCredential is what
     * used to tear down a working tunnel and send the user to pair again.
     */
    private fun readCredential(): DeviceCredential? {
        val raw = secureStorage.read(StorageKeys.DEVICE_CREDENTIAL) ?: return null
        return runCatching {
            json.decodeFromString<DeviceCredential>(raw)
        }.getOrNull()
    }
}
