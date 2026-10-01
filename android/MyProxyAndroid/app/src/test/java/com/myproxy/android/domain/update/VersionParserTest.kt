package com.myproxy.android.domain.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class VersionParserTest {

    @Test
    fun `0_2_0 is newer than 0_1_0`() {
        assertTrue(VersionParser.isNewer("0.2.0", "0.1.0"))
    }

    @Test
    fun `equal versions are not newer`() {
        assertFalse(VersionParser.isNewer("0.1.0", "0.1.0"))
    }

    @Test
    fun `invalid versions are not newer`() {
        assertFalse(VersionParser.isNewer("abc", "0.1.0"))
        assertFalse(VersionParser.isNewer("0.2.0", "not-a-version"))
        assertFalse(VersionParser.isNewer("0.2", "0.1.0"))
        assertFalse(VersionParser.isNewer("", "0.1.0"))
        assertFalse(VersionParser.isNewer("-beta", "0.1.0"))
    }

    @Test
    fun `prerelease and build suffixes parse to their numeric core`() {
        // The server's manifest validator accepts these and Windows truncates
        // them the same way. Rejecting them here would make a published
        // prerelease invisible on Android only — one protocol, two answers.
        assertEquals(VersionCode(1, 2, 3), VersionParser.parse("1.2.3-beta.1"))
        assertEquals(VersionCode(1, 2, 3), VersionParser.parse("1.2.3+build.7"))
        assertTrue(VersionParser.isNewer("0.2.0-beta", "0.1.0"))
    }

    @Test
    fun `0_10_0 is newer than 0_9_0`() {
        assertTrue(VersionParser.isNewer("0.10.0", "0.9.0"))
    }

    @Test
    fun `parse builds comparable version code`() {
        assertEquals(VersionCode(1, 2, 3), VersionParser.parse("1.2.3"))
        assertNull(VersionParser.parse("1.2"))
        assertNull(VersionParser.parse("1.2.3.4"))
        assertNull(VersionParser.parse("1.2.x"))
    }
}
