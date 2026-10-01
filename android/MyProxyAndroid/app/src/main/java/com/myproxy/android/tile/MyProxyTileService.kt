package com.myproxy.android.tile

import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.myproxy.android.MainActivity
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.R
import com.myproxy.android.domain.model.AppState
import com.myproxy.android.logging.AndroidAppLogger
import com.myproxy.android.logging.AppLogger
import com.myproxy.android.vpn.VpnController
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

/**
 * Quick settings tile: connect and disconnect without opening MyProxy.
 *
 * Reads the same [com.myproxy.android.domain.connection.ConnectionController]
 * the app screen does, so the tile and the screen can never disagree -- there
 * is one connection state in the process and both render it. All of the
 * judgement lives in [TilePresentation]; this class only applies it.
 */
class MyProxyTileService : TileService() {

    private val logger: AppLogger = AndroidAppLogger()
    private var scope: CoroutineScope? = null

    private val controller
        get() = (application as MyProxyApplication).connectionController

    override fun onStartListening() {
        super.onStartListening()
        val listeningScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        scope = listeningScope
        listeningScope.launch {
            // The hold flag never changes the tile's own state -- a held
            // tunnel is still "off" as far as reachable networks go -- but it
            // changes the subtitle, so both are collected.
            combine(controller.state, controller.isTrafficBlocked) { state, blocked ->
                state to blocked
            }.collect { (state, blocked) -> render(state, blocked) }
        }
    }

    override fun onStopListening() {
        stopCollecting()
        super.onStopListening()
    }

    override fun onTileRemoved() {
        stopCollecting()
        super.onTileRemoved()
    }

    private fun stopCollecting() {
        scope?.cancel()
        scope = null
    }

    override fun onClick() {
        super.onClick()
        val state = controller.state.value
        val permissionGranted = VpnController.prepare(this) == null
        when (tileActionFor(state, permissionGranted)) {
            TileAction.NONE -> Unit
            TileAction.CONNECT -> controller.connect()
            TileAction.STOP -> controller.stop()
            TileAction.OPEN_APP -> openApp()
        }
    }

    /**
     * On a locked device the shade can be shown without unlocking, so a tap
     * that needs the app must wait for the keyguard to go away --
     * [unlockAndRun] is what asks for that.
     */
    private fun openApp() {
        if (isSecure) unlockAndRun { launchMainActivity() } else launchMainActivity()
    }

    private fun launchMainActivity() {
        val intent = Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                // From API 34 the Intent overload throws; a PendingIntent is
                // the only accepted form.
                startActivityAndCollapse(
                    PendingIntent.getActivity(
                        this,
                        0,
                        intent,
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                    ),
                )
            } else {
                // Two suppression systems, two names for the same call. Kotlin
                // wants DEPRECATION; lint has its own error-severity check,
                // because on targetSdk 34+ this overload throws
                // UnsupportedOperationException -- which the branch above
                // already prevents by never reaching here on 34 or later.
                // Lint cannot see that, so the id is named explicitly.
                @Suppress("DEPRECATION", "StartActivityAndCollapseDeprecated")
                startActivityAndCollapse(intent)
            }
        }.onFailure {
            logger.w(TAG, "failed to open the app from the tile: ${it.javaClass.simpleName}")
        }
    }

    private fun render(state: AppState, trafficBlocked: Boolean) {
        val tile = qsTile ?: return
        tile.state = when (tileVisualFor(state)) {
            TileVisual.UNAVAILABLE -> Tile.STATE_UNAVAILABLE
            TileVisual.INACTIVE -> Tile.STATE_INACTIVE
            TileVisual.ACTIVE -> Tile.STATE_ACTIVE
        }
        tile.label = getString(R.string.app_name)
        tile.icon = Icon.createWithResource(this, R.drawable.ic_tile_myproxy)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = getString(subtitleFor(state, trafficBlocked))
        }
        runCatching { tile.updateTile() }
            .onFailure { logger.w(TAG, "failed to update the tile: ${it.javaClass.simpleName}") }
    }

    private fun subtitleFor(state: AppState, trafficBlocked: Boolean): Int = when (state) {
        AppState.UNBOUND -> R.string.main_status_unbound
        AppState.DISCONNECTED -> R.string.main_status_disconnected
        AppState.CONNECTING -> R.string.main_status_connecting
        AppState.CONNECTED -> R.string.main_status_connected
        AppState.DISCONNECTING -> R.string.main_status_disconnecting
        AppState.ERROR ->
            if (trafficBlocked) R.string.main_status_blocked else R.string.main_status_error
    }

    private companion object {
        const val TAG = "MyProxyTileService"
    }
}
