package com.myproxy.android.tile

import com.myproxy.android.domain.model.AppState

/** How the tile should look. Maps onto the platform's three `Tile` states. */
internal enum class TileVisual {
    /** Dimmed and inert: there is nothing useful a tap could do. */
    UNAVAILABLE,

    /** Off. */
    INACTIVE,

    /** On, or on its way there. */
    ACTIVE,
}

/** What a tap should do. */
internal enum class TileAction {
    /** Ignore the tap: an operation is already in flight. */
    NONE,

    /** Bring up MyProxy, because this needs a screen. */
    OPEN_APP,

    CONNECT,
    STOP,
}

internal fun tileVisualFor(appState: AppState): TileVisual = when (appState) {
    // Nothing to switch on until a pairing code has been entered.
    AppState.UNBOUND -> TileVisual.UNAVAILABLE
    // The platform has no "busy" tile state. Showing CONNECTING as active is
    // the honest half-truth: the user asked for on, and on is what it is
    // heading for. Showing it as off would read as "the tap did nothing".
    AppState.CONNECTING, AppState.CONNECTED -> TileVisual.ACTIVE
    AppState.DISCONNECTING, AppState.DISCONNECTED, AppState.ERROR -> TileVisual.INACTIVE
}

/**
 * [vpnPermissionGranted] decides whether connecting can happen from the tile
 * at all. The system's VPN consent dialog cannot be raised from the quick
 * settings shade, so without consent the only honest action is to open the
 * app and let the normal flow ask for it.
 */
internal fun tileActionFor(appState: AppState, vpnPermissionGranted: Boolean): TileAction =
    when (appState) {
        AppState.UNBOUND -> TileAction.OPEN_APP
        AppState.CONNECTED -> TileAction.STOP
        // A failure may be a kill-switch hold, which must not be released by
        // a stray tap in the shade, and may need an explanation the tile has
        // no room for. Both belong on a screen.
        AppState.ERROR -> TileAction.OPEN_APP
        AppState.DISCONNECTED ->
            if (vpnPermissionGranted) TileAction.CONNECT else TileAction.OPEN_APP
        AppState.CONNECTING, AppState.DISCONNECTING -> TileAction.NONE
    }
