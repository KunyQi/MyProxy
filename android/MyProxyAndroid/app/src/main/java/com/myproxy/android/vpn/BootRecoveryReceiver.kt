package com.myproxy.android.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.VpnService
import androidx.core.content.ContextCompat
import com.myproxy.android.data.storage.SettingsDataStore
import com.myproxy.android.logging.AndroidAppLogger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * 开机与应用升级之后把隧道拉回来。
 *
 * 在此之前，清单里声明了 `RECEIVE_BOOT_COMPLETED` 却**没有任何接收者**——
 * 那条权限是死的，而「启动后自动连接」实际只在 [com.myproxy.android.MainViewModel]
 * 的 init 里生效，也就是「**打开 App** 后自动连接」。用户重启手机，隧道就没了，
 * 直到他自己想起来点开图标。另一条本来也会断的路径是应用升级：进程被杀，
 * 没有任何东西负责把它拉回来。
 *
 * 三道闸门，缺一不启：
 *
 * 1. **用户自己开了「启动后自动连接」**。这条设置本来就是这个意思，不另外加开关。
 * 2. **VPN 授权还在**。开机时没有界面可以去要授权，`prepare()` 返回非 null
 *    就说明还没授权，此时安静退出而不是留一个永远起不来的前台服务。
 * 3. **有一份已验证的配置**。发的是不带 action 的 start，服务侧走
 *    `handleSystemStart()`，它只接受 `verified = true` 的 LKG 配置，否则自己停掉。
 *    未绑定的设备因此不会在开机时多出一个空跑的常驻通知。
 *
 * 为什么 `specialUse` 在这里是可以的：Android 15 禁止从 `BOOT_COMPLETED` 启动
 * `dataSync` / `camera` / `mediaPlayback` / `phoneCall` / `mediaProjection` /
 * `microphone` 这几类前台服务，`specialUse` **不在**这张禁止清单上。targetSdk
 * 已经是 35，这条如果搞错了会在开机那一刻抛 ForegroundServiceStartNotAllowedException，
 * 而那是 CI 永远看不见的一类故障。
 */
class BootRecoveryReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action
        if (action != Intent.ACTION_BOOT_COMPLETED && action != Intent.ACTION_MY_PACKAGE_REPLACED) {
            return
        }

        val app = context.applicationContext
        val logger = AndroidAppLogger()
        // goAsync：读设置是挂起操作，而 onReceive 返回即视为处理完毕。
        val pending = goAsync()
        CoroutineScope(SupervisorJob() + Dispatchers.Default).launch {
            try {
                val autoConnect = withTimeoutOrNull(SETTINGS_READ_TIMEOUT_MS) {
                    SettingsDataStore(app).autoConnect.first()
                }
                if (autoConnect != true) {
                    logger.i(TAG, "$action: auto-connect is off; not starting")
                    return@launch
                }
                if (VpnService.prepare(app) != null) {
                    logger.w(TAG, "$action: VPN consent is not granted; not starting")
                    return@launch
                }
                ContextCompat.startForegroundService(
                    app,
                    Intent(app, MyProxyVpnService::class.java),
                )
                logger.i(TAG, "$action: requested an unattended tunnel start")
            } catch (t: Throwable) {
                // 开机路径失败绝不能把 App 崩掉：用户看到的会是一个「MyProxy 已停止
                // 运行」的对话框，而他什么都没做。
                logger.e(TAG, "boot recovery failed (${t.javaClass.simpleName})")
            } finally {
                pending.finish()
            }
        }
    }

    private companion object {
        const val TAG = "BootRecoveryReceiver"

        /** 读一次 DataStore 的上限。超时按「没开自动连接」处理，宁可不启。 */
        const val SETTINGS_READ_TIMEOUT_MS = 5_000L
    }
}
