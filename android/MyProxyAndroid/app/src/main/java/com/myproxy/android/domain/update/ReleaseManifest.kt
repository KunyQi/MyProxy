package com.myproxy.android.domain.update

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.net.URI

/** Why a manifest was refused. Server-log detail, never shown to a user. */
enum class ManifestRejection {
    NONE,
    MALFORMED_ENVELOPE,
    UNKNOWN_SIGNING_KEY,
    BAD_SIGNATURE,
    MALFORMED_DOCUMENT,
    UNSUPPORTED_SCHEMA,
    WRONG_PLATFORM,
    BAD_VERSION,
    BAD_ARTIFACT,
}

/** A manifest that has passed signature verification and structural checks. */
data class ReleaseManifest(
    val platform: String,
    val version: String,
    val channel: String,
    val mandatory: Boolean,
    val artifactUrl: String,
    val artifactSha256: String,
    val artifactSize: Long,
    val platformSignatureType: String,
    val platformSignatureSubjectSha256: String,
    val issuedAt: String,
    val minimumVersion: String,
)

data class ManifestVerification(
    val rejection: ManifestRejection,
    val manifest: ReleaseManifest? = null,
) {
    val ok: Boolean get() = rejection == ManifestRejection.NONE && manifest != null
}

/**
 * Release manifest verification and structural validation. Pure, no I/O.
 *
 * **The manifest is verified as raw bytes and must never be re-serialised.**
 * The signature covers [SIGNING_DOMAIN] + the manifest's raw UTF-8 bytes.
 * Three independent implementations (Python server, C# and Kotlin clients)
 * have to agree on what was signed; any canonical-JSON scheme would make that
 * agreement depend on three serialisers matching on key order, number
 * formatting, Unicode escaping and separator whitespace. So the base64 is
 * decoded, the signature checked, and only then are those exact bytes handed
 * to a JSON parser — verify first, parse second, never re-encode.
 *
 * The domain prefix mirrors the claim-token message in the server's `auth`
 * module: a signature over a release manifest can never be replayed as a
 * signature over some other MyProxy structure.
 */
object ReleaseManifestVerifier {
    val SIGNING_DOMAIN: ByteArray = "myproxy-release-manifest-v1\u0000".toByteArray(Charsets.US_ASCII)

    const val SCHEMA_VERSION = 1
    const val ANDROID_PLATFORM = "android"
    const val ANDROID_SIGNATURE_TYPE = "apksigner"
    const val MAX_MANIFEST_BYTES = 8192

    private val HEX64 = Regex("^[0-9a-f]{64}$")
    private val json = Json { ignoreUnknownKeys = true }

    /**
     * Verify and parse. [trustedKeys] is the embedded key table; it must
     * **never** be fetched at runtime; signing trust keys ship with the client.
     */
    fun verify(
        manifestBase64: String?,
        signatureBase64: String?,
        signingKeyId: String?,
        trustedKeys: Map<String, ByteArray>,
    ): ManifestVerification {
        if (trustedKeys.isEmpty()) {
            // Fail closed: no trusted key means no trustworthy update.
            return ManifestVerification(ManifestRejection.UNKNOWN_SIGNING_KEY)
        }

        val publicKey = signingKeyId?.let { trustedKeys[it] }
            ?: return ManifestVerification(ManifestRejection.UNKNOWN_SIGNING_KEY)

        val manifestBytes = decodeBase64(manifestBase64, MAX_MANIFEST_BYTES)
            ?: return ManifestVerification(ManifestRejection.MALFORMED_ENVELOPE)
        val signature = decodeBase64(signatureBase64, 64)
            ?: return ManifestVerification(ManifestRejection.MALFORMED_ENVELOPE)
        if (signature.size != 64) {
            return ManifestVerification(ManifestRejection.MALFORMED_ENVELOPE)
        }

        val signed = SIGNING_DOMAIN + manifestBytes
        if (!Ed25519.verify(publicKey, signed, signature)) {
            return ManifestVerification(ManifestRejection.BAD_SIGNATURE)
        }

        return parse(manifestBytes)
    }

