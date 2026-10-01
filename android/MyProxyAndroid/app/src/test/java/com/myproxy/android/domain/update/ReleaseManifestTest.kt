package com.myproxy.android.domain.update

import com.myproxy.android.data.api.ReleaseSigningKeys
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseManifestTest {

    private val secret = ByteArray(32) { (it + 1).toByte() }
    private val otherSecret = ByteArray(32) { (it + 64).toByte() }
    private val keyId = "test-key"
    private val keys: Map<String, ByteArray> = mapOf(keyId to TestSigner.publicKey(secret))

    private fun manifestJson(
        version: String = "1.2.3",
        platform: String = "android",
        channel: String = "stable",
        mandatory: Boolean = false,
        url: String = "",
        sha256: String = "a".repeat(64),
        size: Long = 4096L,
        signatureType: String = "apksigner",
        subject: String = "b".repeat(64),
        minimumVersion: String = "",
        schemaVersion: Int = 1,
    ): String {
        val artifactUrl = url.ifEmpty { "https://releases.example/MyProxy-$version.apk" }
        val document: JsonObject = buildJsonObject {
            put("schemaVersion", schemaVersion)
            put("platform", platform)
            put("version", version)
            put("channel", channel)
            put("mandatory", mandatory)
            put("issuedAt", "2026-09-20T00:00:00Z")
            if (minimumVersion.isNotEmpty()) put("minimumVersion", minimumVersion)
            put(
                "artifact",
                buildJsonObject {
                    put("url", artifactUrl)
                    put("sha256", sha256)
                    put("size", size)
                    put(
                        "signature",
                        buildJsonObject {
                            put("type", signatureType)
                            put("subjectSha256", subject)
                        },
                    )
                },
            )
        }
        return Json.encodeToString(JsonObject.serializer(), document)
    }

    private fun sign(json: String, withSecret: ByteArray = secret): Pair<String, String> {
        val raw = json.toByteArray(Charsets.UTF_8)
        val signed = ReleaseManifestVerifier.SIGNING_DOMAIN + raw
        return base64(raw) to base64(TestSigner.sign(withSecret, signed))
    }

    @Test
    fun `well formed manifest verifies`() {
        val (manifest, signature) = sign(manifestJson())
        val result = ReleaseManifestVerifier.verify(manifest, signature, keyId, keys)

        assertTrue(result.rejection.name, result.ok)
        assertEquals("1.2.3", result.manifest!!.version)
        assertEquals(4096L, result.manifest!!.artifactSize)
    }

    @Test
    fun `no trusted keys fails closed`() {
        val (manifest, signature) = sign(manifestJson())
        assertEquals(
            ManifestRejection.UNKNOWN_SIGNING_KEY,
            ReleaseManifestVerifier.verify(manifest, signature, keyId, emptyMap()).rejection,
        )
    }

    @Test
    fun `signature from an untrusted key is rejected`() {
        val (manifest, signature) = sign(manifestJson(), otherSecret)
        assertEquals(
            ManifestRejection.BAD_SIGNATURE,
            ReleaseManifestVerifier.verify(manifest, signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `reserialised manifest does not verify`() {
        // Pins the "raw bytes" decision: the same document written another
        // way must fail, which is precisely why no hop may re-encode it.
        val json = manifestJson()
        val (_, signature) = sign(json)
        val reserialised = Json { prettyPrint = true }
            .encodeToString(JsonObject.serializer(), Json.parseToJsonElement(json) as JsonObject)
        assertNotEquals(json, reserialised)

        assertEquals(
            ManifestRejection.BAD_SIGNATURE,
            ReleaseManifestVerifier.verify(
                base64(reserialised.toByteArray(Charsets.UTF_8)),
                signature,
                keyId,
                keys,
            ).rejection,
        )
    }

    @Test
    fun `tampered manifest is rejected`() {
        val json = manifestJson()
        val (_, signature) = sign(json)
        val raw = json.toByteArray(Charsets.UTF_8)
        raw[raw.size - 2] = (raw[raw.size - 2].toInt() xor 1).toByte()

        assertEquals(
            ManifestRejection.BAD_SIGNATURE,
            ReleaseManifestVerifier.verify(base64(raw), signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `structural violations are rejected`() {
        val cases = mapOf(
            "http url" to manifestJson(url = "http://releases.example/a.apk"),
            "url with credentials" to manifestJson(url = "https://u:p@releases.example/a.apk"),
            "bad sha256" to manifestJson(sha256 = "z".repeat(64)),
            "uppercase sha256" to manifestJson(sha256 = "A".repeat(64)),
            "zero size" to manifestJson(size = 0L),
            "wrong signature type" to manifestJson(signatureType = "authenticode"),
            "bad subject" to manifestJson(subject = "nothex"),
            "bad version" to manifestJson(version = "1.2"),
            "bad channel" to manifestJson(channel = "nightly"),
            "bad minimum" to manifestJson(minimumVersion = "one"),
        )

        for ((name, json) in cases) {
            val (manifest, signature) = sign(json)
            val rejection = ReleaseManifestVerifier.verify(manifest, signature, keyId, keys).rejection
            assertNotEquals(name, ManifestRejection.NONE, rejection)
        }
    }

    @Test
    fun `a windows manifest is refused on android`() {
        // The server already resolved by platform, but installing a package
        // meant for another platform is the kind of mistake nobody notices.
        val (manifest, signature) = sign(manifestJson(platform = "windows"))
        assertEquals(
            ManifestRejection.WRONG_PLATFORM,
            ReleaseManifestVerifier.verify(manifest, signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `unsupported schema version is rejected`() {
        val (manifest, signature) = sign(manifestJson(schemaVersion = 2))
        assertEquals(
            ManifestRejection.UNSUPPORTED_SCHEMA,
            ReleaseManifestVerifier.verify(manifest, signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `non json payload is rejected after the signature check`() {
        val (manifest, signature) = sign("not json at all")
        assertEquals(
            ManifestRejection.MALFORMED_DOCUMENT,
            ReleaseManifestVerifier.verify(manifest, signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `signing domain matches the server`() {
        // This byte string is the one agreement across all three platforms;
        // change a character and every signed release stops verifying.
        assertEquals(
            "myproxy-release-manifest-v1\u0000",
            String(ReleaseManifestVerifier.SIGNING_DOMAIN, Charsets.US_ASCII),
        )
    }

    @Test
    fun `base64 must be strict`() {
        val (manifest, signature) = sign(manifestJson())
        val withWhitespace = manifest.substring(0, 4) + "\n" + manifest.substring(4)
        assertEquals(
            ManifestRejection.MALFORMED_ENVELOPE,
            ReleaseManifestVerifier.verify(withWhitespace, signature, keyId, keys).rejection,
        )
    }

    @Test
    fun `signing key spec parsing rejects malformed entries`() {
        val hex = TestSigner.publicKey(secret).joinToString("") { "%02x".format(it) }
        assertEquals(1, ReleaseSigningKeys.parse("k1:$hex").size)
        assertEquals(0, ReleaseSigningKeys.parse("").size)

        for (bad in listOf("k1", "k1:", ":$hex", "k1:zz", "k1:$hex,k1:$hex")) {
            var threw = false
            try {
                ReleaseSigningKeys.parse(bad)
            } catch (_: IllegalArgumentException) {
                threw = true
            }
            assertTrue(bad, threw)
        }
    }

    @Test
    fun `shipped key set is empty until real material exists`() {
        // assertion gets changed by the commit that adds a real key, which
        // forces whoever does it to notice that fail-closed no longer holds.
        // **Never add a placeholder key to make this pass.**
        assertEquals(0, ReleaseSigningKeys.TRUSTED.size)
    }

    private fun base64(data: ByteArray): String {
        val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
        val out = StringBuilder()
        var index = 0
        while (index + 2 < data.size) {
            val chunk = ((data[index].toInt() and 0xFF) shl 16) or
                ((data[index + 1].toInt() and 0xFF) shl 8) or
                (data[index + 2].toInt() and 0xFF)
            out.append(alphabet[(chunk shr 18) and 0x3F])
            out.append(alphabet[(chunk shr 12) and 0x3F])
            out.append(alphabet[(chunk shr 6) and 0x3F])
            out.append(alphabet[chunk and 0x3F])
            index += 3
        }
        when (data.size - index) {
            1 -> {
                val chunk = (data[index].toInt() and 0xFF) shl 16
                out.append(alphabet[(chunk shr 18) and 0x3F])
                out.append(alphabet[(chunk shr 12) and 0x3F])
                out.append("==")
            }
            2 -> {
                val chunk = ((data[index].toInt() and 0xFF) shl 16) or
                    ((data[index + 1].toInt() and 0xFF) shl 8)
                out.append(alphabet[(chunk shr 18) and 0x3F])
                out.append(alphabet[(chunk shr 12) and 0x3F])
                out.append(alphabet[(chunk shr 6) and 0x3F])
                out.append('=')
            }
        }
        return out.toString()
    }
}
