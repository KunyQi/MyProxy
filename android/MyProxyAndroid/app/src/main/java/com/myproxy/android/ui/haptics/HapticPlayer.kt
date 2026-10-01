package com.myproxy.android.ui.haptics

import android.content.Context
import android.os.Build
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.provider.Settings
import androidx.annotation.RequiresApi
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext

/**
 * Turns a [HapticCue] into a waveform on the device vibrator.
 *
 * The system-wide "touch feedback" switch is honoured explicitly. Compose's
 * own `LocalHapticFeedback` does that for free but only offers the platform's
 * generic gestures; MyProxy needs a cue distinct enough to tell "connected"
 * from "failed" without looking at the screen, which means driving the
 * vibrator directly -- and therefore checking the setting ourselves.
 *
 * Every call is best-effort. A device with no vibrator, a vendor that refuses
 * the effect, or a user who turned touch feedback off all end in silence
 * rather than an exception on the UI thread.
 */
class HapticPlayer(private val context: Context) {

    private val vibrator: Vibrator? by lazy {
        runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                context.getSystemService(VibratorManager::class.java)?.defaultVibrator
            } else {
                @Suppress("DEPRECATION")
                context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
            }
        }.getOrNull()?.takeIf { it.hasVibrator() }
    }

    private var lastTapAt: Long? = null

    fun play(cue: HapticCue) {
        if (cue == HapticCue.TAP) {
            val now = SystemClock.uptimeMillis()
            val last = lastTapAt
            if (last != null && !tapIsDistinct(now - last)) return
            lastTapAt = now
        }
        val device = vibrator ?: return
        if (!isTouchFeedbackEnabled()) return
        runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                device.vibrate(effectFor(cue))
            } else {
                @Suppress("DEPRECATION")
                device.vibrate(patternFor(cue), -1)
            }
        }
    }

    /**
     * Read live rather than cached: the user may flip the switch in system
     * settings while MyProxy sits in the background.
     */
    private fun isTouchFeedbackEnabled(): Boolean = runCatching {
        Settings.System.getInt(
            context.contentResolver,
            Settings.System.HAPTIC_FEEDBACK_ENABLED,
            1,
        ) != 0
    }.getOrDefault(true)

    @RequiresApi(Build.VERSION_CODES.O)
    private fun effectFor(cue: HapticCue): VibrationEffect =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // Predefined effects are tuned per device, so they feel native
            // instead of like a generic buzz.
            when (cue) {
                HapticCue.TAP -> VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK)
                HapticCue.CONFIRM -> VibrationEffect.createPredefined(VibrationEffect.EFFECT_DOUBLE_CLICK)
                HapticCue.WARN -> VibrationEffect.createPredefined(VibrationEffect.EFFECT_HEAVY_CLICK)
            }
        } else {
            when (cue) {
                HapticCue.TAP -> VibrationEffect.createOneShot(12L, 80)
                HapticCue.CONFIRM -> VibrationEffect.createWaveform(
                    CONFIRM_PATTERN,
                    intArrayOf(0, 120, 0, 190),
                    -1,
                )
                HapticCue.WARN -> VibrationEffect.createWaveform(
                    WARN_PATTERN,
                    intArrayOf(0, 200, 0, 200),
                    -1,
                )
            }
        }

    private fun patternFor(cue: HapticCue): LongArray = when (cue) {
        HapticCue.TAP -> longArrayOf(0L, 12L)
        HapticCue.CONFIRM -> CONFIRM_PATTERN
        HapticCue.WARN -> WARN_PATTERN
    }

    private companion object {
        val CONFIRM_PATTERN = longArrayOf(0L, 16L, 44L, 26L)
        val WARN_PATTERN = longArrayOf(0L, 28L, 70L, 28L)
    }
}

@Composable
fun rememberHapticPlayer(): HapticPlayer {
    val context = LocalContext.current
    return remember(context) { HapticPlayer(context) }
}
