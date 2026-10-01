package com.myproxy.android.data.api

/** Claim endpoint boundary so pairing validation can be tested without HTTP. */
interface ClaimApi {
    /**
     * 向构建配置中的服务器发 claim。HTTPS 使用系统信任库验证服务器身份，
     * 请求参数只包含配对所需的设备信息。
     */
    suspend fun claim(request: ClaimRequest): ClaimResponse
}
