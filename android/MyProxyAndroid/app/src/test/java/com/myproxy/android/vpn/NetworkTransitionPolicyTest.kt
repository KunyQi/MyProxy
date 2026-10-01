package com.myproxy.android.vpn

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NetworkTransitionPolicyTest {

    private fun roam(
        previousNetworkId: Long? = WIFI,
        newNetworkId: Long = CELLULAR,
        vpnState: VpnState = VpnState.RUNNING,
        cleanupStarted: Boolean = false,
        reconfigureInProgress: Boolean = false,
    ): Boolean = shouldRestartCoreForNetwork(
        previousNetworkId = previousNetworkId,
        newNetworkId = newNetworkId,
        vpnState = vpnState,
        cleanupStarted = cleanupStarted,
        reconfigureInProgress = reconfigureInProgress,
    )

    @Test
    fun `moving between networks migrates the core`() {
        assertTrue(roam(previousNetworkId = WIFI, newNetworkId = CELLULAR))
        assertTrue(roam(previousNetworkId = CELLULAR, newNetworkId = WIFI))
    }

    @Test
    fun `the network the tunnel started on is not a migration`() {
        // The first callback after registering reports the network the core
        // already dialled out over; restarting it there would be a pointless
        // interruption at every connect.
        assertFalse(roam(previousNetworkId = null))
    }

    @Test
    fun `the same network is not a migration`() {
        assertFalse(roam(previousNetworkId = WIFI, newNetworkId = WIFI))
    }

    @Test
    fun `only a running tunnel migrates`() {
        for (state in VpnState.entries.filter { it != VpnState.RUNNING }) {
            assertFalse("state $state scheduled a roam", roam(vpnState = state))
        }
        assertTrue(roam(vpnState = VpnState.RUNNING))
    }

    @Test
    fun `a tunnel being torn down or reconfigured is left alone`() {
        // Both already own the core; a roam would race their command epoch.
        assertFalse(roam(cleanupStarted = true))
        assertFalse(roam(reconfigureInProgress = true))
    }

    @Test
    fun `an error during a recovery already in flight is deferred`() {
        assertTrue(shouldDeferXrayError(true, roamInProgress = false, selfHealInProgress = false))
        assertTrue(shouldDeferXrayError(false, roamInProgress = true, selfHealInProgress = false))
        assertTrue(shouldDeferXrayError(false, roamInProgress = false, selfHealInProgress = true))
        assertTrue(shouldDeferXrayError(true, roamInProgress = true, selfHealInProgress = true))
    }

    @Test
    fun `an error with nobody recovering is acted on`() {
        assertFalse(shouldDeferXrayError(false, roamInProgress = false, selfHealInProgress = false))
    }

    @Test
    fun `re-dial retries back off exponentially`() {
        // 1s -> 2s -> 4s -> 8s -> 16s。没信号时线性退避等于以近乎恒定的频率
        // 朝一条不通的链路反复拨号，而每次失败都是一次真实的无线电唤醒。
        assertEquals(1_000L, redialRetryDelayMillis(0, 1_000L))
        assertEquals(2_000L, redialRetryDelayMillis(1, 1_000L))
        assertEquals(4_000L, redialRetryDelayMillis(2, 1_000L))
        assertEquals(8_000L, redialRetryDelayMillis(3, 1_000L))
        assertEquals(16_000L, redialRetryDelayMillis(4, 1_000L))
    }

    @Test
    fun `the backoff is capped and never negative`() {
        assertEquals(5_000L, redialRetryDelayMillis(10, 1_000L, maxDelayMillis = 5_000L))
        // 负的 attempt 不能产生负延迟（那会变成忙等）。
        assertEquals(1_000L, redialRetryDelayMillis(-1, 1_000L))
        // 一个一直在涨的计数器不能因为 `shl 64` 绕回 0 而把退避清零。
        assertTrue(redialRetryDelayMillis(64, 1_000L, maxDelayMillis = 5_000L) > 0L)
    }

    private fun recover(
        vpnState: VpnState = VpnState.ERROR,
        blockingHoldActive: Boolean = true,
        hasActiveConfig: Boolean = true,
        tunnelInterfaceUp: Boolean = true,
        cleanupStarted: Boolean = false,
    ): Boolean = shouldRedialOnNetworkAvailable(
        vpnState = vpnState,
        blockingHoldActive = blockingHoldActive,
        hasActiveConfig = hasActiveConfig,
        tunnelInterfaceUp = tunnelInterfaceUp,
        cleanupStarted = cleanupStarted,
    )

    @Test
    fun `a blocking hold re-dials once the link is back`() {
        // 接口还在、核心已死、流量正在被丢弃：网络一恢复就该把核心装回去，
        // 而不是等用户发现「怎么还连不上」再去点重试。
        assertTrue(recover())
    }

    @Test
    fun `nothing else re-dials on a network callback`() {
        // 没有 hold 就没有可复用的接口；正在清理、没有配置、接口已关，
        // 每一种都意味着这条路不该自己动起来。
        assertFalse(recover(blockingHoldActive = false))
        assertFalse(recover(hasActiveConfig = false))
        assertFalse(recover(tunnelInterfaceUp = false))
        assertFalse(recover(cleanupStarted = true))
        for (state in VpnState.entries.filter { it != VpnState.ERROR }) {
            assertFalse("state $state", recover(vpnState = state))
        }
    }

    private companion object {
        const val WIFI = 101L
        const val CELLULAR = 202L
    }
}
