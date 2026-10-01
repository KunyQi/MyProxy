package com.myproxy.android.data.repository

import com.myproxy.android.data.api.UpdateInfo
import com.myproxy.android.domain.update.ReleaseManifestVerifier
import com.myproxy.android.domain.update.TestSigner
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.Base64

class OfferedUpdateTest {

    private val secret = ByteArray(32) { (it + 1).toByte() }
    private val keys: Map<String, ByteArray> = mapOf("test-key" to TestSigner.publicKey(secret))

    private fun signedManifest(version: String, url: String, mandatory: Boolean): Pair<String, String> {
        val document: JsonObject = buildJsonObject {
            put("schemaVersion", 1)
            put("platform", "android")
            put("version", version)
            put("channel", "stable")
            put("mandatory", mandatory)
            put("issuedAt", "2026-09-20T00:00:00Z")
            put(
                "artifact",
                buildJsonObject {
                    put("url", url)
                    put("sha256", "a".repeat(64))
                    put("size", 4096L)
                    put(
                        "signature",
                        buildJsonObject {
                            put("type", "apksigner")
                            put("subjectSha256", "b".repeat(64))
                        },
                    )
                },
            )
        }
        val raw = Json.encodeToString(JsonObject.serializer(), document).toByteArray(Charsets.UTF_8)
        val signature = TestSigner.sign(secret, ReleaseManifestVerifier.SIGNING_DOMAIN + raw)
        val encoder = Base64.getEncoder()
        return encoder.encodeToString(raw) to encoder.encodeToString(signature)
    }

    @Test
    fun `an unsigned newer version is not offered, not even as a hint`() {
        // Anyone holding the admin token can write these four fields, and mark them mandatory.
        val info = UpdateInfo(
            version = "9.9.9",
            downloadUrl = "https://attacker.example/MyProxy.apk",
            sha256 = "",
            mandatory = true,
        )

        assertNull(offeredUpdate(info, currentVersion = "0.1.0", trustedKeys = keys))
    }

    @Test
    fun `a signed offer shows the signed version and link, not the plain copy`() {
        val (manifest, signature) = signedManifest(
            version = "1.2.3",
            url = "https://releases.example/MyProxy-1.2.3.apk",
            mandatory = false,
        )
        val info = UpdateInfo(
            version = "9.9.9",
            downloadUrl = "https://attacker.example/MyProxy.apk",
            sha256 = "",
            mandatory = true,
            releaseId = "rel_test",
            manifest = manifest,
            signature = signature,
            signingKeyId = "test-key",
        )

        val offer = offeredUpdate(info, currentVersion = "0.1.0", trustedKeys = keys)

        assertNotNull(offer)
        assertEquals("1.2.3", offer!!.version)
        assertEquals("https://releases.example/MyProxy-1.2.3.apk", offer.downloadUrl)
        assertEquals("a".repeat(64), offer.sha256)
        assertFalse(offer.mandatory)
    }

    @Test
    fun `a manifest nobody trusts offers nothing`() {
        val (manifest, signature) = signedManifest(
            version = "1.2.3",
            url = "https://releases.example/MyProxy-1.2.3.apk",
            mandatory = false,
        )
        val info = UpdateInfo(
            version = "1.2.3",
            downloadUrl = "https://releases.example/MyProxy-1.2.3.apk",
            sha256 = "",
            mandatory = false,
            releaseId = "rel_test",
            manifest = manifest,
            signature = signature,
            signingKeyId = "test-key",
        )

        assertNull(offeredUpdate(info, currentVersion = "0.1.0", trustedKeys = emptyMap()))
    }
}
