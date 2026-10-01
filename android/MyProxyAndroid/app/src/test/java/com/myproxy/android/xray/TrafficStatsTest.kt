package com.myproxy.android.xray

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TrafficStatsTest {

    @Test
    fun `rate is bytes over the sampled interval`() {
        assertEquals(1_000L, bytesPerSecond(1_000L, 1_000_000_000L))
        assertEquals(3_000L, bytesPerSecond(1_500L, 500_000_000L))
        assertEquals(512L, bytesPerSecond(1_024L, 2_000_000_000L))
    }

    @Test
    fun `a core restart is not reported as traffic`() {
        // The core zeroes its counters when it restarts, which a roam and a
        // mode switch both do. A negative or impossible reading must show as
        // idle rather than as a spike.
        assertEquals(0L, bytesPerSecond(-4_096L, 1_000_000_000L))
        assertEquals(0L, bytesPerSecond(4_096L, 0L))
        assertEquals(0L, bytesPerSecond(4_096L, -1L))
        assertEquals(0L, bytesPerSecond(0L, 1_000_000_000L))
    }

    @Test
    fun `an implausibly large delta still produces a finite rate`() {
        // Guards the integer path from overflowing into a negative rate.
        val rate = bytesPerSecond(Long.MAX_VALUE / 2L, 1_000_000_000L)
        assertTrue("rate was $rate", rate > 0L)
    }

    @Test
    fun `history keeps only the newest points`() {
        var history = emptyList<TrafficRate>()
        repeat(5) { index ->
            history = appendRate(history, TrafficRate(index.toLong(), 0L), maxPoints = 3)
        }
        assertEquals(3, history.size)
        assertEquals(listOf(2L, 3L, 4L), history.map { it.uplinkBytesPerSecond })
    }

    @Test
    fun `history tolerates a degenerate window`() {
        assertEquals(emptyList<TrafficRate>(), appendRate(emptyList(), TrafficRate.ZERO, maxPoints = 0))
    }

    @Test
    fun `the sparkline floor keeps a quiet tunnel flat`() {
        // 1 KB/s against a 64 KB/s floor must stay near the baseline instead
        // of filling the chart.
        val series = normalizedSeries(listOf(1_024L))
        assertEquals(1, series.size)
        assertTrue("was ${series[0]}", series[0] < 0.05f)
    }

    @Test
    fun `a shared ceiling keeps two series comparable`() {
        val shared = 1_000_000L
        val quiet = normalizedSeries(listOf(100_000L), shared)
        val busy = normalizedSeries(listOf(1_000_000L), shared)
        assertEquals(0.1f, quiet[0], 0.001f)
        assertEquals(1f, busy[0], 0.001f)
    }

    @Test
    fun `normalization stays inside the drawable range`() {
        // Ceiling is the series maximum here, since it exceeds the minimum.
        val series = normalizedSeries(listOf(-5L, 0L, 1_024L, 2_048L), 1_024L)
        assertEquals(listOf(0f, 0f, 0.5f, 1f), series)
    }

    @Test
    fun `empty input normalizes to nothing`() {
        assertEquals(emptyList<Float>(), normalizedSeries(emptyList()))
    }

    @Test
    fun `byte formatting is stable regardless of device locale`() {
        assertEquals("0 B", formatBytes(0L))
        assertEquals("0 B", formatBytes(-1L))
        assertEquals("820 B", formatBytes(820L))
        assertEquals("1.0 KB", formatBytes(1_024L))
        assertEquals("1.5 MB", formatBytes(1_572_864L))
        assertEquals("2.0 GB", formatBytes(2L * 1024L * 1024L * 1024L))
        assertEquals("1.0 TB", formatBytes(1024L * 1024L * 1024L * 1024L))
    }

    @Test
    fun `rate formatting appends a per-second suffix`() {
        assertEquals("0 B/s", formatRate(0L))
        assertEquals("1.0 KB/s", formatRate(1_024L))
    }

    @Test
    fun `duration grows a leading hour only when needed`() {
        assertEquals("00:00", formatDuration(0L))
        assertEquals("00:00", formatDuration(-5_000L))
        assertEquals("00:42", formatDuration(42_000L))
        assertEquals("12:34", formatDuration(754_000L))
        assertEquals("1:02:03", formatDuration(3_723_000L))
        assertEquals("25:00:00", formatDuration(90_000_000L))
    }
}
