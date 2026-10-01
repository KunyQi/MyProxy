package com.myproxy.android.ui.components

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.myproxy.android.ui.theme.Accent
import com.myproxy.android.ui.theme.AccentSubtle
import com.myproxy.android.ui.theme.Border
import com.myproxy.android.ui.theme.BorderStrong
import com.myproxy.android.ui.theme.DesignTokens
import com.myproxy.android.ui.theme.Error
import com.myproxy.android.ui.theme.MyProxyType
import com.myproxy.android.ui.theme.Surface
import com.myproxy.android.ui.theme.TextDisabled
import com.myproxy.android.ui.theme.TextPrimary
import com.myproxy.android.ui.theme.TextSecondary


@Composable
fun UtilityPage(
    header: @Composable ColumnScope.() -> Unit,
    footer: @Composable ColumnScope.() -> Unit = {},
    body: @Composable ColumnScope.() -> Unit,
) {
    BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        val viewport = maxHeight
        Column(
            modifier = Modifier
                .widthIn(max = DesignTokens.ContentMaxWidth)
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                // 在 verticalScroll 之后：子级拿到的是「最小 = 一屏、最大 = 无穷」，
                // Column 在最大高度无穷时按最小高度分配权重，于是两个 Spacer 能撑开。
                .heightIn(min = viewport)
                .padding(horizontal = DesignTokens.SpaceLg),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Spacer(Modifier.height(DesignTokens.SpaceMd))
            header()
            Spacer(Modifier.weight(2f))
            body()
            Spacer(Modifier.weight(3f))
            footer()
            Spacer(Modifier.height(DesignTokens.SpaceMd))
        }
    }
}

/**
 * 页头。三页共用同一个高度与同一个上边距，所以「MyProxy」「设置」两个标题落在
 * 同一条水平线上，切页时标题不跳。
 */
@Composable
fun PageHeader(
    title: String,
    modifier: Modifier = Modifier,
    leading: (@Composable () -> Unit)? = null,
) {
    Box(
        // heightIn：标题 28sp 的行高在 2.0 倍字号下会超过 48dp。
        modifier = modifier.fillMaxWidth().heightIn(min = DesignTokens.ControlHeight),
    ) {
        if (leading != null) {
            Box(Modifier.align(Alignment.CenterStart)) { leading() }
        }
        Text(
            text = title,
            style = MyProxyType.Title,
            color = TextPrimary,
            textAlign = TextAlign.Center,
            modifier = Modifier.align(Alignment.Center),
        )
    }
}


@Composable
fun PrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Button(
        onClick = onClick,
        enabled = enabled,
        // heightIn 而不是 height：系统字体放大到 2.0 时，文字行高吃满了固定 48dp 里
        // 的上下内边距，字形被挤到按钮下沿（模拟器实测）。常规字号下仍是 48dp。
        modifier = modifier.fillMaxWidth().heightIn(min = DesignTokens.ControlHeight),
        shape = RoundedCornerShape(DesignTokens.RadiusSmall),
        colors = ButtonDefaults.buttonColors(
            containerColor = Accent,
            contentColor = Surface,
            disabledContainerColor = Border,
            disabledContentColor = TextSecondary,
        ),
        elevation = ButtonDefaults.buttonElevation(
            defaultElevation = 0.dp,
            pressedElevation = 0.dp,
            focusedElevation = 0.dp,
            hoveredElevation = 0.dp,
            disabledElevation = 0.dp,
        ),
    ) {
        Text(text, style = MyProxyType.Primary, fontWeight = FontWeight.Medium)
    }
}

/**
 * 次要按钮：1px 中性描边、无底色。给「检查连接」这类确实是动作、但不能和主按钮
 * 抢视觉中心的操作用。
 */
@Composable
fun SecondaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier,
        shape = RoundedCornerShape(DesignTokens.RadiusSmall),
        border = BorderStroke(DesignTokens.BorderThin, if (enabled) BorderStrong else Border),
        colors = ButtonDefaults.outlinedButtonColors(
            contentColor = TextPrimary,
            disabledContentColor = TextDisabled,
        ),
        contentPadding = PaddingValues(horizontal = DesignTokens.SpaceMd),
    ) {
        Text(text, style = MyProxyType.Secondary)
    }
}

/**
 * 文字链接式的导航动作：主页页脚的「设置」、设置页的「返回」。两处同色同字号，
 * 先前一个是灰色 12sp、一个是强调色 12sp 且字距 1.2sp。
 */
@Composable
fun LinkButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    color: Color = TextSecondary,
    textAlign: TextAlign = TextAlign.Center,
) {
    TextButton(
        onClick = onClick,
        modifier = modifier.heightIn(min = DesignTokens.ControlHeight),
        shape = RoundedCornerShape(DesignTokens.RadiusSmall),
        contentPadding = PaddingValues(horizontal = DesignTokens.SpaceSm),
    ) {
        Text(text, style = MyProxyType.Secondary, color = color, textAlign = textAlign)
    }
}


@Composable
fun <T> SegmentedControl(
    options: List<Pair<T, String>>,
    selected: T,
    enabled: Boolean,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(DesignTokens.RadiusSmall)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(IntrinsicSize.Min)
            .clip(shape)
            .border(DesignTokens.BorderThin, BorderStrong, shape)
            .selectableGroup(),
    ) {
        options.forEachIndexed { index, (value, label) ->
            if (index > 0) {
                Box(
                    Modifier.width(DesignTokens.BorderThin).fillMaxHeight().background(BorderStrong),
                )
            }
            val isSelected = value == selected
            Box(
                modifier = Modifier
                    .weight(1f)
                    // 行内每一格自己撑到 48dp：放在 verticalScroll 里时 Row 拿不到
                    // 有限的最大高度，子项无法从父级推出自己的高度。
                    .defaultMinSize(minHeight = DesignTokens.ControlHeight)
                    .fillMaxHeight()
                    .background(if (isSelected) AccentSubtle else Color.Transparent)
                    .selectable(
                        selected = isSelected,
                        enabled = enabled,
                        role = Role.RadioButton,
                        onClick = { onSelect(value) },
                    ),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = label,
                    style = MyProxyType.Secondary,
                    fontWeight = if (isSelected) FontWeight.Medium else FontWeight.Normal,
                    color = when {
                        isSelected -> Accent
                        enabled -> TextSecondary
                        else -> TextDisabled
                    },
                    modifier = Modifier.padding(horizontal = DesignTokens.SpaceSm),
                )
            }
        }
    }
}


@Composable
fun InlineError(message: String?, modifier: Modifier = Modifier, lines: Int = 1) {
    Box(
        modifier = modifier.fillMaxWidth().heightIn(min = lineHeightDp(MyProxyType.Secondary, lines)),
        contentAlignment = Alignment.Center,
    ) {
        if (message != null) {
            Text(
                text = message,
                style = MyProxyType.Secondary,
                color = Error,
                textAlign = TextAlign.Center,
            )
        }
    }
}

/** [lines] 行 [style] 文字在当前字体缩放下的高度。给「常驻占位」的行算预留高度用。 */
@Composable
fun lineHeightDp(style: TextStyle, lines: Int): Dp =
    with(LocalDensity.current) { style.lineHeight.toDp() } * lines

/** 设置页的分隔线：发丝粗细、中性色。 */
@Composable
fun Hairline(modifier: Modifier = Modifier) {
    Box(
        modifier
            .fillMaxWidth()
            .height(DesignTokens.DividerThickness)
            .background(Border),
    )
}


val CenteredRowArrangement = Arrangement.spacedBy(DesignTokens.SpaceSm, Alignment.CenterHorizontally)
