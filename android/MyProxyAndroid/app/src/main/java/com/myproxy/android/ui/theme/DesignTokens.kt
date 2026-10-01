package com.myproxy.android.ui.theme

import androidx.compose.ui.unit.dp

object DesignTokens {
    val RadiusSmall = 6.dp
    val RadiusPanel = 8.dp

    val SpaceXxs = 2.dp
    val SpaceXs = 4.dp
    val SpaceSm = 8.dp
    val SpaceMd = 16.dp
    val SpaceLg = 24.dp
    val SpaceXl = 32.dp
    val SpaceXxl = 48.dp
    val SpaceXxxl = 64.dp

    val ControlHeight = 48.dp
    val SettingsRowHeight = 56.dp
    val DividerThickness = 0.5.dp // Hairline
    val BorderThin = 1.dp


    val ContentMaxWidth = 440.dp

    // 能量球。
    /** 热区与投影的外框。 */
    val OrbSize = 180.dp
    /** 圆盘本体。 */
    val OrbSizeInner = 150.dp
    /**
     * 球缘光晕的外径：本体之外约 12dp 的一圈，由球缘向外淡出。比例取自 Windows 能量球
     * （本体 140、光晕 162），呼吸微胀 1.03 倍后仍在 [OrbSize] 热区之内。
     */
    val OrbHaloSize = 174.dp
    /** 电源图标的描边。 */
    val IconStrokeThin = 3.dp

    // 状态标记与 spinner 共用一个直径，两者在同一行里互相替换时行高不跳。
    // 以 dp 而非字号定义：标记是图形不是文字，跟着系统字体缩放走会变形
    // （先前它是一个 titleMedium 的「●」/「○」/「!」字符）。
    val StatusMarkSize = 16.dp

    val StatusMarkStroke = 1.5.dp
}
