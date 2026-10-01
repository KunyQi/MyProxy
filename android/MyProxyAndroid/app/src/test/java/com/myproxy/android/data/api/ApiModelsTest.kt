package com.myproxy.android.data.api

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ApiModelsTest {

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    @Test
    fun `claim response maps config field from server`() {
        val raw = """
            {
              "deviceId": "dev_123",
              "deviceToken": "tok_abc",
              "configVersion": 7,
              "config": {
                "server": "1.2.3.4",
                "port": 443,
                "uuid": "123e4567-e89b-12d3-a456-426614174000",
                "security": "reality",
                "publicKey": "public-key",
                "shortId": "abcd1234",
                "sni": "example.com",
                "fingerprint": "chrome",
                "flow": "xtls-rprx-vision",
                "spiderX": "/"
              },
              "unknownField": "ignored"
            }
        """.trimIndent()

        val response = json.decodeFromString<ClaimResponse>(raw)

        assertEquals("dev_123", response.deviceId)
        assertEquals("tok_abc", response.deviceToken)
        assertEquals(7, response.configVersion)
        assertNotNull(response.config)
        assertEquals("1.2.3.4", response.config?.server)
        assertTrue(response.capabilities.isEmpty())
        assertTrue(response.featureFlags.isEmpty())
    }

    @Test
    fun `config sync response maps config field from server`() {
        val raw = """
            {
              "configVersion": 9,
              "config": {
                "server": "v2.example.com",
                "port": 443,
                "uuid": "123e4567-e89b-12d3-a456-426614174000",
                "security": "reality",
                "publicKey": "public-key",
                "shortId": "abcd1234",
                "sni": "example.com",
                "fingerprint": "chrome",
                "flow": "xtls-rprx-vision",
                "spiderX": "/"
              },
              "schemaVersion": 1,
              "capabilities": ["reality"],
              "featureFlags": {"beta": true}
            }
        """.trimIndent()

        val response = json.decodeFromString<ConfigSyncResponse>(raw)

        assertEquals(9, response.configVersion)
        assertEquals("v2.example.com", response.config.server)
        assertEquals(listOf("reality"), response.capabilities)
        assertEquals(mapOf("beta" to true), response.featureFlags)
    }

    @Test
    fun `update info uses camelCase json names`() {
        val raw = """
            {
              "version": "0.2.0",
              "downloadUrl": "https://example.com/app.apk",
              "sha256": "abc123",
              "mandatory": false
            }
        """.trimIndent()

        val update = json.decodeFromString<UpdateInfo>(raw)

        assertEquals("0.2.0", update.version)
        assertEquals("https://example.com/app.apk", update.downloadUrl)
        assertEquals("abc123", update.sha256)
        assertFalse(update.mandatory)
    }

    @Test
    fun `unknown fields are ignored in all contract models`() {
        val raw = """
            {
              "deviceId": "dev_x",
              "deviceToken": "tok_x",
              "configVersion": 1,
              "config": null,
              "capabilities": [],
              "featureFlags": {},
              "future": 123
            }
        """.trimIndent()

        val response = json.decodeFromString<ClaimResponse>(raw)
        assertEquals("dev_x", response.deviceId)
        assertEquals(null, response.config)
    }

    @Test
    fun `claim request matches server field contract`() {
        val encoded = Json.encodeToString(
            ClaimRequest.serializer(),
            ClaimRequest(
                pairingCode = "ABCD-EFGH",
                deviceName = "Android",
                platform = "android",
                clientVersion = "0.1.0",
                clientInstanceId = "123e4567-e89b-12d3-a456-426614174000",
            ),
        )
        val objectValue = Json.parseToJsonElement(encoded).jsonObject

        assertEquals("ABCD-EFGH", objectValue.getValue("pairingCode").jsonPrimitive.content)
        assertEquals(
            "123e4567-e89b-12d3-a456-426614174000",
            objectValue.getValue("clientInstanceId").jsonPrimitive.content,
        )
        assertFalse(objectValue.containsKey("code"))
    }
}
