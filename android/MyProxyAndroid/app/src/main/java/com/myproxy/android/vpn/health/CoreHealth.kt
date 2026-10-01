package com.myproxy.android.vpn.health

/**
 * One reading of the process the Xray core runs inside.
 *
 * "Inside" is the whole point, and the honest limit of this feature: the core
 * is a Go library loaded into MyProxy's own process, not a child process, so
 * there is no per-core memory figure to read anywhere. Bloat is inferred by
 * comparing the process against a baseline captured just before the core was
 * started, which is only attributable to the core while nothing else in the
 * process is growing -- true when the screen is off and the UI is gone, which
 * is exactly when [selfHealVerdict] is allowed to act.
 */
internal data class CoreHealthSample(
    /** Monotonic, and includes deep sleep. */
    val elapsedRealtimeMs: Long,
    /** Whole-process proportional set size, in KiB. */
    val totalPssKb: Long,
    /** Native slice of the same, in KiB. Diagnostics only. */
    val nativePssKb: Long,
    /** Cumulative process utime + stime, in clock ticks. */
    val cpuTicks: Long,
)

/** What the diagnostics screen shows. */
data class CoreHealthReport(
    val totalPssKb: Long,
    val nativePssKb: Long,
    val pssGrowthKb: Long,
    val cpuPercent: Int,
    val coreUptimeMs: Long,
    val healCount: Int,
    val healingAbandoned: Boolean,
)

/**
 * Thresholds for the audit, in one place so they can be read as a policy and
 * overridden in tests.
 */
internal data class CoreHealthBudget(
    /**
     * A freshly started core climbs to a steady state; restarting during that
     * climb would mistake normal warm-up for a leak.
     */
    val warmUpMs: Long = 10 * 60_000L,

    /** Growth over the pre-start baseline that counts as bloat. */
    val pssGrowthBudgetKb: Long = 180L * 1024L,

    /** Process CPU that, with no traffic to justify it, reads as a spin. */
    val spinCpuPercent: Int = 25,

    /** "No traffic to justify it", for the spin check. */
    val spinTrafficCeilingBytesPerSecond: Long = 2L * 1024L,

    /** Traffic below which a restart is cheap enough to do silently. */
    val idleTrafficCeilingBytesPerSecond: Long = 8L * 1024L,

    /** Minimum gap between silent restarts. */
    val healCooldownMs: Long = 30 * 60_000L,

    /** Hard stop, so a core that always looks sick cannot be restarted forever. */
    val maxHealsPerSession: Int = 3,

    /**
     * Reclaim below which a restart is judged to have achieved nothing. See
     * [healWasEffective] for why that judgement has to exist at all.
     */
    val minimumReclaimKb: Long = 32L * 1024L,
)

/**
 * The audit's conclusion. An enum rather than a boolean so the reason can be
 * logged and asserted, which matters for a mechanism that restarts the data
 * plane behind the user's back.
 */
internal enum class SelfHealVerdict {
    /** The process grew past its budget. Restart the core. */
    HEAL_MEMORY,

    /** CPU is burning with no traffic to explain it. Restart the core. */
    HEAL_SPINNING,

    /** The reading could not be taken, so nothing is concluded. */
    SKIP_NO_DATA,

    /** The user turned self-heal off; the audit still reports. */
    SKIP_DISABLED,

    SKIP_HEALING_ABANDONED,
    SKIP_BUSY,
    SKIP_WARMING_UP,
    SKIP_HEAL_BUDGET_SPENT,
    SKIP_COOLING_DOWN,
    SKIP_HEALTHY,
    SKIP_SCREEN_ON,
    SKIP_TRAFFIC_ACTIVE,
}

/**
 * Reads cumulative process CPU ticks out of a `/proc/<pid>/stat` line.
 *
 * The parsing is fiddly enough to be worth isolating: field 2 is the
 * executable name in parentheses and may itself contain spaces and
 * parentheses, so the fields can only be counted from the *last* `)`.
 * Everything after it starts at field 3, which puts utime at offset 11 and
 * stime at offset 12.
 */
internal fun parseProcStatCpuTicks(line: String): Long? {
    val afterComm = line.lastIndexOf(')')
    if (afterComm < 0) return null
    val fields = line.substring(afterComm + 1).trim().split(' ').filter { it.isNotEmpty() }
    if (fields.size <= STIME_OFFSET) return null
    val utime = fields[UTIME_OFFSET].toLongOrNull() ?: return null
    val stime = fields[STIME_OFFSET].toLongOrNull() ?: return null
    if (utime < 0L || stime < 0L) return null
    return utime + stime
}

