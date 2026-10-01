package com.myproxy.android.xray

import com.myproxy.android.domain.observability.ServiceCategories
import android.content.Context
import com.myproxy.android.BuildConfig
import com.myproxy.android.logging.AndroidAppLogger
import com.myproxy.android.logging.AppLogger
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import libv2ray.CoreCallbackHandler
import libv2ray.CoreController
import libv2ray.Libv2ray
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.SecureRandom

/**
 * Thin wrapper around libv2ray.
 *
 * All calls into the Go core are moved to [Dispatchers.IO]. Callback messages
 * are forwarded to [AppLogger]; configs, tokens and UUIDs are never logged.
 * The public [state] flow tracks core lifecycle for reliable UI/error handling.
 */
class XrayManager(
    private val context: Context,
    private val logger: AppLogger = AndroidAppLogger(),
) : TrafficStatsSource {
    private val lock = Any()

    /** stopLoop 的有界等待上限；超时按 stop 失败处理，保留资源供重试。 */
    private val CORE_STOP_TIMEOUT_MS = 5_000L

    private val _state = MutableStateFlow(XrayState.IDLE)
    val state: StateFlow<XrayState> = _state.asStateFlow()

    @Volatile
    private var coreController: CoreController? = null

    @Volatile
    private var initialized = false

    @Volatile
    private var xuDpKey: String? = null

    @Volatile
    private var expectedStop = false

    private val callback = object : CoreCallbackHandler {
        override fun onEmitStatus(p0: Long, p1: String): Long {
            // Log status for debugging (e.g., inbound binding issues).
            // Filter common sensitive keywords just in case, though production configs
            // should not be echoing full secrets in status messages.
            //
            // 模式在 companion 里编译一次。先前这里写的是 `Regex("...")`，于是核心
            // **每条**状态消息都要重新编译一遍它——隧道开着时这是整个进程最热的
            // 分配点之一，而它的结果绝大多数时候只是原样返回。
            val sanitized = p1.replace(SENSITIVE_VALUE, "$1:***")
            logger.i(TAG, "core status: $sanitized")

            // `contains(ignoreCase = true)` 而不是先 `lowercase()`：后者为每条状态
            // 消息多分配一份整串副本，而这三个词绝大多数消息一个都不含。
            if (!expectedStop && FATAL_STATUS_MARKERS.any { p1.contains(it, ignoreCase = true) }) {
                logger.e(TAG, "core reported a fatal status: $sanitized")
                _state.value = XrayState.ERROR
            }
            return 0L
        }

        override fun shutdown(): Long {
            if (expectedStop) {
                logger.i(TAG, "core shutdown")
            } else {
                logger.e(TAG, "core unexpected shutdown")
                _state.value = XrayState.ERROR
            }
            return 0L
        }

        override fun startup(): Long {
            logger.i(TAG, "core startup")
            return 0L
        }
    }

    fun init(context: Context = this.context) {
        synchronized(lock) {
            if (initialized) return
            val xrayDir = File(context.filesDir, "xray").apply { mkdirs() }
            copyAssetIfNeeded(context, "geoip.dat", File(xrayDir, "geoip.dat"))
            copyAssetIfNeeded(context, "geosite.dat", File(xrayDir, "geosite.dat"))
            val key = xuDpKey ?: randomBase64(32).also { xuDpKey = it }

            // Instruct the Go runtime to be more aggressive with memory recovery.
            // Baseline and peaks will be lower on Android.
            runCatching { android.system.Os.setenv("GOGC", "20", true) }

            Libv2ray.initCoreEnv(xrayDir.absolutePath, key)
            initialized = true
        }
    }

    suspend fun start(configJson: String, tunFd: Int) {
        withContext(Dispatchers.IO) {
            init()
            synchronized(lock) {
                _state.value = XrayState.STARTING
                expectedStop = true
                try {
                    stopCoreLocked()
                    val controller = CoreController(callback)
                    // Retain the handle before entering native code. If startLoop
                    // partially starts and then throws, cleanup can still retry
                    // stopLoop instead of losing the only controller reference.
                    coreController = controller
                    controller.startLoop(configJson, tunFd)
                    _state.value = XrayState.RUNNING
                } catch (t: Throwable) {
                    discardHalfStartedCoreLocked()
                    _state.value = XrayState.ERROR
                    throw t
                } finally {
                    expectedStop = false
                }
            }
        }
    }

    suspend fun stop() {
        withContext(Dispatchers.IO) {
            synchronized(lock) {
                _state.value = XrayState.STOPPING
                expectedStop = true
                try {
                    stopCoreLocked()
                    _state.value = XrayState.STOPPED
                } catch (t: Throwable) {
                    _state.value = XrayState.ERROR
                    throw t
                } finally {
                    expectedStop = false
                }
            }
        }
    }

    suspend fun restart(configJson: String, tunFd: Int) {
        withContext(Dispatchers.IO) {
            init()
            synchronized(lock) {
                _state.value = XrayState.STARTING
                expectedStop = true
                try {
                    stopCoreLocked()
                    val controller = CoreController(callback)
                    coreController = controller
                    controller.startLoop(configJson, tunFd)
                    _state.value = XrayState.RUNNING
                } catch (t: Throwable) {
                    discardHalfStartedCoreLocked()
                    _state.value = XrayState.ERROR
                    throw t
                } finally {
                    expectedStop = false
                }
            }
        }
    }

    fun isRunning(): Boolean = coreController?.isRunning ?: false

    /**
     * 出站计数的台账。核心接口一次读清全部出站计数器，两个读取方各自「读完即清」，
     * 必须经台账分账——原因见 [OutboundTrafficLedger]。
     */
    private val trafficLedger = OutboundTrafficLedger()

    /**
     * Reads and clears the proxy outbound's byte counters.
     *
     * Deliberately does not take [lock]: the lock is held across `startLoop`
     * and `stopLoop`, both of which can block for seconds, and a sampler that
     * queued behind them would stall the UI. Taking a snapshot of the
     * controller reference is enough -- a controller that has since been
     * stopped has no counters and answers nothing, which is the right answer.
     *
     * Returns zeroes rather than propagating: statistics are a readout, and
     * losing one sample must never disturb a live tunnel.
     */
    override suspend fun readAndResetProxyTraffic(): TrafficDelta =
        withContext(Dispatchers.IO) {
            runCatching {
                pullCounters()
                TrafficDelta(
                    uplinkBytes = trafficLedger.take(XrayConfigGenerator.PROXY_OUTBOUND_TAG, STAT_UPLINK),
                    downlinkBytes = trafficLedger.take(XrayConfigGenerator.PROXY_OUTBOUND_TAG, STAT_DOWNLINK),
                )
            }.getOrElse {
                logger.w(TAG, "traffic counters unavailable (${it.javaClass.simpleName})")
                TrafficDelta.NONE
            }
        }

    /**
     * Per-category counters. Tags the running config does not have simply never
     * show up in the ledger and answer 0, so this is safe to call whether or not
     * category outbounds exist -- there is no need to ask the core what shape
     * its config is.
     */
    override suspend fun readAndResetCategoryTraffic(): Map<String, Long> =
        withContext(Dispatchers.IO) {
            runCatching {
                pullCounters()
                val totals = LinkedHashMap<String, Long>()
                for (routed in ServiceCategories.ROUTED) {
                    val total = trafficLedger.take(routed.tag, STAT_UPLINK) +
                        trafficLedger.take(routed.tag, STAT_DOWNLINK)
                    if (total > 0L) totals[routed.tag] = total
                }
                totals as Map<String, Long>
            }.getOrElse {
                logger.w(TAG, "category counters unavailable (${it.javaClass.simpleName})")
                emptyMap()
            }
        }

    /** 把核心当前的出站计数（读取即清零）并入台账。没有核心时什么都不做。 */
    private fun pullCounters() {
        val controller = coreController ?: return
        trafficLedger.absorb(controller.queryAllOutboundTrafficStats())
    }

    private fun stopCoreLocked() {
        val controller = coreController ?: return
        var stopped = false
        try {
            if (controller.isRunning) {
                // stopLoop 是进 Go 侧的阻塞 JNI 调用，Go 协程楔死它就不返回。
                // 在本线程裸调会把 XrayManager 的锁一起带走，服务层 cleanup 永远
                // 完不成——实测复现：快速 stop→start 后无限「连接失败」，重试无效。
                // 放到守护线程上有界 join：超时按 stop 失败抛出，锁随之释放，
                // 走「失败保留资源供再次 cleanup」的既有路径。
                val stopper = Thread({ controller.stopLoop() }, "xray-stop")
                stopper.isDaemon = true
                stopper.start()
                stopper.join(CORE_STOP_TIMEOUT_MS)
                if (stopper.isAlive) {
                    throw IllegalStateException("core stop timed out after ${CORE_STOP_TIMEOUT_MS}ms")
                }
            }
            stopped = true
        } catch (t: Throwable) {
            logger.w(TAG, "failed to stop core (${t.javaClass.simpleName})")
            throw t
        }
        if (stopped) {
            coreController = null
        }
    }

    /**
     * When startLoop fails part-way, libv2ray only resets IsRunning: it does
     * not close the half-started instance (xray-core's Instance.Start does not
     * roll back the features it already started), and stopLoop then sees
     * "not running" and does nothing. A started tun inbound keeps its stack
     * on this fd, a bound loopback listener keeps its port, and the fd number
     * is likely to be reused by the next establish() -- two stacks reading one
     * TUN again. Setting the flag back forces stopLoop through doShutdown.
     * expectedStop is still true here, so the shutdown callback that stopLoop
     * fires is not mistaken for the core dying.
     */
    private fun discardHalfStartedCoreLocked() {
        val controller = coreController ?: return
        try {
            controller.isRunning = true
            stopCoreLocked()
        } catch (t: Throwable) {
            // Keep the handle so a later stop can retry, and never mask the
            // start failure that brought us here.
            logger.w(TAG, "failed to discard a half-started core (${t.javaClass.simpleName})")
        }
    }

    private fun copyAssetIfNeeded(context: Context, assetName: String, target: File) {
        val readyFile = File(target.parentFile, "${target.name}.ready")
        val expectedMarker = "${BuildConfig.VERSION_CODE}:${target.length()}"
        if (target.isFile && target.length() > 0L &&
            runCatching { readyFile.readText(Charsets.UTF_8) }.getOrNull() == expectedMarker
        ) {
            return
        }

        val tmpFile = File(target.parentFile, "${target.name}.tmp")
        tmpFile.delete()
        val copiedBytes = context.assets.open(assetName).use { input ->
            FileOutputStream(tmpFile).use { output ->
                val count = input.copyTo(output)
                output.flush()
                output.fd.sync()
                count
            }
        }
        if (copiedBytes <= 0L) {
            tmpFile.delete()
            throw IOException("Packaged Xray asset is empty: $assetName")
        }
        if (target.exists() && !target.delete()) {
            tmpFile.delete()
            throw IOException("Unable to replace Xray asset: $assetName")
        }
        if (!tmpFile.renameTo(target)) {
            throw IOException("Unable to commit Xray asset: $assetName")
        }

        val markerTmp = File(target.parentFile, "${target.name}.ready.tmp")
        markerTmp.delete()
        FileOutputStream(markerTmp).use { output ->
            output.write("${BuildConfig.VERSION_CODE}:$copiedBytes".toByteArray(Charsets.UTF_8))
            output.flush()
            output.fd.sync()
        }
        if (readyFile.exists() && !readyFile.delete()) {
            markerTmp.delete()
            throw IOException("Unable to replace Xray asset marker: $assetName")
        }
        if (!markerTmp.renameTo(readyFile)) {
            throw IOException("Unable to commit Xray asset marker: $assetName")
        }
    }

    private fun randomBase64(byteCount: Int): String {
        val bytes = ByteArray(byteCount)
        SecureRandom().nextBytes(bytes)
        return android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING or android.util.Base64.URL_SAFE)
    }

    private companion object {
        const val TAG = "XrayManager"

        /** Direction names as they appear in the core's counter names (`outbound>>>tag>>>traffic>>>uplink`). */
        const val STAT_UPLINK = "uplink"
        const val STAT_DOWNLINK = "downlink"

        /** 核心状态消息里疑似机密的 `key: value`。编译一次，复用到进程结束。 */
        val SENSITIVE_VALUE = Regex("(?i)(uuid|key|pass|token|auth)[\\s:=]+[^\\s]+")

        /** 判定核心已经死掉的状态词。 */
        val FATAL_STATUS_MARKERS = arrayOf("failed to start", "core exited", "panic")
    }
}
