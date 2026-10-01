package com.myproxy.android.vpn

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.myproxy.android.MainActivity
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.data.api.ApiConfig
import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.logging.AndroidAppLogger
import com.myproxy.android.logging.AppLogger
import com.myproxy.android.vpn.health.CoreHealthMonitor
import com.myproxy.android.vpn.health.SelfHealVerdict
import com.myproxy.android.xray.XrayConfigGenerator
import com.myproxy.android.xray.XrayManager
import com.myproxy.android.xray.XrayState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.atomic.AtomicLong

class MyProxyVpnService : VpnService() {

    private val lifecycleLock = Any()
    private var serviceScope = newServiceScope()
    private val logger: AppLogger = AndroidAppLogger()
    private val commandMutex = Mutex()
    private val commandEpoch = AtomicLong()

    private var vpnFd: ParcelFileDescriptor? = null

    @Volatile
    private var cleanupStarted = false

    /** Set when this instance's cleanup finished and published IDLE; see [canRestartServiceInstance]. */
    private var cleanupCompleted = false

    /** Most recent Android service start, including commands rejected in STOPPING. */
    private var latestStartId = 0

    /** Most recent start command actually accepted and dispatched by this service. */
    private var acceptedStartId = 0

    @Volatile
    private var reconfigureInProgress = false

    /**
     * A roam is re-dialling the core on the existing interface. The service
     * owns the retries, so a core error during one must not be acted on
     * elsewhere; see [shouldDeferXrayError].
     */
    @Volatile
    private var roamInProgress = false

    /**
     * The config the core is currently running. Kept so a roam can re-dial
     * without asking the control plane again, and cleared on cleanup so the
     * profile it carries does not outlive the tunnel.
     */
    @Volatile
    private var activeConfig: String? = null

    @Volatile
    private var killSwitchEnabled = false

    @Volatile
    private var selfHealEnabled = true

    /** True while the TUN interface is deliberately held open with no core. */
    @Volatile
    private var blockingHoldActive = false

    /** True while the health audit is restarting the core; see [tunnelBusy]. */
    @Volatile
    private var selfHealInProgress = false

    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var underlyingNetwork: Network? = null

    /**
     * 当前有没有默认网络。
     *
     * 只有一个用途，但很重要：**没网时一次都不重拨**。在一条已经不通的链路上
     * 烧完退避只是白白唤醒 CPU 和无线电，而链路回来时 `onAvailable` 会重新
     * 触发恢复——对「没网」，正确的重试次数是零。
     *
     * 乐观初始化为 true：`registerDefaultNetworkCallback` 在已有默认网络时会
     * 立刻回调 onAvailable，但在那之前有一个窗口。猜错的两个方向不对称——
     * 白拨一次只是多一次退避，而该拨不拨会留下一条断掉的隧道。
     */
    @Volatile
    private var hasDefaultNetwork = true

    /**
     * Handle of the last default network seen during this tunnel. Survives
     * `onLost` on purpose: losing Wi-Fi and gaining mobile data is precisely
     * the transition that has to migrate.
     */
    @Volatile
    private var lastNetworkId: Long? = null

    @Volatile
    private var roamJob: Job? = null

    /**
     * Identifies the live roam. Cancelling a roam job does not wait for its
     * `finally` to run, so a superseded roam could otherwise clear
     * [roamInProgress] out from under the roam that replaced it.
     */
    private val roamToken = AtomicLong()
    private var stateCollectorStarted = false
    private var settingsCollectorStarted = false
    private var healthAuditStarted = false
    private var livenessWatchStarted = false
    private var wakeLock: PowerManager.WakeLock? = null

    private val xrayManager: XrayManager
        get() = (application as MyProxyApplication).xrayManager

    private val healthMonitor: CoreHealthMonitor
        get() = (application as MyProxyApplication).coreHealthMonitor


    /**
     * True when some other operation already owns the core. Everything that
     * re-dials it goes through [redialCore] under one command epoch, so an
     * audit must stand aside rather than race them.
     */
    private fun tunnelBusy(): Boolean =
        cleanupStarted ||
            reconfigureInProgress ||
            roamInProgress ||
            selfHealInProgress ||
            blockingHoldActive

