package com.myproxy.android.ui.main

import com.myproxy.android.R
import com.myproxy.android.domain.model.AppState

/** The action the home screen's primary control will perform in the current state. */
internal enum class MainOrbAction {
    START,
    STOP,
    STOP_AND_RELEASE_PROTECTION,
    RETRY,
    CANCEL_CONNECTING,
    UNAVAILABLE,
}

/**
 * Keep the selected action and its accessibility label derived from the same policy.
 * A failed connection can still own a live blocking VPN tunnel, so the actual hold state
 * determines whether the primary action releases protection or retries the connection.
 */
internal fun mainOrbAction(appState: AppState, isTrafficBlocked: Boolean): MainOrbAction =
    when (appState) {
        AppState.CONNECTED -> MainOrbAction.STOP
        AppState.ERROR -> if (isTrafficBlocked) {
            MainOrbAction.STOP_AND_RELEASE_PROTECTION
        } else {
            MainOrbAction.RETRY
        }
        AppState.CONNECTING -> MainOrbAction.CANCEL_CONNECTING
        AppState.DISCONNECTING -> MainOrbAction.UNAVAILABLE
        AppState.DISCONNECTED, AppState.UNBOUND -> MainOrbAction.START
    }

internal fun mainOrbActionLabel(action: MainOrbAction): Int = when (action) {
    MainOrbAction.START -> R.string.main_orb_start
    MainOrbAction.STOP -> R.string.main_orb_stop
    MainOrbAction.STOP_AND_RELEASE_PROTECTION -> R.string.main_orb_stop_and_release
    MainOrbAction.RETRY -> R.string.main_orb_retry
    MainOrbAction.CANCEL_CONNECTING -> R.string.main_orb_cancel
    MainOrbAction.UNAVAILABLE -> R.string.main_status_disconnecting
}

/** A retry is separate from the main stop action only while a failed tunnel blocks traffic. */
internal fun showRetryAction(appState: AppState, isTrafficBlocked: Boolean): Boolean =
    mainOrbAction(appState, isTrafficBlocked) == MainOrbAction.STOP_AND_RELEASE_PROTECTION
