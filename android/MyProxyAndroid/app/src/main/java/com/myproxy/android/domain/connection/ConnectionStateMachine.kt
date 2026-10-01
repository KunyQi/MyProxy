package com.myproxy.android.domain.connection

import com.myproxy.android.domain.model.AppState

/**
 * Pure Kotlin state machine for the app connection lifecycle.
 *
 * Legal transitions:
 * UNBOUND -> DISCONNECTED via bindSuccess()
 * DISCONNECTED -> CONNECTING via connect()
 * CONNECTING -> CONNECTED via connectSuccess()
 * CONNECTING -> ERROR via connectFailure()
 * ERROR -> CONNECTING via connect() (retry)
 * CONNECTED -> DISCONNECTING via disconnect()
 * DISCONNECTING -> DISCONNECTED via disconnectDone()
 * Any bound state -> UNBOUND via rebind()
 */
class ConnectionStateMachine(
    initialState: AppState = AppState.UNBOUND,
) {
    var state: AppState = initialState
        private set

    fun bindSuccess() = transition(AppState.UNBOUND, AppState.DISCONNECTED, "bindSuccess")

    fun connect() = when (state) {
        AppState.DISCONNECTED,
        AppState.ERROR,
        -> transition(state, AppState.CONNECTING, "connect")
        else -> illegal("connect")
    }

    fun connectSuccess() = transition(AppState.CONNECTING, AppState.CONNECTED, "connectSuccess")

    fun connectFailure() = transition(AppState.CONNECTING, AppState.ERROR, "connectFailure")

    fun disconnect() = transition(AppState.CONNECTED, AppState.DISCONNECTING, "disconnect")

    fun disconnectDone() = transition(AppState.DISCONNECTING, AppState.DISCONNECTED, "disconnectDone")

    fun rebind() {
        if (state == AppState.UNBOUND) illegal("rebind")
        state = AppState.UNBOUND
    }

    private fun transition(from: AppState, to: AppState, event: String) {
        if (state != from) illegal(event)
        state = to
    }

    private fun illegal(event: String): Nothing {
        throw IllegalStateException("Illegal transition '$event' from $state")
    }
}
