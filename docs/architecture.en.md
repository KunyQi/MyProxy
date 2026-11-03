# Architecture

The clients provide a simple, stable entry point for everyday use; the server provides a clear contract and a maintainable deployment. Connection parameters are configured by the deployer, and ordinary users get access through a pairing code.

| Part | Responsibilities |
| --- | --- |
| Control plane | Accounts, pairing codes, device credentials, configuration delivery, device revocation, aggregated statistics and update registration |
| Data plane | Xray / libv2ray carry the tunnel connection; the server manages the data plane through its 3x-ui adapter |
| Clients | Binding, connection state, configuration cache, system proxy or VPN management, user interface |
| Linux daemon | Holds the connection and the system proxy; the CLI and GUI operate the same state through a local control channel |

Windows and Linux share the connection state, protocol model, configuration generation and update signature verification through linked source files. Android uses the same HTTP contract. The public API is defined in server/docs/openapi.yaml.

## Workflow

1. The deployer configures the server, HTTPS and the data plane, then builds the three clients.
2. The administrator creates a user and a pairing code, and the user binds the client.
3. The client fetches its device configuration, starts the core and checks the connection; a successfully verified configuration can be cached for later recovery.
4. Before taking over the system proxy, Windows/Linux save and verify a restore backup; when stopping, they restore the system proxy first and then stop the core. If the restore reports an error, the core and the restore information are kept for a later retry. On Android, an explicit stop releases the VPN; with the kill switch enabled, an unexpected failure keeps traffic blocked, and the home screen offers "stop and release protection" or "retry connection", with the notification also keeping a release action. The server can revoke device credentials.

## Runtime boundaries

- Smart routing first blocks visible private addresses and BitTorrent, then matches domestic domains for direct connection, category attribution and international service domains for the proxy; domains that match nothing are resolved to IPs with the built-in DNS. If the resolved set contains a private address it is blocked first; if it contains a foreign public address it goes through the proxy; only domestic public addresses can match the domestic-IP direct rule. Matched domains are not resolved again for IP rules, and global mode keeps using AsIs. DNS keeps a dual-stack cache and queries in parallel within each group; for detailed priorities, service coverage and limits, see [Network routing and optimization](network-routing.en.md).
- Public client requests use HTTPS and verify the certificate chain, validity period and host name; the API does not follow redirects automatically.
- The admin entry is used through a private access channel, and the device API does not return administrator credentials.
- The server API is separated from the helper that holds data-plane management rights, and the two communicate over a protected local channel.
- Usage is recorded in aggregated categories; the admin API can see each device's exit address, and the deployer can configure how much history is kept.
- New installs have automatic update checks off by default; an existing explicit preference is kept. Update manifests and artifacts must pass signature and digest verification, and updates are rejected when no trusted key is configured.
- The Android kill switch is off by default; when traffic is still actually blocked after a failure, the main switch on the home screen stops and releases protection, and retrying is a separate action.
- In Android's pull-down Quick Settings, tapping the MyProxy tile connects or disconnects directly; after an ordinary connection failure, tapping retries, and long-pressing opens the app. When not paired, when VPN permission is needed, or while the kill switch is still blocking, the tile opens the app; first-time permission is requested from that tap, and the connection continues once it is granted. Repeated taps are ignored while connecting or disconnecting.
- The Linux GUI depends on a running daemon; when the backend times out or is unavailable, the window and tray show the state as unknown and clear old traffic and self-check results, and recover once a valid state is received. Desktop capabilities still need to be verified on the target desktop environment.
- Linux GNOME, KDE and environment variables bypass the same internal ranges, covering IPv4 private/link-local and IPv6 loopback, ULA and link-local ranges; environment variables use equivalent whole-byte IPv6 prefixes for compatibility with some curl versions. CIDR/IPv6 matching still depends on application support, and the user's original settings are restored on stop.
- Startup and reconfiguration request /connectivity-check on your own server through the tunnel and require an empty HTTP 204. The deployer can configure up to four HTTPS check addresses, tried in turn on failure, with at most five seconds per attempt and twenty seconds per round; a user cancellation aborts the check. Manual self-checks on all three platforms share a ten-second budget, split across the candidates. Windows/Linux use Cloudflare trace as an auxiliary check only when there is an independent trusted exit IP; the connection entry is not evidence of the exit, and a mismatch caused by address family, NAT or another legitimate exit stays unknown and is not treated as a bypass or a connection failure.
- System proxy restoration depends on a valid backup and writable storage; connecting, abnormal exits and restoration should be verified in the target environment.

For configuration, installation and testing steps, see [Deployment](deployment.en.md).
