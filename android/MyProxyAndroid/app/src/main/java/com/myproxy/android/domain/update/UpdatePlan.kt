package com.myproxy.android.domain.update

/** What to do about a manifest that has already been verified. */
enum class UpdateDecision {
    /** No assignment, or the assigned version is not newer than this build. */
    UP_TO_DATE,

    /** A newer version is available and installable. */
    INSTALL,

    /** The manifest failed verification or validation. Refuse to install. */
    REJECTED,

    /**
     * The assigned release demands a minimum version this build is below.
     * Jumping straight there would produce an install whose upgrade path was
     * never tested.
     */
    UPGRADE_PATH_REQUIRED,
}

data class UpdatePlanResult(
    val decision: UpdateDecision,
    val manifest: ReleaseManifest? = null,
    val rejection: ManifestRejection = ManifestRejection.NONE,
    val releaseId: String = "",
) {
    val mandatory: Boolean get() = manifest?.mandatory ?: false
}

/**
 * "Should this be installed" as a pure decision — no I/O, no network, so
 * every edge can be put in a test. The Windows client has the same rules in
 * `Core/UpdatePlan.cs`; the two must stay in step or one platform will accept
 * a release the other refuses.
 */
object UpdatePlanner {
    /**
     * [verification] must already have been signature-checked: this function
     * never touches signatures, it only answers what to do afterwards.
     */
    fun decide(
        verification: ManifestVerification,
        localVersion: String,
        releaseId: String = "",
    ): UpdatePlanResult {
        val manifest = verification.manifest
        if (!verification.ok || manifest == null) {
            return UpdatePlanResult(UpdateDecision.REJECTED, null, verification.rejection, releaseId)
        }

        val remote = VersionParser.parse(manifest.version)
        val local = VersionParser.parse(localVersion)
        if (remote == null || local == null) {
            // No parse means no verdict. Never fall through to "up to date":
            // that would tell the user everything is fine while they are
            // stuck behind an update that cannot be installed.
            return UpdatePlanResult(
                UpdateDecision.REJECTED,
                manifest,
                ManifestRejection.BAD_VERSION,
                releaseId,
            )
        }

        if (remote <= local) {
            return UpdatePlanResult(UpdateDecision.UP_TO_DATE, manifest, ManifestRejection.NONE, releaseId)
        }

        val minimum = manifest.minimumVersion.takeIf { it.isNotEmpty() }?.let { VersionParser.parse(it) }
        if (minimum != null && local < minimum) {
            return UpdatePlanResult(
                UpdateDecision.UPGRADE_PATH_REQUIRED,
                manifest,
                ManifestRejection.NONE,
                releaseId,
            )
        }

        return UpdatePlanResult(UpdateDecision.INSTALL, manifest, ManifestRejection.NONE, releaseId)
    }
}
