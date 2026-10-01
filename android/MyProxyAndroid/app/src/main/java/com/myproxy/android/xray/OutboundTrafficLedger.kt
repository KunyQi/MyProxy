package com.myproxy.android.xray

/**
 * 出站流量计数的台账：把「一次读清全部」的核心接口，拆回「每个读取方只读清自己那份」。
 *
 * libv2ray 26.9 起只剩一个统计入口 `queryAllOutboundTrafficStats()`：一次返回**并清零**全部
 * 出站计数器，格式 `tag,direction,value;…`，只列出 value > 0 的项。而本进程有两个读取方——
 * 每秒一次的代理流量（[TrafficStatsSource.readAndResetProxyTraffic]）和紧随其后的分类归因
 * （[TrafficStatsSource.readAndResetCategoryTraffic]）——各自都是「读完即清」。
 * 让两边各调一次核心接口，先调的一方就会把另一方的计数清零后丢掉：分类归因会静默归零，
 * 既不报错也不崩溃，只是数字永远是 0。
 *
 * 所以核心接口的每次返回都先 [absorb] 进这里按 (tag, direction) 累加，读取方再用 [take]
 * 只取走自己的键。没人取的键（例如归因关闭时的分类标签、direct/block 出站）留在台账里，
 * 键的集合由生成的配置决定，是有界的。
 */
internal class OutboundTrafficLedger {
    private val pending = HashMap<String, Long>()

    /** 合并一次 `queryAllOutboundTrafficStats()` 的返回值。null / 空串表示核心没有计数器。 */
    @Synchronized
    fun absorb(raw: String?) {
        for (entry in parse(raw)) {
            val key = key(entry.tag, entry.direction)
            pending[key] = (pending[key] ?: 0L) + entry.bytes
        }
    }

    /** 取走并清零 (tag, direction) 的累计值；没有记录时为 0。 */
    @Synchronized
    fun take(tag: String, direction: String): Long = pending.remove(key(tag, direction)) ?: 0L

    internal data class Entry(val tag: String, val direction: String, val bytes: Long)

    companion object {
        /**
         * 解析核心返回的 `tag,direction,value;…`。
         *
         * 宽进：格式不对的片段、非正数值整段跳过而不是抛出。统计是只读的读数，丢一个样本
         * 远好过让解析异常打断正在运行的隧道。
         */
        internal fun parse(raw: String?): List<Entry> {
            if (raw.isNullOrEmpty()) return emptyList()
            val entries = ArrayList<Entry>()
            for (segment in raw.split(';')) {
                if (segment.isEmpty()) continue
                val parts = segment.split(',')
                if (parts.size != 3) continue
                val tag = parts[0]
                val direction = parts[1]
                val bytes = parts[2].toLongOrNull() ?: continue
                if (tag.isEmpty() || direction.isEmpty() || bytes <= 0L) continue
                entries += Entry(tag, direction, bytes)
            }
            return entries
        }

        private fun key(tag: String, direction: String) = "$tag>>>$direction"
    }
}
