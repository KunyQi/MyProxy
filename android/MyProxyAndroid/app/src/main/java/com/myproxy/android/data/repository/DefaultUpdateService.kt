package com.myproxy.android.data.repository

import com.myproxy.android.data.api.ApiConfig
import com.myproxy.android.data.api.MyProxyApiClient
import com.myproxy.android.data.api.ReleaseSigningKeys
import com.myproxy.android.data.api.UpdateInfo
import com.myproxy.android.data.api.UpdateReportRequest
import com.myproxy.android.data.storage.CredentialStorage
import com.myproxy.android.data.storage.CredentialUnreadableException
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.update.AssignedUpdate
import com.myproxy.android.domain.update.FeatureFlags
import com.myproxy.android.domain.update.ManifestVerification
import com.myproxy.android.domain.update.ReleaseManifestVerifier
import com.myproxy.android.domain.update.UpdateDecision
import com.myproxy.android.domain.update.UpdatePlanResult
import com.myproxy.android.domain.update.UpdatePlanner
import com.myproxy.android.domain.update.UpdateService
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.Json

class DefaultUpdateService(
    private val apiClient: MyProxyApiClient,
    private val currentVersion: String = ApiConfig.VERSION,
    private val secureStorage: CredentialStorage? = null,
    private val trustedKeys: Map<String, ByteArray> = ReleaseSigningKeys.TRUSTED,
) : UpdateService {

    private val json = Json { ignoreUnknownKeys = true }

    override suspend fun check(): UpdateInfo? {
        return try {
            offeredUpdate(apiClient.getAndroidLatest(), currentVersion, trustedKeys)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            null
        }
    }

    override suspend fun fetchAssigned(): AssignedUpdate? {
        val token = readToken() ?: return null
        return try {
            val response = apiClient.getDeviceUpdate(token)
            val flags = FeatureFlags.fromJson(response.featureFlags)
            val update = response.update
                ?: return AssignedUpdate(UpdatePlanResult(UpdateDecision.UP_TO_DATE), flags)

            val verification: ManifestVerification = ReleaseManifestVerifier.verify(
                update.manifest,
                update.signature,
                update.signingKeyId,
                trustedKeys,
            )
            AssignedUpdate(
                UpdatePlanner.decide(verification, currentVersion, update.releaseId),
                flags,
                update.releaseId,
            )
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            null
        }
    }

    override suspend fun reportInstall(releaseId: String, status: String, detail: String) {
        val token = readToken() ?: return
        if (releaseId.isEmpty() || status.isEmpty()) return
        try {
            apiClient.reportInstall(
                token,
                UpdateReportRequest(releaseId, status, detail.take(256)),
            )
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Best effort: failing to report must not make a successful
            // install look like a failed one.
        }
    }

    private fun readToken(): String? {
        // Keystore unavailable right now: behave as if there were no token this time.
        val raw = try {
            secureStorage?.read(StorageKeys.DEVICE_CREDENTIAL)
        } catch (_: CredentialUnreadableException) {
            null
        } ?: return null
        return runCatching {
            json.decodeFromString<DeviceCredential>(raw).deviceToken
        }.getOrNull()?.takeIf { it.isNotEmpty() }
    }
}


internal fun offeredUpdate(
    info: UpdateInfo,
    currentVersion: String,
    trustedKeys: Map<String, ByteArray>,
): UpdateInfo? {
    if (!info.hasSignedManifest) return null

    val verification = ReleaseManifestVerifier.verify(
        info.manifest,
        info.signature,
        info.signingKeyId,
        trustedKeys,
    )
    val plan = UpdatePlanner.decide(verification, currentVersion, info.releaseId)
    val manifest = verification.manifest
    if (plan.decision != UpdateDecision.INSTALL || manifest == null) return null

    return info.copy(
        version = manifest.version,
        downloadUrl = manifest.artifactUrl,
        sha256 = manifest.artifactSha256,
        mandatory = manifest.mandatory,
    )
}
