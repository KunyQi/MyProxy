package com.myproxy.android.logging

import android.util.Log
import com.myproxy.android.BuildConfig
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

interface AppLogger {
    fun d(tag: String, message: String)
    fun i(tag: String, message: String)
    fun w(tag: String, message: String)
    fun e(tag: String, message: String, throwable: Throwable? = null)
}

/**
 * Small in-memory diagnostic buffer for the Settings diagnostics screen.
 *
 * The buffer intentionally stores only the already-sanitized log message and
 * exception type. It is bounded so a long-running foreground service cannot
 * grow the app process without limit, and it is not persisted across app
 * restarts (logs are diagnostics, not credentials or durable app state).
 *
 * **写入路径上不分配 List。** 先前它是
 * `_entries.update { (it + line).takeLast(MAX_ENTRIES) }`，每写一行要构造两个
 * 新列表（101 个元素的拼接结果，再截成 100 个）。核心每条状态消息都会走
 * `logger.i`，隧道开着时这是**常开进程里最热的分配点之一**，而其中绝大多数
 * 时间根本没人在看诊断页。
 *
 * 现在写入只是往定长环里放一个引用；只有在真的有人订阅 [entries] 时才物化成
 * 一份不可变快照。诊断页关着 = 零列表分配。
 */
object AppLogBuffer {
    private const val MAX_ENTRIES = 100

    private val ring = ArrayDeque<String>(MAX_ENTRIES)
    private val _entries = MutableStateFlow<List<String>>(emptyList())
    val entries: StateFlow<List<String>> = _entries.asStateFlow()

    fun append(level: String, tag: String, message: String, throwable: Throwable? = null) {
        val suffix = throwable?.let { " (${it.javaClass.simpleName})" }.orEmpty()
        val line = "$level/$tag: $message$suffix"
        val published = synchronized(ring) {
            if (ring.size >= MAX_ENTRIES) ring.removeFirst()
            ring.addLast(line)
            // 没有订阅者就不物化。快照在 publish() 里补齐，订阅一开始就会调它。
            if (_entries.subscriptionCount.value > 0) ring.toList() else null
        }
        if (published != null) _entries.value = published
    }

    /**
     * 把环里现有的内容推到 [entries]。
     *
     * 诊断页打开时调用：在那之前的写入是不物化的，[entries] 停在上一次快照上。
     */
    fun publish() {
        _entries.value = synchronized(ring) { ring.toList() }
    }

    fun clear() {
        synchronized(ring) { ring.clear() }
        _entries.value = emptyList()
    }
}

/**
 * Android Log based implementation.
 *
 * Logging call sites must never pass tokens, UUIDs, pairing codes, or complete
 * server profiles to these methods.
 */
class AndroidAppLogger : AppLogger {
    /**
     * Release 下整条丢弃。
     *
     * `proguard-rules.pro` 用 `assumenosideeffects` 把 `android.util.Log.d`
     * 从 release 包里剥掉，注释写的是「完全剥离」——但它只管得到 `Log.d`
     * 这一次调用，管不到旁边那次 [AppLogBuffer.append]，于是 debug 级日志在
     * release 里照样进环、照样拼字符串。这里补上另一半，顺便让 R8 能把
     * 调用点的字符串拼接一起判成死代码。
     */
    override fun d(tag: String, message: String) {
        if (!BuildConfig.DEBUG) return
        AppLogBuffer.append("D", tag, message)
        Log.d(tag, message)
    }

    override fun i(tag: String, message: String) {
        AppLogBuffer.append("I", tag, message)
        Log.i(tag, message)
    }

    override fun w(tag: String, message: String) {
        AppLogBuffer.append("W", tag, message)
        Log.w(tag, message)
    }

    override fun e(tag: String, message: String, throwable: Throwable?) {
        AppLogBuffer.append("E", tag, message, throwable)
        if (throwable == null) Log.e(tag, message) else Log.e(tag, message, throwable)
    }
}
