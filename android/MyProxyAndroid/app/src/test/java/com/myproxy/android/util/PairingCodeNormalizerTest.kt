package com.myproxy.android.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairingCodeNormalizerTest {

    @Test
    fun `normalizes surrounding whitespace`() {
        assertEquals("ABCD-1234", PairingCodeNormalizer.normalize("  abcd1234  "))
    }

    @Test
    fun `normalizes lowercase to uppercase`() {
        assertEquals("ABCD-1234", PairingCodeNormalizer.normalize("abcd1234"))
    }

    @Test
    fun `normalizes hyphens and spaces`() {
        assertEquals("ABCD-1234", PairingCodeNormalizer.normalize("ab cd-12 34"))
    }

    @Test
    fun `normalizes already formatted code`() {
        assertEquals("ABCD-1234", PairingCodeNormalizer.normalize("ABCD 1234"))
    }

    @Test
    fun `cleans partial input without clearing it`() {
        assertEquals("AB", PairingCodeNormalizer.formatForInput("ab"))
        assertEquals("ABCD1", PairingCodeNormalizer.formatForInput("abcd1"))
    }

    @Test
    fun `cleans pasted input and limits length`() {
        assertEquals("ABCD1234", PairingCodeNormalizer.formatForInput(" ab-cd 1234 extra "))
    }

    @Test
    fun `displays canonical grouping`() {
        assertEquals("AB", PairingCodeNormalizer.display("AB"))
        assertEquals("ABCD", PairingCodeNormalizer.display("ABCD"))
        assertEquals("ABCD-1", PairingCodeNormalizer.display("ABCD1"))
        assertEquals("ABCD-1234", PairingCodeNormalizer.display("ABCD1234"))
    }

    @Test
    fun `offset mapping is identity without a hyphen`() {
        assertEquals(4, PairingCodeNormalizer.originalToTransformed(4, hasHyphen = false))
        assertEquals(4, PairingCodeNormalizer.transformedToOriginal(4, hasHyphen = false))
    }

    @Test
    fun `offset mapping shifts only positions after the group boundary`() {
        assertEquals(4, PairingCodeNormalizer.originalToTransformed(4, hasHyphen = true))
        assertEquals(6, PairingCodeNormalizer.originalToTransformed(5, hasHyphen = true))
        assertEquals(4, PairingCodeNormalizer.transformedToOriginal(5, hasHyphen = true))
        assertEquals(5, PairingCodeNormalizer.transformedToOriginal(6, hasHyphen = true))
    }

    /**
     * 回归：自动插入连字符后光标必须落在**最后一个字符之后**，否则逐字输入
     * CA3F4612 会得到 CA3F-6124（第 5 个字符被后续输入顶到末尾）。
     */
    @Test
    fun `typed order survives the hyphen insertion`() {
        val typed = StringBuilder()
        var cursor = 0
        for (ch in "CA3F4612") {
            typed.insert(cursor, ch)
            cursor += 1
            // 转换层把裸码光标映射到显示串再映射回来，往返必须无损。
            val hasHyphen = typed.length > 4
            val roundTrip = PairingCodeNormalizer.transformedToOriginal(
                PairingCodeNormalizer.originalToTransformed(cursor, hasHyphen),
                hasHyphen
            )
            assertEquals("round trip must preserve the cursor", cursor, roundTrip)
        }
        assertEquals("CA3F4612", typed.toString())
        assertEquals("CA3F-4612", PairingCodeNormalizer.display(typed.toString()))
    }

    @Test
    fun `rejects too short`() {
        assertNull(PairingCodeNormalizer.normalize("abc123"))
    }

    @Test
    fun `rejects too long`() {
        assertNull(PairingCodeNormalizer.normalize("abcd12345"))
    }

    @Test
    fun `rejects non alphanumeric`() {
        assertNull(PairingCodeNormalizer.normalize("abcd12!3"))
    }

    @Test
    fun `rejects null or blank`() {
        assertNull(PairingCodeNormalizer.normalize(null))
        assertNull(PairingCodeNormalizer.normalize("   "))
    }
}
