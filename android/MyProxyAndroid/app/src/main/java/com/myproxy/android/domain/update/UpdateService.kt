package com.myproxy.android.domain.update

import com.myproxy.android.data.api.UpdateInfo

/** The result of asking what this device is assigned. */
data class AssignedUpdate(
    val plan: UpdatePlanResult,
    val flags: FeatureFlags,
    val releaseId: String = "",
)

interface UpdateService {
    /**
     * The public version hint; no device token required. When a signed
     * manifest comes with it, it is verified before being believed.
     */
    suspend fun check(): UpdateInfo?

    /**
     * The release assigned to this device, verified locally. Returns null
     * when unbound or the Control Plane is unreachable; callers fall back to
     * [check].
     */
    suspend fun fetchAssigned(): AssignedUpdate?

    /**
     * Report how an install ended. This is the only way the server learns
     * that a release is failing in the field.
     *
     * Never throws: failing to report must not make a successful install
     * look like a failed one.
     */
    suspend fun reportInstall(releaseId: String, status: String, detail: String)
}
