package com.myproxy.android.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myproxy.android.BuildConfig
import com.myproxy.android.R
import com.myproxy.android.presentation.SettingsViewModel
import com.myproxy.android.ui.components.Hairline
import com.myproxy.android.ui.components.LinkButton
import com.myproxy.android.ui.components.PageHeader
import com.myproxy.android.ui.theme.Accent
import com.myproxy.android.ui.theme.BorderStrong
import com.myproxy.android.ui.theme.DesignTokens
import com.myproxy.android.ui.theme.MyProxyType
import com.myproxy.android.ui.theme.Surface
import com.myproxy.android.ui.theme.TextDisabled
import com.myproxy.android.ui.theme.TextPrimary
import com.myproxy.android.ui.theme.TextSecondary
import com.myproxy.android.vpn.health.CoreHealthReport
import com.myproxy.android.xray.formatBytes
import com.myproxy.android.xray.formatDuration


@Composable
fun SettingsScreen(
    onBack: () -> Unit,
    onRebind: () -> Unit = {},
    onOpenUrl: (String) -> Unit = {},
    viewModel: SettingsViewModel,
) {
    val autoConnect by viewModel.autoConnect.collectAsStateWithLifecycle()
    val autoUpdateCheck by viewModel.autoUpdateCheck.collectAsStateWithLifecycle()
    val killSwitch by viewModel.killSwitch.collectAsStateWithLifecycle()
    val selfHeal by viewModel.selfHeal.collectAsStateWithLifecycle()
    val coreHealth by viewModel.coreHealth.collectAsStateWithLifecycle()
    val updateInfo by viewModel.updateInfo.collectAsStateWithLifecycle()
    val deviceInfo by viewModel.deviceInfo.collectAsStateWithLifecycle()
    val logEntries by viewModel.logEntries.collectAsStateWithLifecycle()
    var showDeviceInfo by remember { mutableStateOf(false) }
    var showLogs by remember { mutableStateOf(false) }
    var showRebindConfirmation by remember { mutableStateOf(false) }

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(
            modifier = Modifier
                .widthIn(max = DesignTokens.ContentMaxWidth)
                .fillMaxWidth()
                .verticalScroll(rememberScrollState()),
        ) {
            // 与主页同一个上边距、同一个页头高度：「设置」与「MyProxy」落在同一条线上。
            Spacer(Modifier.height(DesignTokens.SpaceMd))
            PageHeader(
                title = stringResource(R.string.settings_title),
                // SpaceMd 外边距 + LinkButton 的 SpaceSm 内边距 = SpaceLg，
                // 「返回」两个字与下面每一行的标题落在同一条左边界上（
                modifier = Modifier.padding(horizontal = DesignTokens.SpaceMd),
                leading = { LinkButton(stringResource(R.string.settings_back), onClick = onBack) },
            )

            SettingsSection(stringResource(R.string.settings_section_startup)) {
                SwitchRow(
                    title = stringResource(R.string.settings_auto_connect),
                    checked = autoConnect,
                    onCheckedChange = viewModel::setAutoConnect,
                )
            }

            SettingsSection(stringResource(R.string.settings_section_protection)) {
                SwitchRow(
                    title = stringResource(R.string.settings_kill_switch),
                    subtitle = stringResource(R.string.settings_kill_switch_hint),
                    checked = killSwitch,
                    onCheckedChange = viewModel::setKillSwitch,
                )
                SwitchRow(
                    title = stringResource(R.string.settings_self_heal),
                    subtitle = stringResource(R.string.settings_self_heal_hint),
                    checked = selfHeal,
                    onCheckedChange = viewModel::setSelfHeal,
                )
            }

            // 连接模式：这里只解释两种模式的差别，开关本身留在主页。把开关也搬
            // 过来会让「换个模式」变成两次跳转；而只给两个名字不给说明，用户就
            // 只能靠猜。所以主页给开关，这里给说明——排在可操作的分段之后。
            SettingsSection(stringResource(R.string.main_proxy_mode)) {
                SettingRow(
                    title = stringResource(R.string.main_mode_rule),
                    subtitle = stringResource(R.string.settings_mode_rule_description),
                )
                SettingRow(
                    title = stringResource(R.string.main_mode_global),
                    subtitle = stringResource(R.string.settings_mode_global_description),
                )
            }

            SettingsSection(stringResource(R.string.settings_section_updates)) {
                SwitchRow(
                    title = stringResource(R.string.settings_auto_check_update),
                    checked = autoUpdateCheck,
                    onCheckedChange = viewModel::setAutoUpdateCheck,
                )
                updateInfo?.let { info ->
                    SettingRow(
                        title = stringResource(R.string.settings_update_available, info.version),
                        onClick = { onOpenUrl(info.downloadUrl) },
                        trailing = { Chevron() },
                    )
                }
            }

            SettingsSection(stringResource(R.string.settings_section_device)) {
                SettingRow(
                    title = stringResource(R.string.settings_device_info),
                    onClick = { showDeviceInfo = true },
                    trailing = { Chevron() },
                )
                SettingRow(
                    title = stringResource(R.string.settings_rebind),
                    onClick = { showRebindConfirmation = true },
                    trailing = { Chevron() },
                )
            }

            SettingsSection(stringResource(R.string.settings_section_diagnostics)) {
                SettingRow(
                    title = stringResource(R.string.settings_view_logs),
                    onClick = { showLogs = true },
                    trailing = { Chevron() },
                )
                // Core diagnostics are available in development builds.
                if (BuildConfig.DEBUG) {
                    SettingRow(
                        title = stringResource(R.string.settings_core_health),
                        subtitle = coreHealthSummary(coreHealth),
                    )
                    SettingRow(
                        title = stringResource(R.string.settings_benchmark_title),
                        subtitle = stringResource(R.string.settings_benchmark_subtitle),
                        onClick = { viewModel.runMemoryBenchmark() },
                    )
                }
            }

            SettingsSection(stringResource(R.string.settings_section_about)) {
                SettingRow(
                    title = stringResource(R.string.settings_version),
                    trailing = { TrailingValue(viewModel.versionName) },
                )
            }

            // 滚到底时的收尾留白：最后一行不要贴着导航栏收住。
            Spacer(Modifier.height(DesignTokens.SpaceXl))
        }
    }

    if (showDeviceInfo) {
        SettingsDialog(
            title = stringResource(R.string.settings_device_info),
            onDismiss = { showDeviceInfo = false },
            confirm = {
                DialogButton(stringResource(R.string.settings_close)) { showDeviceInfo = false }
            },
        ) {
            val info = deviceInfo
            Text(
                text = if (info == null) {
                    stringResource(R.string.settings_device_info_unavailable)
                } else {
                    stringResource(
                        R.string.settings_device_info_value,
                        info.deviceName,
                        info.platform,
                        info.clientVersion,
                    )
                },
                style = MyProxyType.Secondary,
            )
        }
    }

    if (showLogs) {
        SettingsDialog(
            title = stringResource(R.string.settings_view_logs),
            onDismiss = { showLogs = false },
            dismiss = { DialogButton(stringResource(R.string.settings_logs_clear), viewModel::clearLogs) },
            confirm = { DialogButton(stringResource(R.string.settings_close)) { showLogs = false } },
        ) {
            Column(
                modifier = Modifier
                    .heightIn(max = 360.dp)
                    .verticalScroll(rememberScrollState()),
            ) {
                Text(
                    text = if (logEntries.isEmpty()) {
                        stringResource(R.string.settings_logs_empty)
                    } else {
                        logEntries.joinToString("\n")
                    },
                    // 日志是「高级日志」（
                    style = if (logEntries.isEmpty()) MyProxyType.Secondary else MyProxyType.Caption,
                )
            }
        }
    }

    if (showRebindConfirmation) {
        SettingsDialog(
            title = stringResource(R.string.settings_rebind_confirm_title),
            onDismiss = { showRebindConfirmation = false },
            dismiss = {
                DialogButton(stringResource(R.string.settings_cancel)) { showRebindConfirmation = false }
            },
            confirm = {
                DialogButton(stringResource(R.string.settings_rebind_confirm)) {
                    showRebindConfirmation = false
                    onRebind()
                }
            },
        ) {
            Text(stringResource(R.string.settings_rebind_confirm_message), style = MyProxyType.Secondary)
        }
    }
}

