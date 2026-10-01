package com.myproxy.android.xray

import com.myproxy.android.vpn.VpnState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Samples the running core's traffic counters and publishes rate, session
 * totals and a short history for the sparkline.
 *
 * Everything hard about this lives in pure functions in [TrafficStats]; this
 * class is only the coroutine shell that drives them.
 *
 * Sampling runs only while the tunnel is up *and* something is collecting
 * [snapshot]. A phone sitting in a pocket with the tunnel up therefore does no
 * JNI work and takes no wakeups for statistics nobody is reading. The price is
 * that the counters accumulate silently across such a gap, which is why the
 * first read after one is drained into the totals without becoming a rate:
 * the interval it covers is unknown.
 */
class TrafficStatsMonitor(
    private val source: TrafficStatsSource,
    private val vpnState: StateFlow<VpnState>,
    scope: CoroutineScope,
    private val sampleIntervalMs: Long = SAMPLE_INTERVAL_MS,
    private val nanoTime: () -> Long = System::nanoTime,
    /**
     * Observability Plane sink, or null when attribution is off.
     *
     * It hangs off this sampler rather than owning a loop of its own because
     * the category counters reset as they are read, exactly like the proxy
     * ones: a second reader would leave each of them seeing a fraction of
     * the traffic. One owner, one read.
     */
    private val categorySink: (suspend (Map<String, Long>) -> Unit)? = null,
    private val categoryAttributionEnabled: () -> Boolean = { false },
) {
    private var sessionStartNanos: Long? = null
    private var lastSampleNanos: Long = 0L
    private var uplinkTotal = 0L
    private var downlinkTotal = 0L
    private var uptimeMillis = 0L
    private var history: List<TrafficRate> = emptyList()
    private var lastRate: TrafficRate = TrafficRate.ZERO

    @OptIn(ExperimentalCoroutinesApi::class)
    val snapshot: StateFlow<TrafficSnapshot> = vpnState
        .flatMapLatest { state: VpnState ->
            if (state == VpnState.RUNNING) sampleWhileRunning() else flowOf(settled(state))
        }
        .stateIn(scope, SharingStarted.WhileSubscribed(SUBSCRIPTION_GRACE_MS), TrafficSnapshot())

    init {
        // 类别上报本身就是一个读者，不能依赖「有没有界面开着」。
        //
        // [reportCategories] 只在采样循环里跑，而采样是 WhileSubscribed 的。
        // 主界面一度是唯一的长期订阅者，它不再订阅 [snapshot] 之后，这条通路
        // 就只剩 [peakRateOver] 那种一次性订阅能偶然带起来——而那是自愈开着
        // 时才会发生的事。计数器读一次清零，所以停摆不会报错，只会安静地不
        // 上报。让 sink 自己当那个订阅者，这条依赖就消失了。
        if (categorySink != null) scope.launch { driveCategoryDrain() }
    }

    /**
     * 在「类别归因打开」期间让采样保持运行。
     *
     * 分段续租而不是一直订阅：归因开关是心跳下发的 feature flag，运行中会变，
     * 而它是个普通 lambda 不是 Flow，只能定期重新问。开关关着时一次 JNI 都不
     * 做——隧道开着躺在口袋里不该为没人读的统计付电量，这条原有约束仍然成立。
     */
    private suspend fun driveCategoryDrain() {
        while (true) {
            // 先等隧道真的起来。核心没在跑就没有计数器可读，这时候每分钟醒一次
            // 去问一个必然答「读不到」的问题纯属白费——而这个循环活在常驻进程里，
            // 隧道停着的时间通常远多于开着的时间。挂在这里等的代价是零。
            vpnState.first { it == VpnState.RUNNING }
            if (categoryAttributionEnabled()) {
                withTimeoutOrNull(ATTRIBUTION_LEASE_MS) { snapshot.collect { } }
            } else {
                delay(ATTRIBUTION_RECHECK_MS)
            }
        }
    }

    /**
     * Peak rate observed over [windowMs].
     *
     * Subscribing is what starts sampling, so this is how a caller with no UI
     * on screen gets a reading without reading the core's counters itself --
     * which would make it a second owner of counters that reset as they are
     * read. The window must span more than one [sampleIntervalMs], because the
     * first sample after a subscription deliberately carries no rate.
     *
     * Peak rather than average on purpose: the question this answers is "is
     * anything happening", and one busy sample is enough to mean yes.
     */
    suspend fun peakRateOver(windowMs: Long): TrafficRate {
        var peak = TrafficRate.ZERO
        withTimeoutOrNull(windowMs) {
            snapshot.collect { current ->
                if (!current.sampling) return@collect
                peak = TrafficRate(
                    uplinkBytesPerSecond = maxOf(
                        peak.uplinkBytesPerSecond,
                        current.rate.uplinkBytesPerSecond,
                    ),
                    downlinkBytesPerSecond = maxOf(
                        peak.downlinkBytesPerSecond,
                        current.rate.downlinkBytesPerSecond,
                    ),
                )
            }
        }
        return peak
    }

    private fun sampleWhileRunning(): Flow<TrafficSnapshot> = flow {
        if (sessionStartNanos == null) sessionStartNanos = nanoTime()
        // Baseline read: drain whatever piled up while nobody was sampling.
        val baseline = source.readAndResetProxyTraffic()
        uplinkTotal += baseline.uplinkBytes
        downlinkTotal += baseline.downlinkBytes
        lastSampleNanos = nanoTime()
        lastRate = TrafficRate.ZERO
        emit(capture(sampling = true))

        while (true) {
            delay(sampleIntervalMs)
            val now = nanoTime()
            val elapsedNanos = now - lastSampleNanos
            lastSampleNanos = now
            val delta = source.readAndResetProxyTraffic()
            uplinkTotal += delta.uplinkBytes
            downlinkTotal += delta.downlinkBytes
            lastRate = TrafficRate(
                uplinkBytesPerSecond = bytesPerSecond(delta.uplinkBytes, elapsedNanos),
                downlinkBytesPerSecond = bytesPerSecond(delta.downlinkBytes, elapsedNanos),
            )
            history = appendRate(history, lastRate)
            emit(capture(sampling = true))

            reportCategories()
        }
    }

    /**
     * Drain the per-category counters into the Observability sink.
     *
     * Best effort in the strongest sense: it swallows everything. A statistic
     * that cannot be attributed must never disturb a live tunnel, and the
     * counters it failed to read are simply gone -- which is the right
     * trade against holding them and retrying inside the sampling loop.
     */
    private suspend fun reportCategories() {
        val sink = categorySink ?: return
        if (!categoryAttributionEnabled()) return
        runCatching {
            val deltas = source.readAndResetCategoryTraffic()
            if (deltas.isNotEmpty()) sink(deltas)
        }
    }

    /**
     * The tunnel is not running.
     *
     * Only IDLE clears the session. STARTING is also published by a mode
     * switch and by the rollback that follows a failed one, and those are the
     * same session; STOPPING, USER_STOPPING and ERROR keep the last numbers on
     * screen, because a session that just ended -- or one the kill switch is
     * holding -- is exactly when its totals are worth reading. Every one of
     * those paths passes through IDLE before another tunnel can start, so the
     * next session still begins from zero.
     */
    private fun settled(state: VpnState): TrafficSnapshot = when (state) {
        VpnState.IDLE -> {
            reset()
            TrafficSnapshot()
        }
        VpnState.STARTING,
        VpnState.RUNNING,
        VpnState.STOPPING,
        VpnState.USER_STOPPING,
        VpnState.ERROR,
        -> capture(sampling = false)
    }

    private fun reset() {
        sessionStartNanos = null
        lastSampleNanos = 0L
        uplinkTotal = 0L
        downlinkTotal = 0L
        uptimeMillis = 0L
        history = emptyList()
        lastRate = TrafficRate.ZERO
    }

    /**
     * Uptime advances only while sampling, so a held or stopping tunnel shows
     * the duration it actually carried traffic instead of counting upward for
     * as long as the screen stays open.
     */
    private fun capture(sampling: Boolean): TrafficSnapshot {
        val started = sessionStartNanos
        if (sampling && started != null) {
            uptimeMillis = (nanoTime() - started) / 1_000_000L
        }
        return TrafficSnapshot(
            sampling = sampling,
            rate = if (sampling) lastRate else TrafficRate.ZERO,
            uplinkBytes = uplinkTotal,
            downlinkBytes = downlinkTotal,
            uptimeMillis = uptimeMillis,
            history = history,
        )
    }

    companion object {
        const val SAMPLE_INTERVAL_MS = 1_000L

        /**
         * Keeps sampling briefly after the last collector goes away so that
         * rotating the phone does not restart the series.
         */
        const val SUBSCRIPTION_GRACE_MS = 3_000L

        /**
         * 类别归因开着时，一次订阅续租多久；到期重新读一遍开关。
         * 期间采样照常按 [SAMPLE_INTERVAL_MS] 进行。
         */
        const val ATTRIBUTION_LEASE_MS = 60_000L

        /** 归因关着时多久重新问一次开关。 */
        const val ATTRIBUTION_RECHECK_MS = 60_000L
    }
}
