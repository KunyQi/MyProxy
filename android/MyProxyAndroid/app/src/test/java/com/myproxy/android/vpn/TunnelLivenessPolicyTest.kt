package com.myproxy.android.vpn

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TunnelLivenessPolicyTest {

    @Test
    fun `a running tunnel must have both a core and an interface`() {
        assertTrue(isTunnelStateHonest(VpnState.RUNNING, coreRunning = true, tunnelInterfaceUp = true))
    }

    @Test
    fun `a running tunnel with a dead core is a false Connected`() {
        // 这正是这条策略存在的原因：核心没了而回调没报上来，界面会一直写着
        // 「已连接」，用户的流量其实一个字节都出不去。
        assertFalse(isTunnelStateHonest(VpnState.RUNNING, coreRunning = false, tunnelInterfaceUp = true))
        assertFalse(isTunnelStateHonest(VpnState.RUNNING, coreRunning = true, tunnelInterfaceUp = false))
        assertFalse(isTunnelStateHonest(VpnState.RUNNING, coreRunning = false, tunnelInterfaceUp = false))
    }

    @Test
    fun `no other state makes a liveness claim to check`() {
        // 只有 RUNNING 对外宣称「现在通着」。其余状态本来就没在保证什么，
        // 拿存活性去对账它们只会在正常的启动与停止过程中误报。
        for (state in VpnState.entries.filter { it != VpnState.RUNNING }) {
            assertTrue(
                "state $state",
                isTunnelStateHonest(state, coreRunning = false, tunnelInterfaceUp = false),
            )
        }
    }
}
