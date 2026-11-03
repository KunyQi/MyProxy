# Deployment guide

This article walks through everything needed to deploy MyProxy from scratch, in order, focusing on why each step matters and where it tends to go wrong. For the exact commands, environment variables and parameters, follow [Configuration, build and deployment](../deployment.en.md) and [Server deployment](../../server/docs/DEPLOYMENT.en.md) in the reference; where the two differ, the reference documents win.

## Before you start

You need four things:

- **A Linux server** that can run systemd, nginx and Python 3, with 3x-ui and Xray installed. The deployment script does not download or install these third-party services for you.
- **A domain name and a real HTTPS certificate.** The certificate must be issued by a public CA (Let's Encrypt, for example) and come with its full chain. The clients verify the certificate strictly; a self-signed certificate cannot be used for the public entry point.
- **A computer to build the clients on**, with the .NET SDK and Python installed; to build the Android client you also need a JDK and the Android SDK.
- **SSH key access to the server**, together with a verified host key fingerprint.

Any example address in the repository is only a placeholder; none of them corresponds to a shared server, account or installer. Every one of them needs to be replaced with your own.

## Step 1: prepare the data plane

The data plane is Xray, which carries the actual proxy traffic. In 3x-ui, create a VLESS + REALITY inbound, generate its keys and short ID, and set a valid SNI. MyProxy only accepts REALITY inbounds; the server rejects any other type.

Plan your ports in advance. The control plane's HTTPS API and the Xray inbound are two independent services: if they share a host, they must use different ports, for example 443 for one and 8443 for the other; if they run on two separate hosts, both can use 443.

## Step 2: write the single entry point

There is exactly one place in the whole project where the server address goes: `deployment.json` in the repository root. Change its `api_base_url` to your own HTTPS address:

~~~json
{
  "api_base_url": "https://api.example.com:8443"
}
~~~

Write only the scheme, host name and port, with no path, credentials or query parameters; if you leave out the port, 443 is used.

The three clients read this file when they are built, and the server deployment uses the same file, which makes it the single entry point of the whole system. It also means that whenever you change it, the clients must be rebuilt; older installers still point to the old address.

If you are worried about relying on a single test address, you can add up to four fallback connectivity-check addresses to the same file; the format is in the reference documentation. Leaving them out is perfectly fine: by default only your own server is used, with no third-party probe service involved.

## Step 3: deploy the server

The server is installed over SSH with the deployment script included in the repository. When you run it, you tell it the server address, the SSH private key and where the certificate files are, and explicitly confirm the deployment so it cannot happen by accident. The script takes care of the following:

- it validates the configuration before deploying, and uploads only the files needed at runtime;
- on first install, it generates a separate administrator token and device signing key, stored where only root and the service account can read them, and never rotates them automatically afterwards;
- it makes the API listen only on the local machine, with nginx exposing just an allowlist of device API routes; the admin API and admin pages are never exposed to the internet;
- before changing anything, it snapshots the service directory, database, configuration, nginx and firewall state, and restores the snapshot automatically if the installation fails.

Once it is installed, run three checks before going further: the health check should return 200; the connectivity-check address should return 204 with an empty body; and requesting the admin API from the internet should return 404. If all three are right, the public entry point is safe. How to run the checks is described in [Server deployment](../../server/docs/DEPLOYMENT.en.md).

After the certificate renews, remember to reload nginx; the deployment script never rewrites your public certificate.

## Step 4: build and sign the clients

All three clients are built on your own computer, and the build bakes the address from the single entry point into the installers.

- **Windows**: build with the .NET SDK.
- **Android**: build the Release variant with Gradle. The Release APK is unsigned by default; sign it with your own long-term key before distributing it. Keep that key safe: later upgrades installed over the top must be signed with the same key, or the system will refuse them.
- **Linux**: build the command-line tool and the tray app; you also need a Linux Xray core of the matching version. The repository includes scripts to fetch and package it, which check the core's version and digest.

Before producing release packages, run the repository's release verification, which checks that the configuration and artifacts meet the release requirements. If the single entry point still holds a placeholder address, production packaging refuses to run.

## Step 5: give users their pairing codes

The admin console can only be opened through a private channel, usually an SSH tunnel. Create a user there, generate a pairing code, copy it and send it to the right person over a secure channel. A pairing code is valid for one hour by default, or one day or seven days if chosen; once you close the window, the console no longer shows its plain text.

Users install the client, enter the pairing code and can start using it right away. You can send them the [Using the clients](clients.en.md) article along with it.

Before handing it over for real, it is worth going through the whole flow once on your own device: bind, connect, disconnect, then revoke the device in the admin console, and check that each step behaves as expected.

## Day-to-day maintenance

Once deployment is done, a few things need ongoing attention:

- **Certificates**: make sure automatic renewal is working, and reload nginx after every renewal.
- **Firewall**: open the API port and the Xray port in both the host firewall and your cloud provider's firewall; keep the private admin port closed to the internet.
- **Backups**: the database contains device records and address information; treat backups as private data.
- **When something goes wrong**: if a device is lost or credentials may have leaked, first revoke the affected device in the admin console, then investigate the rest.
- **Publishing updates**: if you want clients to receive updates, first configure the release signing key and the public key built into the clients, then use the "Release assignment" wizard in the admin console to sign, upload and assign a version; see [Graphical release publishing and assignment](../release-publishing.en.md).
