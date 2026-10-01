package com.myproxy.android.vpn

/** IDLE is safe to publish only after both native core and TUN cleanup succeed. */
internal fun cleanupCompletionState(
    coreStopped: Boolean,
    vpnFdClosed: Boolean,
): VpnState = if (coreStopped && vpnFdClosed) VpnState.IDLE else VpnState.ERROR

/**
 * Whether this still-alive instance may take a new start after its own
 * cleanup. The system keeps a VpnService bound until the TUN is gone, so a
 * start sent right after IDLE -- retry() does exactly that -- is often
 * delivered to the old instance rather than a fresh one.
 *
 * Decided from the instance's own record, never from [VpnStateHolder]:
 * [VpnController.startVpn] writes STARTING *before* the intent is delivered,
 * so a check against IDLE was never true for a real start. Every such start
 * was dropped with START_NOT_STICKY, the controller waited out its timeout
 * and showed an error, and a start that came through startForegroundService
 * without a matching startForeground() is one the system is entitled to
 * crash the process over.
 */
internal fun canRestartServiceInstance(
    cleanupStarted: Boolean,
    cleanupCompleted: Boolean,
): Boolean = cleanupStarted && cleanupCompleted

/**
 * A cleanup coroutine may finalize the service only for the start that
 * initiated it. Android's stopSelfResult performs the same check at the
 * framework boundary; keeping this predicate separate makes the race
 * explicit and testable before touching the service scope or VPN state.
 */
internal fun isCurrentCleanupGeneration(
    cleanupStartId: Int,
    latestStartId: Int,
): Boolean = cleanupStartId == latestStartId

/**
 * Select the framework start id to consume after cleanup. A newer framework
 * start is safe to consume only when it was not accepted by this service;
 * otherwise the old cleanup must leave the new generation untouched.
 */
internal fun cleanupStopStartId(
    cleanupAcceptedStartId: Int,
    latestFrameworkStartId: Int,
    latestAcceptedStartId: Int,
): Int? = if (cleanupAcceptedStartId == latestAcceptedStartId) {
    latestFrameworkStartId
} else {
    null
}

/** A failed cleanup must leave a retryable ERROR after any rejected start. */
internal fun cleanupFailureCanRetry(
    cleanupStarted: Boolean,
    vpnState: VpnState,
): Boolean = !cleanupStarted && vpnState == VpnState.ERROR
