package com.myproxy.android.domain.update

/**
 * How an install ended, as reported back to the server.
 *
 * The vocabulary is shared with the Windows client and with
 * `server/myproxy_server/release.py`; the server rejects anything outside it.
 */
object InstallStatus {
    const val INSTALLED = "installed"
    const val FAILED = "failed"
    const val ROLLED_BACK = "rolledback"
}

/** The stage a pending install has reached. */
enum class InstallStage {
    /** Nothing in flight. */
    IDLE,

    /** The APK is downloaded and fully verified, waiting to be handed over. */
    VERIFIED,

    /** Handed to the platform installer; the outcome has not come back yet. */
    SUBMITTED,
}

/**
 * The record of a pending install.
 *
 * Deliberately much smaller than the Windows `UpdateJournal`, and the
 * difference is not an omission — it is the platform.
 *
 * **Android does not need a rollback journal, because the platform installer
 * is atomic.** A failed or aborted `PackageInstaller` session leaves the
 * currently installed APK exactly as it was; there is no half-replaced
 * install to repair, so there is nothing to restore. On Windows the client
 * has to move its own directories, which is why it needs a journal that
 * survives a power cut mid-swap. Here the only thing worth persisting is
 * "which release did we hand over, so we can report what happened to it".
 *
 * That is also why this client's rollback story is simply: verify before
 * handing over, and if the session fails, report `failed` and stay on the
 * version already installed.
 */
data class PendingInstall(
    val stage: InstallStage = InstallStage.IDLE,
    val releaseId: String = "",
    val targetVersion: String = "",
    val previousVersion: String = "",
    val detail: String = "",
)

/** What to do at startup about a session whose outcome never arrived. */
enum class InstallRecovery {
    NONE,

    /** Verified but never submitted: discard the staged file, say nothing. */
    DISCARD,

    /** We are now running the target: report `installed`. */
    REPORT_INSTALLED,

    /**
     * We handed the APK over, and we are still on the old version. The
     * session was refused or the user declined. Report `failed`; nothing
     * needs repairing, because the platform never replaced anything.
     */
    REPORT_FAILED,
}

object InstallRecoveryPolicy {
    fun decide(pending: PendingInstall?, runningVersion: String): InstallRecovery {
        if (pending == null) return InstallRecovery.NONE

        return when (pending.stage) {
            InstallStage.IDLE -> InstallRecovery.NONE
            InstallStage.VERIFIED -> InstallRecovery.DISCARD
            InstallStage.SUBMITTED ->
                if (versionsMatch(runningVersion, pending.targetVersion)) {
                    InstallRecovery.REPORT_INSTALLED
                } else {
                    InstallRecovery.REPORT_FAILED
                }
        }
    }

    fun reportStatusFor(recovery: InstallRecovery): String = when (recovery) {
        InstallRecovery.REPORT_INSTALLED -> InstallStatus.INSTALLED
        InstallRecovery.REPORT_FAILED -> InstallStatus.FAILED
        else -> ""
    }

    /**
     * Compare by the numeric core, not by string equality: `1.2.3` and
     * `1.2.3+build.7` are the same version, and a string compare would call
     * a successful install a failure.
     */
    private fun versionsMatch(left: String, right: String): Boolean {
        val a = VersionParser.parse(left) ?: return false
        val b = VersionParser.parse(right) ?: return false
        return a.compareTo(b) == 0
    }
}
