package com.myproxy.android.ui.main

import com.myproxy.android.domain.model.AppState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OrbHaloTest {

    @Test
    fun `only a live or failed connection has a halo`() {
        assertEquals(HaloMotion.BREATH, orbHalo(AppState.CONNECTED)?.motion)
        assertEquals(HaloMotion.PULSE, orbHalo(AppState.CONNECTING)?.motion)
        assertEquals(HaloMotion.STILL, orbHalo(AppState.ERROR)?.motion)

        // Idle is plain porcelain; stopping fades the halo out instead of keeping it lit.
        assertNull(orbHalo(AppState.DISCONNECTED))
        assertNull(orbHalo(AppState.UNBOUND))
        assertNull(orbHalo(AppState.DISCONNECTING))
    }

    @Test
    fun `the halo wears the same colour as the status row`() {
        for (state in AppState.entries) {
            val halo = orbHalo(state) ?: continue
            assertEquals(state.name, stateTone(state), halo.tone)
        }
        assertEquals(StateTone.SUCCESS, stateTone(AppState.CONNECTED))
        assertEquals(StateTone.ERROR, stateTone(AppState.ERROR))
        assertEquals(StateTone.ACCENT, stateTone(AppState.CONNECTING))
    }

    @Test
    fun `breathing is quiet, connecting is quicker, and a failure does not move`() {
        assertTrue(HaloMotion.BREATH.halfCycleMs > HaloMotion.PULSE.halfCycleMs)
        assertEquals(0, HaloMotion.STILL.halfCycleMs)
        assertEquals(HaloMotion.STILL.dimAlpha, HaloMotion.STILL.brightAlpha)
        assertEquals(1f, HaloMotion.STILL.swell)

        for (motion in HaloMotion.entries) {
            assertTrue(motion.name, motion.dimAlpha in 0f..motion.brightAlpha)
            // The bright end is where it rests when the system removes animations,
            // so it has to be visible, and still a halo rather than a fill.
            assertTrue(motion.name, motion.brightAlpha in 0.2f..0.6f)
            // The swell must keep the halo inside the orb's 180dp touch box.
            assertTrue(motion.name, 174f * motion.swell <= 180f)
        }
    }
}
