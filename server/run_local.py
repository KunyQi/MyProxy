"""Local development entry point (no TLS, 127.0.0.1:8090).

Settings are loaded from the environment and then overridden to local values:
``db_path`` is ``./data/myproxy.db`` relative to this script, TLS is empty.
If ``MYPROXY_ADMIN_TOKEN`` is not set, the loopback-only debug token is
``admin/admin``.
"""

from __future__ import annotations

import dataclasses
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DATA_DIR = HERE / "data"

sys.path.insert(0, str(HERE))

from myproxy_server.app import MyProxyService  # noqa: E402
from myproxy_server.config import Settings  # noqa: E402
from myproxy_server.server import run  # noqa: E402


def _load_fake_xui():
    """Import the shared fake adapter when available.

    If ``tests/fake_xui.py`` is unavailable, use an equivalent in-memory
    adapter so the local entry point remains self contained.
    """
    try:
        from tests.fake_xui import FakeXuiAdapter

        return FakeXuiAdapter()
    except ImportError:
        pass

    # Fallback equivalent of tests/fake_xui.py
    class FakeXuiAdapter:
        def __init__(self):
            self._clients = {}

        def public_profile(self):
            return {
                "server": "127.0.0.1",
                "port": 8443,
                "security": "reality",
                "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "shortId": "0123456789abcdef",
                "sni": "example.com",
                "fingerprint": "chrome",
                "flow": "xtls-rprx-vision",
                "spiderX": "/",
            }

        def add_client(self, email):
            if email in self._clients:
                raise RuntimeError("client already exists")
            import uuid

            client_id = str(uuid.uuid4())
            self._clients[email] = {"email": email, "id": client_id, "enable": True}
            return client_id

        def update_client(self, email, *, enable=None, limit_ip=None, total_gb=None, expiry_time=None):
            if email not in self._clients:
                raise RuntimeError("client not found")
            if enable is not None:
                self._clients[email]["enable"] = bool(enable)

        def remove_client(self, email):
            if email not in self._clients:
                raise RuntimeError("client not found")
            self._clients.pop(email, None)

        def list_clients(self):
            return [
                {
                    "email": c["email"],
                    "id": c["id"],
                    "enable": c["enable"],
                    "up": 0,
                    "down": 0,
                    "total": 0,
                }
                for c in self._clients.values()
            ]

    return FakeXuiAdapter()


def main() -> None:
    settings = Settings.from_env()
    DATA_DIR.mkdir(parents=True, exist_ok=True)

    settings = dataclasses.replace(
        settings,
        db_path=str(DATA_DIR / "myproxy.db"),
        listen_host="127.0.0.1",
        listen_port=8090,
        tls_cert="",
        tls_key="",
    )

    admin_token = settings.admin_token
    if not admin_token:
        admin_token = "admin/admin"
        print("[run_local] using debug MYPROXY_ADMIN_TOKEN=admin/admin", flush=True)
    device_token_secret = settings.device_token_secret or ("0" * 64)
    settings = dataclasses.replace(
        settings,
        admin_token=admin_token,
        device_token_secret=device_token_secret,
    )

    xui = _load_fake_xui()
    service = MyProxyService(settings, xui)
    print(
        "[run_local] starting http://127.0.0.1:8090 "
        f"db={settings.db_path} admin_token={'set' if admin_token else 'empty'}",
        flush=True,
    )
    run(settings, service)


if __name__ == "__main__":
    main()
