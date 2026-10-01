package com.myproxy.android.xray

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LocalProbeEndpointTest {
    @Test
    fun `endpoint stays on loopback`() {
        // Binding anywhere else would expose the tunnel to the local network.
        assertEquals("127.0.0.1", LocalProbeEndpoint.HOST)
    }

    @Test
    fun `port is a real reserved port and is stable for the process`() {
        val port = LocalProbeEndpoint.port
        assertTrue("port out of range: $port", port in 1..65535)
        // The generator and the probe read this independently; they must agree.
        assertEquals(port, LocalProbeEndpoint.port)
    }

    @Test
    fun `credentials are non-empty, distinct and stable`() {
        val user = LocalProbeEndpoint.username
        val pass = LocalProbeEndpoint.password
        assertEquals(32, user.length)
        assertEquals(32, pass.length)
        assertTrue(user.matches(Regex("[0-9a-f]{32}")))
        assertTrue(pass.matches(Regex("[0-9a-f]{32}")))
        assertNotEquals(user, pass)
        assertEquals(user, LocalProbeEndpoint.username)
        assertEquals(pass, LocalProbeEndpoint.password)
    }
}
