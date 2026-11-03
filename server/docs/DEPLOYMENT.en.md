# Server deployment

## Preparation

Use a Linux host with Python 3, systemd, nginx, OpenSSL, curl, 3x-ui and Xray installed. SSH uses an explicit private key and a verified `known_hosts`; the scripts do not download or install third-party services.

1. Change the root `deployment.json`, for example `{"api_base_url":"https://api.example.com:8443"}`. Production addresses accept only an HTTPS origin, which may omit or keep a single root slash; no other path, query, fragment, credentials or whitespace are accepted. If no port is given, 443 is used.
2. In 3x-ui, create your own VLESS/REALITY inbound, generate its keys and short ID, and set a valid SNI. The provided nginx template listens on all interfaces, so the API and Xray on the same host must use different ports; for example, the API uses 443 and the Xray inbound uses 8443. If they are deployed on separate hosts, each can use 443; to reuse a port on the same machine through different addresses, adapt the nginx listen template yourself and set the Xray bind address. `MYPROXY_SERVER_PORT` overrides only the data-plane port.
3. Prepare a full certificate chain issued by a public CA and a private key for the API domain. nginx uses `/etc/letsencrypt/live/<API host>/fullchain.pem` and `privkey.pem` by default; other absolute paths can be passed explicitly. Symbolic links managed by renewal may stay; the public private key is not copied and its permissions are not changed.
4. Prepare a separate `/etc/myproxy/tls/server.crt` and `server.key` for the private backend; a self-signed certificate is fine. The backend listens only on local loopback, and neither the files nor their parent directories may be symbolic links. Do not reuse the public certificate paths.

Example domains are for illustration only; no shared server, accounts, keys or installers are provided. The nginx deployer supports DNS names and IPv4 addresses; IPv6 can be used in the client origin configuration, but the gateway deployment needs its listen and server_name settings adapted yourself.

## Installation

```sh
MYPROXY_DEPLOY_CONFIRM=YES \
MYPROXY_VPS_HOST=server.example.com \
MYPROXY_SSH_KEY=/path/to/private-key \
MYPROXY_DEVICE_API_TLS_CERT=/etc/letsencrypt/live/api.example.com/fullchain.pem \
MYPROXY_DEVICE_API_TLS_KEY=/etc/letsencrypt/live/api.example.com/privkey.pem \
bash server/deploy/deploy.sh
```

The public name and port are derived from the shared configuration; an explicit `MYPROXY_DEVICE_API_SERVER_NAME` or `MYPROXY_DEVICE_API_PUBLIC_PORT` must match it. The TLS mode is `public-ca`; fixed certificate fingerprints are no longer used. The configuration is validated before deployment, uploads use an allowlist of runtime files, and the configuration goes to `/opt/myproxy-api/deployment.json`. Both systemd units read that file explicitly.

The first installation generates a separate Admin Token and device signing key, stored in `/etc/myproxy-api/admin.env`, readable only by root and the API service group; existing keys are never rotated automatically. The x-ui helper uses the root-only `/etc/myproxy-api/xui-helper.env`. The inbound ID and any necessary explicit data-plane overrides can be changed; an unset or empty host override uses the host from the shared configuration.

The script snapshots the service directory (including `deployment.json`), database, units, credentials, nginx and firewall state, and restores the snapshot on failure. The API runs under a dedicated system account with no supplementary groups, and the root helper operates x-ui through a Unix socket. The deployment script never rewrites the public certificate files; after renewal, run `nginx -t && systemctl reload nginx`.

## Verification and administration

```sh
curl --fail https://api.example.com:8443/healthz
curl --fail -o /dev/null -w '%{http_code}\n' https://api.example.com:8443/connectivity-check
curl -o /dev/null -w '%{http_code}\n' https://api.example.com:8443/api/admin/user
```

health should be 200 and connectivity-check should be 204 with no response body; the public Admin API, `/admin`, `/readyz` and unknown routes should return 404. Public acceptance checks use the system CA trust store and verify the full certificate chain, validity period and host name; an untrusted, expired or mismatched certificate fails the deployment. Only the acceptance check of the local private self-signed backend uses `curl -k`.

By default, clients reach the check endpoint on your own server through the tunnel. You can add one to four `connectivity_check_urls` as candidates in the shared configuration; each must be a full HTTPS URL with a valid certificate that returns an empty 204. Candidates may include a path; credentials, queries and fragments are not allowed. After changing them, update the deployed configuration and rebuild the clients. A detailed example is in the [root deployment guide](../../docs/deployment.en.md).

Administration goes through an SSH tunnel to `127.0.0.1:1820`, and you need to provide the Admin Bearer securely yourself. Never proxy Admin routes to the internet. `predeploy_private.py` can run `--audit-only` first; with `--deploy --confirm PRIVATE-PREDEPLOY` it installs only the private backend and includes the same configuration in the transaction, without changing nginx or the firewall.

The "Release assignment" wizard in the admin console signs update manifests in the browser, uploads installers, and publishes and assigns versions. First-time use requires a systemd drop-in for `MYPROXY_RELEASE_SIGNING_KEYS` and a public key built into the clients; the steps are in [Graphical release publishing and assignment](../../docs/release-publishing.en.md). After the new nginx template is deployed, published installers are available through the restricted `/client/releases/` download route; the admin, upload and signing pages are still reachable only through the private entry.

The root configuration, client release builds and server configuration should be updated together. The open API port and the Xray port need to be allowed in the host and cloud firewalls, and the private port 1820 must stay closed to the internet. The database contains device records and address information; treat backups as private data.
