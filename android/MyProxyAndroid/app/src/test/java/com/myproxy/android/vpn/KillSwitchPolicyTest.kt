package com.myproxy.android.vpn

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class KillSwitchPolicyTest {

    @Test
    fun `an unexpected failure holds the interface when protection is on`() {
        assertTrue(
            shouldHoldBlockingTunnel(
                killSwitchEnabled = true,
                userRequested = false,
                tunnelEstablished = true,
            )
        )
    }

    @Test
    fun `protection off releases the interface as before`() {
        assertFalse(
            shouldHoldBlockingTunnel(
                killSwitchEnabled = false,
                userRequested = false,
                tunnelEstablished = true,
            )
        )
    }

    @Test
    fun `a user-requested stop is never held`() {
        // Asking to disconnect and losing the network instead would be a bug,
        // not protection.
        assertFalse(
            shouldHoldBlockingTunnel(
                killSwitchEnabled = true,
                userRequested = true,
                tunnelEstablished = true,
            )
        )
    }

    @Test
    fun `there is nothing to hold without an established interface`() {
        // A start that failed before establish() has no routes to keep, so
        // the only correct response is ordinary cleanup.
        assertFalse(
            shouldHoldBlockingTunnel(
                killSwitchEnabled = true,
                userRequested = false,
                tunnelEstablished = false,
            )
        )
    }
}