/**
 * Process CPU use between two samples, as a percentage of one core. Can
 * legitimately exceed 100 on a multi-core device, and is deliberately not
 * clamped: a core pinning two threads is worth seeing as 200.
 */
internal fun cpuPercentBetween(
    previousTicks: Long,
    currentTicks: Long,
    elapsedMs: Long,
    ticksPerSecond: Long,
): Int {
    if (elapsedMs <= 0L || ticksPerSecond <= 0L) return 0
    val deltaTicks = currentTicks - previousTicks
    // A negative delta means the counter restarted under us, not negative work.
    if (deltaTicks <= 0L) return 0
    val cpuMs = deltaTicks * 1_000L / ticksPerSecond
    return (cpuMs * 100L / elapsedMs).toInt()
}

/**
 * Whether a silent restart actually reclaimed anything.
 *
 * This check is not optional bookkeeping. Restarting the core does not
 * restart the *process*, and Go returns freed spans to the OS on its own
 * schedule, so a restart can reclaim little or nothing -- and if the growth
 * is not the core's at all, it reclaims nothing by construction. Without this
 * the audit would keep finding the same bloat and keep restarting the data
 * plane forever, which is worse than the bloat.
 */
internal fun healWasEffective(
    pssBeforeKb: Long,
    pssAfterKb: Long,
    minimumReclaimKb: Long,
): Boolean = pssBeforeKb - pssAfterKb >= minimumReclaimKb

/**
 * Whether a traffic reading can still change the verdict.
 *
 * Taking one costs a few seconds of sampling, and these three
 * disqualifications are the ones that persist for a whole session -- without
 * this check a user who turned self-heal off would still pay for a sampling
 * burst every few minutes, forever, to compute an answer that was already
 * decided. Kept beside [selfHealVerdict] so the two cannot drift apart.
 */
internal fun trafficReadingMatters(
    selfHealEnabled: Boolean,
    tunnelBusy: Boolean,
    healingAbandoned: Boolean,
): Boolean = selfHealEnabled && !tunnelBusy && !healingAbandoned

/**
 * Decides whether to silently restart the core.
 *
 * The order of the checks is the order of the reasons: cheap
 * disqualifications first, then health, and only then the two gates that say
 * *now is not the moment*. Reporting SKIP_HEALTHY while the screen is on is
 * intentional -- the screen only becomes interesting once there is something
 * worth healing.
 *
 * [msSinceLastHeal] is null when this session has never healed.
 */
internal fun selfHealVerdict(
    selfHealEnabled: Boolean,
    tunnelBusy: Boolean,
    coreUptimeMs: Long,
    screenInteractive: Boolean,
    peakTrafficBytesPerSecond: Long,
    pssGrowthKb: Long,
    cpuPercent: Int,
    msSinceLastHeal: Long?,
    healCount: Int,
    healingAbandoned: Boolean,
    budget: CoreHealthBudget = CoreHealthBudget(),
): SelfHealVerdict {
    if (!selfHealEnabled) return SelfHealVerdict.SKIP_DISABLED
    if (healingAbandoned) return SelfHealVerdict.SKIP_HEALING_ABANDONED
    // A reconfigure, roam, cleanup or kill-switch hold already owns the core.
    if (tunnelBusy) return SelfHealVerdict.SKIP_BUSY
    if (coreUptimeMs < budget.warmUpMs) return SelfHealVerdict.SKIP_WARMING_UP
    if (healCount >= budget.maxHealsPerSession) return SelfHealVerdict.SKIP_HEAL_BUDGET_SPENT
    if (msSinceLastHeal != null && msSinceLastHeal < budget.healCooldownMs) {
        return SelfHealVerdict.SKIP_COOLING_DOWN
    }

    val overMemoryBudget = pssGrowthKb > budget.pssGrowthBudgetKb
    val spinning = cpuPercent >= budget.spinCpuPercent &&
        peakTrafficBytesPerSecond <= budget.spinTrafficCeilingBytesPerSecond
    if (!overMemoryBudget && !spinning) return SelfHealVerdict.SKIP_HEALTHY

    // Something is wrong, but a restart drops every live connection, so it
    // waits for a moment when nobody can notice.
    if (screenInteractive) return SelfHealVerdict.SKIP_SCREEN_ON
    if (peakTrafficBytesPerSecond > budget.idleTrafficCeilingBytesPerSecond) {
        return SelfHealVerdict.SKIP_TRAFFIC_ACTIVE
    }

    return if (overMemoryBudget) SelfHealVerdict.HEAL_MEMORY else SelfHealVerdict.HEAL_SPINNING
}

private const val UTIME_OFFSET = 11
private const val STIME_OFFSET = 12
