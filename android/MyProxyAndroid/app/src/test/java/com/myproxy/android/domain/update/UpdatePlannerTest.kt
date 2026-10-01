package com.myproxy.android.domain.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdatePlannerTest {

    private fun verified(version: String, minimumVersion: String = "") = ManifestVerification(
        ManifestRejection.NONE,
        ReleaseManifest(
            platform = "android",
            version = version,
            channel = "stable",
            mandatory = false,
            artifactUrl = "https://releases.example/a.apk",
            artifactSha256 = "a".repeat(64),
            artifactSize = 1L,
            platformSignatureType = "apksigner",
            platformSignatureSubjectSha256 = "b".repeat(64),
            issuedAt = "2026-09-20T00:00:00Z",
            minimumVersion = minimumVersion,
        ),
    )

    @Test
    fun `higher version installs`() {
        assertEquals(UpdateDecision.INSTALL, UpdatePlanner.decide(verified("2.0.0"), "1.0.0").decision)
    }

    @Test
    fun `same or lower version is up to date`() {
        assertEquals(UpdateDecision.UP_TO_DATE, UpdatePlanner.decide(verified("1.0.0"), "1.0.0").decision)
        assertEquals(UpdateDecision.UP_TO_DATE, UpdatePlanner.decide(verified("0.9.0"), "1.0.0").decision)
    }

    @Test
    fun `a rejected manifest never becomes up to date`() {
        // "Signature failed" and "you are current" are different answers.
        // Showing the first as the second tells the user everything is fine
        // while they sit behind an update that will not install.
        val plan = UpdatePlanner.decide(ManifestVerification(ManifestRejection.BAD_SIGNATURE), "1.0.0")

        assertEquals(UpdateDecision.REJECTED, plan.decision)
        assertEquals(ManifestRejection.BAD_SIGNATURE, plan.rejection)
    }

    @Test
    fun `an unparseable version is rejected not assumed current`() {
        assertEquals(UpdateDecision.REJECTED, UpdatePlanner.decide(verified("1.2"), "1.0.0").decision)
        assertEquals(
            ManifestRejection.BAD_VERSION,
            UpdatePlanner.decide(verified("1.2"), "1.0.0").rejection,
        )
    }

    @Test
    fun `a minimum version above local requires an upgrade path`() {
        // Jumping straight there produces an install whose upgrade path was
        // never tested.
        assertEquals(
            UpdateDecision.UPGRADE_PATH_REQUIRED,
            UpdatePlanner.decide(verified("3.0.0", minimumVersion = "2.0.0"), "1.0.0").decision,
        )
    }

    @Test
    fun `a satisfied minimum version installs`() {
        assertEquals(
            UpdateDecision.INSTALL,
            UpdatePlanner.decide(verified("3.0.0", minimumVersion = "2.0.0"), "2.1.0").decision,
        )
    }

    @Test
    fun `mandatory is carried through from the manifest`() {
        val plan = UpdatePlanner.decide(verified("2.0.0"), "1.0.0")
        assertFalse(plan.mandatory)
        assertTrue(plan.manifest != null)
    }
}

class InstallRecoveryPolicyTest {

    private fun pending(stage: InstallStage, target: String = "2.0.0") = PendingInstall(
        stage = stage,
        releaseId = "rel_x",
        targetVersion = target,
        previousVersion = "1.0.0",
    )

    @Test
    fun `nothing pending means nothing to do`() {
        assertEquals(InstallRecovery.NONE, InstallRecoveryPolicy.decide(null, "1.0.0"))
        assertEquals(
            InstallRecovery.NONE,
            InstallRecoveryPolicy.decide(pending(InstallStage.IDLE), "1.0.0"),
        )
    }

    @Test
    fun `verified but never submitted only discards`() {
        assertEquals(
            InstallRecovery.DISCARD,
            InstallRecoveryPolicy.decide(pending(InstallStage.VERIFIED), "1.0.0"),
        )
    }

    @Test
    fun `submitted and now running the target reports installed`() {
        assertEquals(
            InstallRecovery.REPORT_INSTALLED,
            InstallRecoveryPolicy.decide(pending(InstallStage.SUBMITTED), "2.0.0"),
        )
    }

    @Test
    fun `submitted but still on the old version reports failed`() {
        // The session was refused or declined. Nothing needs repairing: the
        // platform installer never replaced anything.
        assertEquals(
            InstallRecovery.REPORT_FAILED,
            InstallRecoveryPolicy.decide(pending(InstallStage.SUBMITTED), "1.0.0"),
        )
    }

    @Test
    fun `build metadata does not look like a failed install`() {
        // 1.2.3 and 1.2.3+build.7 are the same version; a string compare
        // would call a successful install a failure.
        assertEquals(
            InstallRecovery.REPORT_INSTALLED,
            InstallRecoveryPolicy.decide(pending(InstallStage.SUBMITTED, "1.2.3"), "1.2.3+build.7"),
        )
    }

    @Test
    fun `report status covers every recovery action`() {
        assertEquals(InstallStatus.INSTALLED, InstallRecoveryPolicy.reportStatusFor(InstallRecovery.REPORT_INSTALLED))
        assertEquals(InstallStatus.FAILED, InstallRecoveryPolicy.reportStatusFor(InstallRecovery.REPORT_FAILED))
        assertEquals("", InstallRecoveryPolicy.reportStatusFor(InstallRecovery.DISCARD))
        assertEquals("", InstallRecoveryPolicy.reportStatusFor(InstallRecovery.NONE))
    }
}
