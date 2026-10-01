package com.myproxy.android.presentation

import android.app.Application
import android.content.Intent
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.myproxy.android.data.api.ApiConfig
import com.myproxy.android.data.api.UpdateInfo
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.data.repository.DefaultUpdateService
import com.myproxy.android.data.storage.SecureStorage
import com.myproxy.android.data.storage.SettingsDataStore
import com.myproxy.android.domain.update.UpdateService
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.model.DeviceInfo
import com.myproxy.android.logging.AppLogBuffer
import com.myproxy.android.vpn.health.CoreHealthReport
import kotlinx.serialization.json.Json
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onStart
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class SettingsViewModel(
    application: Application,
    private val updateService: UpdateService,
) : AndroidViewModel(application) {

    /**
     * Required by AndroidViewModelFactory, which is used by Compose's default
     * viewModel() delegate in SettingsScreen.
     */
    constructor(application: Application) : this(
        application = application,
        // The Update Plane calls need a device token, so the service is given
        // the same secure storage the rest of the app reads credentials from.
        // Without it fetchAssigned/reportInstall would compile and then
        // quietly do nothing.
        //
        // 复用进程级 API 客户端，避免为同一构建入口重复创建连接池。
        updateService = DefaultUpdateService(
            apiClient = (application as MyProxyApplication).apiClient,
            secureStorage = application.secureStorage,
        ),
    )

    private val settingsDataStore = SettingsDataStore(application)

    private val _updateInfo = MutableStateFlow<UpdateInfo?>(null)
    val updateInfo: StateFlow<UpdateInfo?> = _updateInfo.asStateFlow()

    private val _deviceInfo = MutableStateFlow(loadDeviceInfo())
    val deviceInfo: StateFlow<DeviceInfo?> = _deviceInfo.asStateFlow()

    /**
     * `onStart` 里补一次 [AppLogBuffer.publish]：缓冲区在没人订阅时不物化列表
     * （写入路径上不分配是有意的），所以订阅的第一刻必须主动要一份快照，
     * 否则诊断页会停在上一次有人看时的内容上。
     */
    val logEntries: StateFlow<List<String>> = AppLogBuffer.entries
        .onStart { AppLogBuffer.publish() }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(), emptyList())

    val autoConnect: StateFlow<Boolean> = settingsDataStore.autoConnect
        .stateIn(viewModelScope, SharingStarted.Eagerly, false)

    val autoUpdateCheck: StateFlow<Boolean> = settingsDataStore.autoUpdateCheck
        .stateIn(viewModelScope, SharingStarted.Eagerly, false)

    val killSwitch: StateFlow<Boolean> = settingsDataStore.killSwitch
        .stateIn(viewModelScope, SharingStarted.Eagerly, false)

    val selfHeal: StateFlow<Boolean> = settingsDataStore.selfHeal
        .stateIn(viewModelScope, SharingStarted.Eagerly, true)

    /** Null while no tunnel is up: the audit only samples a running core. */
    val coreHealth: StateFlow<CoreHealthReport?> =
        (application as MyProxyApplication).coreHealthMonitor.report

    val proxyMode: StateFlow<com.myproxy.android.domain.model.ProxyMode> =
        settingsDataStore.proxyMode
            .stateIn(viewModelScope, SharingStarted.Eagerly, com.myproxy.android.domain.model.ProxyMode.RULE)

    val versionName: String = ApiConfig.VERSION

    init {
        viewModelScope.launch {
            val enabled = settingsDataStore.autoUpdateCheck.first()
            if (enabled) {
                checkUpdate()
            }
        }
    }

    fun setAutoConnect(enabled: Boolean) {
        viewModelScope.launch {
            settingsDataStore.setAutoConnect(enabled)
        }
    }

    fun setKillSwitch(enabled: Boolean) {
        viewModelScope.launch {
            settingsDataStore.setKillSwitch(enabled)
        }
    }

    fun setSelfHeal(enabled: Boolean) {
        viewModelScope.launch {
            settingsDataStore.setSelfHeal(enabled)
        }
    }

    fun setAutoUpdateCheck(enabled: Boolean) {
        viewModelScope.launch {
            settingsDataStore.setAutoUpdateCheck(enabled)
        }
    }

    fun setProxyMode(mode: com.myproxy.android.domain.model.ProxyMode) {
        viewModelScope.launch {
            settingsDataStore.setProxyMode(mode)
        }
    }

    fun checkUpdate() {
        viewModelScope.launch {
            _updateInfo.value = updateService.check()
        }
    }

    fun runMemoryBenchmark() {
        val intent = Intent(getApplication(), com.myproxy.android.vpn.MyProxyVpnService::class.java).apply {
            action = com.myproxy.android.vpn.MyProxyVpnService.ACTION_FORCE_HEAL
        }
        getApplication<Application>().startService(intent)
    }

    fun clearLogs() {
        AppLogBuffer.clear()
    }

    private fun loadDeviceInfo(): DeviceInfo? {
        val encoded = try {
            SecureStorage(getApplication<Application>())
                .read(com.myproxy.android.data.storage.StorageKeys.DEVICE_CREDENTIAL)
        } catch (_: com.myproxy.android.data.storage.CredentialUnreadableException) {
            null
        } ?: return null
        return runCatching {
            val credential = Json { ignoreUnknownKeys = true }
                .decodeFromString<DeviceCredential>(encoded)
            DeviceInfo(
                deviceName = credential.deviceName,
                platform = credential.platform,
                clientVersion = credential.clientVersion,
            )
        }.getOrNull()
    }

    companion object {
        fun factory(application: Application): ViewModelProvider.Factory =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>): T {
                    return SettingsViewModel(application) as T
                }
            }
    }
}
