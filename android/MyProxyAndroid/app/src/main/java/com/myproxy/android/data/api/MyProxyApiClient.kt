package com.myproxy.android.data.api

import com.myproxy.android.domain.pairing.BindingTarget
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException

/** Fixed API origin; pooled clients use the platform certificate chain and hostname checks. */
class MyProxyApiClient(
    target: BindingTarget? = null,
    private val json: Json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    },
) : ConfigApi, ClaimApi {

    private val clients: TargetClients = TargetClients(
        target ?: ApiConfig.builtInTarget
            ?: error("builtInTargetBaseUrl is not a usable base URL"),
    )

    override suspend fun claim(request: ClaimRequest): ClaimResponse =
        execute(clients.claimClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/claim")
                .post(json.encodeToString(ClaimRequest.serializer(), request).toRequestBody(JSON))
                .build()
        }) { body ->
            json.decodeFromString<ClaimResponse>(body ?: throw IOException("empty response body"))
        }

    override suspend fun getDeviceConfig(token: String): ConfigSyncResponse {
        return execute(clients.configClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/config")
                .header("Authorization", "Bearer $token")
                .get()
                .build()
        }) { body ->
            json.decodeFromString<ConfigSyncResponse>(body ?: throw IOException("empty response body"))
        }
    }

    override suspend fun heartbeat(token: String): HeartbeatResponse {
        return execute(clients.configClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/heartbeat")
                .header("Authorization", "Bearer $token")
                .post(EMPTY_JSON.toRequestBody(JSON))
                .build()
        }) { body ->
            json.decodeFromString<HeartbeatResponse>(body ?: throw IOException("empty response body"))
        }
    }

    suspend fun getAndroidLatest(): UpdateInfo {
        return execute(clients.updateClient, {
            Request.Builder()
                .url("${clients.baseUrl}/client/android/latest.json")
                .get()
                .build()
        }) { body ->
            json.decodeFromString<UpdateInfo>(body ?: throw IOException("empty response body"))
        }
    }

    /** Update Plane: the release assigned to this device, if any. */
    suspend fun getDeviceUpdate(token: String): DeviceUpdateResponse {
        return execute(clients.updateClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/update")
                .header("Authorization", "Bearer $token")
                .get()
                .build()
        }) { body ->
            json.decodeFromString<DeviceUpdateResponse>(body ?: throw IOException("empty response body"))
        }
    }

    /** Update Plane: how an install ended. Audit only; it changes no assignment. */
    suspend fun reportInstall(token: String, request: UpdateReportRequest): OkResponse {
        return execute(clients.updateClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/update/report")
                .header("Authorization", "Bearer $token")
                .post(json.encodeToString(UpdateReportRequest.serializer(), request).toRequestBody(JSON))
                .build()
        }) { body ->
            json.decodeFromString<OkResponse>(body ?: throw IOException("empty response body"))
        }
    }

    /** Observability Plane: per-category byte totals for one hour. */
    suspend fun reportUsage(token: String, request: UsageReportRequest): OkResponse {
        return execute(clients.updateClient, {
            Request.Builder()
                .url("${clients.baseUrl}/api/device/usage")
                .header("Authorization", "Bearer $token")
                .post(json.encodeToString(UsageReportRequest.serializer(), request).toRequestBody(JSON))
                .build()
        }) { body ->
            json.decodeFromString<OkResponse>(body ?: throw IOException("empty response body"))
        }
    }

    private suspend fun <T> execute(
        client: OkHttpClient,
        requestBuilder: () -> Request,
        parse: (String?) -> T,
    ): T = withContext(Dispatchers.IO) {
        if (!clients.target.isConfigured) {
            throw ApiException(ErrorCode.ServerNotConfigured, 0, "尚未配置服务器，请联系部署管理员。")
        }
        try {
            client.newCall(requestBuilder()).execute().use { response ->
                val body = response.body?.string()
                if (!response.isSuccessful) {
                    throw parseError(response.code, body)
                }
                parse(body)
            }
        } catch (e: ApiException) {
            throw e
        } catch (e: SSLException) {
            // 必须排在 IOException 前面：SSLException 是它的子类，而「对面不是
            // 可信的目标服务器」和「连不上」对用户是两件完全不同的事。见
            // tlsFailureCode。
            throw ApiException(tlsFailureCode(e), 0, e.message ?: "TLS failure")
        } catch (e: IOException) {
            throw ApiException(ErrorCode.ApiUnreachable, 0, e.message ?: "network unreachable")
        }
    }

    private fun parseError(status: Int, body: String?): ApiException {
        val detail = runCatching {
            json.decodeFromString<ApiErrorBody>(body ?: "")
        }.getOrNull()?.error
        return ApiException(
            code = normalizeApiErrorCode(detail?.code, status),
            status = status,
            message = detail?.message ?: "HTTP $status",
        )
    }

    /** 同一服务器入口复用三个请求客户端，HTTPS 采用标准证书链与主机名验证。 */
    private class TargetClients(val target: BindingTarget) {
        val baseUrl: String = target.baseUrl

        // 懒建：三个客户端按超时区分，没人调到的那个不必付出连接池与
        // dispatcher 线程池的代价。目标不变，所以它们活到进程结束。
        val claimClient: OkHttpClient by lazy { build(ApiConfig.CLAIM_TIMEOUT_SECONDS) }
        val configClient: OkHttpClient by lazy { build(ApiConfig.CONFIG_TIMEOUT_SECONDS) }
        val updateClient: OkHttpClient by lazy { build(ApiConfig.UPDATE_TIMEOUT_SECONDS) }

        private fun build(timeoutSeconds: Long): OkHttpClient {
            val builder = OkHttpClient.Builder()
                .connectTimeout(timeoutSeconds, TimeUnit.SECONDS)
                .readTimeout(timeoutSeconds, TimeUnit.SECONDS)
                .writeTimeout(timeoutSeconds, TimeUnit.SECONDS)
                .followRedirects(false)
                .followSslRedirects(false)
                .addInterceptor { chain ->
                    chain.proceed(
                        chain.request().newBuilder()
                            .header("User-Agent", ApiConfig.USER_AGENT)
                            .header("Accept", "application/json")
                            .build()
                    )
                }
            require(!ApiConfig.requireHttps || target.isHttps) { "Production API requires HTTPS" }
            // OkHttp uses the platform certificate chain, validity and hostname checks.
            return builder.build()
        }
    }

    private companion object {
        val JSON = "application/json; charset=utf-8".toMediaType()
        const val EMPTY_JSON = "{}"
    }
}
