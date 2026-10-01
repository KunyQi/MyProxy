package com.myproxy.android.data.repository

import com.myproxy.android.data.api.ApiException
import com.myproxy.android.data.api.ConfigApi
import com.myproxy.android.data.api.ConfigSyncResponse
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.api.HeartbeatResponse
import com.myproxy.android.data.storage.ConfigCacheStore
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.CredentialUnreadableException
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.config.ConfigResult
import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.model.ServerProfile
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class DefaultConfigRepositoryTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val json = Json { ignoreUnknownKeys = true }

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
        security = "reality",
    )

    private val credential = DeviceCredential(
        deviceId = "dev_1",
        deviceToken = "tok_1",
        deviceName = "Android",
        platform = "android",
        clientVersion = "0.1.0",
        boundAt = "now",
    )

    private class FakeCredentialStorage(
        private var raw: String?,
    ) : CredentialStorage {
        override fun save(key: String, value: String) {
            raw = value
        }

        override fun read(key: String): String? = raw

        override fun delete(key: String) {
            raw = null
        }
    }

    private class FakeConfigApi(
        private val heartbeatBlock: suspend () -> HeartbeatResponse = {
            HeartbeatResponse(ok = true, configVersion = 1)
        },
        private val block: suspend () -> ConfigSyncResponse,
    ) : ConfigApi {
        override suspend fun getDeviceConfig(token: String): ConfigSyncResponse = block()

        override suspend fun heartbeat(token: String): HeartbeatResponse = heartbeatBlock()
    }

    private fun repository(
        api: ConfigApi,
        storage: CredentialStorage,
        cacheStore: ConfigCacheStore,
    ) = DefaultConfigRepository(api, storage, cacheStore)

    private fun credentialJson(): String =
        json.encodeToString(DeviceCredential.serializer(), credential)

    @Test
    fun `unconfigured server cannot fall back to a previous verified profile`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(ConfigCacheEntry(configVersion = 7, fetchedAt = "old", verified = true, profile = validProfile))
        val api = FakeConfigApi { throw ApiException(ErrorCode.ServerNotConfigured, 0, "尚未配置服务器") }
        val repository = repository(api, FakeCredentialStorage(credentialJson()), store)
        try {
            repository.getConfig()
            org.junit.Assert.fail("The example deployment must never use an old data-plane profile")
        } catch (exception: ApiException) {
            assertEquals(ErrorCode.ServerNotConfigured, exception.code)
        }
    }

    @Test
    fun `a keystore that cannot answer right now is not reported as no credential`() {
        // NoCredential tears down the tunnel and moves to UNBOUND; a transient
        // keystore failure must reach the caller as an error instead.
        val store = ConfigCacheStore(temporaryFolder.root)
        val storage = object : CredentialStorage {
            override fun save(key: String, value: String) = Unit

            override fun read(key: String): String? =
                throw CredentialUnreadableException(java.security.KeyStoreException("keystore busy"))

            override fun delete(key: String) = Unit
        }
        val api = FakeConfigApi { error("no request may be made without a credential") }
        val repository = repository(api, storage, store)

        assertThrows(CredentialUnreadableException::class.java) { runBlocking { repository.getConfig() } }
        assertThrows(CredentialUnreadableException::class.java) { runBlocking { repository.heartbeat() } }
    }

    @Test
    fun `online new version is returned in memory and does not overwrite cache before connect success`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val oldEntry = ConfigCacheEntry(
            configVersion = 1,
            fetchedAt = "old",
            verified = true,
            profile = validProfile,
        )
        store.write(oldEntry)
        val storage = FakeCredentialStorage(credentialJson())
        val api = FakeConfigApi {
            ConfigSyncResponse(
                configVersion = 2,
                config = validProfile.copy(server = "new.example.com"),
            )
        }

        val result = repository(api, storage, store).getConfig()

        assertTrue(result is ConfigResult.Success)
        val success = result as ConfigResult.Success
        assertFalse(success.fromCache)
        assertEquals(2, success.entry.configVersion)
        assertEquals("new.example.com", success.entry.profile?.server)

        val cached = store.read()
        assertEquals(1, cached?.configVersion)
        assertEquals("example.com", cached?.profile?.server)
    }

    @Test
    fun `online downgrade keeps newer verified cache`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val newer = validProfile.copy(server = "newer.example.com")
        store.write(
            ConfigCacheEntry(
                configVersion = 5,
                fetchedAt = "newer",
                verified = true,
                profile = newer,
            )
        )
        val repository = repository(
            FakeConfigApi {
                ConfigSyncResponse(configVersion = 4, config = validProfile)
            },
            FakeCredentialStorage(credentialJson()),
            store,
        )

        val result = repository.getConfig()

        val success = result as ConfigResult.Success
        assertTrue(success.fromCache)
        assertEquals(5, success.entry.configVersion)
        assertEquals("newer.example.com", success.entry.profile?.server)
    }

    @Test
    fun `online downgrade with unverified cache has no valid config`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(
            ConfigCacheEntry(
                configVersion = 5,
                fetchedAt = "newer",
                verified = false,
                profile = validProfile,
            )
        )
        val repository = repository(
            FakeConfigApi {
                ConfigSyncResponse(configVersion = 4, config = validProfile)
            },
            FakeCredentialStorage(credentialJson()),
            store,
        )

        assertEquals(ConfigResult.NoValidConfig, repository.getConfig())
    }

    @Test
    fun `same online version keeps verified cache`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(
            ConfigCacheEntry(
                configVersion = 4,
                fetchedAt = "cached",
                verified = true,
                profile = validProfile.copy(server = "cached.example.com"),
            )
        )
        val repository = repository(
            FakeConfigApi {
                ConfigSyncResponse(
                    configVersion = 4,
                    config = validProfile.copy(server = "online.example.com"),
                )
            },
            FakeCredentialStorage(credentialJson()),
            store,
        )

        val result = repository.getConfig() as ConfigResult.Success

        assertTrue(result.fromCache)
        assertEquals("cached.example.com", result.entry.profile?.server)
    }

    @Test
    fun `cache old version is kept and returned when online is unavailable`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val oldEntry = ConfigCacheEntry(
            configVersion = 3,
            fetchedAt = "old",
            verified = true,
            profile = validProfile,
        )
        store.write(oldEntry)
        val storage = FakeCredentialStorage(credentialJson())
        val api = FakeConfigApi {
            throw ApiException(ErrorCode.ApiUnreachable, 0, "offline")
        }

        val result = repository(api, storage, store).getConfig()

        assertTrue(result is ConfigResult.Success)
        val success = result as ConfigResult.Success
        assertTrue(success.fromCache)
        assertEquals(3, success.entry.configVersion)
        assertEquals(oldEntry, store.read())
    }

    @Test
    fun `token invalid is propagated as ApiException`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val storage = FakeCredentialStorage(credentialJson())
        val api = FakeConfigApi {
            throw ApiException(ErrorCode.TokenInvalid, 401, "invalid token")
        }

        val exception = try {
            repository(api, storage, store).getConfig()
            null
        } catch (e: ApiException) {
            e
        }

        assertNotNull(exception)
        assertEquals(ErrorCode.TokenInvalid, exception?.code)
        assertEquals(credential.deviceToken, exception?.rejectedCredentialToken)
        assertEquals(null, store.read())
    }

    @Test
    fun `unverified cache is never used as offline fallback`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(
            ConfigCacheEntry(
                configVersion = 1,
                verified = false,
                profile = validProfile,
            )
        )
        val storage = FakeCredentialStorage(credentialJson())
        val api = FakeConfigApi {
            throw ApiException(ErrorCode.ApiUnreachable, 0, "offline")
        }

        assertEquals(ConfigResult.NoValidConfig, repository(api, storage, store).getConfig())
    }

    @Test
    fun `missing or unreadable credential is distinguished from missing config`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val storage = FakeCredentialStorage(null)
        val api = FakeConfigApi { error("config endpoint not called") }

        assertEquals(ConfigResult.NoCredential, repository(api, storage, store).getConfig())
    }

    @Test
    fun `delayed token rejection cannot clear a replacement credential`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val replacement = credential.copy(deviceToken = "tok_replacement")
        val storage = FakeCredentialStorage(
            json.encodeToString(DeviceCredential.serializer(), replacement),
        )
        val repository = repository(
            FakeConfigApi { error("not called") },
            storage,
            store,
        )

        assertFalse(repository.clearBinding(expectedToken = credential.deviceToken))
        assertNotNull(storage.read(StorageKeys.DEVICE_CREDENTIAL))
        assertTrue(repository.clearBinding(expectedToken = replacement.deviceToken))
        assertEquals(null, storage.read(StorageKeys.DEVICE_CREDENTIAL))
    }

    @Test
    fun `heartbeat 401 carries request token for compare and clear`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val storage = FakeCredentialStorage(credentialJson())
        val repository = repository(
            FakeConfigApi(
                heartbeatBlock = {
                    throw ApiException(ErrorCode.TokenInvalid, 401, "invalid token")
                },
            ) { error("config endpoint not called") },
            storage,
            store,
        )

        val rejection = try {
            repository.heartbeat()
            null
        } catch (e: ApiException) {
            e
        }
        assertEquals(credential.deviceToken, rejection?.rejectedCredentialToken)

        val replacement = credential.copy(deviceToken = "tok_replacement")
        storage.save(
            StorageKeys.DEVICE_CREDENTIAL,
            json.encodeToString(DeviceCredential.serializer(), replacement),
        )
        assertFalse(repository.clearBinding(rejection?.rejectedCredentialToken))
        assertNotNull(storage.read(StorageKeys.DEVICE_CREDENTIAL))
    }

    @Test
    fun `current credential precheck rejects stale token without changing storage`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val storage = FakeCredentialStorage(credentialJson())
        val repository = repository(
            FakeConfigApi { error("config endpoint not called") },
            storage,
            store,
        )

        assertTrue(repository.isCurrentCredential(credential.deviceToken))
        assertFalse(repository.isCurrentCredential("old-token"))
        assertNotNull(storage.read(StorageKeys.DEVICE_CREDENTIAL))
    }

    @Test
    fun `zero version verified cache is not a last known good config`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(
            ConfigCacheEntry(
                configVersion = 0,
                fetchedAt = "invalid",
                verified = true,
                profile = validProfile,
            )
        )
        val repository = repository(
            FakeConfigApi { error("config endpoint not called") },
            FakeCredentialStorage(credentialJson()),
            store,
        )

        assertEquals(null, repository.lastKnownGood())
    }

    @Test
    fun `promote rejects invalid version and profile`() = runBlocking {
        val store = ConfigCacheStore(temporaryFolder.root)
        val repository = repository(
            FakeConfigApi { error("config endpoint not called") },
            FakeCredentialStorage(credentialJson()),
            store,
        )

        assertThrows(IllegalArgumentException::class.java) {
            runBlocking { repository.promoteCurrentConfig(validProfile, 0) }
        }
        assertThrows(IllegalArgumentException::class.java) {
            runBlocking { repository.promoteCurrentConfig(validProfile.copy(port = 0), 1) }
        }
        assertEquals(null, store.read())
    }
}
