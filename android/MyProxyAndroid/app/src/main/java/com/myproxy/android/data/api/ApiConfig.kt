package com.myproxy.android.data.api

import com.myproxy.android.BuildConfig
import com.myproxy.android.domain.pairing.BindingTarget

object ApiConfig {
    /** Emulator host loopback. A physical debug device can use a development LAN address. */
    const val DEBUG_BASE_URL = "http://10.0.2.2:8090"
    val RELEASE_BASE_URL: String = BuildConfig.DEPLOYMENT_API_BASE_URL
    const val VERSION = "0.1.0"
    const val USER_AGENT = "MyProxy/0.1.0 (Android)"
    const val CLAIM_TIMEOUT_SECONDS = 15L
    const val CONFIG_TIMEOUT_SECONDS = 5L
    const val UPDATE_TIMEOUT_SECONDS = 5L
    val builtInTargetBaseUrl: String
        get() = if (BuildConfig.ENV_MODE == "LOCAL") DEBUG_BASE_URL else RELEASE_BASE_URL
    val builtInTarget: BindingTarget?
        get() = BindingTarget.fromBaseUrl(builtInTargetBaseUrl)
    val requireHttps: Boolean
        get() = BuildConfig.ENV_MODE != "LOCAL"
    val connectivityCheckUrls: List<String>
        get() = BuildConfig.DEPLOYMENT_CONNECTIVITY_CHECK_URLS.toList()
}
