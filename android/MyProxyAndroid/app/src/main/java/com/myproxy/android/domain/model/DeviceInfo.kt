package com.myproxy.android.domain.model

/** Non-sensitive device details safe to show in the Settings screen. */
data class DeviceInfo(
    val deviceName: String,
    val platform: String,
    val clientVersion: String,
)
