package com.myproxy.android.vpn.health

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CoreHealthTest {

    // ---- /proc/<pid>/stat parsing -----------------------------------------

    @Test
    fun `cpu ticks are the sum of user and system time`() {
        assertEquals(333L, parseProcStatCpuTicks(PROC_STAT))
    }

    @Test
    fun `a process name containing spaces and parentheses is handled`() {
        // Field 2 is unquoted and may contain anything, which is why the
        // fields can only be counted from the last ')'. A parser that split
        // on whitespace would read the wrong two numbers here and report
        // nonsense CPU forever.
        val line = "4242 (weird (name) here) S 1 4242 0 0 -1 1077936448 " +
            "12345 0 67 0 111 222 0 0 20 0 25 0"
        assertEquals(333L, parseProcStatCpuTicks(line))
    }

    @Test
    fun `unparseable stat lines yield nothing rather than a wrong number`() {
        assertNull(parseProcStatCpuTicks(""))
        assertNull(parseProcStatCpuTicks("no parenthesis here at all"))
        assertNull(parseProcStatCpuTicks("4242 (proc) S 1 2 3"))
        assertNull(parseProcStatCpuTicks("4242 (proc) S 1 2 3 4 5 6 7 8 9 x y 0 0"))
    }

    // ---- CPU percentage ---------------------------------------------------

    @Test
    fun `cpu percentage is work over wall time`() {
        // 100 ticks at 100Hz is 1s of CPU; over 10s of wall time that is 10%.
        assertEquals(10, cpuPercentBetween(0L, 100L, 10_000L, 100L))
        assertEquals(50, cpuPercentBetween(1_000L, 1_500L, 10_000L, 100L))
    }

    @Test
    fun `more than one busy core reads above one hundred`() {
        // Deliberately not clamped: two pinned threads should look like 200.
        assertEquals(200, cpuPercentBetween(0L, 2_000L, 10_000L, 100L))
    }

    @Test
    fun `degenerate intervals report no cpu use`() {
        assertEquals(0, cpuPercentBetween(0L, 100L, 0L, 100L))
        assertEquals(0, cpuPercentBetween(0L, 100L, -1L, 100L))
        assertEquals(0, cpuPercentBetween(0L, 100L, 10_000L, 0L))
        // A counter that went backwards restarted under us.
        assertEquals(0, cpuPercentBetween(500L, 100L, 10_000L, 100L))
    }

    // ---- baseline and effectiveness ---------------------------------------

    @Test
    fun `the memory floor only ever moves down`() {
        assertEquals(1_000L, lowerBaseline(null, 1_000L))
        assertEquals(800L, lowerBaseline(1_000L, 800L))
        assertEquals(1_000L, lowerBaseline(1_000L, 5_000L))
    }

    @Test
    fun `a restart that reclaimed nothing is not effective`() {
        // Restarting the core does not restart the process, so this can and
        // does happen; the audit has to notice and stop.
        assertFalse(healWasEffective(500_000L, 499_000L, 32_768L))
        assertFalse(healWasEffective(500_000L, 520_000L, 32_768L))
        assertTrue(healWasEffective(500_000L, 400_000L, 32_768L))
        assertTrue(healWasEffective(500_000L, 467_232L, 32_768L))
    }

    // ---- the verdict ------------------------------------------------------

    @Test
    fun `a healthy idle core is left alone`() {
        assertEquals(SelfHealVerdict.SKIP_HEALTHY, verdict())
    }

    @Test
    fun `growth past the budget heals`() {
        assertEquals(
            SelfHealVerdict.HEAL_MEMORY,
            verdict(pssGrowthKb = BUDGET.pssGrowthBudgetKb + 1L),
        )
        assertEquals(
            SelfHealVerdict.SKIP_HEALTHY,
            verdict(pssGrowthKb = BUDGET.pssGrowthBudgetKb),
        )
    }

    @Test
    fun `cpu burning with no traffic to explain it heals`() {
        assertEquals(
            SelfHealVerdict.HEAL_SPINNING,
            verdict(cpuPercent = 40, peakTrafficBytesPerSecond = 0L),
        )
    }

    @Test
    fun `cpu with traffic behind it is just work`() {
        assertEquals(
            SelfHealVerdict.SKIP_HEALTHY,
            verdict(cpuPercent = 40, peakTrafficBytesPerSecond = 100L * 1024L),
        )
    }

    @Test
    fun `a restart waits for the screen to go off`() {
        assertEquals(
            SelfHealVerdict.SKIP_SCREEN_ON,
            verdict(pssGrowthKb = BLOATED, screenInteractive = true),
        )
    }

    @Test
    fun `a restart waits for traffic to die down`() {
        // Silently killing a background transfer is the outcome this gate
        // exists to prevent.
        assertEquals(
            SelfHealVerdict.SKIP_TRAFFIC_ACTIVE,
            verdict(
                pssGrowthKb = BLOATED,
                peakTrafficBytesPerSecond = BUDGET.idleTrafficCeilingBytesPerSecond + 1L,
            ),
        )
        assertEquals(
            SelfHealVerdict.HEAL_MEMORY,
            verdict(
                pssGrowthKb = BLOATED,
                peakTrafficBytesPerSecond = BUDGET.idleTrafficCeilingBytesPerSecond,
            ),
        )
    }

    @Test
    fun `a warming up core is never restarted`() {
        // Memory climbing toward a steady state is not a leak.
        assertEquals(
            SelfHealVerdict.SKIP_WARMING_UP,
            verdict(pssGrowthKb = BLOATED, coreUptimeMs = BUDGET.warmUpMs - 1L),
        )
    }

    @Test
    fun `restarts are capped and spaced out`() {
        assertEquals(
            SelfHealVerdict.SKIP_HEAL_BUDGET_SPENT,
            verdict(pssGrowthKb = BLOATED, healCount = BUDGET.maxHealsPerSession),
        )
        assertEquals(
            SelfHealVerdict.SKIP_COOLING_DOWN,
            verdict(pssGrowthKb = BLOATED, msSinceLastHeal = BUDGET.healCooldownMs - 1L),
        )
        assertEquals(
            SelfHealVerdict.HEAL_MEMORY,
            verdict(pssGrowthKb = BLOATED, msSinceLastHeal = BUDGET.healCooldownMs, healCount = 1),
        )
    }

    @Test
    fun `an operation already owning the core wins`() {
        assertEquals(
            SelfHealVerdict.SKIP_BUSY,
            verdict(pssGrowthKb = BLOATED, tunnelBusy = true),
        )
    }

    @Test
    fun `a session that proved restarts useless stops trying`() {
        assertEquals(
            SelfHealVerdict.SKIP_HEALING_ABANDONED,
            verdict(pssGrowthKb = BLOATED, healingAbandoned = true),
        )
    }

    @Test
    fun `the setting outranks every other reason`() {
        assertEquals(
            SelfHealVerdict.SKIP_DISABLED,
            verdict(
                selfHealEnabled = false,
                pssGrowthKb = BLOATED,
                healingAbandoned = true,
                tunnelBusy = true,
            ),
        )
    }

    @Test
    fun `a traffic reading is only paid for when it can change the answer`() {
        assertTrue(trafficReadingMatters(true, tunnelBusy = false, healingAbandoned = false))
        // Each of these outranks every traffic-dependent branch in the
        // verdict, so sampling for one would be pure waste.
        assertFalse(trafficReadingMatters(false, tunnelBusy = false, healingAbandoned = false))
        assertFalse(trafficReadingMatters(true, tunnelBusy = true, healingAbandoned = false))
        assertFalse(trafficReadingMatters(true, tunnelBusy = false, healingAbandoned = true))
    }

    @Test
    fun `skipping the traffic reading cannot cause a heal`() {
        // When the reading is skipped the monitor passes zero, which on its
        // own looks like a spin. The reasons for skipping must therefore all
        // be checked before traffic is ever consulted.
        for (enabled in listOf(true, false)) {
            for (busy in listOf(true, false)) {
                for (abandoned in listOf(true, false)) {
                    if (trafficReadingMatters(enabled, busy, abandoned)) continue
                    val result = verdict(
                        selfHealEnabled = enabled,
                        tunnelBusy = busy,
                        healingAbandoned = abandoned,
                        cpuPercent = 400,
                        pssGrowthKb = BLOATED,
                        peakTrafficBytesPerSecond = 0L,
                    )
                    assertTrue(
                        "enabled=$enabled busy=$busy abandoned=$abandoned gave $result",
                        result == SelfHealVerdict.SKIP_DISABLED ||
                            result == SelfHealVerdict.SKIP_HEALING_ABANDONED ||
                            result == SelfHealVerdict.SKIP_BUSY,
                    )
                }
            }
        }
    }

    @Test
    fun `no verdict ever heals while the screen is on`() {
        // The one property that matters most: a restart drops live
        // connections, so it must be impossible while anyone is looking.
        val healing = setOf(SelfHealVerdict.HEAL_MEMORY, SelfHealVerdict.HEAL_SPINNING)
        for (growth in listOf(0L, BLOATED, BLOATED * 10L)) {
            for (cpu in listOf(0, 25, 400)) {
                val result = verdict(
                    pssGrowthKb = growth,
                    cpuPercent = cpu,
                    screenInteractive = true,
                )
                assertFalse("growth=$growth cpu=$cpu gave $result", result in healing)
            }
        }
    }

    private fun verdict(
        selfHealEnabled: Boolean = true,
        tunnelBusy: Boolean = false,
        coreUptimeMs: Long = BUDGET.warmUpMs * 2L,
        screenInteractive: Boolean = false,
        peakTrafficBytesPerSecond: Long = 0L,
        pssGrowthKb: Long = 0L,
        cpuPercent: Int = 1,
        msSinceLastHeal: Long? = null,
        healCount: Int = 0,
        healingAbandoned: Boolean = false,
    ): SelfHealVerdict = selfHealVerdict(
        selfHealEnabled = selfHealEnabled,
        tunnelBusy = tunnelBusy,
        coreUptimeMs = coreUptimeMs,
        screenInteractive = screenInteractive,
        peakTrafficBytesPerSecond = peakTrafficBytesPerSecond,
        pssGrowthKb = pssGrowthKb,
        cpuPercent = cpuPercent,
        msSinceLastHeal = msSinceLastHeal,
        healCount = healCount,
        healingAbandoned = healingAbandoned,
        budget = BUDGET,
    )

    private companion object {
        val BUDGET = CoreHealthBudget()
        val BLOATED = BUDGET.pssGrowthBudgetKb * 2L

        const val PROC_STAT = "4242 (com.myproxy.android) S 1 4242 0 0 -1 1077936448 " +
            "12345 0 67 0 111 222 0 0 20 0 25 0 123456 987654321 4096"
    }
}
