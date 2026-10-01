package com.myproxy.android.domain.connection

import com.myproxy.android.domain.model.AppState
import com.myproxy.android.vpn.VpnState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionPoliciesTest {
    @Test
    fun `mode selection is blocked during connecting and disconnecting`() {
        assertFalse(canSelectProxyMode(AppState.CONNECTING))
        assertFalse(canSelectProxyMode(AppState.DISCONNECTING))
        assertFalse(canSelectProxyMode(AppState.UNBOUND))
        assertTrue(canSelectProxyMode(AppState.DISCONNECTED))
        assertTrue(canSelectProxyMode(AppState.CONNECTED))
        assertTrue(canSelectProxyMode(AppState.ERROR))
    }

    @Test
    fun `connection retry is blocked while old vpn cleanup is pending`() {
        assertFalse(canStartConnection(AppState.ERROR, VpnState.STOPPING, false))
        assertFalse(canStartConnection(AppState.ERROR, VpnState.ERROR, false))
        assertFalse(canStartConnection(AppState.ERROR, VpnState.IDLE, true))
        assertTrue(canStartConnection(AppState.ERROR, VpnState.IDLE, false))
        assertTrue(canStartConnection(AppState.DISCONNECTED, VpnState.IDLE, false))
    }

    @Test
    fun `a kill-switch hold is not undone by the controller`() {
        // The service answers an unexpected failure by holding a blocking
        // interface and publishing ERROR. A stop command would release it, so
        // the controller must leave that one case alone.
        assertFalse(shouldReleaseTunnelAfterUnexpectedStop(true, VpnState.ERROR))
        // Every other state means no hold exists and cleanup is still owed.
        for (state in VpnState.entries.filter { it != VpnState.ERROR }) {
            assertTrue(
                "state $state should still be released",
                shouldReleaseTunnelAfterUnexpectedStop(true, state),
            )
        }
        // With protection off there is never a hold to preserve.
        for (state in VpnState.entries) {
            assertTrue(
                "state $state should be released with protection off",
                shouldReleaseTunnelAfterUnexpectedStop(false, state),
            )
        }
    }

    @Test
    fun `retry cleans non-idle vpn and connects only after idle`() {
        assertTrue(shouldRetryCleanup(VpnState.ERROR))
        assertTrue(shouldRetryCleanup(VpnState.STOPPING))
        assertTrue(shouldRetryCleanup(VpnState.RUNNING))
        assertFalse(shouldRetryCleanup(VpnState.IDLE))
    }

    @Test
    fun `notification stop reaches disconnected instead of error`() {
        val stopping = externalStopTransition(AppState.CONNECTED, VpnState.USER_STOPPING)
        assertEquals(AppState.DISCONNECTING, stopping)

        val stopped = externalStopTransition(stopping!!, VpnState.IDLE)
        assertEquals(AppState.DISCONNECTED, stopped)
    }

    @Test
    fun `notification cleanup failure leaves error instead of disconnecting`() {
        assertEquals(
            AppState.ERROR,
            externalVpnFailureTransition(AppState.DISCONNECTING, VpnState.ERROR),
        )
        assertEquals(
            null,
            externalVpnFailureTransition(AppState.CONNECTED, VpnState.ERROR),
        )
    }

    @Test
    fun `conflated user stop marker makes lone idle event disconnected`() {
        val stopped = externalStopTransition(
            appState = AppState.CONNECTED,
            vpnState = VpnState.IDLE,
            userStopRequested = true,
        )

        assertEquals(AppState.DISCONNECTED, stopped)
    }

    @Test
    fun `controlled reconfigure error is reserved for rollback`() {
        assertFalse(
            isUnexpectedVpnTerminal(
                appState = AppState.CONNECTED,
                vpnState = VpnState.ERROR,
                isSwitching = true,
            )
        )
        assertTrue(
            isUnexpectedVpnTerminal(
                appState = AppState.CONNECTED,
                vpnState = VpnState.ERROR,
                isSwitching = false,
            )
        )
    }

    @Test
    fun `stop failure remains error until vpn reports idle`() {
        assertEquals(AppState.ERROR, stopCompletionState(vpnIdle = false))
        assertEquals(AppState.DISCONNECTED, stopCompletionState(vpnIdle = true))
    }

    @Test
    fun `a running VPN is adopted only by a controller that thinks it is disconnected`() {
        // Always-on VPN and START_STICKY both bring the tunnel up before any
        // controller exists; the fresh controller starts at DISCONNECTED.
        assertTrue(shouldAdoptRunningVpn(AppState.DISCONNECTED, VpnState.RUNNING))

        // Every other app state already owns the tunnel or is mid-transition,
        // so adopting would overwrite a more accurate state.
        assertFalse(shouldAdoptRunningVpn(AppState.CONNECTING, VpnState.RUNNING))
        assertFalse(shouldAdoptRunningVpn(AppState.CONNECTED, VpnState.RUNNING))
        assertFalse(shouldAdoptRunningVpn(AppState.DISCONNECTING, VpnState.RUNNING))
        assertFalse(shouldAdoptRunningVpn(AppState.ERROR, VpnState.RUNNING))
        assertFalse(shouldAdoptRunningVpn(AppState.UNBOUND, VpnState.RUNNING))
    }

    @Test
    fun `a blocking hold the service recovered is adopted out of ERROR`() {
        // The network came back and the service redialled the core: the tunnel
        // forwards again, and ERROR is now the stale state.
        assertTrue(shouldAdoptRecoveredHold(AppState.ERROR, VpnState.RUNNING, holdObserved = true))

        // An ERROR the controller reached on its own stays ERROR.
        assertFalse(shouldAdoptRecoveredHold(AppState.ERROR, VpnState.RUNNING, holdObserved = false))

        // Only a RUNNING tunnel is a recovery, and only out of ERROR.
        for (state in VpnState.entries.filter { it != VpnState.RUNNING }) {
            assertFalse(shouldAdoptRecoveredHold(AppState.ERROR, state, holdObserved = true))
        }
        for (app in AppState.entries.filter { it != AppState.ERROR }) {
            assertFalse(shouldAdoptRecoveredHold(app, VpnState.RUNNING, holdObserved = true))
        }
    }

    @Test
    fun `adoption never fires for a VPN that is not running`() {
        for (state in VpnState.entries.filter { it != VpnState.RUNNING }) {
            assertFalse(shouldAdoptRunningVpn(AppState.DISCONNECTED, state))
        }
    }

    private fun beat(
        vpnState: VpnState = VpnState.RUNNING,
        userPresent: Boolean = false,
        quietTicks: Int = 0,
    ): Long = heartbeatIntervalMillis(
        vpnState = vpnState,
        userPresent = userPresent,
        quietTicks = quietTicks,
        activeIntervalMs = ACTIVE,
        idleIntervalMs = IDLE,
        maxQuietIntervalMs = MAX_QUIET,
    )

    @Test
    fun `a user who is present gets the revocation bound interval`() {
        // 亮屏或 App 在前台 = 有人在用这条隧道。这时周期与 Windows 端一致。
        assertEquals(ACTIVE, beat(userPresent = true))
        assertEquals(ACTIVE, beat(VpnState.STARTING, userPresent = true))
        // 退避计数器不应该盖过「用户在场」。
        assertEquals(ACTIVE, beat(userPresent = true, quietTicks = 99))
    }

    @Test
    fun `a locked idle phone backs off by doubling up to the cap`() {
        assertEquals(60_000L, beat(quietTicks = 0))
        assertEquals(120_000L, beat(quietTicks = 1))
        assertEquals(240_000L, beat(quietTicks = 2))
        assertEquals(480_000L, beat(quietTicks = 3))
        assertEquals(MAX_QUIET, beat(quietTicks = 4))
        assertEquals(MAX_QUIET, beat(quietTicks = 5))
    }

    @Test
    fun `the backoff never collapses however long the phone stays quiet`() {
        // quietTicks 是个一直在涨的计数器。没有溢出闸的话 `shl 64` 会绕回 0，
        // 把「省电退避」变成一个每 0 毫秒打一次服务器的忙等循环。
        for (ticks in listOf(16, 31, 32, 63, 64, 65, Int.MAX_VALUE)) {
            assertEquals("ticks $ticks", MAX_QUIET, beat(quietTicks = ticks))
        }
        // 负值同理。
        assertEquals(ACTIVE, beat(quietTicks = -1))
    }

    @Test
    fun `a tunnel that cannot forward uses the idle interval`() {
        // 一个字节也转发不出去，撤销延迟无从谈起。ERROR 也在此列：断网保护
        // 那种「留着接口、没有核心」的 hold 是在丢弃流量，不是在转发。
        for (state in listOf(
            VpnState.IDLE,
            VpnState.STOPPING,
            VpnState.USER_STOPPING,
            VpnState.ERROR,
        )) {
            assertEquals("state $state", IDLE, beat(vpnState = state, userPresent = true))
            assertEquals("state $state", IDLE, beat(vpnState = state, userPresent = false))
        }
    }

    @Test
    fun `every vpn state is assigned a positive interval`() {
        for (state in VpnState.entries) {
            for (present in listOf(true, false)) {
                assertTrue("state $state", beat(vpnState = state, userPresent = present) > 0L)
            }
        }
    }

    @Test
    fun `forwarding covers exactly the two states that can carry traffic`() {
        assertTrue(isForwarding(VpnState.RUNNING))
        assertTrue(isForwarding(VpnState.STARTING))
        for (state in VpnState.entries.filter { it != VpnState.RUNNING && it != VpnState.STARTING }) {
            assertFalse("state $state", isForwarding(state))
        }
    }

    private companion object {
        const val ACTIVE = 60_000L
        const val IDLE = 300_000L
        const val MAX_QUIET = 600_000L
    }
}
