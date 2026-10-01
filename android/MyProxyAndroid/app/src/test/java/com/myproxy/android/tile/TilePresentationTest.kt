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
    fun `an unbound device offers nothing to switch`() {
        assertEquals(TileVisual.UNAVAILABLE, tileVisualFor(AppState.UNBOUND))
        // ...but the tap still has somewhere useful to go.
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
        // without consent the only honest action is to open the app.
        assertEquals(TileAction.CONNECT, tileActionFor(AppState.DISCONNECTED, true))
        assertEquals(TileAction.OPEN_APP, tileActionFor(AppState.DISCONNECTED, false))
    }

    @Test
    fun `a live tunnel stops from the tile`() {
        assertEquals(TileAction.STOP, tileActionFor(AppState.CONNECTED, true))
    }

    @Test
    fun `a failure is never resolved from the shade`() {
        // A failure may be a kill-switch hold. Releasing traffic protection
        // must not be one stray tap away, and it needs an explanation the
        // tile has no room for.
        assertEquals(TileAction.OPEN_APP, tileActionFor(AppState.ERROR, true))
        assertNotEquals(TileAction.CONNECT, tileActionFor(AppState.ERROR, true))
        assertNotEquals(TileAction.STOP, tileActionFor(AppState.ERROR, true))
    }

    @Test
    fun `taps during a transition are ignored`() {
        assertEquals(TileAction.NONE, tileActionFor(AppState.CONNECTING, true))
        assertEquals(TileAction.NONE, tileActionFor(AppState.DISCONNECTING, true))
    }
}
