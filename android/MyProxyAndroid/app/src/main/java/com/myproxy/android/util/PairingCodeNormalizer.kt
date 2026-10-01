package com.myproxy.android.util

object PairingCodeNormalizer {

    /**
     * Cleans user input without rejecting a partial value. Uppercases, keeps
     * only alphanumeric characters, and retains at most eight of them.
     *
     * 连字符**不在这里插入**：输入框的 state 只存裸码，XXXX-XXXX 的视觉排版
     * 由 PairingCodeVisualTransformation 负责。曾经在这里插连字符、把格式化后
     * 的字符串回灌 TextField，会让 Compose 的编辑光标错一位——第五个字符被顶
     * 到末尾（实机复现：逐字输入 CA3F4612 得到 CA3F-6124）。
     */
    fun formatForInput(raw: String?): String {
        return raw
            ?.filter { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' }
            ?.uppercase()
            ?.take(8)
            .orEmpty()
    }

    /** 把裸码显示成规范的 XXXX-XXXX。 */
    fun display(cleaned: String): String {
        return if (cleaned.length <= 4) cleaned else cleaned.take(4) + "-" + cleaned.drop(4)
    }

    /**
     * 裸码 offset → 显示串 offset。连字符插在第 4 个字符之后，它之后的位置
     * 全部 +1；连字符之前的光标不偏移（停在 F 后面就是停在连字符前面）。
     */
    fun originalToTransformed(offset: Int, hasHyphen: Boolean): Int {
        if (!hasHyphen || offset <= 4) return offset
        return offset + 1
    }

    /**
     * 显示串 offset → 裸码 offset。落在连字符上的位置映射回它前面，光标因此
     * 不会停在「视觉上不存在的字符」里。
     */
    fun transformedToOriginal(offset: Int, hasHyphen: Boolean): Int {
        if (!hasHyphen || offset <= 4) return offset
        return offset - 1
    }

    /**
     * Normalizes a raw pairing code: trim, remove spaces and hyphens, uppercase,
     * then group into 4-character blocks. Returns null when the result is not
     * exactly 8 alphanumeric characters.
     */
    fun normalize(raw: String?): String? {
        val cleaned = raw
            ?.trim()
            ?.replace(" ", "")
            ?.replace("-", "")
            ?.uppercase()
            ?: return null

        if (cleaned.length != 8 || !cleaned.all { it in 'A'..'Z' || it in '0'..'9' }) {
            return null
        }
        return cleaned.chunked(4).joinToString("-")
    }
}
