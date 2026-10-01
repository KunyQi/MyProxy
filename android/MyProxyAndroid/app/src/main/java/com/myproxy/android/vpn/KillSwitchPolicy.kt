package com.myproxy.android.vpn

/**
 * Whether an unexpected tunnel failure should leave the TUN interface in
 * place instead of releasing it.
 *
 * This is the whole kill switch. The interface is built with default routes
 * for both address families (`0.0.0.0/0` and `::/0`), so while it exists
 * every packet the system routes goes into it. A core that has died is a
 * tunnel that reads nothing from that interface, and the packets are dropped.
 * Closing the descriptor is what removes the routes and lets traffic fall
 * back to the open network -- so on an unexpected failure the descriptor is
 * exactly what must be kept.
 *
 * Two cases must never hold:
 *  - [userRequested] -- the user asked to disconnect and expects the network
 *    back. Blocking it would be a bug, not protection.
 *  - no established interface, so there is nothing to block with; the correct
 *    response is ordinary cleanup.
 *
 * `onRevoke` is also not a hold: the system took the interface away, so there
 * is no longer anything to keep.
 */
internal fun shouldHoldBlockingTunnel(
    killSwitchEnabled: Boolean,
    userRequested: Boolean,
    tunnelEstablished: Boolean,
): Boolean = killSwitchEnabled && !userRequested && tunnelEstablished
