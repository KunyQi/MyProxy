package com.myproxy.android.domain.model

import org.junit.Assert.assertNull
import org.junit.Assert.assertNotNull
import org.junit.Test

class ServerProfileTest {

    private val validUuid = "123e4567-e89b-12d3-a456-426614174000"

    private fun validProfile(
        port: Int = 443,
        server: String = "example.com",
        uuid: String = validUuid,
        publicKey: String = "public-key",
        shortId: String = "abcd1234",
        sni: String = "example.com",
        fingerprint: String = "chrome",
        flow: String = "xtls-rprx-vision",
        spiderX: String = "spider",
        security: String = "reality",
    ) = ServerProfile(
        server = server,
        uuid = uuid,
        publicKey = publicKey,
        shortId = shortId,
        sni = sni,
        fingerprint = fingerprint,
        flow = flow,
        spiderX = spiderX,
        port = port,
        security = security,
    )

    @Test
    fun `valid profile passes`() {
        assertNull(validProfile().validationError())
    }

    @Test
    fun `port below range fails`() {
        assertNotNull(validProfile(port = 0).validationError())
    }

    @Test
    fun `port above range fails`() {
        assertNotNull(validProfile(port = 65536).validationError())
    }

    @Test
    fun `empty required field fails`() {
        assertNotNull(validProfile(server = "").validationError())
        assertNotNull(validProfile(publicKey = " ").validationError())
        assertNotNull(validProfile(sni = "").validationError())
    }

    @Test
    fun `invalid uuid fails`() {
        assertNotNull(validProfile(uuid = "not-a-uuid").validationError())
    }

    @Test
    fun `non reality security fails`() {
        assertNotNull(validProfile(security = "tls").validationError())
    }
}
