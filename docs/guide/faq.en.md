# FAQ

### Is MyProxy a VPN service?

No. MyProxy is software, not a service. You deploy it on your own server, then hand the clients to the people who will use them. The repository provides no shared servers, accounts or installers, and every example address in the documentation is only a placeholder.

### How is it different from using an Xray client directly?

Xray itself is powerful, but used directly, every person has to import and understand the connection configuration themselves. MyProxy leaves the configuration to the deployer, so users only see a pairing code and a switch. It also issues credentials per device, lets you revoke any device at any time, and keeps usage statistics aggregated by category, which makes managing a group of users much easier.

### Which protocols are supported?

Currently only Xray's VLESS + REALITY. The server only accepts REALITY inbounds; any other type is rejected.

### Does all my traffic go through the server?

That depends on the connection mode. In the default smart routing mode, domestic (mainland China) sites connect directly without going through your server, and everything else goes through the proxy; in global proxy mode, every site goes through the proxy. Both modes block private-network addresses and BitTorrent traffic.

### What does the server record about me?

The control plane records accounts and devices, plus usage aggregated by category. The admin console can see each device's exit address, and how long these records are kept is configured by the deployer. The control plane itself never forwards proxy traffic; proxied traffic is carried by Xray on the same server, so whoever runs the server has to be someone you trust, which is exactly the point of self-hosting.

### Does self-hosting automatically make it more secure and private?

Self-hosting reduces the trust you place in third-party VPN providers, but it does not guarantee privacy by itself. A compromised server, leaked credentials, an insecure device or a misconfigured route can all affect privacy. Keeping system patches, certificates and credentials well managed matters just as much as choosing to self-host.

### What if a pairing code is lost or has expired?

A pairing code is valid for one hour by default, and the administrator can also set one day or seven days; once generated, the admin console no longer shows its plain text. Lost or expired, it doesn't matter: just ask the administrator to generate a new one.

### What if I lose a device?

Ask the administrator to revoke that device in the admin console. A revoked device can no longer fetch its connection configuration; if you find it again later, it needs a new pairing code to bind again.

### Do the clients update automatically?

They never install updates automatically. Newly installed clients do not check for updates by default; even with checking turned on, an update must pass signature and digest verification against the deployer's keys, and installs only after you confirm. If the deployer has not configured their own trusted keys, the client rejects every update.

### Which systems are supported?

Windows, Android and Linux. Android has been tested on real devices, and compatibility with different devices and system versions needs to be confirmed individually; Windows and Linux have build and regression tests. On Linux, tray and system proxy behaviour depends on the desktop environment, and the full experience under GNOME and KDE needs to be verified on the target desktop.

### Are there ready-made installers to download?

No. An installer has to carry your own server address, so it must be built, signed and distributed by the deployer. The repository includes a [download page template for all three platforms](../../server/deploy/downloads/index.html) that deployers can use to set up a download page for their users.

### How do I report a security issue?

Please don't post vulnerability details in public issues or discussions. Create a private report from the repository's Security page on GitHub instead; the details are in the [security policy](../../SECURITY.en.md).

### Can I contribute?

You're welcome to. Start with [Contributing to MyProxy](../../CONTRIBUTING.en.md), which lists the documents to read first, the checks to run after making changes, and the requirements for submitting and reviewing changes.
