package com.myproxy.android.domain.observability

import java.util.Locale
import java.util.TimeZone

/**
 * Folds the core's per-tag byte counters into hourly, per-category totals.
 *
 * Pure logic: no I/O and no clock — the caller passes the time in. That makes
 * the awkward cases testable on the JVM, which matters here because those
 * cases (crossing an hour boundary, the core restarting and zeroing its
 * counters, a tag disappearing and coming back after a roam) only ever happen
 * by accident in a real session. This is the same reason
 * `VpnCleanupPolicy` and `ConnectionPolicies` are pure.
 *
 * **The Android core reports deltas, not cumulative totals.** Reading a
 * counter clears it, so each reading is already the bytes since the last
 * read — unlike Windows, where xray's counters are cumulative and the client
 * has to subtract. That is why this class adds readings straight up and has
 * no counter-reset branch: a restart simply means the next reading starts
 * from a fresh zero, which is exactly what "the bytes since last read" means
 * anyway.
 */
class UsageAccumulator {
    private val pending = LinkedHashMap<String, Long>()
    private var currentBucket: String = ""

    /** The hour bucket being accumulated into; empty before the first sample. */
    val bucket: String get() = currentBucket

    val hasPending: Boolean get() = pending.isNotEmpty()

    /** Fold one reading of per-tag deltas into the current hour. */
    fun observe(deltasByTag: Map<String, Long>, nowMillis: Long) {
        if (currentBucket.isEmpty()) currentBucket = hourBucket(nowMillis)

        for ((tag, delta) in deltasByTag) {
            if (delta <= 0L) continue
            val category = ServiceCategories.categoryForTag(tag)
            // An unknown tag (direct, blocked, or one added later) is not
            // attributed. Putting it in some category would make the numbers
            // look more complete at the cost of them being wrong.
            if (category.isEmpty()) continue
            pending[category] = (pending[category] ?: 0L) + delta
        }
    }

    /**
     * Whether to report now: the hour has turned and there is something to
     * send.
     *
     * Reporting only on the turn, rather than on every sample, is what keeps
     * the request inside the window the server accepts (the current hour or
     * the one before it) while holding it to one request an hour.
     */
    fun shouldFlush(nowMillis: Long): Boolean =
        pending.isNotEmpty() && currentBucket.isNotEmpty() && hourBucket(nowMillis) != currentBucket

    /**
     * Take the pending bucket and clear it. The returned bucket is the hour
     * the data **belongs to**, not the current one.
     */
    fun flush(nowMillis: Long): Pair<String, Map<String, Long>> {
        val snapshot = LinkedHashMap(pending)
        val flushed = currentBucket
        pending.clear()
        currentBucket = hourBucket(nowMillis)
        return flushed to snapshot
    }

    companion object {
        /** `yyyy-MM-ddTHH:00:00Z` for the whole UTC hour containing [millis]. */
        fun hourBucket(millis: Long): String {
            val calendar = java.util.Calendar.getInstance(TimeZone.getTimeZone("UTC"))
            calendar.timeInMillis = millis
            return String.format(
                Locale.US,
                "%04d-%02d-%02dT%02d:00:00Z",
                calendar.get(java.util.Calendar.YEAR),
                calendar.get(java.util.Calendar.MONTH) + 1,
                calendar.get(java.util.Calendar.DAY_OF_MONTH),
                calendar.get(java.util.Calendar.HOUR_OF_DAY),
            )
        }
    }
}
