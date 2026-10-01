package com.myproxy.android.ui.navigation

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.data.storage.CredentialUnreadableException
import com.myproxy.android.data.storage.SecureStorage
import com.myproxy.android.data.storage.StorageKeys
import com.myproxy.android.ui.binding.BindScreen
import com.myproxy.android.ui.main.MainScreen
import com.myproxy.android.ui.settings.SettingsScreen
import com.myproxy.android.presentation.SettingsViewModel
import com.myproxy.android.domain.model.AppState
import com.myproxy.android.ui.theme.Background

object MyProxyRoutes {
    const val BINDING = "binding"
    const val MAIN = "main"
    const val SETTINGS = "settings"
}

@Composable
fun MyProxyNavHost() {
    val navController = rememberNavController()
    val appContext = LocalContext.current.applicationContext
    val app = appContext as MyProxyApplication
    val connectionState by app.connectionController.state.collectAsStateWithLifecycle()
    val startDestination = remember {
        // A keystore that cannot answer right now counts as bound: treating it
        // as unbound would send the user to spend a new one-time pairing code
        // while the credential is still on disk. Connecting then fails with a
        // retryable error until the keystore recovers.
        val hasCredential = try {
            SecureStorage(appContext).read(StorageKeys.DEVICE_CREDENTIAL) != null
        } catch (_: CredentialUnreadableException) {
            true
        }
        if (hasCredential) MyProxyRoutes.MAIN else MyProxyRoutes.BINDING
    }
    val navigateToBinding = {
        if (navController.currentDestination?.route != MyProxyRoutes.BINDING) {
            navController.navigate(MyProxyRoutes.BINDING) {
                popUpTo(MyProxyRoutes.MAIN) { inclusive = true }
                launchSingleTop = true
            }
        }
    }

    LaunchedEffect(connectionState) {
        if (connectionState == AppState.UNBOUND) navigateToBinding()
    }

    // 底色与系统栏避让放在这里，而不是各页面自己加。
    // `MainActivity` 调了 `enableEdgeToEdge()`，内容默认会画到状态栏与导航栏底下；
    // 先前只有 MainScreen 自己加了这两件事，于是绑定页和设置页的顶部内容钻进状态栏，
    // 整页底色也退回 `android:windowBackground`。放在 NavHost 外面意味着以后新增
    // 页面不可能漏掉——这是那次漏掉的唯一成因。
    //
    // safeDrawing 在各页面的 verticalScroll **之外**：滚动内容停在安全区边界，
    // 不从状态栏下面穿过去。MainScreen 原本就是这个顺序，三页因此一致。
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Background)
            .padding(WindowInsets.safeDrawing.asPaddingValues()),
    ) {
        NavHost(
            navController = navController,
            startDestination = startDestination,
        ) {
            composable(MyProxyRoutes.BINDING) {
                BindScreen(
                    onNavigateToMain = {
                        app.connectionController.bindingCompleted()
                        navController.navigate(MyProxyRoutes.MAIN) {
                            popUpTo(MyProxyRoutes.BINDING) { inclusive = true }
                        }
                    },
                )
            }
            composable(MyProxyRoutes.MAIN) {
                MainScreen(
                    onNavigateToSettings = {
                        navController.navigate(MyProxyRoutes.SETTINGS)
                    },
                    onNavigateToBinding = navigateToBinding,
                )
            }
            composable(MyProxyRoutes.SETTINGS) {
                val settingsViewModel: SettingsViewModel = viewModel(
                    factory = SettingsViewModel.factory(app),
                )
                SettingsScreen(
                    viewModel = settingsViewModel,
                    onBack = {
                        navController.popBackStack()
                    },
                    onRebind = {
                        app.connectionController.rebind(navigateToBinding)
                    },
                    onOpenUrl = { url ->
                        val uri = Uri.parse(url)
                        if (uri.scheme == "https") {
                            // Started from the application context, so it needs its own
                            // task: without NEW_TASK the framework throws (targetSdk 28+)
                            // and runCatching swallowed it -- the tap did nothing.
                            val intent = Intent(Intent.ACTION_VIEW, uri)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                            runCatching {
                                ContextCompat.startActivity(appContext, intent, null)
                            }
                        }
                    },
                )
            }
        }
    }
}
