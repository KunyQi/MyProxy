package com.myproxy.android.presentation

import android.app.Application
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.domain.connection.ConnectionController
import com.myproxy.android.domain.model.AppState
import com.myproxy.android.domain.model.ProxyMode
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val app = application as MyProxyApplication
    private val connectionController: ConnectionController =
        app.connectionController

    private val _isBatteryOptimized = MutableStateFlow(false)
    val isBatteryOptimized: StateFlow<Boolean> = _isBatteryOptimized.asStateFlow()

    init {
        checkBatteryOptimization()
        viewModelScope.launch {
            if (app.settingsDataStore.autoConnect.first()) {
                connectionController.connect()
            }
        }
    }

    val appState: StateFlow<AppState> = connectionController.state
    val proxyMode: StateFlow<ProxyMode> = connectionController.mode
    val latencyMs: StateFlow<Int?> = connectionController.latencyMs
    val vpnPermissionRequest: StateFlow<Intent?> = connectionController.vpnPermissionRequest
    val isSwitching: StateFlow<Boolean> = connectionController.isSwitching

    /** True when a failed tunnel is being held open to block traffic. */
    val isTrafficBlocked: StateFlow<Boolean> = connectionController.isTrafficBlocked

    fun checkBatteryOptimization() {
        val pm = app.getSystemService(Context.POWER_SERVICE) as PowerManager
        _isBatteryOptimized.value = !pm.isIgnoringBatteryOptimizations(app.packageName)
    }

    fun requestIgnoreBatteryOptimizations() {
        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
            data = Uri.parse("package:${app.packageName}")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        app.startActivity(intent)
    }

    fun connect() = connectionController.connect()

    fun connectWithPermissionGranted() = connectionController.connectWithPermissionGranted()

    fun stop() = connectionController.stop()

    fun checkConnection() = connectionController.checkConnection()

    fun retry() = connectionController.retry()

    fun setProxyMode(mode: ProxyMode) = connectionController.switchMode(mode)

    /**
     * 前台状态是一个**事件**，不是心跳周期的依据。用户回到 App 时立刻同步一次
     * 控制面，这样他看到的第一眼就是最新的状态，而不是退避定时器上一次留下的。
     */
    fun onAppForegroundChanged(inForeground: Boolean) {
        connectionController.onAppForegroundChanged(inForeground)
    }

    fun rebind() = connectionController.rebind()

    fun consumeVpnPermissionRequest() = connectionController.consumeVpnPermissionRequest()

    fun vpnPermissionDenied() = connectionController.vpnPermissionDenied()
}
