package com.myproxy.android.ui.haptics

import com.myproxy.android.domain.model.AppState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HapticCueTest {

    @Test
    fun `the first observation never buzzes`() {
        // A null previous state is composition, not a transition: opening the
        // app over a live tunnel must be silent.
        AppState.entries.forEach { state ->
            assertNull("state $state buzzed on first observation", hapticCueForTransition(null, state))
        }
    }

    @Test
    fun `a state that did not change never buzzes`() {
        AppState.entries.forEach { state ->
            assertNull("state $state buzzed without changing", hapticCueForTransition(state, state))
        }
    }

    @Test
    fun `connecting successfully confirms`() {
        assertEquals(HapticCue.CONFIRM, hapticCueForTransition(AppState.CONNECTING, AppState.CONNECTED))
        assertEquals(HapticCue.CONFIRM, hapticCueForTransition(AppState.DISCONNECTED, AppState.CONNECTED))
    }

    @Test
    fun `failure warns from wherever it arrives`() {
        assertEquals(HapticCue.WARN, hapticCueForTransition(AppState.CONNECTING, AppState.ERROR))
        assertEquals(HapticCue.WARN, hapticCueForTransition(AppState.CONNECTED, AppState.ERROR))
        assertEquals(HapticCue.WARN, hapticCueForTransition(AppState.DISCONNECTING, AppState.ERROR))
    }

    @Test
    fun `a completed stop taps but bookkeeping does not`() {
        assertEquals(HapticCue.TAP, hapticCueForTransition(AppState.CONNECTED, AppState.DISCONNECTED))
        assertEquals(HapticCue.TAP, hapticCueForTransition(AppState.DISCONNECTING, AppState.DISCONNECTED))
        assertEquals(HapticCue.TAP, hapticCueForTransition(AppState.CONNECTING, AppState.DISCONNECTED))
        // Finishing a binding, or clearing an error, is not an action the
        // user just performed on the tunnel.
        assertNull(hapticCueForTransition(AppState.UNBOUND, AppState.DISCONNECTED))
        assertNull(hapticCueForTransition(AppState.ERROR, AppState.DISCONNECTED))
    }

    @Test
    fun `intermediate states stay silent`() {
        assertNull(hapticCueForTransition(AppState.DISCONNECTED, AppState.CONNECTING))
        assertNull(hapticCueForTransition(AppState.CONNECTED, AppState.DISCONNECTING))
        assertNull(hapticCueForTransition(AppState.CONNECTED, AppState.UNBOUND))
    }

    @Test
    fun `two taps a moment apart are felt as one`() {
        // Measured on the emulator: tap-to-stop acknowledged at +0 ms, stop
        // completed at +190 ms -- back to back, it read as the "connected" beat.
        assertFalse(tapIsDistinct(190))
        assertTrue(tapIsDistinct(TAP_MERGE_WINDOW_MS))
        assertTrue(tapIsDistinct(2_000))
    }
}