@Composable
private fun SettingsSection(
    title: String,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Spacer(Modifier.height(DesignTokens.SpaceLg))
        Text(
            text = title,
            // 小号灰色标签：段标题要读起来像分组名，而不是又一行设置项（
            style = MyProxyType.Caption,
            fontWeight = FontWeight.Medium,
            color = TextSecondary,
            modifier = Modifier.padding(horizontal = DesignTokens.SpaceLg),
        )
        Spacer(Modifier.height(DesignTokens.SpaceSm))
        Hairline(Modifier.padding(horizontal = DesignTokens.SpaceLg))
        content()
    }
}

/**
 * 开关行。整行是一个 toggleable，开关本身只负责画：先前只有右侧那枚开关能点，
 * 而且它与左边的标题是两个互不相干的无障碍节点，TalkBack 读到开关时只会念
 * 「开关，已关闭」，不知道是哪个设置。
 */
@Composable
private fun SwitchRow(
    title: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    subtitle: String? = null,
) {
    SettingsItem(
        modifier = Modifier.toggleable(
            value = checked,
            role = Role.Switch,
            onValueChange = onCheckedChange,
        ),
    ) {
        RowText(title, subtitle)
        Switch(
            checked = checked,
            onCheckedChange = null,
            // 全部取自本套调色板：Material 默认的关闭态是淡紫灰轨道 + 深灰描边。
            colors = SwitchDefaults.colors(
                checkedThumbColor = Surface,
                checkedTrackColor = Accent,
                checkedBorderColor = Accent,
                uncheckedThumbColor = TextDisabled,
                uncheckedTrackColor = Surface,
                uncheckedBorderColor = BorderStrong,
            ),
        )
    }
}

