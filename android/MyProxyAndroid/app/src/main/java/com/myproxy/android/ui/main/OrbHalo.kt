package com.myproxy.android.ui.main

import com.myproxy.android.domain.model.AppState

/**
 * 一个状态在界面上的颜色含义。状态行的标记与文字、能量球的光晕与图标都从这里取色，
 * 所以它们不会各说各的（先前能量球已连接时是 Accent，状态行却是 Success）。
 */
internal enum class StateTone { NEUTRAL, ACCENT, SUCCESS, ERROR }

internal fun stateTone(appState: AppState): StateTone = when (appState) {
    AppState.CONNECTED -> StateTone.SUCCESS
    AppState.ERROR -> StateTone.ERROR
    AppState.CONNECTING, AppState.DISCONNECTING -> StateTone.ACCENT
    AppState.DISCONNECTED, AppState.UNBOUND -> StateTone.NEUTRAL
}

/**
 * 光晕怎么动：单程时长（0 = 不动）、明灭的暗端与亮端、亮端的微胀倍数。
 *
 * 系统「移除动画」时 Compose 把无限动画直接停在终值，也就是亮端——光晕静止但看得见，
 * 状态仍能从颜色读出。所以亮端要取「静止时也合适」的强度，而不是呼吸的峰值那一下。
 */
internal enum class HaloMotion(
    val halfCycleMs: Int,
    val dimAlpha: Float,
    val brightAlpha: Float,
    val swell: Float,
) {
    /** 已连接：慢呼吸，节奏与 Windows 能量球相同（单程 2.6s）。 */
    BREATH(halfCycleMs = 2_600, dimAlpha = 0.10f, brightAlpha = 0.45f, swell = 1.03f),

    /** 连接中：快一档，和状态行的 spinner 一起说「正在进行」。 */
    PULSE(halfCycleMs = 1_000, dimAlpha = 0.12f, brightAlpha = 0.50f, swell = 1.03f),

    /** 失败：静止的淡光。失败不该显得有生气。 */
    STILL(halfCycleMs = 0, dimAlpha = 0.22f, brightAlpha = 0.22f, swell = 1f),
}

internal data class OrbHalo(val tone: StateTone, val motion: HaloMotion)


internal fun orbHalo(appState: AppState): OrbHalo? {
    val motion = when (appState) {
        AppState.CONNECTED -> HaloMotion.BREATH
        AppState.CONNECTING -> HaloMotion.PULSE
        AppState.ERROR -> HaloMotion.STILL
        AppState.DISCONNECTING, AppState.DISCONNECTED, AppState.UNBOUND -> return null
    }
    return OrbHalo(stateTone(appState), motion)
}
