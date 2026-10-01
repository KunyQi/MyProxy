package com.myproxy.android.data.repository

import com.myproxy.android.data.api.MyProxyApiClient
import com.myproxy.android.data.api.UsageReportRequest
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.CredentialUnreadableException
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.observability.UsageAccumulator
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json

/**
 * Observability Plane, client side: per-service-category byte totals.
 *
 * **All it can say is "video moved 5 MB this hour".** No hostname, no URL, no
 * per-connection record — attribution comes entirely from the core's
 * per-outbound-tag byte counters, so the client never has to know where
 * anyone went.
 *
 * It also **cannot move the "how much" number**: the server's totals come
 * from its own sweep of the data plane. What is sent here only affects the
 * category dimension, which is why a compromised device cannot report its
 * usage as zero.
 */
class UsageReporter(
    private val apiClient: MyProxyApiClient,
    private val secureStorage: CredentialStorage,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val accumulator = UsageAccumulator()
    private val gate = Mutex()
    private val json = Json { ignoreUnknownKeys = true }

    /** Fold one per-tag delta reading in, reporting the previous hour if it turned. */
    suspend fun observe(deltasByTag: Map<String, Long>) {
        val now = clock()
        val flushed: Pair<String, Map<String, Long>>? = gate.withLock {
            accumulator.observe(deltasByTag, now)
            if (accumulator.shouldFlush(now)) accumulator.flush(now) else null
        }

        val (bucket, categories) = flushed ?: return
        if (bucket.isEmpty() || categories.isEmpty()) return
        post(bucket, categories)
    }

    private suspend fun post(bucket: String, categories: Map<String, Long>) {
        val token = readToken() ?: return
        try {
            apiClient.reportUsage(token, UsageReportRequest(bucket, categories))
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Observability is best effort. A bucket that cannot be sent is
            // dropped rather than retried: the server only accepts the
            // current hour and the one before it, so a retry an hour later
            // would be refused anyway. Never let this affect the tunnel.
        }
    }

    private fun readToken(): String? {
        // Keystore unavailable right now: skip this report rather than fail.
        val raw = try {
            secureStorage.read(StorageKeys.DEVICE_CREDENTIAL)
        } catch (_: CredentialUnreadableException) {
            null
        } ?: return null
        return runCatching {
            json.decodeFromString<DeviceCredential>(raw).deviceToken
        }.getOrNull()?.takeIf { it.isNotEmpty() }
    }
}