@Composable
private fun SettingRow(
    title: String,
    onClick: (() -> Unit)? = null,
    subtitle: String? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    SettingsItem(
        // 只读行（版本、模式说明）也合成一个无障碍节点：「版本 0.1.0」是一句话，
        // 不该被 TalkBack 拆成两次焦点。
        modifier = onClick?.let { Modifier.clickable(role = Role.Button, onClick = it) }
            ?: Modifier.semantics(mergeDescendants = true) {},
    ) {
        RowText(title, subtitle)
        trailing?.invoke()
    }
}

@Composable
private fun SettingsItem(
    modifier: Modifier,
    content: @Composable RowScope.() -> Unit,
) {
    Row(
        // 可点击区域横贯整行（ripple 从屏幕边缘到边缘），文字与控件则收在
        // SpaceLg 的左右边界之内，与段标题、分隔线、主页主按钮共用同一对边界。
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = DesignTokens.SettingsRowHeight)
            .padding(horizontal = DesignTokens.SpaceLg, vertical = DesignTokens.SpaceSm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(DesignTokens.SpaceMd),
        content = content,
    )
}

@Composable
private fun RowScope.RowText(title: String, subtitle: String?) {
    Column(modifier = Modifier.weight(1f)) {
        Text(text = title, style = MyProxyType.Primary, color = TextPrimary)
        if (subtitle != null) {
            Spacer(Modifier.height(DesignTokens.SpaceXxs))
            Text(text = subtitle, style = MyProxyType.Secondary, color = TextSecondary)
        }
    }
}

/** 行尾的「›」。纯装饰：行本身已经是 Role.Button，TalkBack 不必再念一个尖括号。 */
@Composable
private fun Chevron() {
    Text(
        text = stringResource(R.string.settings_chevron),
        style = MyProxyType.Primary,
        color = TextSecondary,
        modifier = Modifier.clearAndSetSemantics {},
    )
}


@Composable
private fun TrailingValue(value: String) {
    Text(text = value, style = MyProxyType.Secondary, color = TextSecondary)
}


@Composable
private fun SettingsDialog(
    title: String,
    onDismiss: () -> Unit,
    confirm: @Composable () -> Unit,
    dismiss: (@Composable () -> Unit)? = null,
    text: @Composable () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = text,
        confirmButton = confirm,
        dismissButton = dismiss,
        containerColor = Surface,
        titleContentColor = TextPrimary,
        textContentColor = TextSecondary,
        tonalElevation = 0.dp,
    )
}

@Composable
private fun DialogButton(text: String, onClick: () -> Unit) {
    TextButton(onClick = onClick, shape = RoundedCornerShape(DesignTokens.RadiusSmall)) {
        Text(text)
    }
}

/**
 * The audit's numbers, or why there are none. Whole-process figures: the core
 * is a library inside this process, so there is nothing finer to report.
 */
@Composable
private fun coreHealthSummary(report: CoreHealthReport?): String {
    if (report == null) return stringResource(R.string.settings_core_health_idle)
    val headline = stringResource(
        R.string.settings_core_health_value,
        formatBytes(report.totalPssKb * 1024L),
        formatBytes(report.pssGrowthKb.coerceAtLeast(0L) * 1024L),
        report.cpuPercent,
        formatDuration(report.coreUptimeMs),
    )
    return when {
        report.healingAbandoned ->
            headline + "\n" + stringResource(R.string.settings_core_health_abandoned)
        report.healCount > 0 ->
            headline + "\n" + stringResource(R.string.settings_core_health_heals, report.healCount)
        else -> headline
    }
}
