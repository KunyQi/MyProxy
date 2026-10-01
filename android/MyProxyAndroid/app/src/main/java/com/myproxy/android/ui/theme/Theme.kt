package com.myproxy.android.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// 每一个 slot 都落到本套调色板上。Material3 组件默认读的是 surfaceContainerHigh
// （弹窗底）、surfaceContainerHighest（开关关闭态的轨道）、outline（开关与输入框的
// 描边）这类我们从没定义过的槽位，缺省值是 Material You 的淡紫灰（#ECE6F0 /
// #E6E0E9 / #79747E）——这就是先前设置页开关与弹窗底色不属于本套中性色的原因。
private val MyProxyLightColors = lightColorScheme(
    primary = Accent,
    onPrimary = Surface,
    primaryContainer = AccentSubtle,
    onPrimaryContainer = Accent,
    inversePrimary = AccentSubtle,
    // Secondary colors use the same accent palette.
    secondary = Accent,
    onSecondary = Surface,
    secondaryContainer = AccentSubtle,
    onSecondaryContainer = TextPrimary,
    tertiary = Accent,
    onTertiary = Surface,
    tertiaryContainer = AccentSubtle,
    onTertiaryContainer = TextPrimary,
    background = Background,
    onBackground = TextPrimary,
    surface = Surface,
    onSurface = TextPrimary,
    surfaceVariant = Background,
    onSurfaceVariant = TextSecondary,
    // 不要色调叠加：层级靠边框与底色，不靠 tonal elevation。
    surfaceTint = Color.Transparent,
    inverseSurface = TextPrimary,
    inverseOnSurface = Surface,
    error = Error,
    onError = Surface,
    errorContainer = Surface,
    onErrorContainer = Error,
    outline = BorderStrong,
    outlineVariant = Border,
    surfaceBright = Surface,
    surfaceDim = Background,
    surfaceContainerLowest = Surface,
    surfaceContainerLow = Surface,
    surfaceContainer = Surface,
    surfaceContainerHigh = Surface,
    surfaceContainerHighest = Border,
)

// Reuse compact corner radii across component sizes.
private val MyProxyShapes = Shapes(
    extraSmall = RoundedCornerShape(DesignTokens.RadiusSmall),
    small = RoundedCornerShape(DesignTokens.RadiusSmall),
    medium = RoundedCornerShape(DesignTokens.RadiusPanel),
    large = RoundedCornerShape(DesignTokens.RadiusPanel),
    extraLarge = RoundedCornerShape(DesignTokens.RadiusPanel),
)

@Composable
fun MyProxyTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = MyProxyLightColors,
        typography = MyProxyTypography,
        shapes = MyProxyShapes,
        content = content,
    )
}
