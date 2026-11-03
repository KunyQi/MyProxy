# Local browser entry for the admin console

The browser-side cryptography in the release wizard needs a secure context. When the admin console is served over self-signed HTTPS through a restricted SSH tunnel, `deploy/admin_browser_bridge.py` can provide an `http://127.0.0.1` entry on your machine; modern browsers treat HTTP loopback as a secure context, so the backend's self-signed certificate does not need to be added to the system trust store.

First, establish the SSH tunnel with your existing restricted admin launcher, and make sure its local HTTPS port is still listening. The bridge does not create the tunnel, and does not close an existing SSH process when it exits. The SSH identity must be allowed to run the `token` command, and must use an already verified `known_hosts`.

Prepare the **SHA-256 of the DER bytes** of the backend's leaf certificate, 64 hexadecimal characters, verified against a trusted deployment record. Never accept or update a fingerprint automatically from a single unverified connection. Every bridge connection checks this fingerprint before sending any HTTP request, session information or installer; when the certificate changes, verify the new fingerprint independently first.

Replace the host, file paths, existing tunnel port and trusted fingerprint in the example below, then run:

```powershell
python server/deploy/admin_browser_bridge.py --host server.example --ssh-user myproxy-admin --ssh-key C:/keys/admin_ed25519 --known-hosts C:/keys/known_hosts --upstream-port 31820 --tls-sha256 "verified-64-hex-character-fingerprint"
```

By default, the tool picks a free port, listens only on `127.0.0.1`, prints a local URL with a one-time login ticket fragment, and opens the default browser. `--listen-port 32100` fixes the port; `--no-browser` only prints the entry, which suits an existing launcher or browser automation. Never forward this entry to another machine, and never share a URL that carries a ticket. The login page clears the ticket fragment before making any network request.

The tool runs the restricted SSH `token` command only once, and exchanges its output in memory for a short-lived one-time UI ticket. It never logs, stores or prints the administrator Bearer, and never injects it into later browser requests; after login, the admin session obtained by the browser is used. Requests without a session are still handled by the backend as anonymous requests.

Only the admin pages, same-origin static assets, sessions, `/api/admin/*` and health-check paths can be reached through the bridge. The tool rejects foreign Origins, different Hosts, unusual paths and ambiguous request-body boundaries, caps uploads at 512 MiB, and forwards them chunk by chunk. The backend's CSP and security response headers are passed through unchanged. Press `Ctrl+C` to stop the bridge; if it reports a connection failure, check the existing tunnel, the restricted SSH identity, `known_hosts` and the trusted fingerprint.
