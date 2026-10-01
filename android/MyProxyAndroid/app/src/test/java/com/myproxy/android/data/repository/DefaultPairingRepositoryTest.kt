package com.myproxy.android.data.repository

import com.myproxy.android.data.api.ClaimApi
import com.myproxy.android.data.api.ClaimRequest
import com.myproxy.android.data.api.ClaimResponse
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.storage.ConfigCacheStore
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.model.ServerProfile
import com.myproxy.android.domain.pairing.BindResult
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class DefaultPairingRepositoryTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val validProfile = ServerProfile(
        server = "example.com",
        uuid = "123e4567-e89b-12d3-a456-426614174000",
        publicKey = "public-key",
        shortId = "abcd1234",
        sni = "example.com",
        fingerprint = "chrome",
        flow = "xtls-rprx-vision",
        spiderX = "/",
        port = 443,
    )

    @Test
    fun `invalid claim response does not persist credential or cache`() = runBlocking {
        val storage = FakeCredentialStorage()
        val cache = ConfigCacheStore(temporaryFolder.root)
        val api = FakeClaimApi {
            ClaimResponse(
                deviceId = "device-1",
                deviceToken = "",
                configVersion = 2,
                config = validProfile,
            )
        }
        val repository = DefaultPairingRepository(api, storage, cache)

        val result = repository.claim("ABCD-1234", "Android", "android", "0.1.0")

        assertEquals(ErrorCode.Unknown, (result as BindResult.Failure).code)
        assertNull(storage.read(StorageKeys.DEVICE_CREDENTIAL))
        assertNull(cache.read())
    }

    @Test
    fun `valid claim persists credential and unverified config`() = runBlocking {
        val storage = FakeCredentialStorage()
        val cache = ConfigCacheStore(temporaryFolder.root)
        val repository = DefaultPairingRepository(
            FakeClaimApi {
                ClaimResponse(
                    deviceId = "device-1",
                    deviceToken = "token-1",
                    configVersion = 2,
                    config = validProfile,
                )
            },
            storage,
            cache,
        )

        val result = repository.claim("ABCD-1234", "Android", "android", "0.1.0")

        assertEquals("device-1", (result as BindResult.Success).deviceId)
        assertEquals(2, cache.read()?.configVersion)
        assertEquals(false, cache.read()?.verified)
    }

    @Test
    fun `the stored credential carries no server of its own`() = runBlocking {
        val storage = FakeCredentialStorage()
        val repository = DefaultPairingRepository(
            FakeClaimApi { ClaimResponse("device-1", "token-1", configVersion = 2, config = validProfile) },
            storage,
            ConfigCacheStore(temporaryFolder.root),
        )

        repository.claim("ABCD-1234", "Android", "android", "0.1.0")

        // 目标是编译期常量，对每条凭据都一样，所以它不进凭据。
        // ignoreUnknownKeys 让这里读得回旧格式（那时多两个目标字段）。
        val credential = Json { ignoreUnknownKeys = true }.decodeFromString(
            DeviceCredential.serializer(),
            storage.read(StorageKeys.DEVICE_CREDENTIAL)!!,
        )
        assertEquals("device-1", credential.deviceId)
        assertEquals("token-1", credential.deviceToken)
    }

    @Test
    fun `an old credential that still carries a server is readable`() {
        // 本功能之前写下的凭据多两个字段；它们的值本来就是内置那台。
        val legacy = """
            {"deviceId":"d","deviceToken":"t","deviceName":"n","platform":"android",
             "clientVersion":"0.1.0","boundAt":"1",
             "apiBaseUrl":"https://203.0.113.10:820","apiCertSha256":"${"a".repeat(64)}"}
        """.trimIndent()

        val credential = Json { ignoreUnknownKeys = true }
            .decodeFromString(DeviceCredential.serializer(), legacy)

        assertEquals("d", credential.deviceId)
        assertEquals("t", credential.deviceToken)
    }

    private class FakeClaimApi(
        private val response: () -> ClaimResponse,
    ) : ClaimApi {
        override suspend fun claim(request: ClaimRequest): ClaimResponse = response()
    }

    private class FakeCredentialStorage : CredentialStorage {
        private val values = mutableMapOf<String, String>()

        override fun save(key: String, value: String) {
            values[key] = value
        }

        override fun read(key: String): String? = values[key]

        override fun delete(key: String) {
            values.remove(key)
        }
    }
}
