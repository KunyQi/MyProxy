package com.myproxy.android.domain.update

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FeatureFlagsTest {

    private fun parse(json: String) = FeatureFlags.fromJson(Json.parseToJsonElement(json))

    @Test
    fun `scalars are kept`() {
        val flags = parse("""{"a": true, "b": false, "c": 42, "d": "text", "e": null}""")

        assertTrue(flags.isEnabled("a"))
        assertFalse(flags.isEnabled("b"))
        assertEquals(42L, flags.getLong("c"))
        assertEquals("text", flags.getString("d"))
    }

    @Test
    fun `nested values are dropped not accepted`() {
        // Nesting is the shape you reach for when smuggling structured
        // instructions down a channel meant for switches.
        val flags = parse("""{"ok": true, "nested": {"x": 1}, "list": [1,2]}""")

        assertTrue(flags.isEnabled("ok"))
        assertEquals(1, flags.size)
    }

    @Test
    fun `an unknown flag does not break the known ones`() {
        // A switch the server added must not stop the ones this build does
        // understand from arriving.
        val flags = parse(
            """{"${KnownFeatureFlags.USAGE_CATEGORIES}": true, "somethingNew": "x"}""",
        )

        assertTrue(flags.isEnabled(KnownFeatureFlags.USAGE_CATEGORIES))
        assertEquals(2, flags.size)
    }

    @Test
    fun `a non object payload yields empty`() {
        assertEquals(0, parse("[]").size)
        assertEquals(0, parse("null").size)
        assertEquals(0, FeatureFlags.fromJson(null).size)
        assertEquals(0, FeatureFlags.EMPTY.size)
    }

    @Test
    fun `a missing flag is disabled`() {
        assertFalse(FeatureFlags.EMPTY.isEnabled(KnownFeatureFlags.DISABLE_AUTO_UPDATE))
        assertEquals(7L, FeatureFlags.EMPTY.getLong("missing", 7L))
        assertEquals("fallback", FeatureFlags.EMPTY.getString("missing", "fallback"))
    }

    @Test
    fun `an over long key is dropped`() {
        val flags = parse("""{"${"x".repeat(80)}": true, "fine": true}""")
        assertEquals(1, flags.size)
        assertTrue(flags.isEnabled("fine"))
    }
}
