# Graphical release publishing and assignment

The "Release assignment" page of the private admin console provides a release wizard: choose an installer, generate or import a release key in the current browser, sign the update manifest, upload the installer, then publish and save the assignment. Signing happens in the browser; the server only receives the manifest, the signature and the installer, never the private key.

## First-time setup

1. Deploy this version of the server and the nginx template. The admin console is still reached through the existing SSH admin entry; only the download addresses of published installers are open to the internet. The HTTPS API origin in `deployment.json` must already be configured.
2. Use a modern browser that supports Ed25519 in Web Crypto, and open the admin console from `localhost` or over HTTPS. Generate a key in the wizard, download the private key PEM as a backup, and fill in a stable `keyId`; use the same key for later releases. You can also import an existing Ed25519 PKCS#8 PEM/DER private key. The private key is not kept in browser storage; after a refresh or logout you need to select it again.
3. Write the `keyId:public-key-hex` shown on the page into a systemd drop-in for `myproxy-api.service`:

   ```ini
   [Service]
   Environment="MYPROXY_RELEASE_SIGNING_KEYS=your-keyId:64-hex-character-public-key-shown-on-the-page"
   ```

   Run `systemctl daemon-reload` and `systemctl restart myproxy-api`, then refresh the admin console. Do not put this setting in `admin.env`; the deployer only allows that file to contain the administrator and device authentication keys.
4. Compile the same release public key into the clients' `ReleaseSigningKeys`. Linux also needs `LinuxArtifactSigningKeys`, in the format `SHA256(raw 32-byte public key):public-key-hex`. The trust tables in this repository are empty by default; for versions already distributed with an empty table, a client with the public key configured has to be installed manually once.

The configured public keys only decide which signatures are accepted. Generating a key does not add it to the server's or the clients' trust tables automatically.

Browser signing requires a trusted origin for the admin page. The private key is never uploaded through the page, but if the admin console were tampered with, a malicious script could still read browser memory; when the release key needs to be isolated, use an offline signing tool and register the manifest through the advanced entry.

## Publishing a version

1. Raise the source version number and build the installers. Android also needs a higher `versionCode`, and upgrades installed over the top keep the original package name and signing certificate.
2. In the wizard, choose the platform, version and channel, then choose the installer:

   | Platform | Installer requirements |
   | --- | --- |
   | Windows | A ZIP whose root contains an Authenticode-signed `MyProxy.exe`; for portable builds, the update package contains only that EXE |
   | Android | A signed APK; fill in the SHA-256 of the actual APK signing certificate |
   | Linux | The client `.tar.gz`; the wizard creates a detached Ed25519 signature of the installer with the current release key |

   For Windows, also fill in the SHA-256 of the actual Authenticode signing certificate. The wizard signs the update manifest; it does not replace the Windows/Android platform signing tools. Size, digest and a declared certificate fingerprint cannot prove that a package really carries a platform signature, and the clients still verify it at install time.
3. Import or generate the release key and choose the target scope. A platform release targets every device on that platform by default; choose users for per-user targeting, or devices for per-device targeting. The wizard computes the installer's size and SHA-256, generates the signed manifest, and shows a reviewable summary of the version, files and targets.
4. After checking the summary, click the upload-and-publish button. The steps run in order: register a draft, upload the installer (plus the detached signature for Linux), publish the release, save the assignment. The result shows exactly which stage completed; after a failure you can retry on the same page. A version that has already been published cannot have its package replaced.
5. Check the result in the release and assignment lists. The priority of version assignments is device, then user, then platform. If you find a problem, you can revoke the release to stop delivering that version; revoking does not automatically downgrade clients that have already installed it.

Installers are stored by default in a `releases` directory next to the database, with download addresses of the form `/client/releases/<installer SHA256>/myproxy-<platform>-<version>.<extension>`. Drafts and revoked releases are not publicly downloadable. Uploads verify the manifest signature, size and actual SHA-256; for Linux, the detached signature must also pass verification before the release can be published.

The download origin defaults to the API origin in `deployment.json`. To keep the existing API address and certificate while serving installers from a separate download site with a trusted CA certificate, set the following in a systemd drop-in for `myproxy-api.service`:

```ini
[Service]
Environment="MYPROXY_RELEASE_ARTIFACT_ORIGIN=https://downloads.example.com:8443"
```

This setting changes the hosted installer download address used by the wizard; it does not change the API address, port, probe URL or API certificate. The value must be a configured pure HTTPS origin, optionally with a port; it must not contain a path, query parameters, a fragment or user information, nor use an `.invalid` placeholder domain. An empty value keeps the default behaviour. The separate download site must use a trusted CA certificate matching its host name, and forward the strict `/client/releases/` download route to the same service; it must not expose the admin console or the admin API. After saving, run `systemctl daemon-reload` and `systemctl restart myproxy-api`, refresh the admin console and check the returned `artifactBaseUrl`. Do not put this environment variable in `admin.env`. Recognition of hosted addresses also follows the origin, so manifests on the old origin no longer count as hosted by the current service; if packages have already been published, keep the old download service running separately before switching. Switching the setting does not rewrite the signatures or URLs of existing manifests.

A single installer is limited to 512 MiB by default; set `MYPROXY_RELEASE_ARTIFACT_MAX_BYTES` (in bytes; `0` disables uploads) in a systemd drop-in to change it. `MYPROXY_RELEASE_ARTIFACT_DIR` changes the storage directory; with a custom path, also give the `myproxy` user write permission and add the path to the service's `ReadWritePaths`.

The wizard can also export a signed manifest for a manual process; the original Base64 registration form remains as an advanced entry. Exporting or generating a manifest never publishes it automatically.

## Current scope on the clients

This wizard covers the publisher's signing, uploading, publishing and assignment. The Windows/Android interfaces currently only check the platform's public version, and are not yet wired up to per-user/per-device assignment queries or built-in installation; on Linux, `myproxy update check` and `myproxy update apply` are available, and the GUI's automatic-check switch does not yet write the daemon's preference. Wiring up the clients and real installation acceptance on each platform still need to be done separately.
