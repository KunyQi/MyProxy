package com.myproxy.android.data.update

import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import com.myproxy.android.domain.update.ReleaseManifest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

/** The outcome of downloading and verifying a release. */
sealed interface StageResult {
    data class Ready(val file: File) : StageResult
    data class Refused(val reason: String) : StageResult
}

/**
 * Downloads a release APK, verifies it, and hands it to the platform
 * installer.
 *
 * **Android needs no rollback machinery, and that is a platform difference
 * rather than a gap.** `PackageInstaller` replaces a package atomically: a
 * session that fails or is declined leaves the installed APK exactly as it
 * was. There is no half-replaced install to repair, so there is nothing to
 * restore. The Windows client moves its own directories and therefore needs
 * a journal that survives a power cut mid-swap; here the whole rollback
 * story is "verify before handing over, and if the session fails, stay on
 * the version already installed and report it".
 */
class UpdateInstaller(
    private val context: Context,
    private val verifier: ApkVerifier = ApkVerifier(context),
    http: OkHttpClient? = null,
) {
    /**
     * Artifacts use a separate HTTP client because they may be hosted on
     * another HTTPS origin. TLS validates the normal certificate chain and
     * hostname; SHA-256 and the signer identity from the Ed25519-signed
     * manifest independently authenticate the downloaded artifact.
     *
     * Redirects are off for the same reason as on the API client, plus one
     * more: the URL came from a signed manifest, so a redirect would land
     * somewhere the signature never covered.
     */
    private val client: OkHttpClient = http ?: OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.MINUTES)
        .writeTimeout(1, TimeUnit.MINUTES)
        .followRedirects(false)
        .followSslRedirects(false)
        .build()

    private val stagingDir: File get() = File(context.cacheDir, "updates")

    suspend fun stage(manifest: ReleaseManifest): StageResult = withContext(Dispatchers.IO) {
        val target = File(stagingDir, "MyProxy-${sanitize(manifest.version)}.apk")
        try {
            stagingDir.mkdirs()
            target.delete()

            val request = Request.Builder().url(manifest.artifactUrl).get().build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    return@withContext refuse(target, "download HTTP ${response.code}")
                }

                // The declared size is the first gate: it turns away a
                // swapped, enormous file before it fills the disk, rather
                // than after the hash finally says so.
                val declared = response.body?.contentLength() ?: -1L
                if (declared >= 0 && declared != manifest.artifactSize) {
                    return@withContext refuse(target, "artifact size mismatch")
                }

                val body = response.body ?: return@withContext refuse(target, "empty response body")
                target.outputStream().use { sink ->
                    val source = body.byteStream()
                    val buffer = ByteArray(64 * 1024)
                    var written = 0L
                    while (true) {
                        val read = source.read(buffer)
                        if (read <= 0) break
                        written += read
                        if (written > manifest.artifactSize) {
                            return@withContext refuse(target, "artifact exceeds the declared size")
                        }
                        sink.write(buffer, 0, read)
                    }
                }
            }

            val rejection = verifier.verify(
                target,
                manifest.artifactSize,
                manifest.artifactSha256,
                manifest.platformSignatureSubjectSha256,
            )
            if (rejection != ApkRejection.NONE) {
                return@withContext refuse(target, rejection.name.lowercase())
            }

            StageResult.Ready(target)
        } catch (e: IOException) {
            refuse(target, e.javaClass.simpleName)
        }
    }

    /**
     * Hands a verified APK to the platform installer.
     *
     * Requires the user to confirm; the app never installs silently. The
     * caller records that the session was submitted so the outcome can be
     * reported after the process restarts.
     */
    suspend fun submit(file: File, statusIntent: android.app.PendingIntent): Boolean =
        withContext(Dispatchers.IO) {
            runCatching {
                val installer = context.packageManager.packageInstaller
                val params = PackageInstaller.SessionParams(
                    PackageInstaller.SessionParams.MODE_FULL_INSTALL,
                )
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    params.setRequireUserAction(
                        PackageInstaller.SessionParams.USER_ACTION_REQUIRED,
                    )
                }

                val sessionId = installer.createSession(params)
                installer.openSession(sessionId).use { session ->
                    session.openWrite("package", 0, file.length()).use { sink ->
                        file.inputStream().use { source -> source.copyTo(sink) }
                        session.fsync(sink)
                    }
                    session.commit(statusIntent.intentSender)
                }
                true
            }.getOrElse { false }
        }

    /** Remove staged packages. Safe to call at any time. */
    fun discardStaging() {
        runCatching { stagingDir.deleteRecursively() }
    }

    private fun refuse(target: File, reason: String): StageResult {
        runCatching { target.delete() }
        return StageResult.Refused(reason)
    }

    private fun sanitize(version: String): String {
        val cleaned = version.take(64).map { ch ->
            if (ch.isLetterOrDigit() || ch == '.' || ch == '-' || ch == '_' || ch == '+') ch else '_'
        }.joinToString("")
        return cleaned.ifEmpty { "unknown" }
    }

    companion object {
        /** Action for the install session's status callback. */
        const val ACTION_INSTALL_STATUS = "com.myproxy.android.INSTALL_STATUS"

        fun statusIntent(context: Context): Intent =
            Intent(ACTION_INSTALL_STATUS).setPackage(context.packageName)
    }
}
