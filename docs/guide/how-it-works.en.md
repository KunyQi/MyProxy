# How it works

MyProxy is built around one division of labour: the deployer does the complicated part once, and everyday use stays as simple as possible. This article starts with the three roles involved, then follows one pairing and one connection to show what happens behind each step.

## Three roles

The **deployer** prepares the server, certificates and Xray, writes the server address into the single entry point, and builds the three clients. This is the only step in the whole process that requires networking knowledge.

The **administrator** creates an account and a pairing code for each user in the admin console, and revokes devices when needed. The deployer and the administrator are often the same person.

The **user** only receives a pairing code. They don't need to know the server address, protocol or port, and never come into contact with any administrator credentials.

## Control plane and data plane are separate

MyProxy splits "who may connect, with what configuration" from "how the traffic flows".

The control plane is MyProxy Server. It manages accounts, pairing codes and device credentials, delivers connection configuration to the clients, records usage aggregated by category, and handles device revocation and update registration. The control plane never forwards proxy traffic.

The data plane is Xray. Once a client has its configuration, it connects directly to Xray on your server over VLESS + REALITY, and the actual traffic only goes through that channel. The server manages users inside Xray through its 3x-ui adapter; the part with data-plane management rights runs separately from the public API, and the two only talk over a protected local channel on the machine.

Only the device API is exposed to the internet. The admin API and admin pages are reachable only through a private channel (an SSH tunnel, for example), so even someone who knows the server address cannot see the admin entry.

## One pairing

After creating a user in the admin console, the administrator generates a pairing code for them. A pairing code is valid for one hour by default, or one day or seven days if chosen. Its plain text is shown only once, when it is generated; the administrator copies it and passes it to the right person over a secure channel.

When the user enters the pairing code, the client exchanges it with the control plane for credentials that belong to this device alone, and the pairing code's job is done. All later connections use the device credentials, which are stored on the device. The administrator can revoke any single device in the admin console at any time; a revoked device can no longer fetch its configuration and needs a new pairing code to bind again.

## One connection

After you press the connection switch, the client does four things in order:

1. **Get the configuration.** It asks the control plane for this device's connection configuration. If the network or the server is temporarily unreachable, it can use the configuration cached after the last successful verification.
2. **Start the core.** Windows and Linux use Xray; Android uses libv2ray, which is built on Xray.
3. **Take over the traffic.** Windows and Linux set the system proxy, backing up and verifying the original settings before changing anything; Android sets up a system VPN.
4. **Confirm it works.** Through the newly established tunnel, it requests the test address on your own server, and only shows "connected" once it receives the expected response.

Disconnecting happens in reverse: the system proxy is restored first, then the core is stopped. If restoring fails, the client keeps the core and the restore information so it can try again later, instead of leaving a broken system proxy behind.

On Linux, the connection is held by a background daemon. The command-line tool and the tray app only control it through a local control channel, so closing the tray app does not disconnect you.

## How traffic is routed

The client offers two connection modes, and the user can switch between them on the home screen at any time.

**Smart routing** is the default: domestic (mainland China) sites connect directly, and everything else goes through the proxy. Decisions follow a fixed order. Private-network addresses and BitTorrent traffic are blocked first; next, domestic domains connect directly and the domains of common international services go through the proxy; any remaining domain that matches no rule is resolved by the built-in DNS. If the result contains a private address, the request is blocked; if it contains a foreign public address, it goes through the proxy; it connects directly only when every address is a domestic public address.

**Global proxy** sends every site through the proxy, and private-network addresses and BitTorrent traffic are still always blocked.

To make lookups faster, DNS is split into a domestic group and an international group, with parallel queries within each group and a cache. In one test with simulated slow and fast resolvers, the median of eight cold lookups dropped from about 623 ms when queried one after another to about 65 ms in parallel. That means less time waiting for name resolution, not faster downloads. For the full rule order, the services covered and the test method, see [Network routing and optimization](../network-routing.en.md) in the reference.

## What "connected" means

Many clients report "connected" as soon as the core starts. MyProxy takes one more step: through the tunnel, it requests the test address on your own server to confirm the path really works. By default, no public probe site is involved.

The deployer can configure up to four additional fallback test addresses in the single entry point, and the client tries them in turn. Each address gets at most five seconds and a whole round at most twenty; if all of them fail, the client does not confirm the connection. Users can also press "Check connection" at any time to run a check manually and see the current latency.

## Security and privacy boundaries

- The client talks to the control plane over standard HTTPS, verifies the server certificate's chain, validity period and host name, and never follows redirects automatically.
- Users only hold a pairing code and their device credentials; the device API never returns administrator credentials.
- Usage is recorded aggregated by category. The admin console can see each device's exit address, and how long that is kept is up to the deployer.
- Self-hosting reduces the trust you place in third-party VPN providers, but privacy still depends on the security of the server itself, how credentials are managed, and how the clients are maintained. The default routing rules include direct connections; which traffic actually goes through the proxy is decided by the policy.

## Updates

Newly installed clients do not check for updates by default. Once checking is turned on, both the update manifest and the installer must pass signature and digest verification; if the deployer has not configured their own trusted keys, the client rejects every update. A new version is never installed automatically, either: the user always confirms.

To publish a new version, the deployer signs and uploads it in the "Release assignment" wizard in the admin console and chooses who it is assigned to; see [Graphical release publishing and assignment](../release-publishing.en.md).
