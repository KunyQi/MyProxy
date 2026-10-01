package com.myproxy.android.vpn

import org.junit.Assert.assertEquals
import org.junit.Test

class VpnCleanupPolicyTest {

    @Test
    fun `idle requires native core and tun cleanup`() {
        assertEquals(VpnState.IDLE, cleanupCompletionState(true, true))
        assertEquals(VpnState.ERROR, cleanupCompletionState(false, true))
        assertEquals(VpnState.ERROR, cleanupCompletionState(true, false))
        assertEquals(VpnState.ERROR, cleanupCompletionState(false, false))
    }

    @Test
    fun `service scope restart is allowed only after completed cleanup`() {
        // Cleanup still running: the start is rejected and consumed by that
        // cleanup's stopSelfResult.
        assertEquals(false, canRestartServiceInstance(cleanupStarted = true, cleanupCompleted = false))
        // This instance's own cleanup finished: accept, whatever the global
        // state says. VpnController.startVpn writes STARTING before the intent
        // arrives, so the old check against a global IDLE never held for a
        // real start and retry() after a cleanup always timed out.
        assertEquals(true, canRestartServiceInstance(cleanupStarted = true, cleanupCompleted = true))
        assertEquals(false, canRestartServiceInstance(cleanupStarted = false, cleanupCompleted = false))
    }

    @Test
    fun `stale cleanup generation cannot finalize a newer start`() {
        assertEquals(true, isCurrentCleanupGeneration(7, 7))
        assertEquals(false, isCurrentCleanupGeneration(7, 8))
    }

    @Test
    fun `successful cleanup consumes rejected newer framework start`() {
        assertEquals(8, cleanupStopStartId(7, 8, 7))
        assertEquals(7, cleanupStopStartId(7, 7, 7))
        assertEquals(null, cleanupStopStartId(7, 8, 8))
    }

    @Test
    fun `failed cleanup after rejected start remains retryable`() {
        assertEquals(true, cleanupFailureCanRetry(false, VpnState.ERROR))
        assertEquals(false, cleanupFailureCanRetry(true, VpnState.ERROR))
        assertEquals(false, cleanupFailureCanRetry(false, VpnState.STOPPING))
    }

    @Test
    fun `lifecycle race simulation - framework start delivered while stopping`() {
        val cleanupStartId = 10
        var latestStartId = 10
        var acceptedStartId = 10

        // Cleanup begins for startId 10
        val isCurrent = isCurrentCleanupGeneration(cleanupStartId, latestStartId)
        assertEquals(true, isCurrent)

        // Framework delivers startId 11 while cleanup 10 is in progress.
        // The service is in STOPPING/cleanupStarted=true, so it rejects it.
        latestStartId = 11

        // Cleanup 10 completes successfully.
        // It should consume the latest framework ID (11) so no dangling service remains.
        val stopId = cleanupStopStartId(cleanupStartId, latestStartId, acceptedStartId)
        assertEquals(11, stopId)
    }
}
