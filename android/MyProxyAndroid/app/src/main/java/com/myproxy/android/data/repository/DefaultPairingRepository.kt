package com.myproxy.android.data.repository

import com.myproxy.android.data.api.ApiException
import com.myproxy.android.data.api.ClaimApi
import com.myproxy.android.data.api.ClaimRequest
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.storage.ConfigCacheStore
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.pairing.BindResult
import com.myproxy.android.domain.pairing.PairingRepository
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.Json
import java.util.UUID

class DefaultPairingRepository(
    private val apiClient: ClaimApi,
    private val secureStorage: CredentialStorage,
    private val configCacheStore: ConfigCacheStore,
) : PairingRepository {

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    override suspend fun claim(
        code: String,
        deviceName: String,
        platform: String,
        clientVersion: String,
    ): BindResult {
        return try {
            val response = apiClient.claim(
                ClaimRequest(
                    pairingCode = code,
                    deviceName = deviceName,
                    platform = platform,
                    clientVersion = clientVersion,
                    clientInstanceId = getOrCreateClientInstanceId(),
                )
            )
            if (response.deviceId.isBlank() ||
                response.deviceToken.isBlank() ||
                response.configVersion <= 0 ||
                response.config?.isValid() != true
            ) {
                return BindResult.Failure(
                    ErrorCode.Unknown,
                    "invalid claim response",
                )
            }
            val credential = DeviceCredential(
                deviceId = response.deviceId,
                deviceToken = response.deviceToken,
                deviceName = deviceName,
                platform = platform,
                clientVersion = clientVersion,
                boundAt = System.currentTimeMillis().toString(),
            )
            secureStorage.save(
                StorageKeys.DEVICE_CREDENTIAL,
                json.encodeToString(DeviceCredential.serializer(), credential),
            )
            configCacheStore.write(
                ConfigCacheEntry(
                    configVersion = response.configVersion,
                    fetchedAt = System.currentTimeMillis().toString(),
                    verified = false,
                    profile = response.config,
                    capabilities = response.capabilities,
                    featureFlags = response.featureFlags,
                )
            )
            BindResult.Success(
                deviceId = response.deviceId,
                configVersion = response.configVersion,
            )
        } catch (e: ApiException) {
            BindResult.Failure(e.code, e.message ?: "pairing failed")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            BindResult.Failure(ErrorCode.Unknown, e.message ?: "pairing failed")
        }
    }

    private fun getOrCreateClientInstanceId(): String {
        secureStorage.read(StorageKeys.CLIENT_INSTANCE_ID)
            ?.trim()
            ?.takeIf { it.matches(CLIENT_INSTANCE_ID_PATTERN) }
            ?.let { return it }

        val generated = UUID.randomUUID().toString()
        // Persist before sending claim so retries remain idempotent even if the response is lost.
        secureStorage.save(StorageKeys.CLIENT_INSTANCE_ID, generated)
        return generated
    }

    private companion object {
        val CLIENT_INSTANCE_ID_PATTERN = Regex("^[A-Za-z0-9._~-]{16,128}$")
    }
}
