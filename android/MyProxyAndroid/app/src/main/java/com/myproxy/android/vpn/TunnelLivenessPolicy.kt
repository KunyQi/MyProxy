package com.myproxy.android.vpn

/**
 * 「界面上写的状态」与「实际发生的事」是否还对得上。
 *
 * 存在的理由只有一条：**不出现假 Connected。**
 *
 * 核心的死亡通常会经由 `CoreCallbackHandler` 报上来，于是 `XrayState.ERROR`
 * 会把隧道带进错误态。但那条通路不是必然的——Go 侧 panic 被吞掉、厂商 ROM 把
 * 进程冻结后又解冻、native 线程被杀，都可能让核心已经不在了而回调从未触发。
 * 那时 [VpnState.RUNNING] 会一直挂着，界面一直显示「已连接」，而用户的流量
 * 其实一个字节都出不去。这是最难被发现的一类故障：它看起来完全正常。
 *
 * 所以状态要定期与事实对账，而不是只等着被通知。
 *
 * [coreRunning] 读不到时必须按 `true` 传入。一个读不出来的存活性答案不等于
 * 「已经死了」，把它当死的会去拆一条正在好好工作的隧道——对账的方向错了，
 * 造成的破坏比它要修的问题更大。
 */
internal fun isTunnelStateHonest(
    vpnState: VpnState,
    coreRunning: Boolean,
    tunnelInterfaceUp: Boolean,
): Boolean =
    vpnState != VpnState.RUNNING || (coreRunning && tunnelInterfaceUp)
