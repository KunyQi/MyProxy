package com.myproxy.android.domain.pairing

import com.myproxy.android.data.api.ErrorCode

/**
 * 绑定只有一步：把配对码发给随构建发出去的那台服务器。
 *
 * 与 Windows 的 `IBindingService` 使用相同契约。服务器入口在构建时确定，
 * HTTPS 通过系统信任库校验证书链、有效期和主机名；用户只输入配对码。
 */
interface PairingRepository {
    suspend fun claim(
        code: String,
        deviceName: String,
        platform: String,
        clientVersion: String,
    ): BindResult
}

sealed class BindResult {
    data class Success(
        val deviceId: String,
        val configVersion: Int?,
    ) : BindResult()

    data class Failure(
        val code: ErrorCode,
        val message: String,
    ) : BindResult()
}
