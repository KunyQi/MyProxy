"""In-memory XuiAdapter implementation for local runs and app/api tests."""

from __future__ import annotations

import uuid

from myproxy_server.xui import XuiError

_DEFAULT_PROFILE = {
    "server": "127.0.0.1",
    "port": 443,
    "security": "reality",
    "publicKey": "fake-public-key",
    "shortId": "0123456789abcdef",
    "sni": "example.com",
    "fingerprint": "chrome",
    "flow": "xtls-rprx-vision",
    "spiderX": "/",
}


class FakeXuiAdapter:
    """A deterministic XuiAdapter that stores clients in a list.

    ``profile`` is copied so callers cannot mutate the adapter from the
    outside.  The default profile is suitable for local development.
    """

    def __init__(self, profile: dict | None = None) -> None:
        self.profile = dict(profile) if profile is not None else dict(_DEFAULT_PROFILE)
        self._clients: list[dict] = []

    # ------------------------------------------------------------------
    # XuiAdapter implementation
    # ------------------------------------------------------------------
    def public_profile(self) -> dict:
        return dict(self.profile)

    def add_client(self, email: str) -> str:
        if not email:
            raise XuiError("email is required")
        if any(c["email"] == email for c in self._clients):
            raise XuiError("client already exists")
        client_id = str(uuid.uuid4())
        self._clients.append(
            {
                "email": email,
                "id": client_id,
                "enable": True,
                "up": 0,
                "down": 0,
                "total": 0,
                "limit_ip": 0,
                "total_gb": 0,
                "expiry_time": 0,
            }
        )
        return client_id

    def update_client(
        self,
        email: str,
        *,
        enable: bool | None = None,
        limit_ip: int | None = None,
        total_gb: int | None = None,
        expiry_time: int | None = None,
    ) -> None:
        if not email:
            raise XuiError("email is required")
        for client in self._clients:
            if client["email"] == email:
                break
        else:
            raise XuiError("client not found", code="client_not_found")

        if enable is not None:
            client["enable"] = bool(enable)
        if limit_ip is not None:
            client["limit_ip"] = int(limit_ip)
        if total_gb is not None:
            client["total_gb"] = int(total_gb)
        if expiry_time is not None:
            client["expiry_time"] = int(expiry_time)

    def remove_client(self, email: str) -> None:
        if not email:
            raise XuiError("email is required")
        remaining = [c for c in self._clients if c["email"] != email]
        if len(remaining) == len(self._clients):
            raise XuiError("client not found", code="client_not_found")
        self._clients = remaining

    def list_clients(self) -> list[dict]:
        return [
            {
                "email": c["email"],
                "id": c["id"],
                "enable": bool(c["enable"]),
                "up": int(c["up"]),
                "down": int(c["down"]),
                "total": int(c["total"]),
            }
            for c in self._clients
        ]
