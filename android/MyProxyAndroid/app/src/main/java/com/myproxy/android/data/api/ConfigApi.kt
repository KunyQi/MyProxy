package com.myproxy.android.data.api

interface ConfigApi {
    suspend fun getDeviceConfig(token: String): ConfigSyncResponse

    suspend fun heartbeat(token: String): HeartbeatResponse
}
