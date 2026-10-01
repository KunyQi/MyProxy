package com.myproxy.android.ui.theme

import androidx.compose.ui.graphics.Color

// Accent and status colors maintain at least 4.5:1 contrast with white.
val Background = Color(0xFFFCFBF9) // MilkyWhite
val Surface = Color(0xFFFFFFFF)
val TextPrimary = Color(0xFF2D3436) // Dense Charcoal
val TextSecondary = Color(0xFF636E72) // Jade Gray
val TextDisabled = Color(0xFFB2BEC3) // Faint Gray
val Border = Color(0xFFEAEAEA) // Ceramic Border
// Visible outlines for text fields, segments and switches.
val BorderStrong = Color(0xFFC5CCD0)
val Accent = Color(0xFF3B6FA0) // Muted Steel Blue — 5.29:1 on white
// Subtle selected-state background with an 8% accent tint.
val AccentSubtle = Color(0xFFEEF3F8)
val ShadowSoft = Color(0x08000000)
val GlazeHighlight = Color(0x66FFFFFF) // Porcelain sheen
// 瓷胎：冷调白瓷，由偏左上的光心向球缘渐暗，读成球体而不是平盘。任何状态下都不变——
// 状态色仅用于球缘光晕。
val OrbPorcelainLight = Color(0xFFFFFFFF)
val OrbPorcelainMid = Color(0xFFF8F7F4)
val OrbPorcelainShade = Color(0xFFE4E1DB)
val Success = Color(0xFF3F7D58) // Muted Jade — 4.87:1 on white
val Error = Color(0xFFB5443F) // Muted Brick — 5.42:1 on white
