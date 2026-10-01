package com.myproxy.android.data.api

import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.IOException
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.SSLPeerUnverifiedException
import javax.net.ssl.SSLProtocolException

/**
 * 服务器证书链、有效期或主机名校验失败，与网络传输故障分别映射。
 * 证书身份错误需要部署管理员检查证书配置，客户端重试或重新绑定不能修复它。
 *
 * 纯 JVM：只用到 JDK 的 javax.net.ssl 与 java.security.cert，不碰 Android
 * 框架类，所以这条在单测 lane 里真的会跑。
 */
class TlsFailureCodeTest {

    @Test
    fun `an untrusted certificate chain is reported as an untrusted server`() {
        // SSLHandshakeException 再交给 OkHttp——这是线上最常见的那条路径。
        val wrapped = SSLHandshakeException("handshake failed").apply {
            initCause(CertificateException("certificate chain is not trusted"))
        }

        assertEquals(ErrorCode.ServerUntrusted, tlsFailureCode(wrapped))
    }

    @Test
    fun `a rejected hostname is reported as an untrusted server`() {
        assertEquals(
            ErrorCode.ServerUntrusted,
            tlsFailureCode(SSLPeerUnverifiedException("hostname mismatch")),
        )
    }

    @Test
    fun `a certificate cause nested deeper is still found`() {
        val inner = SSLHandshakeException("inner").apply {
            initCause(CertificateException("outside its validity window"))
        }
        val outer = SSLException("outer").apply { initCause(inner) }

        assertEquals(ErrorCode.ServerUntrusted, tlsFailureCode(outer))
    }

    @Test
    fun `a transport level TLS failure stays unreachable`() {
        // 协议协商谈不拢与对端半路断开都不是身份问题：那些重试有意义，
        // 而且不该把用户推去重新绑定。
        assertEquals(
            ErrorCode.ApiUnreachable,
            tlsFailureCode(SSLProtocolException("no shared cipher suites")),
        )
        assertEquals(
            ErrorCode.ApiUnreachable,
            tlsFailureCode(SSLException("connection closed by peer").apply {
                initCause(IOException("unexpected end of stream"))
            }),
        )
    }

    @Test
    fun `a cyclic cause chain terminates`() {
        // 平台异常的 cause 链不由我们构造。成环时这里必须返回，而不是让
        // 调用线程转不出来。（Throwable.initCause 不允许自指，所以用两个节点
        // 构成环。）
        val first = SSLException("first")
        val second = IOException("second")
        first.initCause(second)
        second.initCause(first)

        assertEquals(ErrorCode.ApiUnreachable, tlsFailureCode(first))
    }
}
