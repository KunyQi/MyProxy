package com.myproxy.android.xray

import java.util.Locale

/** Bytes the proxy outbound moved since the previous counter read. */
data class TrafficDelta(
    val uplinkBytes: Long,
    val downlinkBytes: Long,
) {
    companion object {
        val NONE = TrafficDelta(0L, 0L)
    }
}

/** Throughput over one sampling interval. */
data class TrafficRate(
    val uplinkBytesPerSecond: Long,
    val downlinkBytesPerSecond: Long,
) {
    companion object {
        val ZERO = TrafficRate(0L, 0L)
    }
}

/**
 * What the UI renders. [sampling] is false while the tunnel is going down or
 * being held by the kill switch: the session totals are still meaningful, the
 * rate is not.
 */
data class TrafficSnapshot(
    val sampling: Boolean = false,
    val rate: TrafficRate = TrafficRate.ZERO,
    val uplinkBytes: Long = 0L,
    val downlinkBytes: Long = 0L,
    val uptimeMillis: Long = 0L,
    val history: List<TrafficRate> = emptyList(),
)

/**
 * Reads and clears the running core's proxy counters.
 *
 * The core resets a counter as it reports it, so each read returns a delta and
 * exactly one caller may own the reads -- two samplers would each see a
 * fraction of the traffic. [TrafficStatsMonitor] is that owner.
 */
interface TrafficStatsSource {
    suspend fun readAndResetProxyTraffic(): TrafficDelta

    /**
     * Reads and clears the per-category outbound counters, for Observability
     * Plane attribution.
     *
     * Returns an empty map when category attribution is off, which is the
     * default: the tags only exist when the generated config was asked for
     * them. Like [readAndResetProxyTraffic], reading clears, so exactly one
     * caller may own these reads.
     */
    suspend fun readAndResetCategoryTraffic(): Map<String, Long> = emptyMap()
}

/** How many samples the sparkline keeps. At one sample a second, one minute. */
internal const val TRAFFIC_HISTORY_POINTS = 60

/**
 * The sparkline's vertical floor, in bytes per second. Scaling purely to the
 * observed maximum would magnify an idle tunnel's stray kilobyte into a
 * full-height spike; anything below this draws proportionally flat.
 */
internal const val TRAFFIC_SPARKLINE_FLOOR_BYTES_PER_SECOND = 64L * 1024L

private const val NANOS_PER_SECOND = 1_000_000_000L

/**
 * Throughput implied by [deltaBytes] observed over [elapsedNanos].
 *
 * Returns 0 for a non-positive interval and for a negative delta: the core
 * zeroes a counter when it restarts, and a roam or a config switch restarts
 * it mid-session, so a "negative" reading is a restart rather than traffic.
 */
internal fun bytesPerSecond(deltaBytes: Long, elapsedNanos: Long): Long {
    if (deltaBytes <= 0L || elapsedNanos <= 0L) return 0L
    return if (deltaBytes <= Long.MAX_VALUE / NANOS_PER_SECOND) {
        deltaBytes * NANOS_PER_SECOND / elapsedNanos
    } else {
        (deltaBytes / (elapsedNanos.toDouble() / NANOS_PER_SECOND)).toLong()
    }
}

/** Appends [rate], dropping the oldest samples once [maxPoints] is reached. */
internal fun appendRate(
    history: List<TrafficRate>,
    rate: TrafficRate,
    maxPoints: Int = TRAFFIC_HISTORY_POINTS,
): List<TrafficRate> {
    if (maxPoints <= 0) return emptyList()
    val appended = history + rate
    return if (appended.size <= maxPoints) appended else appended.takeLast(maxPoints)
}

/**
 * Maps a rate series onto 0f..1f for drawing, scaled by whichever is larger:
 * the series maximum or [minimumCeilingBytesPerSecond].
 *
 * Passing a caller-computed minimum is how two mirrored series are drawn to
 * the same scale: hand both of them the maximum across both, and neither can
 * be magnified relative to the other.
 */
internal fun normalizedSeries(
    values: List<Long>,
    minimumCeilingBytesPerSecond: Long = TRAFFIC_SPARKLINE_FLOOR_BYTES_PER_SECOND,
): List<Float> {
    if (values.isEmpty()) return emptyList()
    val ceiling = maxOf(values.max(), minimumCeilingBytesPerSecond, 1L)
    return values.map { value ->
        when {
            value <= 0L -> 0f
            value >= ceiling -> 1f
            else -> value.toFloat() / ceiling.toFloat()
        }
    }
}

/** `"0 B"`, `"820 B"`, `"1.4 MB"`. Binary units, US decimal separator. */
fun formatBytes(bytes: Long): String {
    if (bytes <= 0L) return "0 B"
    if (bytes < 1024L) return "$bytes B"
    var value = bytes.toDouble() / 1024.0
    var unit = 0
    while (value >= 1024.0 && unit < BYTE_UNITS.lastIndex) {
        value /= 1024.0
        unit += 1
    }
    return String.format(Locale.US, "%.1f %s", value, BYTE_UNITS[unit])
}

/** [formatBytes] with a per-second suffix. */
fun formatRate(bytesPerSecond: Long): String = "${formatBytes(bytesPerSecond)}/s"

/** `"00:42"` under an hour, `"1:02:03"` beyond it. */
fun formatDuration(millis: Long): String {
    val totalSeconds = (if (millis > 0L) millis else 0L) / 1000L
    val hours = totalSeconds / 3600L
    val minutes = (totalSeconds % 3600L) / 60L
    val seconds = totalSeconds % 60L
    return if (hours > 0L) {
        String.format(Locale.US, "%d:%02d:%02d", hours, minutes, seconds)
    } else {
        String.format(Locale.US, "%02d:%02d", minutes, seconds)
    }
}

private val BYTE_UNITS = arrayOf("KB", "MB", "GB", "TB")
