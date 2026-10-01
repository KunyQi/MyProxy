package com.myproxy.android.ui.binding

import android.app.Application
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.LocalTextSelectionColors
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.error
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myproxy.android.R
import com.myproxy.android.presentation.BindingUiState
import com.myproxy.android.presentation.BindingViewModel
import com.myproxy.android.ui.components.InlineError
import com.myproxy.android.ui.components.PageHeader
import com.myproxy.android.ui.components.PrimaryButton
import com.myproxy.android.ui.components.UtilityPage
import com.myproxy.android.ui.theme.Accent
import com.myproxy.android.ui.theme.Background
import com.myproxy.android.ui.theme.Border
import com.myproxy.android.ui.theme.BorderStrong
import com.myproxy.android.ui.theme.DesignTokens
import com.myproxy.android.ui.theme.Error
import com.myproxy.android.ui.theme.MyProxyType
import com.myproxy.android.ui.theme.Surface
import com.myproxy.android.ui.theme.TextPrimary
import com.myproxy.android.ui.theme.TextSecondary

/**
 * 绑定页。只有一个输入：配对码。
 *
 * 服务器入口由 deployment.json 在构建时确定，HTTPS 使用系统信任库，
 * 校验证书链、有效期和主机名；用户只输入配对码。
 */
@Composable
fun BindScreen(
    onNavigateToMain: () -> Unit,
    viewModel: BindingViewModel = viewModel(
        factory = BindingViewModel.factory(
            LocalContext.current.applicationContext as Application
        )
    ),
) {
    val pairingCode by viewModel.pairingCode.collectAsState()
    val uiState by viewModel.uiState.collectAsState()
    val navigationEvent by viewModel.navigationEvent.collectAsState()

    LaunchedEffect(navigationEvent) {
        if (navigationEvent) {
            viewModel.consumeNavigationEvent()
            onNavigateToMain()
        }
    }

    val binding = uiState.status == BindingUiState.BindingStatus.BINDING
    // 归属由 BindingViewModel 决定。界面按资源 id 反推的话，每加一个错误码
    // 都要去改一张白名单，漏掉就静默走错槽位。
    val failed = uiState.status == BindingUiState.BindingStatus.ERROR
    val codeError = uiState.codeErrorRes.takeIf { failed }
    val formError = uiState.formErrorRes.takeIf { failed }

    // 页头与主页共用同一个 PageHeader，绑定完成跳到主页时「MyProxy」不跳位。
    UtilityPage(header = { PageHeader(stringResource(R.string.app_name)) }) {
        Text(
            text = stringResource(R.string.binding_subtitle),
            style = MyProxyType.Primary,
            fontWeight = FontWeight.Medium,
            color = TextPrimary,
        )
        Spacer(Modifier.height(DesignTokens.SpaceSm))
        Text(
            text = stringResource(R.string.binding_hint),
            style = MyProxyType.Secondary,
            color = TextSecondary,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(DesignTokens.SpaceLg))

        PairingCodeField(
            value = pairingCode,
            onValueChange = viewModel::onPairingCodeChange,
            onDone = { if (!binding) viewModel.bind() },
            enabled = !binding,
            errorMessage = codeError?.let { stringResource(it) },
        )
        // 两个槽常驻占位。
        // 说明更长，大字号下会折行，预留两行才不会在出错时推动按钮。
        InlineError(codeError?.let { stringResource(it) }, lines = 1)
        InlineError(formError?.let { stringResource(it) }, lines = 2)
        Spacer(Modifier.height(DesignTokens.SpaceSm))
        PrimaryButton(
            text = stringResource(
                if (binding) R.string.binding_button_binding else R.string.binding_button,
            ),
            onClick = viewModel::bind,
            enabled = !binding,
        )
    }
}


@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PairingCodeField(
    value: String,
    onValueChange: (String) -> Unit,
    onDone: () -> Unit,
    enabled: Boolean,
    errorMessage: String?,
) {
    val interactionSource = remember { MutableInteractionSource() }
    // 连字符只是视觉排版：见 PairingCodeVisualTransformation 的 kdoc。
    val visualTransformation = remember { PairingCodeVisualTransformation() }
    val isError = errorMessage != null
    val colors = OutlinedTextFieldDefaults.colors(
        focusedTextColor = TextPrimary,
        unfocusedTextColor = TextPrimary,
        disabledTextColor = TextSecondary,
        focusedContainerColor = Surface,
        unfocusedContainerColor = Surface,
        disabledContainerColor = Background,
        errorContainerColor = Surface,
        focusedBorderColor = Accent,
        unfocusedBorderColor = BorderStrong,
        disabledBorderColor = Border,
        errorBorderColor = Error,
        cursorColor = Accent,
        errorCursorColor = Error,
    )

    CompositionLocalProvider(LocalTextSelectionColors provides colors.textSelectionColors) {
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            modifier = Modifier
                .fillMaxWidth()
                // OutlinedTextField 出错时挂一条通用的「输入无效」，这里直接给出原因。
                .semantics { if (errorMessage != null) error(errorMessage) },
            enabled = enabled,
            // 略加字距让四位一组更好辨认；这里全是 ASCII，没有中文被拉散的问题。
            // 颜色即 colors 里的文字色（错误态沿用 onSurface = TextPrimary）。
            textStyle = MyProxyType.Title.copy(
                color = if (enabled) TextPrimary else TextSecondary,
                textAlign = TextAlign.Center,
                letterSpacing = 2.sp,
            ),
            cursorBrush = SolidColor(if (isError) Error else Accent),
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.Characters,
                keyboardType = KeyboardType.Ascii,
                imeAction = ImeAction.Done,
            ),
            keyboardActions = KeyboardActions(onDone = { onDone() }),
            visualTransformation = visualTransformation,
            singleLine = true,
            interactionSource = interactionSource,
            decorationBox = { innerTextField ->
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        text = stringResource(R.string.binding_code_label),
                        style = MyProxyType.Caption,
                        color = TextSecondary,
                    )
                    Spacer(Modifier.height(DesignTokens.SpaceSm))
                    // 框的最小高度原本由 OutlinedTextField 加在整个控件上；标签进来之后
                    // 只能加在框这一层，propagateMinConstraints 把它交给 DecorationBox。
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .defaultMinSize(minHeight = OutlinedTextFieldDefaults.MinHeight),
                        propagateMinConstraints = true,
                    ) {
                        OutlinedTextFieldDefaults.DecorationBox(
                            value = value,
                            innerTextField = innerTextField,
                            enabled = enabled,
                            singleLine = true,
                            visualTransformation = visualTransformation,
                            interactionSource = interactionSource,
                            isError = isError,
                            colors = colors,
                            container = {
                                OutlinedTextFieldDefaults.Container(
                                    enabled = enabled,
                                    isError = isError,
                                    interactionSource = interactionSource,
                                    colors = colors,
                                    shape = RoundedCornerShape(DesignTokens.RadiusSmall),
                                )
                            },
                        )
                    }
                }
            },
        )
    }
}
