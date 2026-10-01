package com.myproxy.android.vpn

import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.atomic.AtomicBoolean

enum class VpnState {
    IDLE,
    STARTING,
    RUNNING,
    USER_STOPPING,
    STOPPING,
    ERROR,
}

object VpnStateHolder {
    private val _state = MutableStateFlow(VpnState.IDLE)
    val state: StateFlow<VpnState> = _state.asStateFlow()
    private val userStopRequested = AtomicBoolean(false)

    private val _blockingHold = MutableStateFlow(false)

    /**
     * True while the service is holding a TUN interface open with no core
     * behind it, so that traffic is dropped rather than leaking after an
     * unexpected failure. [state] is ERROR throughout such a hold, and this
     * flag is what tells that apart from an ordinary failure whose tunnel is
     * already gone.
     */
    val blockingHold: StateFlow<Boolean> = _blockingHold.asStateFlow()

    /**
     * 默认网络变化。服务侧的 `NetworkCallback` 发，控制器侧收。
     *
     * 存在的理由是「事件驱动优先」：切网之后控制面的答案很可能已经变了
     * （尤其是从蜂窝回到 Wi-Fi 这种离开受限网络的场景），这时该立刻问一次，
     * 而不是等退避里那个可能长达十分钟的定时器。
     *
     * replay = 0、缓冲 1、溢出丢旧：这是一个**提示**不是一份待办清单。
     * 一次漫游会连着产生好几个回调，攒起来补发它们没有任何意义——
     * 需要的只是「有变化，去问一次」。
     */
    private val _networkChanges = MutableSharedFlow<Unit>(
        replay = 0,
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val networkChanges: SharedFlow<Unit> = _networkChanges.asSharedFlow()

    fun markNetworkChanged() {
        _networkChanges.tryEmit(Unit)
    }

    fun setState(newState: VpnState) {
        _state.value = newState
    }

    fun setBlockingHold(held: Boolean) {
        _blockingHold.value = held
    }

    fun markUserStopRequested() {
        userStopRequested.set(true)
    }

    fun consumeUserStopRequested(): Boolean = userStopRequested.getAndSet(false)
}
