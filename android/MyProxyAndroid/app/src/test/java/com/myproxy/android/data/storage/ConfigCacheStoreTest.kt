package com.myproxy.android.data.storage

import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.ServerProfile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class ConfigCacheStoreTest {

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
        spiderX = "",
        port = 443,
        security = "reality",
    )

    @Test
    fun `write and read round trip`() {
        val store = ConfigCacheStore(temporaryFolder.root)
        val entry = ConfigCacheEntry(
            schemaVersion = 1,
            configVersion = 7,
            fetchedAt = "123",
            verified = true,
            profile = validProfile,
            capabilities = listOf("reality"),
            featureFlags = mapOf("beta" to true),
        )

        store.write(entry)

        assertEquals(entry, store.read())
    }

    @Test
    fun `corrupted file returns null`() {
        val store = ConfigCacheStore(temporaryFolder.root)
        File(temporaryFolder.root, "config-cache.json").writeText("{ not json")

        assertNull(store.read())
    }

    @Test
    fun `missing file returns null`() {
        val store = ConfigCacheStore(temporaryFolder.root)
        assertNull(store.read())
    }

    @Test
    fun `clear removes cached file`() {
        val store = ConfigCacheStore(temporaryFolder.root)
        store.write(ConfigCacheEntry(configVersion = 1))
        store.clear()

        assertNull(store.read())
    }

    @Test
    fun `backup is recovered after interrupted atomic replacement`() {
        val store = ConfigCacheStore(temporaryFolder.root)
        val entry = ConfigCacheEntry(
            configVersion = 9,
            verified = true,
            profile = validProfile,
        )
        store.write(entry)
        val target = File(temporaryFolder.root, "config-cache.json")
        val backup = File(temporaryFolder.root, "config-cache.json.bak")
        target.renameTo(backup)
        File(temporaryFolder.root, "config-cache.json.tmp").writeText("partial")

        assertEquals(entry, store.read())
        assertEquals(entry, store.read())
    }
}
