package com.myproxy.android.xray

import java.net.InetSocketAddress
import java.net.Proxy
import java.util.concurrent.TimeUnit
import com.myproxy.android.data.api.ApiConfig
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Credentials
import okhttp3.OkHttpClient
import okhttp3.Request

/**
 * Verifies that traffic actually reaches the internet through the tunnel.
 *
 * Deliberately identical in strictness to the TUI's NetworkService: the reply
 * must be exactly 204 with an empty body, and redirects are not followed. Any
 * 2xx would do is not good enough, because the result of this check is what
 * promotes a config to verified last-known-good. A captive portal or a
 * hijacking middlebox answering 200 would otherwise be enough to write a
 * broken config into the cache as verified.
 */
class ConnectivityProbe(
    private val endpoint: LocalProbeEndpoint = LocalProbeEndpoint,
    private val urls: List<String> = ApiConfig.connectivityCheckUrls,
    private val clientOverride: OkHttpClient? = null,
) {
    init { require(urls.size in 1..4) { "One to four probe URLs are required" } }
    @Volatile private var preferredUrl: String? = null
    private val client: OkHttpClient by lazy {
        clientOverride ?: OkHttpClient.Builder()
            .proxy(
                Proxy(
                    Proxy.Type.HTTP,
                    InetSocketAddress.createUnresolved(LocalProbeEndpoint.HOST, endpoint.port),
                ),
            )
            .proxyAuthenticator { _, response ->
                // Give up rather than loop if the inbound rejects the credentials.
                if (response.request.header(PROXY_AUTHORIZATION) != null) return@proxyAuthenticator null
                response.request.newBuilder()
                    .header(
                        PROXY_AUTHORIZATION,
                        Credentials.basic(endpoint.username, endpoint.password),
                    )
                    .build()
            }
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .readTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .callTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .build()
    }

    /** Round-trip latency in milliseconds. Throws when the tunnel does not carry traffic. */
    suspend fun measure(budgetMs: Long = TOTAL_TIMEOUT_MS): Int = withContext(Dispatchers.IO) { measureCandidates(budgetMs) }

    private suspend fun measureCandidates(budgetMs: Long): Int = withTimeout(budgetMs) {
        var lastFailure: Exception? = null
        val preferred = preferredUrl
        val candidates = listOfNotNull(preferred) + urls.filter { it != preferred }
        val attemptBudgetMs = minOf(TIMEOUT_SECONDS * 1_000, maxOf(1, budgetMs / candidates.size))
        for (url in candidates) {
            try {
                val latency = measureOne(url, attemptBudgetMs)
                preferredUrl = url
                return@withTimeout latency
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                lastFailure = failure
            }
        }
        throw IllegalStateException("connectivity probe failed for every configured endpoint", lastFailure)
    }

    private suspend fun measureOne(url: String, attemptBudgetMs: Long): Int {
        val request = Request.Builder().url(url).get().build()
        val startedAt = System.nanoTime()
        try {
            val call = client.newCall(request)
            call.timeout().timeout(attemptBudgetMs, TimeUnit.MILLISECONDS)
            return call.awaitResponse().use { response ->
                val elapsedMs = ((System.nanoTime() - startedAt) / 1_000_000L).toInt()
                if (response.code != EXPECTED_STATUS) {
                    error("connectivity probe to $url failed: expected $EXPECTED_STATUS, got ${response.code}")
                }
                if (!response.hasEmptyBody()) {
                    error("connectivity probe to $url failed: received a body where $EXPECTED_STATUS forbids one")
                }
                elapsedMs
            }
        } catch (e: Exception) {
            if (e is CancellationException) throw e
            if (e is IllegalStateException && e.message?.contains("connectivity probe") == true) throw e
            throw IllegalStateException("connectivity probe to $url failed: ${e.javaClass.simpleName} ${e.message}", e)
        }
    }

    private suspend fun Call.awaitResponse(): okhttp3.Response = suspendCancellableCoroutine { continuation ->
        continuation.invokeOnCancellation { cancel() }
        enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (!continuation.isCancelled) continuation.resumeWithException(e)
            }
            override fun onResponse(call: Call, response: okhttp3.Response) {
                continuation.resume(response) { _, resource, _ -> resource.close() }
            }
        })
    }

    private fun okhttp3.Response.hasEmptyBody(): Boolean {
        val body = body ?: return true
        if (body.contentLength() > 0L) return false
        // contentLength may be unknown; one byte settles it.
        return body.byteStream().read() == -1
    }

    private companion object {
        const val PROXY_AUTHORIZATION = "Proxy-Authorization"
        const val EXPECTED_STATUS = 204

        /** Matches the TUI's probe timeout. */
        const val TIMEOUT_SECONDS = 5L
        const val TOTAL_TIMEOUT_MS = 20_000L
    }
}
