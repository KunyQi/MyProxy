package com.myproxy.android.ui

import com.myproxy.android.domain.model.ProxyMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The user-facing mode names, and the boundary that the rename does not cross.
 *
 * `strings.xml` is read as a plain file rather than through `R.string`:
 * these unit tests run on a bare JVM with no Robolectric and no
 * `returnDefaultValues`, so touching an Android framework class would fail.
 * Reading the resource file directly still pins exactly what ships.
 */
class ProxyModeNamingTest {

    private val strings: String by lazy { resourceFile("values/strings.xml").readText() }

    private fun resourceFile(relative: String): File {
        // Gradle runs unit tests with the module directory as the working
        // directory, but walking up makes this survive being run from the
        // repository root too.
        var directory: File? = File(".").absoluteFile
        while (directory != null) {
            val candidate = File(directory, "app/src/main/res/$relative")
            if (candidate.isFile) return candidate
            val inModule = File(directory, "src/main/res/$relative")
            if (inModule.isFile) return inModule
            directory = directory.parentFile
        }
        throw AssertionError("could not locate res/$relative")
    }

    private fun string(name: String): String {
        val match = Regex("""<string name="$name">([^<]*)</string>""").find(strings)
            ?: throw AssertionError("missing string resource: $name")
        return match.groupValues[1]
    }

    @Test
    fun `names say how traffic flows not that rules are editable`() {
        // "规则" sounds like a rule set the user can add to or edit, while the
        // split policy is built into the client and fixed.
        assertEquals("智能分流", string("main_mode_rule"))
        assertEquals("全局代理", string("main_mode_global"))
    }

    @Test
    fun `both descriptions mention that blocking never stops`() {
        // Mentioning "private addresses and BitTorrent stay blocked" under only
        // one mode would suggest switching modes turns it off.
        for (name in listOf("settings_mode_rule_description", "settings_mode_global_description")) {
            val description = string(name)
            assertTrue(name, description.contains("私网"))
            assertTrue(name, description.contains("BT"))
        }
    }

    @Test
    fun `the old labels are gone from user facing strings`() {
        assertFalse(strings.contains(">规则<"))
        assertFalse(strings.contains(">全局<"))
    }

    @Test
    fun `enum and stored values are unchanged`() {
        // The rename touches wording only. The enum names back stored values
        // and protocol fields; changing them would require a data migration.
        assertEquals("RULE", ProxyMode.RULE.name)
        assertEquals("GLOBAL", ProxyMode.GLOBAL.name)
        assertEquals(0, ProxyMode.RULE.ordinal)
        assertEquals(1, ProxyMode.GLOBAL.ordinal)
    }

    @Test
    fun `the windows client uses the same two words`() {
        // One mode with two names across the two clients is the usual outcome
        // of a rename like this, and nothing else would catch it: the two
        // codebases never reference each other.
        var directory: File? = File(".").absoluteFile
        var shared: File? = null
        while (directory != null && shared == null) {
            val candidate = File(directory, "windows/MyProxy/Core/ProxyModeText.cs")
            if (candidate.isFile) shared = candidate
            directory = directory.parentFile
        }

        // The Windows source is only present in the monorepo checkout; skip
        // rather than fail if this module is ever built on its own.
        val windows = shared ?: return
        val text = windows.readText()
        assertTrue(text.contains("\"${string("main_mode_rule")}\""))
        assertTrue(text.contains("\"${string("main_mode_global")}\""))
    }
}
