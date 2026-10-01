package com.myproxy.android.data.repository

import com.myproxy.android.domain.pairing.BindResult
import com.myproxy.android.domain.pairing.PairingRepository

class BindingService(
    private val repository: PairingRepository,
) {
    /** 把配对码发给随构建发出去的那台服务器。 */
    suspend fun bind(
        code: String,
        deviceName: String,
        platform: String,
        clientVersion: String,
    ): BindResult {
        return repository.claim(
            code = code,
            deviceName = deviceName.take(MAX_DEVICE_NAME_LENGTH),
            platform = platform,
            clientVersion = clientVersion,
        )
    }

    private companion object {
        const val MAX_DEVICE_NAME_LENGTH = 128
    }
}
