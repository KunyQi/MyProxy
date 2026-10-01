package com.myproxy.android

import android.graphics.Color
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.myproxy.android.ui.navigation.MyProxyNavHost
import com.myproxy.android.ui.theme.MyProxyTheme


private val DarkScrim = Color.argb(0x80, 0x1B, 0x1B, 0x1B)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(Color.TRANSPARENT, DarkScrim),
            navigationBarStyle = SystemBarStyle.light(Color.TRANSPARENT, DarkScrim),
        )
        setContent {
            MyProxyTheme {
                MyProxyNavHost()
            }
        }
    }
}
