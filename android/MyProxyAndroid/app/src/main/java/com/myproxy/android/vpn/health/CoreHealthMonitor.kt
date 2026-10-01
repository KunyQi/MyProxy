package com.myproxy.android.vpn.health

import com.myproxy.android.logging.AndroidAppLogger
import com.myproxy.android.logging.AppLogger
import com.myproxy.android.xray.TrafficRate
import com.myproxy.android.xray.TrafficStatsMonitor
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Audits the process the Xray core runs in, and decides when the core is
 * worth restarting behind the user's back.
 *
 * All of the judgement is in the pure functions in [CoreHealth]; this class
 * holds the per-session bookkeeping those functions need and publishes a
 * [report] for the diagnostics screen. The restart itself belongs to
 * `MyProxyVpnService`, which owns the TUN descriptor and the command lock --
 * this class never touches the core.
 */
internal class CoreHealthMonitor(
    private val trafficStats: TrafficStatsMonitor,
    private val isScreenInteractive: () -> Boolean,
    private val metrics: CoreHealthMetricsSource = ProcessMetricsReader(),
    private val logger: AppLogger = AndroidAppLogger(),
    private val budget: CoreHealthBudget = CoreHealthBudget(),
    private val trafficWindowMs: Long = TRAFFIC_WINDOW_MS,
) {
    private val _report = MutableStateFlow<CoreHealthReport?>(null)

    /** Null until the first audit of a session has something to say. */
    val report: StateFlow<CoreHealthReport?> = _report.asStateFlow()

    private var baselinePssKb: Long? = null
    private var coreStartedAtMs: Long? = null
    private var previousSample: CoreHealthSample? = null
    private var lastHealAtMs: Long? = null
    private var healCount = 0
    private var healingAbandoned = false

    /** PSS measured just before the last silent restart, awaiting its verdict. */
    private var pssAwaitingVerificationKb: Long? = null

    /**
     * Called before the core is started, so the first reading is a floor that
     * does not yet include it.
     *
     * The floor is usually taken with the UI in the foreground, and the
     * process shrinks once the activity goes away -- so growth measured
     * against it understates. That is the safe direction, and [evaluate]
     * lowers the floor whenever it sees the process live below it.
     */
    fun onCoreStarting() {
        val sample = metrics.read()
        baselinePssKb = sample?.totalPssKb
        coreStartedAtMs = sample?.elapsedRealtimeMs
        previousSample = sample
        healingAbandoned = false
        pssAwaitingVerificationKb = null
        healCount = 0
        lastHealAtMs = null
        _report.value = null
    }

    fun onCoreStopped() {
        baselinePssKb = null
        coreStartedAtMs = null
        previousSample = null
        pssAwaitingVerificationKb = null
        _report.value = null
    }

    /**
     * Takes a reading and returns what should be done about it.
     *
     * Traffic is read from [TrafficStatsMonitor] rather than from the core
     * directly, because the core's counters reset as they are read and may
     * only have one owner. The read is skipped when
     * [trafficReadingMatters] says it cannot change the outcome.
     */
    suspend fun evaluate(
        selfHealEnabled: Boolean,
        tunnelBusy: Boolean,
    ): SelfHealVerdict {
        val sample = metrics.read() ?: return SelfHealVerdict.SKIP_NO_DATA
        // The reading taken at start can fail. Adopt this one as the start and
        // the floor rather than writing the whole session off; warm-up then
        // delays any action, which is the safe direction.
        val startedAt = coreStartedAtMs ?: sample.elapsedRealtimeMs.also {
            coreStartedAtMs = it
            baselinePssKb = sample.totalPssKb
        }

        verifyPreviousHeal(sample)

        // The smallest the process has been since the core started is a
        // better floor than the one taken at start: it is a size the process
        // has actually demonstrated it can run at.
        val baseline = lowerBaseline(baselinePssKb, sample.totalPssKb)
        baselinePssKb = baseline

        val previous = previousSample
        val cpuPercent = if (previous == null) {
            0
        } else {
            cpuPercentBetween(
                previousTicks = previous.cpuTicks,
                currentTicks = sample.cpuTicks,
                elapsedMs = sample.elapsedRealtimeMs - previous.elapsedRealtimeMs,
                ticksPerSecond = metrics.cpuTicksPerSecond,
            )
        }
        previousSample = sample

        val peak = if (
            trafficReadingMatters(
                selfHealEnabled = selfHealEnabled,
                tunnelBusy = tunnelBusy,
                healingAbandoned = healingAbandoned,
            )
        ) {
            trafficStats.peakRateOver(trafficWindowMs)
        } else {
            // Cannot affect the verdict; the checks that rule it out come
            // before any use of traffic in selfHealVerdict.
            TrafficRate.ZERO
        }
        val peakBytesPerSecond = maxOf(peak.uplinkBytesPerSecond, peak.downlinkBytesPerSecond)
        val growthKb = sample.totalPssKb - baseline

        val verdict = selfHealVerdict(
            selfHealEnabled = selfHealEnabled,
            tunnelBusy = tunnelBusy,
            coreUptimeMs = sample.elapsedRealtimeMs - startedAt,
            screenInteractive = isScreenInteractive(),
            peakTrafficBytesPerSecond = peakBytesPerSecond,
            pssGrowthKb = growthKb,
            cpuPercent = cpuPercent,
            msSinceLastHeal = lastHealAtMs?.let { sample.elapsedRealtimeMs - it },
            healCount = healCount,
            healingAbandoned = healingAbandoned,
            budget = budget,
        )

        _report.value = CoreHealthReport(
            totalPssKb = sample.totalPssKb,
            nativePssKb = sample.nativePssKb,
            pssGrowthKb = growthKb,
            cpuPercent = cpuPercent,
            coreUptimeMs = sample.elapsedRealtimeMs - startedAt,
            healCount = healCount,
            healingAbandoned = healingAbandoned,
        )

        // Memory figures are not secrets and this is the only record of why a
        // tunnel restarted itself, so the audit is always logged.
        logger.i(
            TAG,
            "audit pss=${sample.totalPssKb}KiB growth=${growthKb}KiB cpu=$cpuPercent% " +
                "peak=${peakBytesPerSecond}B/s heals=$healCount -> $verdict",
        )
        if (verdict == SelfHealVerdict.HEAL_MEMORY || verdict == SelfHealVerdict.HEAL_SPINNING) {
            pssAwaitingVerificationKb = sample.totalPssKb
        }
        return verdict
    }

    fun onHealSucceeded() {
        healCount += 1
        lastHealAtMs = previousSample?.elapsedRealtimeMs
        _report.update { it?.copy(healCount = healCount) }
        logger.w(TAG, "core restarted by the health audit (heal $healCount)")
    }

    /**
     * The restart this audit asked for did not happen -- it failed, or the
     * service declined it because something else took the core in the
     * meantime.
     *
     * Dropping the pending verification is the point. A restart that never
     * ran would otherwise be judged by the next audit as one that reclaimed
     * nothing, and self-heal would be abandoned for the session over a
     * restart that was never attempted.
     *
     * A failure itself is the service's problem, not the audit's: it hands it
     * to the unexpected-failure path, which either holds a blocking tunnel or
     * tears it down.
     */
    fun onHealNotCompleted(reason: String) {
        pssAwaitingVerificationKb = null
        logger.w(TAG, "self-heal did not complete ($reason); verification dropped")
    }

    /**
     * Measures current PSS for a benchmark before the core is restarted.
     * Returned value should be passed to [recordBenchmarkResult] after the
     * restart.
     */
    fun takeBenchmarkPreSample(): Long? = metrics.read()?.totalPssKb

    /**
     * Measures current PSS after a manual benchmark restart and logs the
     * recovery amount. Does not affect production session state.
     */
    fun recordBenchmarkResult(prePssKb: Long) {
        val postPssKb = metrics.read()?.totalPssKb ?: return
        val reclaimed = prePssKb - postPssKb
        logger.i(TAG, "benchmark recovery report: before=${prePssKb}KiB after=${postPssKb}KiB reclaimed=${reclaimed}KiB")
    }

    private fun verifyPreviousHeal(sample: CoreHealthSample) {
        val before = pssAwaitingVerificationKb ?: return
        pssAwaitingVerificationKb = null
        if (healWasEffective(before, sample.totalPssKb, budget.minimumReclaimKb)) {
            logger.i(TAG, "the last restart reclaimed ${before - sample.totalPssKb}KiB")
            return
        }
        // Restarting the core does not restart the process, so there was never
        // a guarantee this would help. It did not, so stop doing it.
        healingAbandoned = true
        logger.w(
            TAG,
            "the last restart reclaimed ${before - sample.totalPssKb}KiB; " +
                "abandoning self-heal for this session",
        )
    }

    companion object {
        private const val TAG = "CoreHealthMonitor"

        /**
         * Long enough for the traffic monitor's first, deliberately
         * rate-less baseline sample plus a couple of real ones.
         */
        const val TRAFFIC_WINDOW_MS = 3_500L

        /** How often the service asks for an audit. */
        const val AUDIT_INTERVAL_MS = 5 * 60_000L
    }
}

/** The floor only ever moves down; see [CoreHealthMonitor.evaluate]. */
internal fun lowerBaseline(currentBaselineKb: Long?, sampleKb: Long): Long =
    if (currentBaselineKb == null) sampleKb else minOf(currentBaselineKb, sampleKb)
