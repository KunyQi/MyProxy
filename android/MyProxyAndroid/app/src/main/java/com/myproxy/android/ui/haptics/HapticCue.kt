package com.myproxy.android.ui.haptics

import com.myproxy.android.domain.model.AppState

/**
 * A physical cue the UI may play.
 *
 * Deliberately an enum instead of a `VibrationEffect`: deciding *which* cue a
 * state change deserves is pure Kotlin and unit-tested on the JVM, while the
 * only Android dependency lives in [HapticPlayer], which turns a cue into a
 * waveform.
 */
enum class HapticCue {
    /** Acknowledges a touch. Short and light. */
    TAP,

    /** The requested operation succeeded. Two crisp beats. */
    CONFIRM,

    /** The operation failed, or protection took over. Two heavier beats. */
    WARN,
}

/**
 * The cue owed to a state change, or `null` when the change is not worth a
 * buzz.
 *
 * A `null` [previous] is the first observation after the screen is composed --
 * process start, rotation, or returning from the background. The state did not
 * change then, so nothing is played: buzzing every time the app is opened over
 * a live tunnel would be noise.
 */
internal fun hapticCueForTransition(previous: AppState?, current: AppState): HapticCue? {
    if (previous == null || previous == current) return null
    return when (current) {
        AppState.CONNECTED -> HapticCue.CONFIRM
        AppState.ERROR -> HapticCue.WARN
        AppState.DISCONNECTED -> when (previous) {
            // Reaching "disconnected" from a live or stopping tunnel is the
            // completion of a stop the user asked for. Arriving from UNBOUND
            // or ERROR is bookkeeping and gets no cue.
            AppState.CONNECTED, AppState.DISCONNECTING, AppState.CONNECTING -> HapticCue.TAP
            else -> null
        }
        AppState.CONNECTING,
        AppState.DISCONNECTING,
        AppState.UNBOUND,
        -> null
    }
}

/**
 * Whether a TAP arriving [sinceLastTapMs] after the previous one should play.
 *
 * A press is acknowledged with a TAP, and a stop the user asked for ends with
 * one too. A fast stop completes within a couple of hundred milliseconds, so
 * the two ticks land back to back and feel like the double beat that means
 * "connected". Two TAPs this close are one touch to the hand anyway; a slow
 * stop still gets both -- "heard you", then "done".
 */
internal fun tapIsDistinct(sinceLastTapMs: Long): Boolean = sinceLastTapMs >= TAP_MERGE_WINDOW_MS

internal const val TAP_MERGE_WINDOW_MS = 300L
