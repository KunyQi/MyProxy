package com.myproxy.android.ui.main

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.EaseInOutSine
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.PressInteraction
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RadialGradientShader
import androidx.compose.ui.graphics.Shader
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.currentStateAsState
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myproxy.android.R
import com.myproxy.android.domain.model.AppState
import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.presentation.MainViewModel
import com.myproxy.android.ui.components.CenteredRowArrangement
import com.myproxy.android.ui.components.LinkButton
import com.myproxy.android.ui.components.PageHeader
import com.myproxy.android.ui.components.SecondaryButton
import com.myproxy.android.ui.components.SegmentedControl
import com.myproxy.android.ui.components.UtilityPage
import com.myproxy.android.ui.components.lineHeightDp
import com.myproxy.android.ui.haptics.HapticCue
import com.myproxy.android.ui.haptics.HapticPlayer
import com.myproxy.android.ui.haptics.hapticCueForTransition
import com.myproxy.android.ui.haptics.rememberHapticPlayer
import com.myproxy.android.ui.theme.Accent
import com.myproxy.android.ui.theme.Border
import com.myproxy.android.ui.theme.DesignTokens
import com.myproxy.android.ui.theme.Error
import com.myproxy.android.ui.theme.GlazeHighlight
import com.myproxy.android.ui.theme.MyProxyType
import com.myproxy.android.ui.theme.OrbPorcelainLight
import com.myproxy.android.ui.theme.OrbPorcelainMid
import com.myproxy.android.ui.theme.OrbPorcelainShade
import com.myproxy.android.ui.theme.ShadowSoft
import com.myproxy.android.ui.theme.Success
import com.myproxy.android.ui.theme.TextPrimary
import com.myproxy.android.ui.theme.TextSecondary
import kotlinx.coroutines.delay

