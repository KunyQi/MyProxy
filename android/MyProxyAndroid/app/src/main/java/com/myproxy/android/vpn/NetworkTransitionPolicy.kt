package com.myproxy.android.vpn

/**
 * Whether a change of default network should make the service restart the
 * core on the existing TUN interface.
 *
 * Moving between Wi-Fi and mobile data does not tell the core anything: its
 * TCP connections to the server were bound to an interface that is gone, and
 * they do not fail fast -- they sit there until a retransmission timeout,
 * which is the stall a user reads as "the VPN is broken after I left the
 * house". Restarting the core makes it dial out again over the new network,
 * and because the TUN file descriptor is untouched there is no window where
 * routes are missing and no second VPN-permission prompt.
 *
 * [previousNetworkId] is `null` only before any network has been seen, which
 * is the network the tunnel was started on -- nothing to migrate from. It is
 * deliberately *not* cleared when a network is lost: losing Wi-Fi and getting
 * mobile data is exactly the case that must migrate.
 */
internal fun shouldRestartCoreForNetwork(
    previousNetworkId: Long?,
    newNetworkId: Long,
    vpnState: VpnState,
    cleanupStarted: Boolean,
    reconfigureInProgress: Boolean,
): Boolean =
    !cleanupStarted &&
        !reconfigureInProgress &&
        vpnState == VpnState.RUNNING &&
        previousNetworkId != null &&
        previousNetworkId != newNetworkId

/**
 * Whether a core-reported error should be left to whoever is already
 * recovering from it instead of tearing the tunnel down.
 *
 * A reconfigure is owned by the controller, which holds the previous verified
 * profile and will issue a rollback. A roam and a health-audit self-heal are
 * owned by the service itself, which retries and only then gives up. In every
 * case the error is an expected step of a recovery already in flight, so
 * reacting to it would destroy a tunnel that is about to come back.
 */
internal fun shouldDeferXrayError(
    reconfigureInProgress: Boolean,
    roamInProgress: Boolean,
    selfHealInProgress: Boolean,
): Boolean = reconfigureInProgress || roamInProgress || selfHealInProgress

/**
 * 重拨第 [attempt] 次（0 起）之前的退避：**指数**，1s → 2s → 4s → 8s → 16s…，
 * 封顶 [maxDelayMillis]。
 *
 * 先前是线性的（base、2×base、3×base）。差别全在没信号的时候：线性退避会在一条
 * 已经不通的链路上以几乎恒定的频率反复拨号，而每一次失败的拨号都是一次真实的
 * CPU 唤醒加一次无线电唤醒。指数退避让这个代价随失败次数掉下去。
 *
 * 另一半在调用处，比这里更重要：**没有默认网络时一次都不拨**，直接等
 * `onAvailable`（见 [shouldRedialOnNetworkAvailable]）。退避是给「有网但拨不通」
 * 准备的，不是给「没网」准备的——对「没网」，正确的重试次数是零。
 */
internal fun redialRetryDelayMillis(
    attempt: Int,
    baseDelayMillis: Long,
    maxDelayMillis: Long = Long.MAX_VALUE,
): Long =
    (baseDelayMillis shl attempt.coerceIn(0, MAX_BACKOFF_SHIFT)).coerceAtMost(maxDelayMillis)

/** 溢出闸：`shl 64` 会绕回 0，把退避变成忙等。 */
private const val MAX_BACKOFF_SHIFT = 16

/**
 * 网络回来了，要不要在已经建好的接口上重新拨一次核心。
 *
 * 这条只服务一种局面：断网保护正握着一个「接口还在、核心已死」的 hold。
 * 那种状态下流量被丢弃（这是对的，不泄漏），但网络一旦恢复，用户要的显然是
 * 隧道自己回来，而不是等他发现「怎么还连不上」再去点重试。
 *
 * **它不释放 hold**，只是尝试把核心装回去：成功就回到 RUNNING，失败就继续握着。
 * 「放开阻断」仍然只能由用户显式触发——那条不变量没有被动过。
 */
internal fun shouldRedialOnNetworkAvailable(
    vpnState: VpnState,
    blockingHoldActive: Boolean,
    hasActiveConfig: Boolean,
    tunnelInterfaceUp: Boolean,
    cleanupStarted: Boolean,
): Boolean =
    !cleanupStarted &&
        blockingHoldActive &&
        hasActiveConfig &&
        tunnelInterfaceUp &&
        vpnState == VpnState.ERROR
