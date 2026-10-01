package com.myproxy.android.xray

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class XrayState {
    IDLE,
    STARTING,
    RUNNING,
    STOPPING,
    STOPPED,
    ERROR,
}

object XrayStateHolder {
    private val _state = MutableStateFlow(XrayState.IDLE)
    val state: StateFlow<XrayState> = _state.asStateFlow()

    fun setState(newState: XrayState) {
        _state.value = newState
    }
}
