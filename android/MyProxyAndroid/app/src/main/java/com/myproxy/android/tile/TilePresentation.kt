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

    /** Ask for VPN consent in the app, then continue connecting. */
    REQUEST_CONNECT,

    RETRY,
    STOP,
}

internal fun tileVisualFor(appState: AppState): TileVisual = when (appState) {
    // Keep the tile clickable so it can open the app for pairing.
    AppState.UNBOUND -> TileVisual.INACTIVE
    // The platform has no "busy" tile state. Showing CONNECTING as active is
    // the honest half-truth: the user asked for on, and on is what it is
    // heading for. Showing it as off would read as "the tap did nothing".
    AppState.CONNECTING, AppState.CONNECTED -> TileVisual.ACTIVE
    AppState.DISCONNECTING, AppState.DISCONNECTED, AppState.ERROR -> TileVisual.INACTIVE
}

/**
 * [vpnPermissionGranted] decides whether connecting can happen from the tile
 * at all. The system's VPN consent dialog cannot be raised from the quick
 * settings shade, so without consent the app must ask for it and continue the
 * requested connection there. A blocking hold still needs an explicit choice
 * in the app rather than a retry from the shade.
 */
internal fun tileActionFor(
    appState: AppState,
    vpnPermissionGranted: Boolean,
    trafficBlocked: Boolean = false,
): TileAction =
    when (appState) {
        AppState.UNBOUND -> TileAction.OPEN_APP
        AppState.CONNECTED -> TileAction.STOP
        AppState.ERROR -> when {
            trafficBlocked -> TileAction.OPEN_APP
            vpnPermissionGranted -> TileAction.RETRY
            else -> TileAction.REQUEST_CONNECT
        }
        AppState.DISCONNECTED ->
            if (vpnPermissionGranted) TileAction.CONNECT else TileAction.REQUEST_CONNECT
        AppState.CONNECTING, AppState.DISCONNECTING -> TileAction.NONE
    }
