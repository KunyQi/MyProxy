package com.myproxy.android.xray

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The core's only stats call reads **and clears every** outbound counter, while
 * two readers each expect "read and clear mine". These tests pin the split.
 */
class OutboundTrafficLedgerTest {

    @Test
    fun `parses the core's tag direction value list`() {
        val entries = OutboundTrafficLedger.parse("proxy,uplink,120;proxy,downlink,4096;direct,uplink,7;")
        assertEquals(
            listOf(
                OutboundTrafficLedger.Entry("proxy", "uplink", 120),
                OutboundTrafficLedger.Entry("proxy", "downlink", 4096),
                OutboundTrafficLedger.Entry("direct", "uplink", 7),
            ),
            entries,
        )
    }

    @Test
    fun `empty and null answers mean no counters`() {
        assertTrue(OutboundTrafficLedger.parse(null).isEmpty())
        assertTrue(OutboundTrafficLedger.parse("").isEmpty())
        assertTrue(OutboundTrafficLedger.parse(";;").isEmpty())
    }

    @Test
    fun `malformed or non positive segments are skipped rather than thrown`() {
        // Statistics are a readout: one bad segment must not cost the others,
        // and must never surface as an exception inside a live tunnel.
        val entries = OutboundTrafficLedger.parse(
            "proxy,uplink;proxy,downlink,abc;,uplink,5;proxy,,5;proxy,uplink,0;proxy,uplink,-3;" +
                "a,b,c,d;proxy,downlink,9",
        )
        assertEquals(listOf(OutboundTrafficLedger.Entry("proxy", "downlink", 9)), entries)
    }

    @Test
    fun `taking the proxy counters leaves category counters for their own reader`() {
        // The regression this class exists for: the proxy sampler runs first in
        // the same loop. If its core call cleared the category counters and
        // dropped them, attribution would silently read 0 forever.
        val ledger = OutboundTrafficLedger()
        ledger.absorb("proxy,uplink,100;proxy,downlink,200;category-video,uplink,30;category-video,downlink,40")

        assertEquals(100, ledger.take("proxy", "uplink"))
        assertEquals(200, ledger.take("proxy", "downlink"))

        // The category reader pulls from the core again; the core has nothing
        // new because the proxy read already cleared it.
        ledger.absorb("")
        assertEquals(30, ledger.take("category-video", "uplink"))
        assertEquals(40, ledger.take("category-video", "downlink"))
    }

    @Test
    fun `take clears only what it returns`() {
        val ledger = OutboundTrafficLedger()
        ledger.absorb("proxy,uplink,10;proxy,downlink,20")

        assertEquals(10, ledger.take("proxy", "uplink"))
        assertEquals(0, ledger.take("proxy", "uplink"))
        assertEquals(20, ledger.take("proxy", "downlink"))
    }

    @Test
    fun `counts not yet taken accumulate across core reads`() {
        val ledger = OutboundTrafficLedger()
        ledger.absorb("category-video,uplink,5")
        ledger.absorb("category-video,uplink,7;proxy,uplink,1")

        assertEquals(12, ledger.take("category-video", "uplink"))
        assertEquals(1, ledger.take("proxy", "uplink"))
    }

    @Test
    fun `unknown keys answer zero`() {
        assertEquals(0, OutboundTrafficLedger().take("proxy", "uplink"))
    }
}
