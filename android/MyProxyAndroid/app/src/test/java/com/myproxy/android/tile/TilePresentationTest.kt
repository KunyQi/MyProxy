package com.myproxy.android.tile

import com.myproxy.android.domain.model.AppState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class TilePresentationTest {

    @Test
    fun `every state maps to a visual`() {
        // Guards against a new AppState silently falling into a wrong branch
        // when the when-expression is extended.
        AppState.entries.forEach { state -> tileVisualFor(state) }
    }

    @Test
    fun `an unbound device stays clickable for pairing`() {
        assertEquals(TileVisual.INACTIVE, tileVisualFor(AppState.UNBOUND))
        assertEquals(TileAction.OPEN_APP, tileActionFor(AppState.UNBOUND, true))
        assertEquals(TileAction.OPEN_APP, tileActionFor(AppState.UNBOUND, false))
    }

    @Test
    fun `connecting reads as on so the tap does not look ignored`() {
        assertEquals(TileVisual.ACTIVE, tileVisualFor(AppState.CONNECTING))
        assertEquals(TileVisual.ACTIVE, tileVisualFor(AppState.CONNECTED))
    }

    @Test
    fun `stopping and failure read as off`() {
        assertEquals(TileVisual.INACTIVE, tileVisualFor(AppState.DISCONNECTING))
        assertEquals(TileVisual.INACTIVE, tileVisualFor(AppState.DISCONNECTED))
        assertEquals(TileVisual.INACTIVE, tileVisualFor(AppState.ERROR))
    }

    @Test
    fun `connecting from the tile needs vpn consent already granted`() {
        // The system consent dialog cannot be raised from the shade, so
        // without consent the app must obtain it and then continue connecting.
        assertEquals(TileAction.CONNECT, tileActionFor(AppState.DISCONNECTED, true))
        assertEquals(TileAction.REQUEST_CONNECT, tileActionFor(AppState.DISCONNECTED, false))
    }

    @Test
    fun `a live tunnel stops from the tile`() {
        assertEquals(TileAction.STOP, tileActionFor(AppState.CONNECTED, true))
        assertEquals(TileAction.STOP, tileActionFor(AppState.CONNECTED, false))
    }

    @Test
    fun `an ordinary failure retries after vpn consent`() {
        assertEquals(TileAction.RETRY, tileActionFor(AppState.ERROR, true))
        assertEquals(TileAction.REQUEST_CONNECT, tileActionFor(AppState.ERROR, false))
    }

    @Test
    fun `a blocking hold is only resolved in the app`() {
        for (permissionGranted in listOf(true, false)) {
            val action = tileActionFor(AppState.ERROR, permissionGranted, trafficBlocked = true)
            assertEquals(TileAction.OPEN_APP, action)
            assertNotEquals(TileAction.CONNECT, action)
            assertNotEquals(TileAction.RETRY, action)
            assertNotEquals(TileAction.REQUEST_CONNECT, action)
            assertNotEquals(TileAction.STOP, action)
        }
    }

    @Test
    fun `taps during a transition are ignored`() {
        for (permissionGranted in listOf(true, false)) {
            assertEquals(TileAction.NONE, tileActionFor(AppState.CONNECTING, permissionGranted))
            assertEquals(TileAction.NONE, tileActionFor(AppState.DISCONNECTING, permissionGranted))
        }
    }
}
