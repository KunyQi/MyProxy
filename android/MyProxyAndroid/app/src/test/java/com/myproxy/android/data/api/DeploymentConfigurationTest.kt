package com.myproxy.android.data.api

import com.myproxy.android.domain.pairing.BindingTarget
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DeploymentConfigurationTest {
    @Test
    fun `root deployment setting is available in the generated build config`() {
        val target = BindingTarget.fromBaseUrl(ApiConfig.RELEASE_BASE_URL)!!
        assertTrue(target.isHttps)
        assertTrue(com.myproxy.android.BuildConfig.DEPLOYMENT_CONNECTIVITY_CHECK_URLS.size in 1..4)
        com.myproxy.android.BuildConfig.DEPLOYMENT_CONNECTIVITY_CHECK_URLS.forEach { assertTrue(it.startsWith("https://", ignoreCase = true)) }
        assertEquals(com.myproxy.android.BuildConfig.DEPLOYMENT_CONNECTIVITY_CHECK_URLS.toList(), ApiConfig.connectivityCheckUrls)
    }

    @Test
    fun `standard origin ports and ipv6 are preserved`() {
        assertEquals(80, BindingTarget.fromBaseUrl("http://127.0.0.1")!!.port)
        assertEquals(443, BindingTarget.fromBaseUrl("https://api.example.invalid/")!!.port)
        assertEquals(8443, BindingTarget.fromBaseUrl("https://[::1]:8443")!!.port)
        assertFalse(BindingTarget.fromBaseUrl("https://api.example.invalid")!!.isConfigured)
        assertFalse(BindingTarget.fromBaseUrl("https://invalid")!!.isConfigured)
        assertTrue(BindingTarget.fromBaseUrl("https://203.0.113.10")!!.isConfigured)
    }

    @Test
    fun `example server is rejected before a pairing request`() = runBlocking {
        val client = MyProxyApiClient(target = BindingTarget.fromBaseUrl("https://api.example.invalid"))
        try {
            client.getDeviceConfig("test-token")
            org.junit.Assert.fail("The example origin must never be contacted")
        } catch (exception: ApiException) {
            assertEquals(ErrorCode.ServerNotConfigured, exception.code)
            assertTrue(exception.message!!.contains("尚未配置服务器"))
        }
    }
}
