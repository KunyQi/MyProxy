package com.myproxy.android.domain.model

import kotlinx.serialization.Serializable

/**
 * 一条设备绑定。
 *
 * 目标服务器不在这里：它是编译期常量（`ApiConfig.builtInTargetBaseUrl`），
 * 对每条凭据都一样，不是随绑定变化的运行态。
 *
 * `ignoreUnknownKeys` 让本类型读得回旧格式的凭据——那时这里还多两个目标字段，
 * 而它们的值本来就是内置那台。
 */
@Serializable
data class DeviceCredential(
    val deviceId: String,
    val deviceToken: String,
    val deviceName: String,
    val platform: String,
    val clientVersion: String,
    val boundAt: String,
)
