package com.myproxy.android.xray

import java.net.ServerSocket
import java.security.SecureRandom

/**
 * Loopback HTTP inbound that exists only so the connectivity probe can test
 * the tunnel the way the TUI does.
 *
 * The app excludes itself from the VPN, so an ordinary socket from this
 * process bypasses the tunnel entirely and proves nothing about it. Sending
 * the probe through a local inbound is what makes the check meaningful.
 *
 * Every app on the device can reach another app's loopback listener, so the
 * inbound is closed with credentials generated once per process: an open
 * local proxy would let any installed app ride this tunnel. The port is taken
 * from the ephemeral range rather than fixed, so the listener is not at a
 * guessable address.
 */
object LocalProbeEndpoint {
    const val HOST = "127.0.0.1"

    val port: Int by lazy { reserveEphemeralPort() }
    val username: String by lazy { randomToken() }
    val password: String by lazy { randomToken() }

    /**
     * Asks the OS for a free port and releases it immediately. The port could
     * in principle be taken in the gap before xray binds it; that surfaces as
     * a core start failure, which the connection flow already treats as a
     * failed start.
     */
    private fun reserveEphemeralPort(): Int =
        ServerSocket(0).use { socket ->
            socket.reuseAddress = true
            socket.localPort
        }

    private fun randomToken(): String {
        val bytes = ByteArray(16)
        SecureRandom().nextBytes(bytes)
        return bytes.joinToString(separator = "") { "%02x".format(it.toInt() and 0xff) }
    }
}