    override fun onCreate() {
        super.onCreate()
        startStateCollection()
        startSettingsCollection()
        startHealthAudit()
        startLivenessWatch()
        registerNetworkCallback()
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "MyProxy:VpnTransition").apply {
            // 关掉引用计数。acquire/release 由多条恢复路径调用——漫游、重配、
            // 自愈、存活性对账——而它们会重叠。计数模式下少配一次 release
            // 就意味着 CPU 被永久钉醒，那正是「VPN 很耗电」最经典的成因，
            // 而且不会有任何报错。
            //
            // 非计数模式下 acquire 幂等、release 一定真的放开；最坏情况是某条
            // 路径提前放开了另一条还在用的锁，代价只是那一步可能慢一点，
            // 不是整夜的电量。15 秒超时是第二道保险。
            setReferenceCounted(false)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        synchronized(lifecycleLock) {
            latestStartId = startId
        }
        synchronized(lifecycleLock) {
            if (cleanupStarted) {
                // A successful cleanup publishes IDLE only after the old
                // scope has been cancelled. Recreate the scope so a fast
                // stop -> start delivered to this still-alive instance is
                // handled instead of being launched into a cancelled scope.
                if (!canRestartServiceInstance(cleanupStarted, cleanupCompleted)) {
                    logger.w(TAG, "ignoring start while cleanup is incomplete")
                    return START_NOT_STICKY
                }
                prepareForRestartLocked()
            }
            acceptedStartId = startId
            when (intent?.action) {
                ACTION_STOP -> cleanup(userRequested = true)
                ACTION_STOP_INTERNAL -> cleanup()
                ACTION_RECONFIGURE -> handleReconfigure(intent)
                ACTION_START -> handleStart(intent)
                ACTION_FORCE_HEAL -> handleForceHeal()
                else -> handleSystemStart()
            }
        }
        return START_STICKY
    }

    override fun onRevoke() {
        // The system took the interface away, so there is nothing left to
        // block traffic with and a kill-switch hold would be a lie.
        cleanup(userRequested = true)
        super.onRevoke()
    }

    /**
     * 用户把 App 从最近任务里划掉了。
     *
     * **有意什么都不做。** 被划掉的是 Activity 的任务栈，隧道是前台服务，
     * 常驻通知还在；清单里的 `android:stopWithTask="false"` 已经把这一点写死。
     *
     * 尤其**不要**在这里重发一条 start 来「保活」：本实例还活着，那条 start 会
     * 走到 `handleSystemStart()` → `beginStart()`，把一条正在转发的隧道拆掉重建。
     * 真的被系统杀掉时由 START_STICKY 负责拉起，这里插手只会制造断流。
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        logger.i(TAG, "task removed; keeping the tunnel and its foreground notification")
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        unregisterNetworkCallback()
        cleanup()
        wakeLock?.let { if (it.isHeld) it.release() }
        super.onDestroy()
    }

    private fun startStateCollection() {
        if (stateCollectorStarted) return
        stateCollectorStarted = true
        serviceScope.launch {
            xrayManager.state.collect { xrayState ->
                if (xrayState == XrayState.ERROR) {
                    if (shouldDeferXrayError(
                            reconfigureInProgress,
                            roamInProgress,
                            selfHealInProgress,
                        )
                    ) {
                        logger.w(TAG, "core error during a recovery already in flight; deferring")
                        return@collect
                    }
                    logger.e(TAG, "xray state became ERROR")
                    handleXrayError()
                }
            }
        }
    }

    /**
     * Tracks the kill-switch setting live rather than capturing it at start,
     * so turning protection on while already connected takes effect without
     * reconnecting.
     */
    private fun startSettingsCollection() {
        if (settingsCollectorStarted) return
        settingsCollectorStarted = true
        serviceScope.launch {
            (application as MyProxyApplication).settingsDataStore.killSwitch.collect { enabled ->
                killSwitchEnabled = enabled
            }
        }
        serviceScope.launch {
            (application as MyProxyApplication).settingsDataStore.selfHeal.collect { enabled ->
                selfHealEnabled = enabled
            }
        }
    }

    private fun handleXrayError() {
        if (blockingHoldActive) return
        if (cleanupStarted) {
            VpnStateHolder.setState(VpnState.ERROR)
            return
        }
        if (shouldHoldBlockingTunnel(
                killSwitchEnabled = killSwitchEnabled,
                userRequested = false,
                tunnelEstablished = vpnFd != null,
            )
        ) {
            enterBlockingHold()
            return
        }
        performCleanup(initialState = VpnState.ERROR)
    }

    /**
     * Keeps the TUN interface -- and therefore the default routes -- while the
     * core is gone, so traffic is dropped instead of leaking to the open
     * network. Releasing it stays an explicit user action.
     */
    private fun enterBlockingHold() {
        blockingHoldActive = true
        reconfigureInProgress = false
        logger.w(TAG, "holding a blocking tunnel after an unexpected failure")
        startForegroundVpn(
            contentText = getString(com.myproxy.android.R.string.notification_blocked),
            actionLabel = getString(com.myproxy.android.R.string.notification_action_release),
        )
        VpnStateHolder.setBlockingHold(true)
        VpnStateHolder.setState(VpnState.ERROR)
        // Claim the command generation so a start accepted while this is in
        // flight wins: without it, reaping the dead core could land after a
        // retry has already started a new one and would kill it.
        val epoch = commandEpoch.incrementAndGet()
        serviceScope.launch {
            commandMutex.withLock {
                if (!isCommandCurrent(epoch)) return@withLock
                // Release the dead core's resources. The interface stays, so
                // packets keep going nowhere instead of leaking.
                runCatching { xrayManager.stop() }
            }
        }
    }