@Composable
fun MainScreen(
    onNavigateToSettings: () -> Unit,
    onNavigateToBinding: () -> Unit,
    viewModel: MainViewModel = viewModel(),
) {
    val context = LocalContext.current
    val appState by viewModel.appState.collectAsStateWithLifecycle()
    val proxyMode by viewModel.proxyMode.collectAsStateWithLifecycle()
    val latencyMs by viewModel.latencyMs.collectAsStateWithLifecycle()
    val isSwitching by viewModel.isSwitching.collectAsStateWithLifecycle()
    val vpnPermission by viewModel.vpnPermissionRequest.collectAsStateWithLifecycle()
    val isBatteryOptimized by viewModel.isBatteryOptimized.collectAsStateWithLifecycle()
    val isTrafficBlocked by viewModel.isTrafficBlocked.collectAsStateWithLifecycle()
    val haptics = rememberHapticPlayer()

    val lifecycleOwner = LocalLifecycleOwner.current
    val lifecycleState by lifecycleOwner.lifecycle.currentStateAsState()
    LaunchedEffect(lifecycleState) {
        val resumed = lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
        viewModel.onAppForegroundChanged(resumed)
        if (resumed) viewModel.checkBatteryOptimization()
    }
    var lastHapticState by remember { mutableStateOf<AppState?>(null) }
    LaunchedEffect(appState) {
        hapticCueForTransition(lastHapticState, appState)?.let(haptics::play)
        lastHapticState = appState
        if (appState == AppState.UNBOUND) onNavigateToBinding()
    }

    var notificationPermissionRequested by rememberSaveable { mutableStateOf(false) }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { viewModel.retry() }
    val vpnPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK) viewModel.connectWithPermissionGranted()
        else viewModel.vpnPermissionDenied()
    }
    LaunchedEffect(vpnPermission) {
        vpnPermission?.let {
            vpnPermissionLauncher.launch(it)
            viewModel.consumeVpnPermissionRequest()
        }
    }

    fun startOrRetry() {
        val needsNotificationPermission = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            !notificationPermissionRequested &&
            ContextCompat.checkSelfPermission(
                context, Manifest.permission.POST_NOTIFICATIONS,
            ) != PackageManager.PERMISSION_GRANTED
        if (needsNotificationPermission) {
            notificationPermissionRequested = true
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            viewModel.retry()
        }
    }

    val busy = isSwitching || appState == AppState.CONNECTING || appState == AppState.DISCONNECTING

    // （能量球只改颜色）；该状态下没有内容的行画成空行。所以正文块里每一行都有固定的预留高度，
    // 块的总高度不随状态变化，UtilityPage 把它居中之后各行也就不会上下跳。
    UtilityPage(
        header = { PageHeader(stringResource(R.string.app_name)) },
        footer = {
            // 后台限制提示与连接状态无关，放在页脚区而不是正文块里；槽位常驻，
            // 它出现或消失时页脚高度不变，居中的正文块也就不会跟着上下挪。
            //
            // 后台限制提示使用中性色。
            Box(
                Modifier.fillMaxWidth().heightIn(min = DesignTokens.ControlHeight),
                contentAlignment = Alignment.Center,
            ) {
                if (isBatteryOptimized) {
                    LinkButton(
                        text = stringResource(R.string.main_battery_warning),
                        color = TextPrimary,
                        onClick = {
                            haptics.play(HapticCue.TAP)
                            viewModel.requestIgnoreBatteryOptimizations()
                        },
                    )
                }
            }
            LinkButton(
                text = stringResource(R.string.main_settings),
                onClick = onNavigateToSettings,
            )
        },
    ) {
        // 能量球。
        PowerOrb(
            appState = appState,
            isTrafficBlocked = isTrafficBlocked,
            enabled = appState != AppState.UNBOUND &&
                (appState == AppState.CONNECTING || (!busy && appState != AppState.DISCONNECTING)),
            haptics = haptics,
            onClick = {
                when (mainOrbAction(appState, isTrafficBlocked)) {
                    MainOrbAction.START,
                    MainOrbAction.RETRY,
                    -> startOrRetry()
                    MainOrbAction.STOP,
                    MainOrbAction.STOP_AND_RELEASE_PROTECTION,
                    MainOrbAction.CANCEL_CONNECTING,
                    -> viewModel.stop()
                    MainOrbAction.UNAVAILABLE -> Unit
                }
            },
        )

        Spacer(Modifier.height(DesignTokens.SpaceLg))
        // 颜色之外的第二个状态信号。
        ConnectionStatus(appState, isTrafficBlocked)
        Spacer(Modifier.height(DesignTokens.SpaceXs))
        // 状态说明行：常驻两行高度。连接失败的说明在大字号下会折成两行，
        // 预留两行才能保证它出现时不把下面的模式行往下推。
        Box(
            Modifier.fillMaxWidth().heightIn(min = lineHeightDp(MyProxyType.Secondary, 2)),
            contentAlignment = Alignment.TopCenter,
        ) {
            val description = if (com.myproxy.android.data.api.ApiConfig.builtInTarget?.isConfigured != true) {
                stringResource(R.string.binding_error_not_configured)
            } else when (appState) {
                AppState.CONNECTED -> stringResource(R.string.main_connected_description)
                AppState.ERROR -> stringResource(
                    if (isTrafficBlocked) R.string.main_error_blocked else R.string.main_error_message,
                )
                else -> ""
            }
            Text(
                text = description,
                style = MyProxyType.Secondary,
                color = if (appState == AppState.ERROR) Error else TextSecondary,
                textAlign = TextAlign.Center,
            )
        }

        Spacer(Modifier.height(DesignTokens.SpaceLg))
        Text(
            text = stringResource(R.string.main_proxy_mode),
            style = MyProxyType.Caption,
            color = TextSecondary,
        )
        Spacer(Modifier.height(DesignTokens.SpaceSm))
        SegmentedControl(
            options = listOf(
                ProxyMode.RULE to stringResource(R.string.main_mode_rule),
                ProxyMode.GLOBAL to stringResource(R.string.main_mode_global),
            ),
            selected = proxyMode,
            enabled = !busy,
            onSelect = { mode ->
                haptics.play(HapticCue.TAP)
                viewModel.setProxyMode(mode)
            },
        )

        Spacer(Modifier.height(DesignTokens.SpaceLg))
        // 次要状态行：切换中 / 延迟 + 检查连接。两者回答的是同一个问题——
        // 「链路此刻在干什么」——所以共用一行，而且这一行在所有状态下都占位：
        // 先前「检查连接」只在已连接时单独多出一行，连上与断开之间整页的页脚会跳。
        Row(
            modifier = Modifier.fillMaxWidth().heightIn(min = DesignTokens.ControlHeight),
            horizontalArrangement = CenteredRowArrangement,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            // Read the delegate once: `x != null` followed by `x!!` is two
            // reads, and the second one is only safe for as long as both stay
            // inside the same composition pass.
            val latency = latencyMs
            when {
                isSwitching -> Text(
                    text = stringResource(R.string.main_switching),
                    style = MyProxyType.Secondary,
                    color = TextSecondary,
                )
                showRetryAction(appState, isTrafficBlocked) -> SecondaryButton(
                    text = stringResource(R.string.main_orb_retry),
                    onClick = {
                        haptics.play(HapticCue.TAP)
                        startOrRetry()
                    },
                )
                appState == AppState.CONNECTED -> {
                    if (latency != null) {
                        Text(
                            text = stringResource(R.string.main_latency, latency),
                            style = MyProxyType.Secondary,
                            color = TextSecondary,
                        )
                    }
                    SecondaryButton(
                        text = stringResource(R.string.main_check_connection),
                        onClick = {
                            haptics.play(HapticCue.TAP)
                            viewModel.checkConnection()
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun ConnectionStatus(appState: AppState, trafficBlocked: Boolean) {
    // 与能量球的光晕同一个来源（stateTone），两处不会各说各的。
    val color = toneColor(stateTone(appState), neutral = TextSecondary)
    val status = when (appState) {
        AppState.UNBOUND -> R.string.main_status_unbound
        AppState.DISCONNECTED -> R.string.main_status_disconnected
        AppState.CONNECTING -> R.string.main_status_connecting
        AppState.CONNECTED -> R.string.main_status_connected
        AppState.DISCONNECTING -> R.string.main_status_disconnecting
        AppState.ERROR -> if (trafficBlocked) R.string.main_status_blocked else R.string.main_status_error
    }
    Row(
        // heightIn, not height: this row carries titleMedium, whose line box
        // already exceeds SpaceXl once the system font scale passes ~1.3.
        // Pinning it clipped the one thing this screen exists to report.
        modifier = Modifier.fillMaxWidth().heightIn(min = DesignTokens.SpaceXl),
        // 标记与文字当成一个整体居中。
        horizontalArrangement = CenteredRowArrangement,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (appState == AppState.CONNECTING || appState == AppState.DISCONNECTING) {
            CircularProgressIndicator(
                modifier = Modifier.size(DesignTokens.StatusMarkSize).clearAndSetSemantics { },
                color = color,
                strokeWidth = DesignTokens.StatusMarkStroke,
            )
        } else {
            StatusMark(appState, color)
        }
        Text(
            text = stringResource(status),
            style = MyProxyType.Primary,
            fontWeight = FontWeight.Medium,
            color = color,
        )
    }
}


@Composable
private fun StatusMark(appState: AppState, color: Color) {
    Canvas(
        modifier = Modifier
            .size(DesignTokens.StatusMarkSize)
            .clearAndSetSemantics { },
    ) {
        val stroke = DesignTokens.StatusMarkStroke.toPx()
        val center = Offset(size.width / 2f, size.height / 2f)
        when (appState) {
            // 已连接：唯一一个实心的状态。实心比同直径的空心重，所以半径取小一档。
            AppState.CONNECTED -> drawCircle(
                color = color,
                radius = size.minDimension * 0.25f,
                center = center,
            )
            // 失败：一竖一点。
            AppState.ERROR -> {
                drawLine(
                    color = color,
                    start = Offset(center.x, size.height * 0.18f),
                    end = Offset(center.x, size.height * 0.60f),
                    strokeWidth = stroke * 1.4f,
                    cap = StrokeCap.Round,
                )
                drawCircle(
                    color = color,
                    radius = stroke * 0.8f,
                    center = Offset(center.x, size.height * 0.84f),
                )
            }
            // 未绑定 / 未连接 / 停止中：空心环。
            else -> drawCircle(
                color = color,
                radius = size.minDimension * 0.34f,
                center = center,
                style = Stroke(width = stroke),
            )
        }
    }
}


/** 状态色。未连接时状态行用灰字、能量球图标用 Accent（它是主操作），由调用方给出。 */
private fun toneColor(tone: StateTone, neutral: Color): Color = when (tone) {
    StateTone.NEUTRAL -> neutral
    StateTone.ACCENT -> Accent
    StateTone.SUCCESS -> Success
    StateTone.ERROR -> Error
}


@Composable
private fun PowerOrb(
    appState: AppState,
    isTrafficBlocked: Boolean,
    enabled: Boolean,
    haptics: HapticPlayer,
    onClick: () -> Unit,
) {
    val interactionSource = remember { MutableInteractionSource() }

    // 按压缩放从交互事件流取，并至少保持 MIN_PRESS_VISUAL_MS：快速轻点的 Press 与
    // Release 背靠背到达，只看「当前是否按下」就一帧都显示不出来。
    var pressedVisual by remember { mutableStateOf(false) }
    LaunchedEffect(interactionSource) {
        var pressedAt = 0L
        interactionSource.interactions.collect { interaction ->
            when (interaction) {
                is PressInteraction.Press -> {
                    pressedAt = System.nanoTime()
                    pressedVisual = true
                }
                is PressInteraction.Release, is PressInteraction.Cancel -> {
                    val heldMs = (System.nanoTime() - pressedAt) / 1_000_000
                    if (heldMs < MIN_PRESS_VISUAL_MS) delay(MIN_PRESS_VISUAL_MS - heldMs)
                    pressedVisual = false
                }
            }
        }
    }

    val scale by animateFloatAsState(
        targetValue = if (pressedVisual) 0.96f else 1f,
        animationSpec = spring(dampingRatio = 1.0f, stiffness = 300f),
        label = "PressScale",
    )

    val iconColor by animateColorAsState(
        targetValue = toneColor(stateTone(appState), neutral = Accent),
        animationSpec = tween(ORB_COLOR_MS),
        label = "IconColor",
    )

    // 光晕消失（停止中、断开）时保留最后的颜色与节奏淡出，而不是先变色再消失。
    val halo = orbHalo(appState)
    var lastHalo by remember { mutableStateOf(halo ?: OrbHalo(StateTone.ACCENT, HaloMotion.STILL)) }
    SideEffect { if (halo != null) lastHalo = halo }
    val shownHalo = halo ?: lastHalo

    val presence = animateFloatAsState(
        targetValue = if (halo != null) 1f else 0f,
        animationSpec = tween(ORB_COLOR_MS),
        label = "HaloPresence",
    )
    val haloColor by animateColorAsState(
        targetValue = toneColor(shownHalo.tone, neutral = Accent),
        animationSpec = tween(ORB_COLOR_MS),
        label = "HaloColor",
    )
    // 完全淡出后连呼吸的无限动画一起撤掉：未连接的主页不该每帧重绘。
    val haloVisible by remember { derivedStateOf { presence.value > 0f } }
    val phase = rememberHaloPhase(shownHalo.motion, running = haloVisible)
    val motion = shownHalo.motion

    val orbDescription = stringResource(
        mainOrbActionLabel(mainOrbAction(appState, isTrafficBlocked)),
    )

    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier.size(DesignTokens.OrbSize),
    ) {
        // Rim halo：由球缘向外淡出的一圈状态色。动画只读在 graphicsLayer / drawBehind 里，
        // 每帧只重绘这一层，不触发重组。
        Box(
            modifier = Modifier
                .size(DesignTokens.OrbHaloSize)
                .graphicsLayer {
                    val p = phase.value
                    val swell = 1f + (motion.swell - 1f) * p
                    scaleX = swell
                    scaleY = swell
                    alpha = presence.value * (motion.dimAlpha + (motion.brightAlpha - motion.dimAlpha) * p)
                }
                .drawBehind {
                    val c = haloColor
                    val rim = DesignTokens.OrbSizeInner / DesignTokens.OrbHaloSize
                    drawCircle(
                        brush = Brush.radialGradient(
                            0f to c,
                            rim to c,
                            rim + (1f - rim) * 0.28f to c.copy(alpha = 0.66f),
                            rim + (1f - rim) * 0.56f to c.copy(alpha = 0.35f),
                            rim + (1f - rim) * 0.80f to c.copy(alpha = 0.13f),
                            1f to Color.Transparent,
                            center = center,
                            radius = size.minDimension / 2f,
                        ),
                    )
                },
        )

        // Porcelain body：任何状态都是同一只白瓷球。
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(DesignTokens.OrbSizeInner * scale)
                .shadow(
                    elevation = 4.dp,
                    shape = CircleShape,
                    ambientColor = ShadowSoft,
                    spotColor = ShadowSoft,
                )
                .background(brush = PorcelainBody, shape = CircleShape)
                .border(DesignTokens.DividerThickness, Border, CircleShape)
                .clickable(
                    interactionSource = interactionSource,
                    indication = null,
                    enabled = enabled,
                    role = Role.Button,
                    onClick = {
                        haptics.play(HapticCue.TAP)
                        onClick()
                    },
                )
                .semantics { contentDescription = orbDescription },
        ) {
            // Precise Hairline Icon
            Canvas(modifier = Modifier.size(DesignTokens.SpaceXxxl)) {
                val stroke = DesignTokens.IconStrokeThin.toPx()
                drawArc(
                    color = iconColor,
                    startAngle = -230f,
                    sweepAngle = 280f,
                    useCenter = false,
                    style = Stroke(width = stroke, cap = StrokeCap.Round),
                )
                drawLine(
                    color = iconColor,
                    start = center.copy(y = 0f + stroke / 1.5f),
                    end = center.copy(y = size.height * 0.45f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round,
                )
            }
        }

        // Sheen Layer (Subtle top-left highlight)
        Box(
            modifier = Modifier
                .size(DesignTokens.OrbSizeInner * scale)
                .padding(2.dp)
                .border(
                    BorderStroke(1.dp, Brush.linearGradient(listOf(GlazeHighlight, Color.Transparent))),
                    CircleShape,
                ),
        )
    }
}

/**
 * 呼吸的相位，0 = 暗端、1 = 亮端。不动的光晕与已撤下的光晕都停在亮端。
 *
 * 系统「移除动画」时 Compose 把无限动画直接停在终值（亮端）并不再逐帧推进，所以
 * 减少动效的用户看到的是一圈静止的光——颜色仍在，只是不动。
 */
@Composable
private fun rememberHaloPhase(motion: HaloMotion, running: Boolean): State<Float> =
    if (running && motion.halfCycleMs > 0) {
        // 换节奏（连接中 → 已连接）时重建：InfiniteTransition 只在起止值变化时更新，
        // 只换 animationSpec 不会生效。
        key(motion) {
            rememberInfiniteTransition(label = "OrbHalo").animateFloat(
                initialValue = 0f,
                targetValue = 1f,
                animationSpec = infiniteRepeatable(
                    animation = tween(motion.halfCycleMs, easing = EaseInOutSine),
                    repeatMode = RepeatMode.Reverse,
                ),
                label = "HaloPhase",
            )
        }
    } else {
        remember { mutableFloatStateOf(1f) }
    }

/**
 * 白瓷胎：光心偏左上（与釉面高光同一盏灯），向右下球缘渐暗，读成球体而不是平盘。
 * 用 ShaderBrush 是因为偏心的径向渐变要按实际尺寸算圆心。
 */
private val PorcelainBody = object : ShaderBrush() {
    override fun createShader(size: Size): Shader = RadialGradientShader(
        center = Offset(size.width * 0.38f, size.height * 0.32f),
        radius = size.minDimension * 0.78f,
        colors = listOf(OrbPorcelainLight, OrbPorcelainMid, OrbPorcelainShade),
        colorStops = listOf(0f, 0.45f, 1f),
    )
}

private const val ORB_COLOR_MS = 800

/** 快速轻点的按压缩放至少显示这么久，否则 Press 与 Release 同帧到达时看不见。 */
private const val MIN_PRESS_VISUAL_MS = 100L
