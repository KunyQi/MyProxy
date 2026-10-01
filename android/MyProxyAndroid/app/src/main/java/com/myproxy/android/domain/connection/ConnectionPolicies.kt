package com.myproxy.android.domain.connection

import com.myproxy.android.domain.model.AppState
import com.myproxy.android.vpn.VpnState

internal fun canSelectProxyMode(state: AppState): Boolean =
    state == AppState.DISCONNECTED || state == AppState.CONNECTED || state == AppState.ERROR

internal fun canStartConnection(
    appState: AppState,
    vpnState: VpnState,
    rebindInProgress: Boolean,
): Boolean =
    !rebindInProgress &&
        vpnState == VpnState.IDLE &&
        (appState == AppState.DISCONNECTED || appState == AppState.ERROR)

/** Retry must clean a non-idle VPN before a new start can be accepted. */
internal fun shouldRetryCleanup(vpnState: VpnState): Boolean = vpnState != VpnState.IDLE

/**
 * The tunnel can already be up before any controller exists: always-on VPN
 * starts the service at boot and START_STICKY restarts it after a process
 * kill. A fresh controller must adopt that tunnel, otherwise the UI reports
 * "disconnected" while traffic is actually flowing through it.
 */
internal fun shouldAdoptRunningVpn(appState: AppState, vpnState: VpnState): Boolean =
    vpnState == VpnState.RUNNING && appState == AppState.DISCONNECTED

/**
 * The service recovered a blocking hold (kill switch) on its own: the network
 * came back and it redialled the core. The controller has been showing ERROR
 * since the hold began, and nothing else moves it out of ERROR -- so the main
 * page, the tile and the orb kept saying "connection failed" over a tunnel
 * that was forwarding traffic, new configs were skipped (they need
 * CONNECTED), and a mode switch only wrote the setting.
 *
 * Adoption is limited to that case. An ERROR the controller reached on its
 * own (a failed connect or config change) stays ERROR: that is the more
 * accurate state, and a RUNNING seen afterwards is not a recovery.
 */
internal fun shouldAdoptRecoveredHold(
    appState: AppState,
    vpnState: VpnState,
    holdObserved: Boolean,
): Boolean = holdObserved && appState == AppState.ERROR && vpnState == VpnState.RUNNING

internal fun externalStopTransition(
    appState: AppState,
    vpnState: VpnState,
    userStopRequested: Boolean = false,
): AppState? = when {
    vpnState == VpnState.USER_STOPPING &&
        (appState == AppState.CONNECTING || appState == AppState.CONNECTED) -> AppState.DISCONNECTING

    vpnState == VpnState.IDLE && (
        appState == AppState.DISCONNECTING ||
            (userStopRequested && appState != AppState.UNBOUND)
        ) -> AppState.DISCONNECTED

    else -> null
}

internal fun externalVpnFailureTransition(
    appState: AppState,
    vpnState: VpnState,
): AppState? =
    if (vpnState == VpnState.ERROR && appState == AppState.DISCONNECTING) {
        AppState.ERROR
    } else {
        null
    }

internal fun isUnexpectedVpnTerminal(
    appState: AppState,
    vpnState: VpnState,
    isSwitching: Boolean,
): Boolean =
    !isSwitching &&
        (appState == AppState.CONNECTING || appState == AppState.CONNECTED) &&
        (vpnState == VpnState.ERROR || vpnState == VpnState.IDLE)

/**
 * Whether the controller should send a stop command after noticing the tunnel
 * died under it.
 *
 * With the kill switch on, the service answers an unexpected failure by
 * holding a blocking TUN interface and publishing [VpnState.ERROR]. A stop
 * command would undo that hold, so the controller leaves it alone and only
 * surfaces the error; releasing it stays an explicit user action (retry or
 * stop). Any other state means no hold exists and the tunnel still needs
 * cleaning up.
 */
internal fun shouldReleaseTunnelAfterUnexpectedStop(
    killSwitchEnabled: Boolean,
    vpnState: VpnState,
): Boolean = !(killSwitchEnabled && vpnState == VpnState.ERROR)

/** A stop may only finalize the app state after the service reports IDLE. */
internal fun stopCompletionState(vpnIdle: Boolean): AppState =
    if (vpnIdle) AppState.DISCONNECTED else AppState.ERROR

/** 隧道是否正在（或即将）转发流量。 */
internal fun isForwarding(vpnState: VpnState): Boolean =
    vpnState == VpnState.STARTING || vpnState == VpnState.RUNNING

/**
 * 心跳周期：**事件驱动优先，固定轮询其次**。
 *
 * 轮询只是兜底。真正让控制面保持新鲜的是事件——App 回到前台、默认网络变化、
 * 隧道刚建立，这三件事都会立刻触发一次心跳并把 [quietTicks] 清零
 * （见 `ConnectionController.requestImmediateHeartbeat`）。所以这里的周期回答的
 * 是「在什么都没发生的情况下，最长可以多久不问一次」。
 *
 * 三档：
 *
 * - **隧道没起来**：[idleIntervalMs]。一个字节也转发不出去，心跳唯一还能做的事
 *   是把「凭据已被吊销」尽早变成绑定页，而那并不紧急。
 * - **用户在场**（亮屏或 App 在前台）：[activeIntervalMs]，与 Windows 端一致。
 * - **锁屏且无人看**：从 [activeIntervalMs] 起逐次翻倍，封顶 [maxQuietIntervalMs]。
 *   60s → 120 → 240 → 480 → 600 → 600…
 *
 * **代价要写明白**：心跳周期同时也是「一个被撤销的设备还能转发多久流量」的上界。
 * 锁屏静置时这个上界从 60 秒放宽到 10 分钟。可接受的理由是它**只在没有任何事件
 * 发生时**才退到那么远——用户一碰手机、一换网络，下一次心跳立刻发生；而真正的
 * 长时间静置意味着几乎没有流量在走，也就没有多少可被滥用的转发。
 * 不接受这个代价的话，把 [maxQuietIntervalMs] 调成 [activeIntervalMs] 即可退回旧行为。
 */
internal fun heartbeatIntervalMillis(
    vpnState: VpnState,
    userPresent: Boolean,
    quietTicks: Int,
    activeIntervalMs: Long,
    idleIntervalMs: Long,
    maxQuietIntervalMs: Long,
): Long {
    if (!isForwarding(vpnState)) return idleIntervalMs
    if (userPresent) return activeIntervalMs
    // coerceIn 是溢出闸：quietTicks 是个一直在涨的计数器，
    // 没有上限的 shl 会在第 64 次变成 0，把退避变成忙等。
    val scaled = activeIntervalMs shl quietTicks.coerceIn(0, MAX_QUIET_SHIFT)
    return scaled.coerceAtMost(maxQuietIntervalMs).coerceAtLeast(activeIntervalMs)
}

private const val MAX_QUIET_SHIFT = 16
