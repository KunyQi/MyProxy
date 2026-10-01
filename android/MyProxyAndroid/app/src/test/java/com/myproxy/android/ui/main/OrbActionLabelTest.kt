package com.myproxy.android.ui.main

import com.myproxy.android.R
import com.myproxy.android.domain.model.AppState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OrbActionLabelTest {

    @Test
    fun `the orb label names the action in each state`() {
        assertEquals(R.string.main_orb_start, label(AppState.DISCONNECTED))
        assertEquals(R.string.main_orb_stop, label(AppState.CONNECTED))
        assertEquals(R.string.main_orb_retry, label(AppState.ERROR))
        assertEquals(R.string.main_orb_cancel, label(AppState.CONNECTING))
        assertEquals(R.string.main_status_disconnecting, label(AppState.DISCONNECTING))
    }

    @Test
    fun `a failed blocking tunnel can be stopped from home and retried separately`() {
        assertEquals(
            MainOrbAction.STOP_AND_RELEASE_PROTECTION,
            mainOrbAction(AppState.ERROR, isTrafficBlocked = true),
        )
        assertEquals(
            R.string.main_orb_stop_and_release,
            mainOrbActionLabel(mainOrbAction(AppState.ERROR, isTrafficBlocked = true)),
        )
        assertTrue(showRetryAction(AppState.ERROR, isTrafficBlocked = true))

        assertEquals(MainOrbAction.RETRY, mainOrbAction(AppState.ERROR, isTrafficBlocked = false))
        assertEquals(R.string.main_orb_retry, label(AppState.ERROR))
        assertFalse(showRetryAction(AppState.ERROR, isTrafficBlocked = false))
    }

    @Test
    fun `connected stops and connecting cancels`() {
        assertEquals(MainOrbAction.STOP, mainOrbAction(AppState.CONNECTED, isTrafficBlocked = false))
        assertEquals(
            MainOrbAction.CANCEL_CONNECTING,
            mainOrbAction(AppState.CONNECTING, isTrafficBlocked = false),
        )
    }

    private fun label(state: AppState): Int = mainOrbActionLabel(
        mainOrbAction(state, isTrafficBlocked = false),
    )
}
