package com.myproxy.android.domain.config

import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.ServerProfile

interface ConfigRepository {
    suspend fun getConfig(): ConfigResult

    suspend fun heartbeat(): HeartbeatResult

    suspend fun lastKnownGood(): ConfigCacheEntry?

    suspend fun promoteCurrentConfig(profile: ServerProfile, configVersion: Int)

    /** Returns whether the credential that received a 401 is still current. */
    suspend fun isCurrentCredential(expectedToken: String): Boolean

    /**
     * Clears the binding only when [expectedToken] is still current. This prevents a delayed 401
     * from an old credential from deleting a credential created by a concurrent rebind.
     */
    suspend fun clearBinding(expectedToken: String? = null): Boolean
}

sealed class ConfigResult {
    data class Success(
        val entry: ConfigCacheEntry,
        val fromCache: Boolean = false,
    ) : ConfigResult()

    object NoCredential : ConfigResult()

    object NoValidConfig : ConfigResult()
}

sealed class HeartbeatResult {
    data class Success(val configVersion: Int) : HeartbeatResult()

    object NoCredential : HeartbeatResult()
}