    private fun releaseBlockingHold() {
        if (!blockingHoldActive) return
        blockingHoldActive = false
        VpnStateHolder.setBlockingHold(false)
    }

    private fun handleStart(intent: Intent?) {
        val config = intent?.getStringExtra(EXTRA_CONFIG)
        if (config.isNullOrBlank()) {
            logger.w(TAG, "start requested without config")
            failCleanup()
            return
        }
        logger.i(TAG, "handling explicit start command")
        beginStart()
        val epoch = commandEpoch.incrementAndGet()
        serviceScope.launch { runTunnel(config, epoch) }
    }

    /**
     * Start with no config in hand. Two callers reach this: START_STICKY
     * restarting the service after a process kill (null intent), and always-on
     * VPN starting it at boot.
     *
     * Always-on is supported by default -- the manifest deliberately does not
     * set SUPPORTS_ALWAYS_ON to false -- so the system lists MyProxy in its
     * always-on picker and expects the service to come up unattended. Restoring
     * the last verified config here is what makes that setting, and unattended
     * recovery after a kill, actually work.
     */
    private fun handleSystemStart() {
        logger.i(TAG, "handling system/sticky start (always-on or process recovery)")
        beginStart()
        val epoch = commandEpoch.incrementAndGet()
        serviceScope.launch {
            val app = application as MyProxyApplication
            val entry = runCatching { app.configRepository.lastKnownGood() }.getOrNull()
            val profile = entry?.profile
            if (entry == null || profile == null || !entry.verified) {
                // Either unbound, or the cache holds a config that never passed
                // a connectivity check. Nobody is watching an unattended start,
                // so an unverified config must not reach the wire.
                logger.w(TAG, "no verified config for a system start; stopping")
                failCleanup()
                return@launch
            }
            val mode = runCatching { app.settingsDataStore.proxyMode.first() }
                .getOrDefault(ProxyMode.RULE)
            val config = runCatching { XrayConfigGenerator.generate(profile, mode) }.getOrNull()
            if (config == null) {
                logger.e(TAG, "cached config failed validation on a system start")
                failCleanup()
                return@launch
            }
            runTunnel(config, epoch)
        }
    }

    /**
     * 进入 RUNNING，并把常驻通知同步成「已连接」。
     *
     * 两件事必须一起做。先前 [beginStart] 在隧道还没建立时就贴出了默认文案，
     * 而默认文案写的是「已连接」——于是 START_STICKY 拉起服务、always-on 开机
     * 启动、以及任何一次启动的头几百毫秒里，通知栏都在说一件还没发生的事。
     * 那是一个字面意义上的假 Connected。
     */
    private fun markRunning() {
        VpnStateHolder.setState(VpnState.RUNNING)
        startForegroundVpn()
    }

    private fun beginStart() {
        reconfigureInProgress = false
        roamInProgress = false
        selfHealInProgress = false
        releaseBlockingHold()
        // Before establish() and before the core starts, so the audit's
        // memory floor does not already include either.
        healthMonitor.onCoreStarting()
        startForegroundVpn(
            contentText = getString(com.myproxy.android.R.string.notification_connecting),
        )
        VpnStateHolder.setState(VpnState.STARTING)
    }