    private fun parse(manifestBytes: ByteArray): ManifestVerification {
        val root = runCatching {
            json.parseToJsonElement(manifestBytes.toString(Charsets.UTF_8)).jsonObject
        }.getOrNull() ?: return ManifestVerification(ManifestRejection.MALFORMED_DOCUMENT)

        if (readLong(root, "schemaVersion") != SCHEMA_VERSION.toLong()) {
            return ManifestVerification(ManifestRejection.UNSUPPORTED_SCHEMA)
        }

        val platform = readString(root, "platform")
        if (platform != ANDROID_PLATFORM) {
            // The server already resolved by platform, but the client checks
            // again: installing a manifest meant for another platform is the
            // kind of mistake nobody would notice.
            return ManifestVerification(ManifestRejection.WRONG_PLATFORM)
        }

        val version = readString(root, "version")
        if (VersionParser.parse(version) == null) {
            return ManifestVerification(ManifestRejection.BAD_VERSION)
        }

        val channel = readString(root, "channel")
        if (channel != "stable" && channel != "beta") {
            return ManifestVerification(ManifestRejection.MALFORMED_DOCUMENT)
        }

        val issuedAt = readString(root, "issuedAt")
        if (issuedAt.isEmpty()) {
            return ManifestVerification(ManifestRejection.MALFORMED_DOCUMENT)
        }

        val minimumVersion = readString(root, "minimumVersion")
        if (minimumVersion.isNotEmpty() && VersionParser.parse(minimumVersion) == null) {
            return ManifestVerification(ManifestRejection.BAD_VERSION)
        }

        val artifact = runCatching { root["artifact"]?.jsonObject }.getOrNull()
            ?: return ManifestVerification(ManifestRejection.BAD_ARTIFACT)

        val url = readString(artifact, "url")
        val uri = runCatching { URI(url) }.getOrNull()
        if (uri == null ||
            !"https".equals(uri.scheme, ignoreCase = true) ||
            uri.host.isNullOrBlank() ||
            !uri.userInfo.isNullOrEmpty()
        ) {
            return ManifestVerification(ManifestRejection.BAD_ARTIFACT)
        }

        val sha256 = readString(artifact, "sha256")
        if (!HEX64.matches(sha256)) {
            return ManifestVerification(ManifestRejection.BAD_ARTIFACT)
        }

        val size = readLong(artifact, "size") ?: 0L
        if (size <= 0L) {
            return ManifestVerification(ManifestRejection.BAD_ARTIFACT)
        }

        val signatureBlock = runCatching { artifact["signature"]?.jsonObject }.getOrNull()
            ?: return ManifestVerification(ManifestRejection.BAD_ARTIFACT)

        val signatureType = readString(signatureBlock, "type")
        if (signatureType != ANDROID_SIGNATURE_TYPE) {
            return ManifestVerification(ManifestRejection.BAD_ARTIFACT)
        }

        val subject = readString(signatureBlock, "subjectSha256")
        if (!HEX64.matches(subject)) {
            return ManifestVerification(ManifestRejection.BAD_ARTIFACT)
        }

        return ManifestVerification(
            ManifestRejection.NONE,
            ReleaseManifest(
                platform = platform,
                version = version,
                channel = channel,
                mandatory = readBoolean(root, "mandatory"),
                artifactUrl = url,
                artifactSha256 = sha256,
                artifactSize = size,
                platformSignatureType = signatureType,
                platformSignatureSubjectSha256 = subject,
                issuedAt = issuedAt,
                minimumVersion = minimumVersion,
            ),
        )
    }

    private fun readString(parent: JsonObject, name: String): String =
        (parent[name] as? JsonPrimitive)?.takeIf { it.isString }?.content ?: ""

    private fun readLong(parent: JsonObject, name: String): Long? =
        runCatching { parent[name]?.jsonPrimitive?.longOrNull }.getOrNull()

    private fun readBoolean(parent: JsonObject, name: String): Boolean =
        runCatching { parent[name]?.jsonPrimitive?.booleanOrNull }.getOrNull() ?: false

    /**
     * Strict base64 decode with a byte ceiling.
     *
     * `java.util.Base64` needs API 26 and this app supports 24, so decode by
     * hand; it also lets the alphabet be rejected strictly, which matters
     * because whatever comes out is exactly what was signed.
     */
    private fun decodeBase64(value: String?, maxBytes: Int): ByteArray? {
        if (value.isNullOrEmpty() || value.length > maxBytes * 2) return null

        val cleaned = value.trimEnd('=')
        val output = ArrayList<Byte>(cleaned.length * 3 / 4 + 3)
        var buffer = 0
        var bits = 0
        for (ch in cleaned) {
            val index = ALPHABET.indexOf(ch)
            if (index < 0) return null
            buffer = (buffer shl 6) or index
            bits += 6
            if (bits >= 8) {
                bits -= 8
                output.add(((buffer shr bits) and 0xFF).toByte())
                if (output.size > maxBytes) return null
            }
        }

        // Leftover bits must be zero padding; anything else is a second
        // encoding of the same bytes.
        if (bits >= 6 || (buffer and ((1 shl bits) - 1)) != 0) return null
        return output.toByteArray()
    }

    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
}
