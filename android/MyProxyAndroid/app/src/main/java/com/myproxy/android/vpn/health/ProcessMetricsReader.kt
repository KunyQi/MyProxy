package com.myproxy.android.vpn.health

import android.os.Debug
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import java.io.File

/** Where [CoreHealthMonitor] gets its numbers. Separated so it can be faked. */
internal interface CoreHealthMetricsSource {
    /** Null when the platform would not answer; the audit then skips a tick. */
    fun read(): CoreHealthSample?

    /** Clock ticks per second, for turning CPU ticks into a percentage. */
    val cpuTicksPerSecond: Long
}

/**
 * Reads whole-process memory and CPU.
 *
 * Memory comes from `Debug.MemoryInfo`, which walks `/proc/self/smaps` and is
 * not cheap -- fine at the audit's interval of minutes, not something to put
 * on a per-second timer. CPU comes from `/proc/self/stat`, which is cheap;
 * the awkward part of that file is the parsing, which lives in
 * [parseProcStatCpuTicks] where it is tested.
 */
internal class ProcessMetricsReader : CoreHealthMetricsSource {

    override val cpuTicksPerSecond: Long by lazy {
        runCatching { Os.sysconf(OsConstants._SC_CLK_TCK) }
            .getOrDefault(DEFAULT_TICKS_PER_SECOND)
            .takeIf { it > 0L }
            ?: DEFAULT_TICKS_PER_SECOND
    }

    override fun read(): CoreHealthSample? {
        val memory = runCatching {
            Debug.MemoryInfo().also { Debug.getMemoryInfo(it) }
        }.getOrNull() ?: return null
        val cpuTicks = runCatching {
            parseProcStatCpuTicks(File(PROC_SELF_STAT).readText())
        }.getOrNull() ?: return null

        return CoreHealthSample(
            elapsedRealtimeMs = SystemClock.elapsedRealtime(),
            totalPssKb = memory.getTotalPss().toLong(),
            nativePssKb = memory.nativePss.toLong(),
            cpuTicks = cpuTicks,
        )
    }

    private companion object {
        const val PROC_SELF_STAT = "/proc/self/stat"
        const val DEFAULT_TICKS_PER_SECOND = 100L
    }
}
