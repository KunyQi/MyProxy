package com.myproxy.android

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import android.os.PowerManager
import com.myproxy.android.data.api.ApiConfig
import com.myproxy.android.data.api.MyProxyApiClient
import com.myproxy.android.data.repository.DefaultConfigRepository
import com.myproxy.android.data.repository.DefaultPairingRepository
import com.myproxy.android.data.storage.ConfigCacheStore
import com.myproxy.android.data.storage.SecureStorage
import com.myproxy.android.data.storage.SettingsDataStore
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.domain.config.ConfigRepository
import com.myproxy.android.domain.connection.ConnectionController
import com.myproxy.android.domain.model.DeviceCredential
import com.myproxy.android.domain.pairing.BindingTarget
import com.myproxy.android.domain.pairing.PairingRepository
import kotlinx.serialization.json.Json
import com.myproxy.android.vpn.MyProxyVpnService
import com.myproxy.android.vpn.VpnController
import com.myproxy.android.vpn.VpnStateHolder
import com.myproxy.android.vpn.health.CoreHealthMonitor
import com.myproxy.android.xray.TrafficStatsMonitor
import com.myproxy.android.xray.XrayManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

class MyProxyApplication : Application() {

    private val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    val secureStorage: SecureStorage by lazy { SecureStorage(this) }
    val configCacheStore: ConfigCacheStore by lazy { ConfigCacheStore(filesDir) }

    /** 入口在构建时确定，HTTPS 使用系统信任库；进程共享同一个 API 客户端。 */
    val apiClient: MyProxyApiClient by lazy { MyProxyApiClient() }

    val settingsDataStore: SettingsDataStore by lazy { SettingsDataStore(this) }

    val configRepository: ConfigRepository by lazy {
        DefaultConfigRepository(
            apiClient = apiClient,
            secureStorage = secureStorage,
            configCacheStore = configCacheStore,
        )
    }

    val pairingRepository: PairingRepository by lazy {
        DefaultPairingRepository(apiClient, secureStorage, configCacheStore)
    }

    val xrayManager: XrayManager by lazy { XrayManager(this) }

    /**
     * One monitor per process: the core clears a counter as it reports it, so
     * a second reader would silently halve everyone's numbers.
     */
    val trafficStatsMonitor: TrafficStatsMonitor by lazy {
        TrafficStatsMonitor(
            source = xrayManager,
            vpnState = VpnStateHolder.state,
            scope = applicationScope,
        )
    }

    /**
     * Audits the process the core runs in. Lives here rather than in the VPN
     * service so the diagnostics screen can read its report, and so a service
     * restart does not lose the session's history.
     */
    internal val coreHealthMonitor: CoreHealthMonitor by lazy {
        CoreHealthMonitor(
            trafficStats = trafficStatsMonitor,
            isScreenInteractive = {
                // Defaults to "awake" when it cannot be read: an unknown
                // screen state must never be taken as permission to restart
                // the tunnel under someone who is using it.
                runCatching {
                    (getSystemService(Context.POWER_SERVICE) as PowerManager).isInteractive
                }.getOrDefault(true)
            },
        )
    }

    val connectionController: ConnectionController by lazy {
        ConnectionController(
            context = this,
            configRepository = configRepository,
            settingsDataStore = settingsDataStore,
            vpnController = VpnController,
            scope = applicationScope,
        )
    }

    override fun onCreate() {
        super.onCreate()
        createVpnNotificationChannel()
        // Android can create this process solely to restore the VPN service.
        // Start the process-wide controller here so its heartbeat and config
        // synchronization do not depend on an Activity ever being opened.
        connectionController
    }

    private fun createVpnNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                MyProxyVpnService.CHANNEL_ID,
                getString(R.string.app_name),
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = getString(R.string.vpn_notification_channel_description)
                setShowBadge(false)
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }
}
