# MyProxy

**Keep the complexity in the code. Users just connect.**

MyProxy is a self-hosted VPN. You deploy its control plane on your own server, then hand the Windows, Android and Linux clients to the people who will use them. That one-time deployment needs someone who knows networking; after that, a user enters an 8-character pairing code the first time they open the client, and from then on there is only one thing to do each day: switch the connection on or off.

## Features

<div class="grid cards" markdown>

- **Pair once, then just a switch**

    A user only enters an 8-character pairing code. The server address, certificates and protocol are set up in advance by the deployer, and the user never has to see them.

- **Traffic goes through your own server**

    The client connects straight to Xray on your server over VLESS + REALITY, with no third-party VPN provider in between.

- **Smart routing out of the box**

    By default, domestic (mainland China) sites connect directly and everything else goes through the proxy; private-network addresses and BitTorrent traffic are always blocked. Switch to global proxy from the home screen whenever you need to.

- **Connected means it actually works**

    Every connection checks a test address on your own server through the tunnel, and only reports "connected" once that check passes. No public probe sites are involved.

- **Managed per device, revocable any time**

    Every device has its own credentials. If one is lost, the administrator revokes just that device; everyone else carries on as usual.

- **Leaves your system the way it found it**

    Before taking over the system proxy, Windows and Linux back up and verify the original settings, and restore them first when disconnecting. On Android you can turn on a kill switch that blocks traffic if the connection drops unexpectedly, so nothing silently bypasses the proxy.

- **Consistent, lightweight, ad-free clients**

    Windows, Android and Linux share one API contract and one server entry point. The interface keeps only what you need; Android adds a Quick Settings tile, and the desktop clients offer three looks: Classic, Porcelain and Ceramic.

- **Trusted updates, never installed behind your back**

    Updates must pass signature and digest verification against the deployer's own keys. Update checks are off by default, and even when a new version is found, it installs only after the user confirms.

- **Reproducible deployment, reviewable configuration**

    One entry-point file decides where every client and the server connect. The deployment script checks first and changes second, and restores its snapshot if anything fails; the server API has a public OpenAPI contract.

- **Open source and yours**

    MIT-licensed: the source, the builds and the deployment are all in your hands. If something is missing, add it, and leave the improvement for the next person.

</div>

## Why it exists

Open-source tools for running your own VPN have long been powerful enough. The hard part is handing it to someone else: protocols, addresses, ports and certificates all have to be understood before the user can connect. Commercial VPNs hide all of that, at the cost of handing your traffic to a third party.

MyProxy wants both: traffic through your own server, and an experience as simple as a commercial VPN. The deployer fills in the server, certificates and connection parameters once; users connect with the default policy and never have to learn network configuration first.

## What it is made of

**Three clients.** Windows, Android and Linux, with an interface that keeps only the connection switch, the connection mode and a few settings. No ads. All three read the same server entry point, so moving to a new server means changing one place and rebuilding.

**One control plane.** A server written in Python that handles accounts, pairing codes, device credentials, configuration delivery and device revocation, and keeps usage statistics aggregated by category. It only answers "who may connect, and with what configuration"; it never forwards proxy traffic.

**Your own data plane.** The server keeps the 3x-ui panel for managing Xray, and the clients connect directly to Xray over VLESS + REALITY. The server and the traffic stay in your hands.

## What using it looks like

For a user, the whole process takes three steps. The administrator creates an account for them in the admin console and generates a pairing code; they enter the code in the client to bind the device; after that, they just press the connection switch.

The default "smart routing" mode connects to domestic sites directly and sends everything else through the proxy, so everyday use needs no adjustment; switch to "global proxy" when you need to. On Android, you can also tap the MyProxy tile in Quick Settings to connect or disconnect directly.

## Where to start

- To understand how it works first, read [How it works](guide/how-it-works.en.md).
- To deploy your own server, follow the [Deployment guide](guide/deploy.en.md) step by step.
- If you already have a pairing code and want to get started, see [Using the clients](guide/clients.en.md).
- For anything else, check the [FAQ](guide/faq.en.md) first.

When you need exact commands, parameters and API definitions, the "Reference" sections on the left contain all of the repository's technical documentation, maintained together with the code.

## Project status

MyProxy is currently a 0.1.x public beta for developers who deploy their own server. The repository provides source code and build inputs only, with no prebuilt installers and no hosted service.

The Android client has been tested on real devices; Windows, Linux and the server have full build and regression tests, and integration on desktop environments still needs to be verified on the target systems. The project's own code and documentation are licensed under [MIT](../LICENSE); third-party components keep their own licenses, see the [third-party notices](../THIRD_PARTY_NOTICES.md).
