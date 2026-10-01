package com.myproxy.android.domain.model

enum class AppState {
    UNBOUND,
    DISCONNECTED,
    CONNECTING,
    CONNECTED,
    DISCONNECTING,
    ERROR,
}
