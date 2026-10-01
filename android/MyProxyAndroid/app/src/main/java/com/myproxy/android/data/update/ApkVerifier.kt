package com.myproxy.android.data.update

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.content.pm.Signature
import androidx.annotation.RequiresApi
import android.os.Build
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

/** Why a downloaded package was refused. Log detail, never shown to a user. */
enum class ApkRejection {
    NONE,
    SIZE_MISMATCH,
    HASH_MISMATCH,
    UNREADABLE,
    UNSIGNED,
    SIGNER_MISMATCH,
}

/**
 * Checks a downloaded APK against the manifest that was already verified.
 *
 * The order is **manifest signature, then SHA-256, then platform signature**,
 * and a failed step never falls through to the next. The manifest signature
 * is checked before this class is reached; what happens here is the other
 * two, and **any mismatch means the package is deleted and not installed.**
 * There is deliberately no "verification failed but we'll offer it anyway"
 * branch — that branch always gets taken eventually.
 *
 * Checking the APK's signing certificate matters as much as the hash. The
 * hash says the bytes are the ones the manifest named; the certificate says
 * they were built by whoever holds the release key. Accepting "it is signed"
 * without comparing *who* signed it would accept any self-signed APK.
 */
class ApkVerifier(private val context: Context) {

    /**
     * @param expectedSubjectSha256 the signer certificate fingerprint from
     * the manifest, which is itself covered by the Ed25519 signature — so it
     * cannot be swapped for the attacker's own certificate.
     */
    fun verify(
        file: File,
        expectedSize: Long,
        expectedSha256: String,
        expectedSubjectSha256: String,
    ): ApkRejection {
        if (!file.isFile) return ApkRejection.UNREADABLE
        if (file.length() != expectedSize) return ApkRejection.SIZE_MISMATCH

        val actualSha256 = runCatching { sha256(file) }.getOrNull()
            ?: return ApkRejection.UNREADABLE
        if (!actualSha256.equals(expectedSha256, ignoreCase = true)) {
            return ApkRejection.HASH_MISMATCH
        }

        val signers = readSignerSha256(file)
        if (signers.isEmpty()) return ApkRejection.UNSIGNED
        if (signers.none { it.equals(expectedSubjectSha256, ignoreCase = true) }) {
            return ApkRejection.SIGNER_MISMATCH
        }

        return ApkRejection.NONE
    }

    /**
     * SHA-256 of the signing certificates of an APK **file** (not of an
     * installed package).
     *
     * API 28 added `GET_SIGNING_CERTIFICATES` and the v2/v3 scheme; below
     * that only `GET_SIGNATURES` exists, so both paths are here. minSdk is
     * 24, so dropping the old path would silently make verification
     * impossible on the oldest supported devices — and "cannot verify"
     * must never quietly become "do not verify".
     */
    private fun readSignerSha256(file: File): List<String> {
        val certificates =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) modernSigners(file)
            else legacySigners(file)

        return certificates.mapNotNull { signature ->
            runCatching {
                MessageDigest.getInstance("SHA-256")
                    .digest(signature.toByteArray())
                    .toHex()
            }.getOrNull()
        }
    }

    @RequiresApi(Build.VERSION_CODES.P)
    private fun modernSigners(file: File): Array<Signature> {
        val info: PackageInfo = runCatching {
            context.packageManager.getPackageArchiveInfo(
                file.absolutePath,
                PackageManager.GET_SIGNING_CERTIFICATES,
            )
        }.getOrNull() ?: return emptyArray()

        val signing = info.signingInfo ?: return emptyArray()
        return if (signing.hasMultipleSigners()) {
            signing.apkContentsSigners ?: emptyArray()
        } else {
            signing.signingCertificateHistory ?: emptyArray()
        }
    }

    @Suppress("DEPRECATION")
    private fun legacySigners(file: File): Array<Signature> {
        val info: PackageInfo = runCatching {
            context.packageManager.getPackageArchiveInfo(
                file.absolutePath,
                PackageManager.GET_SIGNATURES,
            )
        }.getOrNull() ?: return emptyArray()
        return info.signatures ?: emptyArray()
    }

    companion object {
        fun sha256(file: File): String = file.inputStream().use { sha256(it) }

        fun sha256(stream: InputStream): String {
            val digest = MessageDigest.getInstance("SHA-256")
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = stream.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
            return digest.digest().toHex()
        }

        private fun ByteArray.toHex(): String {
            val out = StringBuilder(size * 2)
            for (byte in this) {
                val value = byte.toInt() and 0xFF
                out.append(HEX[value ushr 4])
                out.append(HEX[value and 0x0F])
            }
            return out.toString()
        }

        private const val HEX = "0123456789abcdef"
    }
}
