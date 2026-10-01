package com.myproxy.android.vpn

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.VpnService
import androidx.core.content.ContextCompat
import com.myproxy.android.domain.model.ProxyMode

/**
 * Small controller that prepares and sends commands to [MyProxyVpnService].
 * It never keeps an Activity reference.
 */
object VpnController {

    fun prepare(context: Context): Intent? = VpnService.prepare(context)

    fun startVpn(context: Context, configJson: String, mode: ProxyMode) {
        VpnStateHolder.setState(VpnState.STARTING)
        val intent = Intent(context, MyProxyVpnService::class.java)
            .setAction(MyProxyVpnService.ACTION_START)
            .putExtra(MyProxyVpnService.EXTRA_CONFIG, configJson)
            .putExtra(MyProxyVpnService.EXTRA_MODE, mode.name)
        ContextCompat.startForegroundService(context, intent)
    }

    fun stopVpn(context: Context) {
        val intent = Intent(context, MyProxyVpnService::class.java)
            .setAction(MyProxyVpnService.ACTION_STOP_INTERNAL)
        context.startService(intent)
    }

    fun reconfigure(context: Context, configJson: String, mode: ProxyMode) {
        // Prevent awaiters from mistaking the previous RUNNING value for this operation's result.
        VpnStateHolder.setState(VpnState.STARTING)
        val intent = Intent(context, MyProxyVpnService::class.java)
            .setAction(MyProxyVpnService.ACTION_RECONFIGURE)
            .putExtra(MyProxyVpnService.EXTRA_CONFIG, configJson)
            .putExtra(MyProxyVpnService.EXTRA_MODE, mode.name)
        context.startService(intent)
    }
}
