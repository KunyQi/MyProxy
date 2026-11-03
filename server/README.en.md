# MyProxy Server

MyProxy's Python control plane, providing pairing, configuration, heartbeat and usage APIs for the Windows, Android and Linux clients. Data traffic goes directly from the clients to Xray; the server API does not forward proxy traffic. At runtime it uses only the Python standard library; a Linux deployment additionally needs 3x-ui/Xray, nginx, OpenSSL and systemd.

## Running locally

```sh
cd server
python run_local.py
```

The local entry point uses an in-memory x-ui simulator and SQLite under `server/data/`, and listens on `http://127.0.0.1:8090`. If `MYPROXY_ADMIN_TOKEN` is not provided, `admin/admin` is used, locally only. The local example configuration does not connect to a real server.

```sh
python -m unittest discover -s tests -v
```

## Configuration and deployment

`api_base_url` in the root `deployment.json` is the HTTPS origin shared by the clients and the server. The example `https://api.example.invalid` must be replaced before a production deployment and before distributing clients. `MYPROXY_DEPLOYMENT_CONFIG` can point to a different file; by default the server takes the data-plane host from this URL, and `MYPROXY_SERVER_HOST` can override it explicitly. The data-plane port comes from the 3x-ui inbound or `MYPROXY_SERVER_PORT`, independent of the HTTPS API port.

In production the API listens on `127.0.0.1:1820`, and nginx exposes only an allowlist of Device API routes. The Admin API, admin web pages and readiness stay private; the production process and the root helper use the same `/opt/myproxy-api/deployment.json`.

The public deployment method is described in [Deployment](docs/DEPLOYMENT.en.md), and requests and responses in [OpenAPI](docs/openapi.yaml). Automatic updates do not register releases by default; enabling them requires separately configuring the release signature public key and a signed publishing process.
