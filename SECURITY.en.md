# Security policy

MyProxy involves device credentials, the server control plane and user traffic. When reporting a vulnerability, put protecting affected users' data and access first.

## Supported scope

The `main` branch and the 0.1.x versions are currently maintained. 0.1.x is in public beta and accepts security fixes; platform compatibility follows the verified scope in the README. When reproducing an issue, record the exact version or commit; do not use "latest" in place of version information.

Server deployers are responsible for their own system patches, public CA certificates, 3x-ui/Xray configuration, administrative access and backups. The clients' own code is MIT-licensed; security updates for third-party components follow their respective upstream versions and licenses.

## Reporting a vulnerability privately

Create a private report under **Security → Report a vulnerability** in the GitHub repository. This entry requires the maintainers to enable Private vulnerability reporting; the setup steps before the first public release are in the [maintenance notes](docs/maintaining.en.md). If the entry is not available yet, first open an issue without any vulnerability details asking the maintainers to enable it.

Do not post exploitable vulnerability details, real pairing codes, device tokens, administrator credentials, private keys, real user traffic or unredacted logs in public issues, pull requests or discussions.

A private report should include the affected version and platform, a minimal reproduction, expected and actual behaviour, the scope of impact and, optionally, a suggested fix. Use your own test server and sample accounts; keep the position and format of sensitive fields, but replace their real values.

The maintainers will confirm the issue in the private report, coordinate the fix and decide when to disclose. Once a fix is released, they will provide the affected versions, the fixed version and migration steps; no response time is promised for issues not yet confirmed.

## Configuration and runtime boundaries

The production control plane uses standard HTTPS certificate chain, validity-period and host-name verification, and the client API does not follow redirects automatically. Ordinary users only receive a pairing code and never need to touch administrator credentials. The project's example addresses are not a ready-made online service.

Self-hosting reduces the trust placed in third-party VPN providers; a compromised server, leaked credentials, an insecure device or incorrect routing can still affect privacy. The default routing includes direct-connection rules, and what is actually forwarded is decided by the deployment's policy.

If you suspect credentials have leaked, revoke the affected devices or credentials first, then deal with configuration and client updates. Release keys are kept by the deployer; this repository contains no private key shared by all deployments.
