package com.myproxy.android.presentation

import android.app.Application
import android.os.Build
import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.myproxy.android.MyProxyApplication
import com.myproxy.android.R
import com.myproxy.android.data.api.ApiConfig
import com.myproxy.android.data.api.ErrorCode
import com.myproxy.android.data.repository.BindingService
import com.myproxy.android.data.repository.DefaultPairingRepository
import com.myproxy.android.domain.pairing.BindResult
import com.myproxy.android.util.PairingCodeNormalizer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class BindingUiState(
    val status: BindingStatus = BindingStatus.IDLE,
    /** 配对码输入框的就地错误。 */
    @StringRes val codeErrorRes: Int? = null,
    /** 不归属输入框的失败：限流、服务端不可用、未知。 */
    @StringRes val formErrorRes: Int? = null,
) {
    enum class BindingStatus {
        IDLE,
        BINDING,
        SUCCESS,
        ERROR,
    }
}

/** 一次失败：说什么，以及该不该把配对码框标红。 */
private data class BindingError(val blamesCode: Boolean, @StringRes val messageRes: Int)

/** 用户只输入配对码；部署管理员在根 deployment.json 配置服务器。 */
class BindingViewModel(
    private val bindingService: BindingService,
) : ViewModel() {

    constructor(application: Application) : this(
        BindingService(
            DefaultPairingRepository(
                apiClient = (application as MyProxyApplication).apiClient,
                secureStorage = application.secureStorage,
                configCacheStore = application.configCacheStore,
            )
        )
    )

    private val _pairingCode = MutableStateFlow("")
    val pairingCode: StateFlow<String> = _pairingCode.asStateFlow()

    private val _uiState = MutableStateFlow(BindingUiState())
    val uiState: StateFlow<BindingUiState> = _uiState.asStateFlow()

    private val _navigationEvent = MutableStateFlow(false)
    val navigationEvent: StateFlow<Boolean> = _navigationEvent.asStateFlow()

    fun onPairingCodeChange(value: String) {
        _pairingCode.value = PairingCodeNormalizer.formatForInput(value)
        clearError()
    }

    fun bind() {
        if (_uiState.value.status == BindingUiState.BindingStatus.BINDING) return

        val normalized = PairingCodeNormalizer.normalize(_pairingCode.value)
        if (normalized == null) {
            fail(BindingError(blamesCode = true, R.string.binding_error_invalid_format))
            return
        }

        _uiState.value = BindingUiState(status = BindingUiState.BindingStatus.BINDING)
        viewModelScope.launch { complete(normalized) }
    }

    fun consumeNavigationEvent() {
        _navigationEvent.value = false
    }

    private suspend fun complete(normalizedCode: String) {
        val result = bindingService.bind(
            code = normalizedCode,
            deviceName = Build.MODEL ?: "Android",
            platform = "android",
            clientVersion = ApiConfig.VERSION,
        )
        when (result) {
            is BindResult.Success -> {
                _uiState.value = BindingUiState(status = BindingUiState.BindingStatus.SUCCESS)
                _navigationEvent.value = true
            }

            is BindResult.Failure -> fail(friendlyError(result.code))
        }
    }

    private fun fail(error: BindingError) {
        _uiState.value = BindingUiState(
            status = BindingUiState.BindingStatus.ERROR,
            codeErrorRes = error.messageRes.takeIf { error.blamesCode },
            formErrorRes = error.messageRes.takeIf { !error.blamesCode },
        )
    }

    private fun clearError() {
        if (_uiState.value.status == BindingUiState.BindingStatus.ERROR) {
            _uiState.value = BindingUiState()
        }
    }

    /**
     * 错误码 → 说什么，以及该不该把配对码框标红。`when` 穷举全部 [ErrorCode]，
     * 新增错误码会在这里编译失败，而不是悄悄落进表单级。
     *
     * 归属由**这里**决定而不是由界面反推：界面按 string 资源 id 猜的话，每加一个
     * 错误码都要去改一张白名单，漏掉就静默走错槽位。
     */
    private fun friendlyError(code: ErrorCode): BindingError = when (code) {
        ErrorCode.PairingInvalid ->
            BindingError(true, R.string.binding_error_invalid)
        ErrorCode.PairingExpired ->
            BindingError(true, R.string.binding_error_expired)
        ErrorCode.TokenInvalid,
        ErrorCode.DeviceNotFound,
        -> BindingError(true, R.string.binding_error_device)
        ErrorCode.RateLimited ->
            BindingError(false, R.string.binding_error_rate_limited)
        ErrorCode.ServerError ->
            BindingError(false, R.string.binding_error_server)
        ErrorCode.ApiUnreachable ->
            BindingError(false, R.string.binding_error_unreachable)
        ErrorCode.ServerNotConfigured ->
            BindingError(false, R.string.binding_error_not_configured)
        ErrorCode.ServerUntrusted ->
            BindingError(false, R.string.binding_error_untrusted)
        ErrorCode.BadRequest,
        ErrorCode.NotFound,
        ErrorCode.Conflict,
        ErrorCode.Unknown,
        -> BindingError(false, R.string.binding_error_unknown)
    }

    companion object {
        fun factory(application: Application): ViewModelProvider.Factory =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>): T {
                    return BindingViewModel(application) as T
                }
            }
    }
}
