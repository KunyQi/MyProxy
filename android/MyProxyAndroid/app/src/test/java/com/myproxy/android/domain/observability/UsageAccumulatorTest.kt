package com.myproxy.android.domain.observability

import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.domain.model.ServerProfile
import com.myproxy.android.xray.XrayConfigGenerator
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class UsageAccumulatorTest {

    /** 2026-09-20T12:05:00Z */
    private val noon = 1_789_214_700_000L

    private val hour = 3_600_000L

    @Test
    fun `hour bucket truncates to the whole utc hour`() {
        val bucket = UsageAccumulator.hourBucket(noon)
        assertTrue(bucket, bucket.endsWith(":00:00Z"))
        assertEquals(bucket, UsageAccumulator.hourBucket(noon + 600_000L))
        assertNotEquals(bucket, UsageAccumulator.hourBucket(noon + hour))
    }

    @Test
    fun `deltas accumulate per category`() {
        val accumulator = UsageAccumulator()
        accumulator.observe(mapOf("cat-video" to 100L, "cat-social" to 50L), noon)
        accumulator.observe(mapOf("cat-video" to 20L), noon)

        val (bucket, categories) = accumulator.flush(noon + hour)

        assertEquals(UsageAccumulator.hourBucket(noon), bucket)
        assertEquals(120L, categories[ServiceCategories.VIDEO])
        assertEquals(50L, categories[ServiceCategories.SOCIAL])
    }

    @Test
    fun `unknown tags are not attributed to anything`() {
        // Putting direct or blocked traffic in some category would make the
        // numbers look more complete at the cost of them being wrong.
        val accumulator = UsageAccumulator()
        accumulator.observe(mapOf("proxy" to 500L, "direct" to 900L, "blocked" to 1L), noon)

        val (_, categories) = accumulator.flush(noon + hour)
        assertEquals(0, categories.size)
    }

    @Test
    fun `non positive deltas are ignored`() {
        // The core zeroes a counter when it restarts, and a roam restarts it
        // mid-session, so a non-positive reading is a restart, not traffic.
        val accumulator = UsageAccumulator()
        accumulator.observe(mapOf("cat-video" to 0L, "cat-social" to -5L), noon)

        assertFalse(accumulator.hasPending)
    }

    @Test
    fun `flush only happens after the hour turns`() {
        val accumulator = UsageAccumulator()
        accumulator.observe(mapOf("cat-video" to 10L), noon)

        assertFalse(accumulator.shouldFlush(noon + 1_800_000L))
        assertTrue(accumulator.shouldFlush(noon + hour))
    }

    @Test
    fun `nothing pending never flushes`() {
        val accumulator = UsageAccumulator()
        assertFalse(accumulator.shouldFlush(noon + 5 * hour))

        accumulator.observe(mapOf("direct" to 10L), noon)
        assertFalse(accumulator.shouldFlush(noon + hour))
    }

    @Test
    fun `flush moves the bucket forward and clears the pending`() {
        val accumulator = UsageAccumulator()
        accumulator.observe(mapOf("cat-messaging" to 10L), noon)
        accumulator.flush(noon + hour)

        assertFalse(accumulator.hasPending)
        assertEquals(UsageAccumulator.hourBucket(noon + hour), accumulator.bucket)
    }

    @Test
    fun `every routed tag maps to a known category`() {
        // The server rejects a category outside its vocabulary rather than
        // folding it into `other`, so one bad mapping fails the whole report.
        for (routed in ServiceCategories.ROUTED) {
            assertEquals(routed.category, ServiceCategories.categoryForTag(routed.tag))
            assertTrue(routed.category, ServiceCategories.isKnown(routed.category))
        }

        assertEquals("", ServiceCategories.categoryForTag("proxy"))
        assertEquals("", ServiceCategories.categoryForTag("direct"))
    }

    @Test
    fun `the vocabulary matches the server's closed list`() {
        assertEquals(
            listOf("video", "social", "messaging", "web", "download", "other"),
            ServiceCategories.ALL,
        )
    }
}

class CategoryRoutingTest {

    private val json = Json { ignoreUnknownKeys = true }

