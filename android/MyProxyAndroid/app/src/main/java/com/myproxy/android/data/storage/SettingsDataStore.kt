package com.myproxy.android.data.storage

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.myproxy.android.domain.model.ProxyMode
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.map
import java.io.IOException

private val Context.settingsDataStore by preferencesDataStore(name = "settings")

class SettingsDataStore(private val context: Context) {

    private object Keys {
        val AUTO_CONNECT = booleanPreferencesKey("auto_connect")
        val AUTO_UPDATE_CHECK = booleanPreferencesKey("auto_update_check")
        val KILL_SWITCH = booleanPreferencesKey("kill_switch")
        val SELF_HEAL = booleanPreferencesKey("self_heal")
        val PROXY_MODE = stringPreferencesKey("proxy_mode")
    }

    val autoConnect: Flow<Boolean> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map { prefs -> prefs[Keys.AUTO_CONNECT] ?: false }

    val autoUpdateCheck: Flow<Boolean> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map { prefs -> prefs[Keys.AUTO_UPDATE_CHECK] ?: false }

    /**
     * Off by default on purpose. With it on, a tunnel that fails keeps
     * blocking traffic until the user acts, which is the point -- but a
     * device that silently loses all connectivity is not something to hand
     * anyone who did not ask for it.
     */
    val killSwitch: Flow<Boolean> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map { prefs -> prefs[Keys.KILL_SWITCH] ?: false }

    /**
     * On by default, unlike [killSwitch]. The audit only restarts the core
     * with the screen off and traffic at a trickle, gives up once it can see
     * a restart is not reclaiming anything, and is capped per session -- so
     * the worst case is a few dropped background connections, against a core
     * that would otherwise grow until Android kills the process.
     */
    val selfHeal: Flow<Boolean> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map { prefs -> prefs[Keys.SELF_HEAL] ?: true }

    val proxyMode: Flow<ProxyMode> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map { prefs ->
            runCatching { ProxyMode.valueOf(prefs[Keys.PROXY_MODE] ?: ProxyMode.RULE.name) }
                .getOrDefault(ProxyMode.RULE)
        }

    suspend fun setAutoConnect(enabled: Boolean) {
        context.settingsDataStore.edit { prefs -> prefs[Keys.AUTO_CONNECT] = enabled }
    }

    suspend fun setAutoUpdateCheck(enabled: Boolean) {
        context.settingsDataStore.edit { prefs -> prefs[Keys.AUTO_UPDATE_CHECK] = enabled }
    }

    suspend fun setKillSwitch(enabled: Boolean) {
        context.settingsDataStore.edit { prefs -> prefs[Keys.KILL_SWITCH] = enabled }
    }

    suspend fun setSelfHeal(enabled: Boolean) {
        context.settingsDataStore.edit { prefs -> prefs[Keys.SELF_HEAL] = enabled }
    }

    suspend fun setProxyMode(mode: ProxyMode) {
        context.settingsDataStore.edit { prefs -> prefs[Keys.PROXY_MODE] = mode.name }
    }
}
