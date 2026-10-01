package com.myproxy.android.domain.connection

import android.content.Context
import android.content.Intent
import android.os.PowerManager
import com.myproxy.android.data.api.ApiException
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.storage.CredentialUnreadableException
import com.myproxy.android.data.storage.SettingsDataStore
import com.myproxy.android.domain.config.ConfigRepository
import com.myproxy.android.domain.config.ConfigResult
import com.myproxy.android.domain.config.HeartbeatResult
import com.myproxy.android.domain.model.AppState
import com.myproxy.android.domain.model.ConfigCacheEntry
import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.domain.model.ServerProfile
import com.myproxy.android.logging.AndroidAppLogger
import com.myproxy.android.logging.AppLogger
import com.myproxy.android.vpn.VpnController
import com.myproxy.android.vpn.VpnState
import com.myproxy.android.xray.ConnectivityProbe
import com.myproxy.android.vpn.VpnStateHolder
import com.myproxy.android.xray.XrayConfigGenerator
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

class ConnectionController(
    private val context: Context,
    private val configRepository: ConfigRepository,
    private val settingsDataStore: SettingsDataStore,
    private val vpnController: VpnController,
    private val scope: CoroutineScope,
    private val logger: AppLogger = AndroidAppLogger(),
    private val connectivityProbe: ConnectivityProbe = ConnectivityProbe(),
    private val heartbeatIntervalMs: Long = HEARTBEAT_INTERVAL_MS,
) {
    init {
        require(heartbeatIntervalMs > 0) { "heartbeat interval must be positive" }
    }

    private val _state = MutableStateFlow(AppState.DISCONNECTED)
    val state: StateFlow<AppState> = _state.asStateFlow()

    private val _mode = MutableStateFlow(ProxyMode.RULE)
    val mode: StateFlow<ProxyMode> = _mode.asStateFlow()

    private val _latencyMs = MutableStateFlow<Int?>(null)
    val latencyMs: StateFlow<Int?> = _latencyMs.asStateFlow()

    private var recheckInProgress = false

    private val _vpnPermissionRequest = MutableStateFlow<Intent?>(null)
    val vpnPermissionRequest: StateFlow<Intent?> = _vpnPermissionRequest.asStateFlow()

    private val _isSwitching = MutableStateFlow(false)
    val isSwitching: StateFlow<Boolean> = _isSwitching.asStateFlow()

    /**
     * True while the service is holding a blocking tunnel after an unexpected
     * failure. The app state is [AppState.ERROR] either way; this is what
     * lets the UI say "traffic is blocked" instead of just "failed".
     */
    val isTrafficBlocked: StateFlow<Boolean> = VpnStateHolder.blockingHold

    private val _isAppInForeground = MutableStateFlow(false)

    /**
     * 「现在就去问一次控制面」。
     *
     * 用 conflated Channel 而**不是** SharedFlow：replay = 0 的 SharedFlow 在
     * 没有收集者的瞬间会把发射直接丢掉，而这个循环大部分时间正卡在
     * `synchronizeControlPlane()` 里（一次网络往返）——恰好是事件最可能到来的
     * 那段时间。那样丢掉一个唤醒，就意味着退回到那条可能长达十分钟的定时器上。
     *
     * Channel 不管有没有人在收都会留住最后一个值；conflated 则保证「连着来三个
     * 事件」和「来一个事件」触发的是同一次同步。
     */
    private val heartbeatWake = Channel<Unit>(Channel.CONFLATED)

    @Volatile
    private var killSwitchEnabled = false

    @Volatile
    private var expectingStop = false

    @Volatile
    private var externalStopInProgress = false

    @Volatile
    private var unexpectedVpnStopHandled = false

    @Volatile
    private var rebindInProgress = false

    /**
     * Set when the VPN reported ERROR while the service was holding the
     * tunnel blocked; see [shouldAdoptRecoveredHold]. The service raises the
     * hold before publishing ERROR, so it is visible at that moment.
     */
    private var blockingHoldObserved = false

    @Volatile
    private var operationEpoch = 0L

    init {
        scope.launch {
            settingsDataStore.proxyMode.collect { savedMode ->
                if (!_isSwitching.value && _state.value != AppState.CONNECTING) {
                    _mode.value = savedMode
                }
            }
        }
        scope.launch {
            VpnStateHolder.state.collect { vpnState ->
                handleVpnStateChange(vpnState)
            }
        }
        scope.launch {
            settingsDataStore.killSwitch.collect { enabled -> killSwitchEnabled = enabled }
        }
        scope.launch {
            // 切网之后控制面的答案很可能已经变了，立刻问一次，
            // 而不是等退避里那个可能长达十分钟的定时器。
            VpnStateHolder.networkChanges.collect {
                requestImmediateHeartbeat("the default network changed")
            }
        }
        scope.launch {
            delay(HEARTBEAT_INITIAL_DELAY_MS)
            // 静置轮数。任何事件都会把它清零，于是「退到十分钟」只发生在
            // 真的十分钟里什么都没发生的时候。
            var quietTicks = 0
            while (currentCoroutineContext().isActive) {
                synchronizeControlPlane()
                val userPresent = isUserPresent()
                val interval = heartbeatIntervalMillis(
                    vpnState = VpnStateHolder.state.value,
                    userPresent = userPresent,
                    quietTicks = quietTicks,
                    activeIntervalMs = heartbeatIntervalMs,
                    idleIntervalMs = HEARTBEAT_IDLE_INTERVAL_MS,
                    maxQuietIntervalMs = HEARTBEAT_MAX_QUIET_INTERVAL_MS,
                )
                // 睡到超时、或被事件叫醒，以先到者为准。这就是「事件驱动优先，
                // 固定轮询其次」在代码里的样子：轮询只是这个 await 的上界。
                val wokenByEvent = withTimeoutOrNull(interval) { heartbeatWake.receive() } != null
                quietTicks = if (wokenByEvent || userPresent) 0 else quietTicks + 1
                if (wokenByEvent) {
                    // 事件驱动不等于「来一个事件就打一次服务器」。事件常常成串到来
                    // ——网络抖动就是一串 onAvailable，切前后台也可能连着好几次。
                    // 这个地板把一串事件合并成一次同步，顺便挡住任何把心跳变成
                    // 忙等的外部抖动。
                    delay(HEARTBEAT_EVENT_COALESCE_MS)
                }
            }
        }
    }

    /**
     * App 是否回到了前台。
     *
     * 前台本身不再决定心跳周期——「App 在后台」从来都不等于「隧道停了」，
     * 用它做退避依据会让一个被撤销的设备在用户锁屏用网时继续转发。
     * 它现在的作用有两个：作为 [isUserPresent] 的一半，以及作为一个**事件**。
     */
    fun onAppForegroundChanged(inForeground: Boolean) {
        _isAppInForeground.value = inForeground
        if (inForeground) requestImmediateHeartbeat("the app came to the foreground")
    }

    /**
     * 立刻跑一次控制面同步，并把退避清零。
     *
     * 只是叫醒那个循环而不是自己发请求：同步逻辑里有 epoch 判断和 401 处理，
     * 两个并发的实例会互相踩。
     */
    fun requestImmediateHeartbeat(reason: String) {
        if (heartbeatWake.trySend(Unit).isSuccess) logger.i(TAG, "heartbeat requested: $reason")
    }

    /**
     * 有没有人在用这台手机。
     *
     * 读不到就按「在场」算：那一侧是心跳更勤、撤销延迟更短，
     * 猜错的代价只是一点电量；反过来猜错的代价是安全性质的。
     */
    private fun isUserPresent(): Boolean =
        _isAppInForeground.value ||
            runCatching {
                (context.getSystemService(Context.POWER_SERVICE) as PowerManager).isInteractive
            }.getOrDefault(true)

    fun connect() {
        val current = _state.value
        if (!canStartConnection(current, VpnStateHolder.state.value, rebindInProgress)) return

        unexpectedVpnStopHandled = false
        externalStopInProgress = false
        _state.value = AppState.CONNECTING
        _latencyMs.value = null

        val permissionIntent = vpnController.prepare(context)
        if (permissionIntent != null) {
            _vpnPermissionRequest.value = permissionIntent
        } else {
            connectWithPermissionGranted()
        }
    }

    fun connectWithPermissionGranted() {
        unexpectedVpnStopHandled = false
        val epoch = operationEpoch
        scope.launch {
            if (_state.value != AppState.CONNECTING || !isCurrent(epoch)) return@launch
            try {
                val result = configRepository.getConfig()
                if (!isCurrent(epoch) || _state.value != AppState.CONNECTING) return@launch
                val entry = when (result) {
                    is ConfigResult.Success -> result.entry
                    ConfigResult.NoCredential -> {
                        handleMissingCredential()
                        return@launch
                    }
                    ConfigResult.NoValidConfig -> throw IllegalStateException("no valid config")
                }
                val profile = entry.profile
                    ?: throw IllegalStateException("cached config has no profile")
                val requestedMode = _mode.value
                val configJson = XrayConfigGenerator.generate(profile, requestedMode)

                vpnController.startVpn(context, configJson, requestedMode)
                awaitVpnRunning()
                if (!isCurrent(epoch) || _state.value != AppState.CONNECTING) return@launch

                val latency = connectivityProbe.measure()
                if (!isCurrent(epoch) || _state.value != AppState.CONNECTING) return@launch

                if (!entry.verified) {
                    configRepository.promoteCurrentConfig(profile, entry.configVersion ?: 0)
                }
                _latencyMs.value = latency
                _state.value = AppState.CONNECTED
                // 刚建立的隧道要尽快对一次账：配置版本、以及这台设备是不是
                // 在离线期间已经被撤销了。
                requestImmediateHeartbeat("the tunnel just came up")
            } catch (e: CancellationException) {
                runCatching { vpnController.stopVpn(context) }
                _latencyMs.value = null
                if (isCurrent(epoch)) _state.value = AppState.ERROR
                throw e
            } catch (e: ApiException) {
                if (e.isCredentialRejected()) {
                    handleRejectedCredential(e.rejectedCredentialToken)
                } else if (isCurrent(epoch)) {
                    failConnect(e)
                }
            } catch (t: Throwable) {
                if (isCurrent(epoch)) failConnect(t)
            }
        }
    }

    fun stop() {
        val current = _state.value
        if (current != AppState.CONNECTED && current != AppState.ERROR && current != AppState.CONNECTING) {
            return
        }
        operationEpoch += 1
        scope.launch { stopInternal() }
    }

    /**
     * An error may be reported while the service still owns a TUN/core.
     * Retry cleanup first in that case; starting immediately would be
     * rejected by [canStartConnection] and could race an old STOP command.
     */
    fun retry() {
        if (!shouldRetryCleanup(VpnStateHolder.state.value)) {
            connect()
            return
        }
        operationEpoch += 1
        scope.launch {
            // Reconnect once the tunnel is confirmed gone. Without this a
            // retry only cleaned up and left the user to press again -- and a
            // kill-switch hold, which is never idle, always took that path.
            if (stopInternal()) connect()
        }
    }

    fun bindingCompleted() {
        operationEpoch += 1
        rebindInProgress = false
        unexpectedVpnStopHandled = false
        externalStopInProgress = false
        _latencyMs.value = null
        _state.value = AppState.DISCONNECTED
    }

    fun switchMode(mode: ProxyMode) {
        if (mode == _mode.value || _isSwitching.value || rebindInProgress) return
        if (!canSelectProxyMode(_state.value)) return
        when (_state.value) {
            AppState.CONNECTING,
            AppState.DISCONNECTING,
            AppState.UNBOUND,
            -> return

            AppState.DISCONNECTED,
            AppState.ERROR,
            -> {
                _mode.value = mode
                scope.launch { settingsDataStore.setProxyMode(mode) }
                return
            }

            AppState.CONNECTED -> Unit
        }

        val oldMode = _mode.value
        val epoch = operationEpoch
        _mode.value = mode
        _isSwitching.value = true
        scope.launch {
            var rollbackEntry: ConfigCacheEntry? = null
            try {
                rollbackEntry = configRepository.lastKnownGood()
                    ?: throw IllegalStateException("no verified config available for rollback")
                val result = loadConfigResult()
                if (!isCurrent(epoch) || _state.value != AppState.CONNECTED) return@launch
                val entry = result.entry
                val profile = entry.profile
                    ?: throw IllegalStateException("cached config has no profile")
                val latency = reconfigureAndVerify(profile, mode)
                if (!isCurrent(epoch) || _state.value != AppState.CONNECTED) return@launch

                if (!entry.verified) {
                    configRepository.promoteCurrentConfig(profile, entry.configVersion ?: 0)
                }
                _latencyMs.value = latency
                settingsDataStore.setProxyMode(mode)
            } catch (e: CancellationException) {
                _mode.value = oldMode
                throw e
            } catch (e: ApiException) {
                _mode.value = oldMode
                if (e.isCredentialRejected()) {
                    handleRejectedCredential(e.rejectedCredentialToken)
                } else if (isCurrent(epoch) && _state.value == AppState.CONNECTED) {
                    rollbackAfterFailure(rollbackEntry, oldMode, e)
                }
            } catch (t: Throwable) {
                _mode.value = oldMode
                if (isCurrent(epoch) && _state.value == AppState.CONNECTED) {
                    rollbackAfterFailure(rollbackEntry, oldMode, t)
                }
            } finally {
                _isSwitching.value = false
            }
        }
    }

    fun rebind(onComplete: (() -> Unit)? = null) {
        if (rebindInProgress) return
        if (_state.value == AppState.UNBOUND) {
            onComplete?.invoke()
            return
        }
        rebindInProgress = true
        operationEpoch += 1
        scope.launch {
            try {
                if (_state.value != AppState.DISCONNECTED || VpnStateHolder.state.value != VpnState.IDLE) {
                    if (!stopInternal()) return@launch
                }
                configRepository.clearBinding()
                _latencyMs.value = null
                _state.value = AppState.UNBOUND
                onComplete?.invoke()
            } catch (t: Throwable) {
                logger.e(TAG, "rebind failed", t)
                _latencyMs.value = null
                _state.value = AppState.ERROR
            } finally {
                rebindInProgress = false
            }
        }
    }

    fun consumeVpnPermissionRequest() {
        _vpnPermissionRequest.value = null
    }

    /**
     * 手动「检查连接」：在已连接的隧道上重跑连通性探针并刷新延迟显示。
     * 只在 CONNECTED 时有意义——其余状态静默返回，按钮本身也只在已连接时出现。
     * 探针失败只记日志、保留旧读数：手动检查不是状态机事件，一次网络抖动不该
     * 把一条正在好好工作的隧道判成错误。
     */
    fun checkConnection() {
        if (_state.value != AppState.CONNECTED || recheckInProgress) return
        recheckInProgress = true
        scope.launch {
            try {
                // 所有备用地址共享十秒预算；沿用启动检测已经建立的连接池。
                val latency = connectivityProbe.measure(budgetMs = 10_000L)
                if (_state.value == AppState.CONNECTED) {
                    _latencyMs.value = latency
                }
            } catch (t: Throwable) {
                logger.w(TAG, "manual connectivity check failed: " + t.javaClass.simpleName + ": " + t.message)
            } finally {
                recheckInProgress = false
            }
        }
    }

    fun vpnPermissionDenied() {
        if (_state.value == AppState.CONNECTING) {
            _state.value = AppState.ERROR
            _latencyMs.value = null
        }
    }

    private fun handleVpnStateChange(vpnState: VpnState) {
        when (vpnState) {
            VpnState.ERROR -> if (VpnStateHolder.blockingHold.value) blockingHoldObserved = true
            VpnState.IDLE -> blockingHoldObserved = false
            else -> Unit
        }
        if (!expectingStop && !_isSwitching.value && !rebindInProgress &&
            shouldAdoptRecoveredHold(_state.value, vpnState, blockingHoldObserved)
        ) {
            blockingHoldObserved = false
            logger.i(TAG, "the service recovered a blocking hold; adopting the tunnel")
            unexpectedVpnStopHandled = false
            _state.value = AppState.CONNECTED
            // The control plane may have moved on while traffic was held.
            requestImmediateHeartbeat("recovered from a blocking hold")
            return
        }
        if (!expectingStop && !_isSwitching.value &&
            shouldAdoptRunningVpn(_state.value, vpnState)
        ) {
            logger.i(TAG, "adopting a VPN that was already running")
            _state.value = AppState.CONNECTED
            // 接管的是一条在本控制器出生之前就在跑的隧道（always-on、
            // START_STICKY 恢复、开机恢复）。它已经转发了多久、期间控制面
            // 发生过什么，这里一概不知道，所以立刻问。
            requestImmediateHeartbeat("adopted a tunnel that was already running")
            return
        }
        val userStopRequested = vpnState == VpnState.IDLE &&
            VpnStateHolder.consumeUserStopRequested()
        val externalTransition = externalStopTransition(
            appState = _state.value,
            vpnState = vpnState,
            userStopRequested = userStopRequested,
        )
        if (externalTransition == AppState.DISCONNECTING && !expectingStop) {
            externalStopInProgress = true
            _state.value = externalTransition
            return
        }
        if (externalTransition == AppState.DISCONNECTED &&
            (externalStopInProgress || userStopRequested) &&
            !expectingStop
        ) {
            externalStopInProgress = false
            unexpectedVpnStopHandled = false
            _latencyMs.value = null
            if (_state.value != AppState.UNBOUND) _state.value = AppState.DISCONNECTED
            return
        }
        if (expectingStop) return

        externalVpnFailureTransition(_state.value, vpnState)?.let { failureState ->
            externalStopInProgress = false
            unexpectedVpnStopHandled = false
            _latencyMs.value = null
            _state.value = failureState
            return
        }

        if (isUnexpectedVpnTerminal(_state.value, vpnState, _isSwitching.value)) {
            handleUnexpectedVpnStop()
        }
    }

    private fun handleUnexpectedVpnStop() {
        if (unexpectedVpnStopHandled) return
        unexpectedVpnStopHandled = true
        logger.w(TAG, "VPN stopped unexpectedly")
        scope.launch {
            if (shouldReleaseTunnelAfterUnexpectedStop(
                    killSwitchEnabled = killSwitchEnabled,
                    vpnState = VpnStateHolder.state.value,
                )
            ) {
                runCatching { vpnController.stopVpn(context) }
            } else {
                logger.w(TAG, "leaving the blocking tunnel in place; release is explicit")
            }
            _latencyMs.value = null
            _state.value = AppState.ERROR
        }
    }

    private suspend fun stopInternal(expectedEpoch: Long? = null): Boolean {
        // A stale app state must not prevent cleanup, but an already-idle VPN
        // does not need another fire-and-forget service command. This also
        // lets a 401 observed before TUN establishment safely reach UNBOUND.
        if (expectedEpoch != null && !isCurrent(expectedEpoch)) return false
        if (VpnStateHolder.state.value == VpnState.IDLE) {
            if (_state.value != AppState.UNBOUND) _state.value = AppState.DISCONNECTED
            _latencyMs.value = null
            return true
        }
        expectingStop = true
        externalStopInProgress = false
        try {
            if (expectedEpoch != null && !isCurrent(expectedEpoch)) return false
            _state.value = AppState.DISCONNECTING
            VpnStateHolder.setState(VpnState.STOPPING)
            val requestFailure = runCatching { vpnController.stopVpn(context) }.exceptionOrNull()
            val vpnIdle = awaitVpnIdle()
            if (expectedEpoch != null && !isCurrent(expectedEpoch)) return false
            val stopped = requestFailure == null && vpnIdle
            _state.value = stopCompletionState(stopped)
            _latencyMs.value = null
            if (!stopped) {
                logger.e(TAG, "VPN stop did not reach idle")
            }
            return stopped
        } finally {
            expectingStop = false
        }
    }

    private suspend fun synchronizeControlPlane() {
        if (rebindInProgress) return
        val epoch = operationEpoch
        try {
            when (val heartbeat = configRepository.heartbeat()) {
                HeartbeatResult.NoCredential -> {
                    if (isCurrent(epoch) && _state.value != AppState.UNBOUND) {
                        handleMissingCredential()
                    }
                    return
                }
                is HeartbeatResult.Success -> {
                    if (!isCurrent(epoch)) return
                    applyNewConfigVersion(heartbeat.configVersion, epoch)
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException) {
            if (e.isCredentialRejected()) {
                handleRejectedCredential(e.rejectedCredentialToken)
            } else {
                logger.w(TAG, "heartbeat unavailable")
            }
        } catch (_: Throwable) {
            logger.w(TAG, "heartbeat unavailable")
        }
    }

    private suspend fun applyNewConfigVersion(remoteVersion: Int, epoch: Long) {
        val oldEntry = configRepository.lastKnownGood() ?: return
        val currentVersion = oldEntry.configVersion ?: 0
        if (remoteVersion <= currentVersion || _state.value != AppState.CONNECTED || _isSwitching.value) {
            return
        }

        val currentMode = _mode.value
        _isSwitching.value = true
        try {
            val result = loadConfigResult()
            if (!isCurrent(epoch) || _state.value != AppState.CONNECTED) return
            val entry = result.entry
            if ((entry.configVersion ?: 0) <= currentVersion) return
            val profile = entry.profile ?: throw IllegalStateException("updated config has no profile")
            val latency = reconfigureAndVerify(profile, currentMode)
            if (!isCurrent(epoch) || _state.value != AppState.CONNECTED) return

            if (!entry.verified) {
                configRepository.promoteCurrentConfig(profile, entry.configVersion ?: 0)
            }
            _latencyMs.value = latency
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException) {
            if (e.isCredentialRejected()) {
                handleRejectedCredential(e.rejectedCredentialToken)
            } else if (isCurrent(epoch) && _state.value == AppState.CONNECTED) {
                rollbackAfterFailure(oldEntry, currentMode, e)
            }
        } catch (t: Throwable) {
            if (isCurrent(epoch) && _state.value == AppState.CONNECTED) {
                rollbackAfterFailure(oldEntry, currentMode, t)
            }
        } finally {
            _isSwitching.value = false
        }
    }

    private suspend fun handleRejectedCredential(expectedToken: String?) {
        // Every step below re-reads the credential, and a keystore that fails
        // right now throws CredentialUnreadableException. This runs inside a
        // catch (ApiException) block, so the sibling catch (Throwable) does
        // not see it: it would escape the launch and take the process down.
        // Leave the revocation for the next heartbeat instead.
        try {
            clearRejectedCredential(expectedToken)
        } catch (e: CredentialUnreadableException) {
            logger.w(TAG, "credential unreadable while handling a rejected token; will retry")
        }
    }

    private suspend fun clearRejectedCredential(expectedToken: String?) {
        // Do not erase a revoked credential while a TUN/core may still be
        // serving traffic. Stop must be confirmed first; a failed stop leaves
        // the credential intact so the user can retry cleanup explicitly.
        if (expectedToken == null || !configRepository.isCurrentCredential(expectedToken)) return
        operationEpoch += 1
        if (rebindInProgress || !configRepository.isCurrentCredential(expectedToken)) return
        val stopEpoch = operationEpoch
        if (!stopInternal(expectedEpoch = stopEpoch)) return
        // The precheck avoids stopping a newly rebound VPN in the normal stale
        // 401 case. Keep this second compare-and-clear for the TOCTOU window
        // between stopping and deleting credentials.
        if (configRepository.isCurrentCredential(expectedToken) &&
            configRepository.clearBinding(expectedToken)
        ) {
            markUnbound()
        }
    }

    /**
     * A no-credential observation must not clear storage: a binding may have
     * completed after the repository made that observation.  401 responses
     * use [handleRejectedCredential] with the exact rejected token instead.
     */
    private suspend fun handleMissingCredential() {
        transitionToUnbound()
    }

    private suspend fun transitionToUnbound() {
        operationEpoch += 1
        if (!stopInternal()) return
        markUnbound()
    }

    private fun markUnbound() {
        _latencyMs.value = null
        _state.value = AppState.UNBOUND
    }

    private suspend fun rollbackAfterFailure(
        rollbackEntry: ConfigCacheEntry?,
        mode: ProxyMode,
        cause: Throwable,
    ) {
        logger.e(TAG, "configuration change failed, rolling back (${cause.javaClass.simpleName})")
        val profile = rollbackEntry?.profile
        if (profile != null) {
            try {
                _latencyMs.value = reconfigureAndVerify(profile, mode)
                settingsDataStore.setProxyMode(mode)
                return
            } catch (rollback: Throwable) {
                logger.e(TAG, "rollback after configuration change failed (${rollback.javaClass.simpleName})")
            }
        }

        stopInternal()
        _state.value = AppState.ERROR
        _latencyMs.value = null
    }

    private suspend fun reconfigureAndVerify(profile: ServerProfile, mode: ProxyMode): Int {
        val config = XrayConfigGenerator.generate(profile, mode)
        vpnController.reconfigure(context, config, mode)
        awaitVpnRunning()
        return connectivityProbe.measure()
    }

    private suspend fun awaitVpnRunning(timeoutMs: Long = VPN_TIMEOUT_MS) {
        val terminal = withTimeoutOrNull(timeoutMs) {
            VpnStateHolder.state.first {
                it == VpnState.RUNNING || it == VpnState.ERROR || it == VpnState.IDLE
            }
        } ?: error("VPN start timed out")
        if (terminal != VpnState.RUNNING) {
            error("VPN failed to enter running state")
        }
    }

    private suspend fun awaitVpnIdle(timeoutMs: Long = VPN_TIMEOUT_MS): Boolean {
        val terminal = withTimeoutOrNull(timeoutMs) {
            VpnStateHolder.state.first { it == VpnState.IDLE || it == VpnState.ERROR }
        }
        val idle = terminal == VpnState.IDLE
        if (!idle) {
            if (terminal == VpnState.ERROR) {
                logger.w(TAG, "VPN cleanup entered error state")
            } else {
                logger.w(TAG, "timed out waiting for VPN idle")
            }
        }
        return idle
    }

    private suspend fun loadConfigResult(): ConfigResult.Success {
        val result = configRepository.getConfig()
        return when (result) {
            is ConfigResult.Success -> result
            ConfigResult.NoCredential -> throw IllegalStateException("device credential is missing")
            ConfigResult.NoValidConfig -> throw IllegalStateException("no valid config")
        }
    }

    private suspend fun failConnect(t: Throwable) {
        logger.e(TAG, "connection failed (${t.javaClass.simpleName})")
        // Stop is asynchronous. Wait for the service to report IDLE before
        // exposing ERROR, otherwise an immediate retry can enqueue START
        // behind this old STOP and tear down the new connection.
        operationEpoch += 1
        val cleanupEpoch = operationEpoch
        stopInternal()
        if (!isCurrent(cleanupEpoch)) return
        _latencyMs.value = null
        _state.value = AppState.ERROR
    }

    private fun ApiException.isCredentialRejected(): Boolean =
        code == ErrorCode.TokenInvalid || code == ErrorCode.DeviceNotFound || status == 401

    private fun isCurrent(epoch: Long): Boolean = epoch == operationEpoch && !rebindInProgress

    private companion object {
        const val TAG = "ConnectionController"
        const val VPN_TIMEOUT_MS = 10_000L
        const val HEARTBEAT_INITIAL_DELAY_MS = 2_000L
        /**
         * Matches the TUI. The interval bounds how long a revoked device keeps
         * serving traffic, so the two clients must not diverge: a longer Android
         * interval would leave revocation unnoticed for minutes after the TUI
         * has already torn the connection down.
         */
        const val HEARTBEAT_INTERVAL_MS = 60_000L

        /**
         * 隧道没在转发时的周期。选择依据见 [heartbeatIntervalMillis]。
         */
        const val HEARTBEAT_IDLE_INTERVAL_MS = 300_000L

        /**
         * 锁屏静置时退避的上限。60s 起逐次翻倍：60 → 120 → 240 → 480 → 600。
         */
        const val HEARTBEAT_MAX_QUIET_INTERVAL_MS = 600_000L

        /**
         * 被事件叫醒之后，下一次同步之前的最小间隔。事件成串到来时把它们并成一次。
         */
        const val HEARTBEAT_EVENT_COALESCE_MS = 3_000L
    }
}