    private val profile = ServerProfile(
        server = "203.0.113.10",
        port = 443,
        uuid = "11111111-2222-3333-4444-555555555555",
        security = "reality",
        publicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        shortId = "0123456789abcdef",
        sni = "www.microsoft.com",
        fingerprint = "chrome",
        flow = "xtls-rprx-vision",
        spiderX = "/",
    )

    private fun outboundTags(categoryAttribution: Boolean): List<String> =
        json.parseToJsonElement(
            XrayConfigGenerator.generate(profile, ProxyMode.RULE, categoryAttribution),
        ).jsonObject.getValue("outbounds").jsonArray.map {
            it.jsonObject.getValue("tag").jsonPrimitive.content
        }

    private fun ruleTags(categoryAttribution: Boolean): List<String> =
        json.parseToJsonElement(
            XrayConfigGenerator.generate(profile, ProxyMode.RULE, categoryAttribution),
        ).jsonObject.getValue("routing").jsonObject.getValue("rules").jsonArray.map {
            it.jsonObject.getValue("outboundTag").jsonPrimitive.content
        }

    private fun userTrafficRuleTags(categoryAttribution: Boolean): List<String> =
        json.parseToJsonElement(
            XrayConfigGenerator.generate(profile, ProxyMode.RULE, categoryAttribution),
        ).jsonObject.getValue("routing").jsonObject.getValue("rules").jsonArray
            .filterNot { rule ->
                rule.jsonObject["inboundTag"]?.jsonArray?.any {
                    it.jsonPrimitive.content == XrayConfigGenerator.PROBE_INBOUND_TAG
                } == true
            }
            .map { it.jsonObject.getValue("outboundTag").jsonPrimitive.content }

    @Test
    fun `disabled produces the same config as before`() {
        // Pins "the data plane does not change by default": every extra tag
        // is another connection pool to the same server, which should only
        // happen when an administrator turns it on for a device.
        assertEquals(listOf("proxy", "direct", "blocked"), outboundTags(false))
        assertTrue(ruleTags(false).none { it.startsWith("cat-") })
    }

    @Test
    fun `enabled adds one outbound per routed category`() {
        val tags = outboundTags(true)
        for (routed in ServiceCategories.ROUTED) {
            assertTrue(routed.tag, tags.contains(routed.tag))
        }
        assertEquals(3 + ServiceCategories.ROUTED.size, tags.size)
    }

    @Test
    fun `category outbounds point at the same server`() {
        // Attribution must never change where traffic goes: the outbounds
        // differ only in their tag.
        val outbounds = json.parseToJsonElement(
            XrayConfigGenerator.generate(profile, ProxyMode.RULE, true),
        ).jsonObject.getValue("outbounds").jsonArray

        // Blank the tag with a regex: JsonElement.toString() is compact while
        // the generator pretty-prints, so a hand-built literal would silently
        // fail to match and turn the assertion into one that always passes.
        val tagPattern = Regex("\"tag\":\\s*\"[^\"]*\"")
        fun normalise(element: kotlinx.serialization.json.JsonElement) =
            tagPattern.replace(element.toString(), "\"tag\":\"X\"")

        val proxy = outbounds.first { it.jsonObject.getValue("tag").jsonPrimitive.content == "proxy" }
        for (routed in ServiceCategories.ROUTED) {
            val candidate = outbounds.first {
                it.jsonObject.getValue("tag").jsonPrimitive.content == routed.tag
            }
            assertEquals(routed.tag, normalise(proxy), normalise(candidate))
        }
    }

    @Test
    fun `category rules come after direct but before blocked`() {
        // Ahead of geosite:cn they would steal rule mode's "domestic sites
        // go direct" meaning; behind geoip:private they would never be
        // reached, because the core takes the first matching rule.
        val tags = userTrafficRuleTags(true)

        val lastDirect = tags.indexOfLast { it == "direct" }
        val firstCategory = tags.indexOfFirst { it.startsWith("cat-") }
        val firstBlocked = tags.indexOfFirst { it == "blocked" }

        assertTrue("categories must follow the direct rules", lastDirect in 0 until firstCategory)
        assertTrue("categories must precede the blocked rules", firstBlocked > firstCategory)
    }
}
