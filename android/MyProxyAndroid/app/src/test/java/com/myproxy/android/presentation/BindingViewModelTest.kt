package com.myproxy.android.presentation

import com.myproxy.android.R
import com.myproxy.android.data.api.ClaimApi
import com.myproxy.android.data.api.ClaimRequest
import com.myproxy.android.data.api.ClaimResponse
import com.myproxy.android.data.repository.BindingService
import com.myproxy.android.data.repository.DefaultPairingRepository
import com.myproxy.android.domain.pairing.BindResult
import com.myproxy.android.domain.pairing.PairingRepository
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class BindingViewModelTest {

    @Test
    fun `pairing input is formatted while it is still partial`() {
        val viewModel = BindingViewModel(BindingService(NoOpPairingRepository))

        viewModel.onPairingCodeChange("ab")
        assertEquals("AB", viewModel.pairingCode.value)

        viewModel.onPairingCodeChange("ABCD1")
        // state 存裸码，连字符是显示层的事（PairingCodeVisualTransformation）。
        assertEquals("ABCD1", viewModel.pairingCode.value)
    }

    @Test
    fun `pairing input normalizes pasted separators`() {
        val viewModel = BindingViewModel(BindingService(NoOpPairingRepository))

        viewModel.onPairingCodeChange(" ab-cd 1234 ")
        assertEquals("ABCD1234", viewModel.pairingCode.value)
    }

    @Test
    fun `an invalid code never reaches the binding service`() {
        val repository = RecordingPairingRepository()
        val viewModel = BindingViewModel(BindingService(repository))
        viewModel.onPairingCodeChange("abc")

        viewModel.bind()

        val state = viewModel.uiState.value
        assertEquals(BindingUiState.BindingStatus.ERROR, state.status)
        assertEquals(R.string.binding_error_invalid_format, state.codeErrorRes)
        assertNull("格式错误归属输入框，不该占用表单级槽位", state.formErrorRes)
        assertEquals(0, repository.claimCalls)
    }

    @Test
    fun `editing the code clears the previous error`() {
        val viewModel = BindingViewModel(BindingService(RecordingPairingRepository()))
        viewModel.onPairingCodeChange("abc")
        viewModel.bind()
        assertEquals(BindingUiState.BindingStatus.ERROR, viewModel.uiState.value.status)

        viewModel.onPairingCodeChange("abcd1234")

        val state = viewModel.uiState.value
        assertEquals(BindingUiState.BindingStatus.IDLE, state.status)
        assertNull(state.codeErrorRes)
        assertNull(state.formErrorRes)
    }

    // 合法配对码之后 bind() 会进 viewModelScope.launch，而这些单测跑在裸 JVM 上、
    // 没有 Main dispatcher，测到那一步只会抛 IllegalStateException。覆盖它要引入
    // kotlinx-coroutines-test 并给整个测试集加 Dispatchers.setMain——那是独立的
    // 一件事，不该顺手夹带。

    private object NoOpPairingRepository : PairingRepository {
        override suspend fun claim(
            code: String,
            deviceName: String,
            platform: String,
            clientVersion: String,
        ): BindResult = BindResult.Success(deviceId = "d", configVersion = 1)
    }

    private class RecordingPairingRepository : PairingRepository {
        var claimCalls = 0
            private set

        override suspend fun claim(
            code: String,
            deviceName: String,
            platform: String,
            clientVersion: String,
        ): BindResult {
            claimCalls++
            return BindResult.Success(deviceId = "d", configVersion = 1)
        }
    }
}
