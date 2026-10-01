package com.myproxy.android.domain.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 目标是编译期常量，所以这里要守的只剩一件事：那个常量能不能被如实解析。
 * 端口尤其要守——解析错了不会报错，只会把请求发到另一个端口上去。
 *
 * 与 Windows 的 `BindingTargetTests.cs` 是同一组断言。
 */
class BindingTargetTest {

    @Test
    fun `explicit port survives parsing`() {
        // 平台的 URI 解析会把方案默认端口归一化掉，于是明写的 :443 会被当成
        // 「没写端口」导致端口发生漂移。这条断言就是为了这个：写了什么就是什么。
        assertEquals(443, BindingTarget.fromBaseUrl("https://203.0.113.10:443")!!.port)
        assertEquals(8090, BindingTarget.fromBaseUrl("http://10.0.2.2:8090")!!.port)
    }

    @Test
    fun `missing port falls back to the device api port`() {
        val target = BindingTarget.fromBaseUrl("https://203.0.113.10")!!
        assertEquals(BindingTarget.DEFAULT_PORT, target.port)
        assertEquals("https://203.0.113.10:443", target.baseUrl)
        assertTrue(target.isHttps)
    }

    @Test
    fun `http targets are parsed but not https`() {
        val target = BindingTarget.fromBaseUrl("http://10.0.2.2:8090")!!
        assertFalse(target.isHttps)
    }

    @Test
    fun `unusable base urls are rejected`() {
        val rejected = listOf(
            "",
            "   ",
            "203.0.113.10",
            "ftp://203.0.113.10",
            "https://",
            "https://203.0.113.10/path",
            "https://203.0.113.10//",
            "https://203.0.113.10/./",
            "https://203.0.113.10?q=1",
            "https://203.0.113.10#fragment",
            "https://203.0.113.10:",
            " https://203.0.113.10",
            "https://203.0.113.10 ",
            "https://user@203.0.113.10",
            "https://203.0.113.10:0",
            "https://203.0.113.10:70000",
            "https://api.example.invalid.",
            "https://127.1",
            "https://0177.0.0.1",
            "https://2130706433",
            "https://0x7f000001",
            "https://0x7f.0.0.1",
            "https://bad_host.example.invalid",
            "https://-host.example.invalid",
        )
        for (candidate in rejected) {
            assertNull("应当拒绝：$candidate", BindingTarget.fromBaseUrl(candidate))
        }
    }

    @Test
    fun `fingerprint normalization rejects anything that is not 64 hex`() {
        assertEquals("", BindingTarget.normalizeSha256(null))
        assertEquals("", BindingTarget.normalizeSha256("abc"))
        assertEquals("", BindingTarget.normalizeSha256("z".repeat(64)))
        assertEquals("a".repeat(64), BindingTarget.normalizeSha256("A".repeat(64)))
        assertEquals("ab".repeat(32), BindingTarget.normalizeSha256(("AB:").repeat(32).trimEnd(':')))
    }
}
