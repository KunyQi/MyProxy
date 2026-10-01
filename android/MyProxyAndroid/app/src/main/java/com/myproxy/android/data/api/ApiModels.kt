package com.myproxy.android.data.api

import com.myproxy.android.domain.model.ServerProfile
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

@Serializable
data class ClaimRequest(
    val pairingCode: String,
    val deviceName: String,
    val platform: String,
    val clientVersion: String,
    val clientInstanceId: String,
)

@Serializable
data class ClaimResponse(
    val deviceId: String,
    val deviceToken: String,
    val configVersion: Int,
    @SerialName("config") val config: ServerProfile? = null,
    val capabilities: List<String> = emptyList(),
    val featureFlags: Map<String, Boolean> = emptyMap(),
)

@Serializable
data class ConfigSyncResponse(
    val configVersion: Int,
    @SerialName("config") val config: ServerProfile,
    val schemaVersion: Int = 1,
    val capabilities: List<String> = emptyList(),
    val featureFlags: Map<String, Boolean> = emptyMap(),
)

@Serializable
data class HeartbeatResponse(
    val ok: Boolean,
    val configVersion: Int,
    val serverTime: String? = null,
    /**
     * The release assigned to this device, or null when there is none.
     * **Identity only** — the signed manifest still has to be fetched from
     * `/api/device/update` and verified locally.
     */
    val release: AssignedReleaseInfo? = null,
    /**
     * Kept as a raw [JsonElement] so an unrecognised switch cannot fail the
     * whole response; [com.myproxy.android.domain.update.FeatureFlags]
     * drops what it does not understand.
     */
    val featureFlags: JsonElement? = null,
)

/** The identity of an assigned release, as carried by heartbeat and update. */
@Serializable
data class AssignedReleaseInfo(
    val releaseId: String = "",
    val version: String = "",
    val channel: String = "",
    val mandatory: Boolean = false,
    /** Which assignment scope matched: device, user or platform. */
    val source: String = "",
)

/**
 * The release payload from `/api/device/update`.
 *
 * [manifest] and [signature] are base64 and are **passed through and verified
 * exactly as received**; any re-serialisation invalidates the signature.
 */
@Serializable
data class AssignedReleasePayload(
    val releaseId: String = "",
    val version: String = "",
    val channel: String = "",
    val mandatory: Boolean = false,
    val source: String = "",
    val manifest: String = "",
    val signature: String = "",
    val signingKeyId: String = "",
)

@Serializable
data class DeviceUpdateResponse(
    val update: AssignedReleasePayload? = null,
    val featureFlags: JsonElement? = null,
    val serverTime: String? = null,
)

@Serializable
data class UpdateReportRequest(
    val releaseId: String,
    val status: String,
    val detail: String = "",
)

/**
 * A usage report. Categories only: the server's totals come from its own
 * sweep of the data plane, so a client can say what kind of traffic it
 * moved, never how much.
 */
@Serializable
data class UsageReportRequest(
    val bucketStart: String,
    val categories: Map<String, Long>,
)

@Serializable
data class OkResponse(
    val ok: Boolean = false,
    val serverTime: String? = null,
)

@Serializable
data class UpdateInfo(
    @SerialName("version") val version: String,
    @SerialName("downloadUrl") val downloadUrl: String,
    @SerialName("sha256") val sha256: String,
    @SerialName("mandatory") val mandatory: Boolean,
    /**
     * When a platform-scope assignment exists, latest.json also carries the
     * signed manifest. This route needs no device token, which is how an
     * update check survives the Control Plane being unreachable.
     */
    @SerialName("releaseId") val releaseId: String = "",
    @SerialName("manifest") val manifest: String = "",
    @SerialName("signature") val signature: String = "",
    @SerialName("signingKeyId") val signingKeyId: String = "",
) {
    /** Whether a verifiable manifest came with it. Without one nothing is offered, not even a hint. */
    val hasSignedManifest: Boolean
        get() = manifest.isNotEmpty() && signature.isNotEmpty() && signingKeyId.isNotEmpty()
}

@Serializable
data class ApiErrorBody(
    val error: ApiErrorDetail? = null,
)

@Serializable
data class ApiErrorDetail(
    val code: String? = null,
    val message: String? = null,
)
