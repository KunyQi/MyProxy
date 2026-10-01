package com.myproxy.android.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp


object MyProxyType {
    /** 页面标题：MyProxy、设置。 */
    val Title = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Medium,
        fontSize = 20.sp,
        lineHeight = 28.sp,
        letterSpacing = 0.sp,
    )

    /** 主信息：连接状态、主按钮、设置行标题、配对码。 */
    val Primary = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 16.sp,
        lineHeight = 24.sp,
        letterSpacing = 0.sp,
    )

    /** 次要信息：状态说明、延迟、模式名、设置行说明、页脚动作、错误提示。 */
    val Secondary = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 14.sp,
        lineHeight = 20.sp,
        letterSpacing = 0.sp,
    )

    /** 说明文字：分组标签（代理模式、设置页段标题）。 */
    val Caption = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 12.sp,
        lineHeight = 16.sp,
        letterSpacing = 0.sp,
    )
}

// Material3 组件会自己去读某个 slot：Button/TextButton 读 labelLarge，AlertDialog 标题读
// headlineSmall、正文读 bodyMedium，OutlinedTextField 读 bodyLarge 与 bodySmall。
// **没写的 slot 会静默退回 Material3 默认值**（例如 headlineSmall = 24sp），既不编译
// 报错也不被 lint 拦住，于是弹窗标题悄悄变成第五个字号。所以这里把全部 15 个 slot
// 都显式映射到上面四级之一——没有哪个 slot 能漏出这张表。
val MyProxyTypography = Typography(
    displayLarge = MyProxyType.Title,
    displayMedium = MyProxyType.Title,
    displaySmall = MyProxyType.Title,
    headlineLarge = MyProxyType.Title,
    headlineMedium = MyProxyType.Title,
    headlineSmall = MyProxyType.Title,
    titleLarge = MyProxyType.Title,
    titleMedium = MyProxyType.Primary.copy(fontWeight = FontWeight.Medium),
    titleSmall = MyProxyType.Secondary.copy(fontWeight = FontWeight.Medium),
    bodyLarge = MyProxyType.Primary,
    bodyMedium = MyProxyType.Secondary,
    bodySmall = MyProxyType.Caption,
    labelLarge = MyProxyType.Primary.copy(fontWeight = FontWeight.Medium),
    labelMedium = MyProxyType.Caption.copy(fontWeight = FontWeight.Medium),
    labelSmall = MyProxyType.Caption,
)