    private suspend fun runTunnel(config: String, epoch: Long) {
        if (ApiConfig.builtInTarget?.isConfigured != true) {
            logger.e(TAG, "尚未配置服务器，请联系部署管理员。")
            failCleanup()
            return
        }
        try {
            acquireWakeLock()
            val started = commandMutex.withLock {
                if (!isCommandCurrent(epoch)) return@withLock false

                // A kill-switch hold leaves an interface open with no core. If
                // a start reaches this instance while one is held, release the
                // old descriptor before establish() returns a new one, or it
                // leaks for the life of the process.
                vpnFd?.let { stale ->
                    runCatching { stale.close() }
                        .onFailure { logger.w(TAG, "failed to close a held VPN fd") }
                    vpnFd = null
                }

                val builder = Builder()
                    .setMtu(1500)
                    .addAddress("10.0.0.2", 30)
                    .addAddress("fd00::2", 126)
                    .addRoute("0.0.0.0", 0)
                    .addRoute("::", 0)
                    .addDnsServer("1.1.1.1")
                    .addDnsServer("8.8.8.8")
                    .addDnsServer("2606:4700:4700::1111")
                    .addDisallowedApplication(packageName)
                    .setSession(getString(com.myproxy.android.R.string.app_name))
                // From API 29 a VPN is metered unless it says otherwise, and it is
                // the device's default network: backups wait for Wi-Fi, Play and
                // system updates pause, "unmetered only" downloads stop -- on
                // Wi-Fi, for as long as the tunnel is up. Unmetered here means
                // "metered only if the underlying network is", which the
                // setUnderlyingNetworks calls below keep current.
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    builder.setMetered(false)
                }

                val fd = builder.establish()
                    ?: error("VpnService.Builder.establish() returned null")
                if (!isCommandCurrent(epoch)) {
                    fd.close()
                    return@withLock false
                }
                vpnFd = fd
                xrayManager.start(config, fd.fd)
                isCommandCurrent(epoch)
            }
            if (started) {
                activeConfig = config
                markRunning()
            }
        } catch (t: Throwable) {
            if (isCommandCurrent(epoch)) {
                logger.e(TAG, "VPN start failed (${t.javaClass.simpleName})")
                failCleanup()
            }
        } finally {
            releaseWakeLock()
        }
    }

    private fun handleReconfigure(intent: Intent) {
        val config = intent.getStringExtra(EXTRA_CONFIG)
        if (config.isNullOrBlank()) {
            logger.w(TAG, "reconfigure requested without config")
            return
        }
        if (cleanupStarted) return
        val epoch = commandEpoch.incrementAndGet()
        reconfigureInProgress = true
        serviceScope.launch {
            try {
                acquireWakeLock()
                val applied = commandMutex.withLock {
                    if (!isCommandCurrent(epoch)) return@withLock false
                    val fd = vpnFd ?: error("VPN fd is unavailable during reconfigure")
                    xrayManager.restart(config, fd.fd)
                    isCommandCurrent(epoch)
                }
                if (applied) {
                    activeConfig = config
                    markRunning()
                    reconfigureInProgress = false
                }
            } catch (t: Throwable) {
                if (isCommandCurrent(epoch)) {
                    logger.e(TAG, "VPN reconfigure failed (${t.javaClass.simpleName})")
                    // Keep the TUN fd and foreground service alive. The controller
                    // owns the LKG rollback and will issue a second reconfigure;
                    // only a failed rollback performs the final cleanup.
                    VpnStateHolder.setState(VpnState.ERROR)
                }
            } finally {
                releaseWakeLock()
            }
        }
    }

    /**
     * Re-dials the core over a new default network, on the interface that is
     * already up.
     *
     * Failure is retried rather than fatal: a roam usually fires while the new
     * link is still coming up. Until a retry succeeds the interface stays in
     * place with no core behind it, so traffic is dropped -- the same
     * fail-closed posture as the kill switch, for the seconds the migration
     * takes.
     */
    private fun scheduleRoam(network: Network) {
        roamJob?.cancel()
        roamJob = serviceScope.launch {
            delay(ROAM_DEBOUNCE_MS)
            roamToCurrentNetwork(network)
        }
    }

    private suspend fun roamToCurrentNetwork(network: Network) {
        // A roam can be waiting out its debounce when an unrelated failure
        // starts a kill-switch hold. Re-dialling then would quietly undo the
        // hold, so the state is re-checked after the wait, not only before it.
        if (blockingHoldActive || cleanupStarted) return
        val config = activeConfig ?: return
        val token = roamToken.incrementAndGet()
        val epoch = commandEpoch.incrementAndGet()
        roamInProgress = true
        try {
            acquireWakeLock()
            if (redialCore(config, epoch, "roam onto $network")) {
                markRunning()
                return
            }
            if (isCommandCurrent(epoch)) {
                // No core, interface still up: traffic is already being
                // dropped. Fall through to the unexpected-failure path, which
                // either holds that interface or releases it.
                roamInProgress = false
                handleXrayError()
            }
        } finally {
            if (roamToken.get() == token) roamInProgress = false
            releaseWakeLock()
        }
    }

    /**
     * Restarts the core on the TUN descriptor that is already established.
     *
     * Shared by roaming and by the health audit, because both want the same
     * thing: a core dialling out afresh without the interface, its routes or
     * the VPN consent being disturbed. Retried rather than fatal -- while no
     * core is attached the interface swallows packets, so the tunnel is down
     * either way and another attempt costs nothing but time.
     *
     * [reason] is only ever a fixed description plus a network handle or a
     * verdict name, never anything drawn from the profile.
     */
    private suspend fun redialCore(config: String, epoch: Long, reason: String): Boolean {
        repeat(REDIAL_MAX_ATTEMPTS) { attempt ->
            if (!isCommandCurrent(epoch)) return false
            if (!hasDefaultNetwork) {
                // 没网时正确的重试次数是零。在一条不通的链路上烧完退避只是
                // 反复唤醒无线电；链路回来时 onAvailable 会重新把恢复跑起来。
                logger.w(TAG, "$reason: no default network; waiting for one instead of retrying")
                return false
            }
            val applied = runCatching {
                commandMutex.withLock {
                    if (!isCommandCurrent(epoch)) return@withLock false
                    val fd = vpnFd ?: return@withLock false
                    xrayManager.restart(config, fd.fd)
                    isCommandCurrent(epoch)
                }
            }.getOrElse { t ->
                logger.w(TAG, "$reason attempt ${attempt + 1} failed (${t.javaClass.simpleName})")
                false
            }
            if (applied) {
                logger.i(TAG, "$reason succeeded")
                return true
            }
            if (!isCommandCurrent(epoch)) return false
            // No backoff after the final attempt: traffic is being dropped
            // meanwhile, so reaching the decision sooner beats waiting to
            // give up.
            if (attempt < REDIAL_MAX_ATTEMPTS - 1) {
                delay(
                    redialRetryDelayMillis(
                        attempt = attempt,
                        baseDelayMillis = REDIAL_RETRY_BASE_DELAY_MS,
                        maxDelayMillis = REDIAL_RETRY_MAX_DELAY_MS,
                    ),
                )
            }
        }
        logger.e(TAG, "$reason exhausted its attempts")
        return false
    }

    /**
     * Periodically audits the process the core runs in, and restarts the core
     * when the audit says it has gone wrong in a way a restart can fix.
     *
     * `delay` does not wake a sleeping device, so on an idle phone these ticks
     * land in Doze maintenance windows. That is the right moment anyway:
     * screen off with no traffic is exactly what
     * [com.myproxy.android.vpn.health.selfHealVerdict] requires before it will
     * act.
     */
    private fun startHealthAudit() {
        if (healthAuditStarted) return
        healthAuditStarted = true
        serviceScope.launch {
            while (true) {
                delay(CoreHealthMonitor.AUDIT_INTERVAL_MS)
                if (VpnStateHolder.state.value != VpnState.RUNNING) continue
                val verdict = runCatching {
                    healthMonitor.evaluate(
                        selfHealEnabled = selfHealEnabled,
                        tunnelBusy = tunnelBusy(),
                    )
                }
                    .getOrElse { t ->
                        logger.w(TAG, "health audit failed (${t.javaClass.simpleName})")
                        SelfHealVerdict.SKIP_NO_DATA
                    }
                when (verdict) {
                    SelfHealVerdict.HEAL_MEMORY, SelfHealVerdict.HEAL_SPINNING -> selfHeal(verdict)
                    else -> Unit
                }
            }
        }
    }

    /**
     * 定期把 RUNNING 与事实对账。
     *
     * 核心的死亡**通常**会经 `CoreCallbackHandler` 报上来，但那条通路不是必然的：
     * Go 侧 panic 被吞掉、厂商 ROM 冻结进程后又解冻、native 线程被单独杀掉，
     * 都会留下「核心已经不在，而回调从未触发」的局面。那时 RUNNING 会一直挂着，
     * 界面一直写「已连接」，用户的流量其实一个字节也出不去——这是最难被发现的
     * 一类故障，因为它看起来完全正常。
     *
     * 所以状态要主动对账，不能只等着被通知。判据见 [isTunnelStateHonest]。
     *
     * 和健康审计一样，`delay` 不唤醒设备：手机睡着时这些 tick 会顺延到 Doze
     * 维护窗口，代价为零。
     */
    private fun startLivenessWatch() {
        if (livenessWatchStarted) return
        livenessWatchStarted = true
        serviceScope.launch {
            while (true) {
                delay(LIVENESS_INTERVAL_MS)
                val state = VpnStateHolder.state.value
                // 正在被别的恢复流程拿着时不要插手：那些路径本来就会短暂地
                // 让核心不在，对账会把一次正常的重拨误判成死亡。
                if (state != VpnState.RUNNING || tunnelBusy()) continue
                // 读不到就按「活着」算。读不出来的存活性答案不等于「已经死了」，
                // 把它当死的会去拆一条正在好好工作的隧道——对账的方向如果错了，
                // 造成的破坏比它要修的问题更大。
                val coreRunning = runCatching { xrayManager.isRunning() }.getOrDefault(true)
                val interfaceUp = vpnFd != null
                if (isTunnelStateHonest(state, coreRunning, interfaceUp)) continue
                logger.e(
                    TAG,
                    "liveness reconciliation: core=$coreRunning tun=$interfaceUp; leaving RUNNING",
                )
                handleXrayError()
            }
        }
    }

    /**
     * 网络回来了，把核心装回一条正被断网保护握着的接口上。
     *
     * 只在成功之后才解除 hold 标记。失败时 hold 原样保留——「放开阻断」仍然
     * 只能由用户显式触发，那条不变量没有被动过；这里做的是相反的事：
     * 把保护从「丢弃流量」恢复成「正常转发」。
     */
    private fun scheduleHoldRecovery() {
        roamJob?.cancel()
        roamJob = serviceScope.launch {
            delay(ROAM_DEBOUNCE_MS)
            recoverBlockingHold()
        }
    }

    private suspend fun recoverBlockingHold() {
        if (cleanupStarted || !blockingHoldActive) return
        val config = activeConfig ?: return
        val token = roamToken.incrementAndGet()
        val epoch = commandEpoch.incrementAndGet()
        roamInProgress = true
        try {
            acquireWakeLock()
            if (redialCore(config, epoch, "recover a blocking hold")) {
                releaseBlockingHold()
                markRunning()
            }
        } finally {
            if (roamToken.get() == token) roamInProgress = false
            releaseWakeLock()
        }
    }

    /**
     * Restarts the core because the audit asked for it.
     *
     * Every live connection through the tunnel dies here. The audit only
     * reaches this point with the screen off and traffic at a trickle, which
     * is what makes that acceptable rather than rude.
     */
    private suspend fun selfHeal(verdict: SelfHealVerdict) {
        // Re-checked because the audit spends a few seconds sampling traffic
        // before it reports, and the core can be claimed within that window.
        if (tunnelBusy()) {
            healthMonitor.onHealNotCompleted("the core was claimed while the audit ran")
            return
        }
        val config = activeConfig ?: return healthMonitor.onHealNotCompleted("no active config")
        val epoch = commandEpoch.incrementAndGet()
        selfHealInProgress = true
        try {
            acquireWakeLock()
            if (redialCore(config, epoch, "self-heal ($verdict)")) {
                healthMonitor.onHealSucceeded()
                markRunning()
                return
            }
            healthMonitor.onHealNotCompleted("the restart exhausted its attempts")
            if (isCommandCurrent(epoch)) {
                // The core is down and this service put it there, so the
                // honest response is the same as any other unexpected
                // failure: hold the interface, or release it.
                selfHealInProgress = false
                handleXrayError()
            }
        } finally {
            selfHealInProgress = false
            releaseWakeLock()
        }
    }

    private fun cleanup(userRequested: Boolean = false) {
        reconfigureInProgress = false
        if (userRequested) VpnStateHolder.markUserStopRequested()
        performCleanup(
            initialState = if (userRequested) VpnState.USER_STOPPING else VpnState.STOPPING,
        )
    }

    private fun performCleanup(initialState: VpnState) {
        val cleanupStartId: Int
        val cleanupGeneration: Long
        synchronized(lifecycleLock) {
            if (cleanupStarted) return
            cleanupStarted = true
            cleanupGeneration = commandEpoch.incrementAndGet()
            cleanupStartId = if (acceptedStartId != 0) acceptedStartId else latestStartId
            reconfigureInProgress = false
            roamInProgress = false
            roamJob?.cancel()
            roamJob = null
            selfHealInProgress = false
            activeConfig = null
            healthMonitor.onCoreStopped()
            releaseBlockingHold()
            unregisterNetworkCallback()
            VpnStateHolder.setState(initialState)
        }
        serviceScope.launch {
            var coreStopped = false
            var vpnFdClosed = vpnFd == null
            commandMutex.withLock {
                try {
                    xrayManager.stop()
                    coreStopped = true
                } catch (t: Throwable) {
                    logger.e(TAG, "failed to stop Xray core (${t.javaClass.simpleName})")
                }
                if (coreStopped && !vpnFdClosed) {
                    try {
                        vpnFd?.close()
                        vpnFdClosed = true
                    } catch (t: Throwable) {
                        logger.w(TAG, "failed to close VPN fd (${t.javaClass.simpleName})")
                    }
                }
                if (coreStopped && vpnFdClosed) vpnFd = null
            }
            if (!coreStopped || !vpnFdClosed) {
                synchronized(lifecycleLock) {
                    if (!isCurrentCleanupGeneration(cleanupStartId, latestStartId)) {
                        // A newer framework start may have been delivered and
                        // rejected while cleanup was in progress. It was not
                        // accepted by this instance, so do not leave the old
                        // cleanup marked busy; expose ERROR so retry() can
                        // issue another stop command.
                        logger.w(TAG, "cleanup failure followed a rejected newer start")
                    }
                    cleanupStarted = false
                    registerNetworkCallback()
                    VpnStateHolder.setState(cleanupCompletionState(coreStopped, vpnFdClosed))
                }
                logger.e(TAG, "VPN cleanup incomplete; retaining service for retry")
                return@launch
            }
            synchronized(lifecycleLock) {
                if (commandEpoch.get() != cleanupGeneration) {
                    logger.w(TAG, "stale cleanup generation ignored")
                    return@launch
                }
                val stopStartId = cleanupStopStartId(
                    cleanupAcceptedStartId = cleanupStartId,
                    latestFrameworkStartId = latestStartId,
                    latestAcceptedStartId = acceptedStartId,
                )
                if (stopStartId == null) {
                    // A newer start was accepted by this instance. Do not
                    // remove its foreground entry, cancel its scope, or write
                    // IDLE over the new connection.
                    logger.w(TAG, "cleanup superseded by newer service start")
                    return@launch
                }
                // Native/TUN cleanup has completed and no newer accepted
                // generation exists. Use the latest framework id so starts
                // rejected while STOPPING are consumed without an unsafe
                // no-argument stopSelf().
                ServiceCompat.stopForeground(this@MyProxyVpnService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                val stopped = stopSelfResult(stopStartId)
                if (!stopped) {
                    // The selected id is the latest framework id and there
                    // is still no accepted newer generation. Finalize the
                    // old scope even if Android reports it was already
                    // stopping; never fall back to stopSelf().
                    logger.w(TAG, "service stop result was false without an accepted newer start")
                }
                // IDLE is published last. If Android delivers a new start to
                // this instance after that point, onStartCommand recreates a
                // live scope before dispatching the command.
                serviceScope.cancel()
                cleanupCompleted = true
                VpnStateHolder.setState(cleanupCompletionState(coreStopped, vpnFdClosed))
            }
        }
    }

    private fun handleForceHeal() {
        if (VpnStateHolder.state.value != VpnState.RUNNING || tunnelBusy()) {
            logger.w(TAG, "ignoring manual heal request: tunnel is busy or not running")
            return
        }
        val config = activeConfig ?: return
        val epoch = commandEpoch.incrementAndGet()
        // The state collector must defer errors until redialCore exhausts its
        // retries, just as it does for an automatic heal. Claim the core before
        // launching so a second benchmark cannot supersede this one.
        selfHealInProgress = true
        serviceScope.launch {
            try {
                acquireWakeLock()
                val prePssKb = runCatching { healthMonitor.takeBenchmarkPreSample() }.getOrNull()
                if (redialCore(config, epoch, "manual benchmark heal")) {
                    markRunning()
                    prePssKb?.let { healthMonitor.recordBenchmarkResult(it) }
                } else if (isCommandCurrent(epoch)) {
                    selfHealInProgress = false
                    handleXrayError()
                }
            } finally {
                selfHealInProgress = false
                releaseWakeLock()
            }
        }
    }

    private fun prepareForRestartLocked() {
        serviceScope = newServiceScope()
        cleanupStarted = false
        cleanupCompleted = false
        reconfigureInProgress = false
        roamInProgress = false
        selfHealInProgress = false
        stateCollectorStarted = false
        settingsCollectorStarted = false
        healthAuditStarted = false
        livenessWatchStarted = false
        startStateCollection()
        startSettingsCollection()
        startHealthAudit()
        startLivenessWatch()
        registerNetworkCallback()
    }

    private fun acquireWakeLock() {
        runCatching {
            wakeLock?.acquire(WAKE_LOCK_TIMEOUT_MS)
        }.onFailure {
            logger.w(TAG, "failed to acquire wake lock: ${it.message}")
        }
    }

    private fun releaseWakeLock() {
        runCatching {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        }.onFailure {
            logger.w(TAG, "failed to release wake lock: ${it.message}")
        }
    }

    private fun newServiceScope(): CoroutineScope =
        CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private fun failCleanup() {
        performCleanup(initialState = VpnState.ERROR)
    }

    private fun isCommandCurrent(epoch: Long): Boolean =
        commandEpoch.get() == epoch && !cleanupStarted

    private fun startForegroundVpn(
        contentText: String = getString(com.myproxy.android.R.string.notification_connected),
        actionLabel: String = getString(com.myproxy.android.R.string.notification_action_stop),
    ) {
        val stopIntent = PendingIntent.getService(
            this,
            0,
            Intent(this, MyProxyVpnService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        // 点通知本体回到应用。先前没有 contentIntent，点一下什么都不发生——
        // 而一个常驻通知最常被点的位置就是它自己，不是旁边那个动作按钮。
        val openIntent = PendingIntent.getActivity(
            this,
            1,
            Intent(this, MainActivity::class.java)
                .setFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(this, CHANNEL_ID)
            // 状态栏图标用磁贴那枚单色电源标记，而不是启动器前景图
            // （后者是一个 108dp 的实心白圆，缩进状态栏就是一个圆点）。
            .setSmallIcon(com.myproxy.android.R.drawable.ic_tile_myproxy)
            .setContentTitle(getString(com.myproxy.android.R.string.app_name))
            .setContentText(contentText)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .setSilent(true)
            .setShowWhen(false)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            // 渠道是 IMPORTANCE_LOW；API 26 以下没有渠道，靠这条对齐。
            .setPriority(NotificationCompat.PRIORITY_LOW)
            // API 31+ 默认会把前台服务通知压后最多 10 秒才显示。对一个「点了启动
            // 就该立刻看到它在跑」的开关来说，那 10 秒看起来就是没启动。
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .addAction(0, actionLabel, stopIntent)
            .build()

        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, type)
    }

    private fun registerNetworkCallback() {
        val manager = getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        lastNetworkId = null
        // 乐观起步：已有默认网络时 onAvailable 会立刻纠正它，而在那之前
        // 宁可白拨一次，也不要因为「还不知道有没有网」而拒绝拨号。
        hasDefaultNetwork = true
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                logger.i(TAG, "underlying network available: $network")
                underlyingNetwork = network
                hasDefaultNetwork = true
                setUnderlyingNetworks(arrayOf(network))
                val newId = network.networkHandle
                val vpnState = VpnStateHolder.state.value
                val roam = shouldRestartCoreForNetwork(
                    previousNetworkId = lastNetworkId,
                    newNetworkId = newId,
                    vpnState = vpnState,
                    cleanupStarted = cleanupStarted,
                    reconfigureInProgress = reconfigureInProgress,
                )
                val recover = shouldRedialOnNetworkAvailable(
                    vpnState = vpnState,
                    blockingHoldActive = blockingHoldActive,
                    hasActiveConfig = activeConfig != null,
                    tunnelInterfaceUp = vpnFd != null,
                    cleanupStarted = cleanupStarted,
                )
                lastNetworkId = newId
                // 控制面也该知道网络换了：切网之后它的答案很可能已经变了，
                // 这比等退避定时器要及时得多。
                VpnStateHolder.markNetworkChanged()
                when {
                    roam -> {
                        logger.i(TAG, "default network changed; scheduling a core roam")
                        scheduleRoam(network)
                    }
                    recover -> {
                        logger.i(TAG, "network is back; scheduling a blocking-hold recovery")
                        scheduleHoldRecovery()
                    }
                }
            }

            override fun onCapabilitiesChanged(network: Network, capabilities: android.net.NetworkCapabilities) {
                logger.d(TAG, "network capabilities changed: $network")
            }

            override fun onLost(network: Network) {
                logger.i(TAG, "underlying network lost: $network")
                if (underlyingNetwork == network) {
                    underlyingNetwork = null
                    // 从这一刻起 redialCore 一次都不会拨，直到 onAvailable 回来。
                    hasDefaultNetwork = false
                    setUnderlyingNetworks(null)
                }
            }
        }
        runCatching {
            manager.registerDefaultNetworkCallback(callback)
            networkCallback = callback
        }.onFailure {
            logger.w(TAG, "failed to register default network callback: ${it.message}")
        }
    }

    private fun unregisterNetworkCallback() {
        val callback = networkCallback ?: return
        networkCallback = null
        underlyingNetwork = null
        lastNetworkId = null
        // 不再有回调来告诉我们网络状况，下一次注册会重新乐观起步。
        hasDefaultNetwork = true
        val manager = getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        runCatching {
            manager.unregisterNetworkCallback(callback)
        }.onFailure {
            logger.w(TAG, "failed to unregister network callback: ${it.message}")
        }
    }

    companion object {
        /**
         * START is used by the app's UI to initiate a connection.
         * Secured by android.permission.BIND_VPN_SERVICE.
         */
        const val ACTION_START = "com.myproxy.android.vpn.START"

        /**
         * STOP is used by the UI or the foreground notification to disconnect.
         */
        const val ACTION_STOP = "com.myproxy.android.vpn.STOP"

        const val ACTION_STOP_INTERNAL = "com.myproxy.android.vpn.STOP_INTERNAL"
        const val ACTION_RECONFIGURE = "com.myproxy.android.vpn.RECONFIGURE"
        const val EXTRA_CONFIG = "extra_config_json"
        const val EXTRA_MODE = "extra_proxy_mode"
        const val CHANNEL_ID = "myproxy_vpn"

        /**
         * Triggers a non-destructive core restart to measure memory recovery.
         * Secured by android.permission.BIND_VPN_SERVICE on the service.
         */
        const val ACTION_FORCE_HEAL = "com.myproxy.android.vpn.FORCE_HEAL"

        private const val TAG = "MyProxyVpnService"
        private const val NOTIFICATION_ID = 1001
        private const val WAKE_LOCK_TIMEOUT_MS = 15_000L

        /**
         * A single physical move produces several callbacks as the new link
         * is validated. Coalescing them avoids restarting the core three
         * times for one walk out of the front door.
         */
        private const val ROAM_DEBOUNCE_MS = 1_200L

        /**
         * 重拨：5 次，指数退避 1s → 2s → 4s → 8s，封顶 16s。
         * 全部用完约 15 秒，之后才认定失败。
         *
         * 次数比先前的 3 次多，但总代价更小：退避是指数的，而且没网时
         * 一次都不拨（见 [redialCore]）。
         */
        private const val REDIAL_MAX_ATTEMPTS = 5
        private const val REDIAL_RETRY_BASE_DELAY_MS = 1_000L
        private const val REDIAL_RETRY_MAX_DELAY_MS = 16_000L

        /**
         * 存活性对账周期。`delay` 不唤醒设备，睡眠时顺延到 Doze 维护窗口，
         * 所以这个数字不等于每分钟一次真实唤醒。
         */
        private const val LIVENESS_INTERVAL_MS = 60_000L
    }
}
