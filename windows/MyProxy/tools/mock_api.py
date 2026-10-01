#!/usr/bin/env python3
"""MyProxy Debug Mock API (127.0.0.1:8090). Contract: server/docs/openapi.yaml."""
import json
import os
import re
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 8090
CONFIG_VERSION = int(os.environ.get("MOCK_CONFIG_VERSION", "7"))
LATEST_VERSION = os.environ.get("MOCK_LATEST_VERSION", "0.2.0")
CLAIM_REPLAY_WINDOW_SECONDS = 600
CLIENT_INSTANCE_ID_RE = re.compile(r"^[A-Za-z0-9._~-]{16,128}$")

_claim_lock = threading.Lock()
_claimed_instance_id = None
_claimed_platform = None
_claimed_at = None

DEVICE_ID = "dev_mock_0001"
DEVICE_TOKEN = "tok_mock_0001"
PROFILE = {
    "server": "203.0.113.10",
    "port": 443,
    "uuid": "00000000-0000-0000-0000-000000000000",
    "security": "reality",
    "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    "shortId": "0123456789abcdef",
    "sni": "www.microsoft.com",
    "fingerprint": "chrome",
    "flow": "xtls-rprx-vision",
    "spiderX": "/",
}


def norm_pairing_code(raw: str):
    if not isinstance(raw, str):
        return None
    s = raw.strip().replace(" ", "").replace("-", "").upper()
    if len(s) != 8 or re.fullmatch(r"[A-Z0-9]{8}", s) is None:
        return None
    return "-".join(s[i : i + 4] for i in range(0, len(s), 4))


def norm_client_instance_id(raw):
    if raw is None or raw == "":
        return ""
    if not isinstance(raw, str):
        raise ValueError("BadRequest")
    value = raw.strip()
    if CLIENT_INSTANCE_ID_RE.fullmatch(value) is None:
        raise ValueError("BadRequest")
    return value


def utc_now_text():
    return datetime.now(timezone.utc).replace(microsecond=0).strftime(
        "%Y-%m-%dT%H:%M:%SZ"
    )


class Handler(BaseHTTPRequestHandler):
    def _json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _error(self, code, message, status):
        self._json(status, {"error": {"code": code, "message": message}})

    def _read_json(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except (TypeError, ValueError):
            raise ValueError("BadRequest")
        if length < 0 or length > 64 * 1024:
            raise ValueError("BadRequest")
        raw = self.rfile.read(length)
        if len(raw) != length:
            raise ValueError("BadRequest")
        if not raw:
            return {}
        if "application/json" not in (
            self.headers.get("Content-Type", "") or ""
        ).lower():
            return {}
        try:
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ValueError("BadRequest")
        if not isinstance(value, dict):
            raise ValueError("BadRequest")
        return value

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/api/device/config":
            auth = self.headers.get("Authorization", "")
            if auth == f"Bearer {DEVICE_TOKEN}":
                self._json(200, {"configVersion": CONFIG_VERSION, "config": PROFILE})
            else:
                self._error("TokenInvalid", "设备已失效，请重新绑定", 401)
            return
        if path == "/client/windows/latest.json":
            self._json(
                200,
                {
                    "version": LATEST_VERSION,
                    "downloadUrl": "https://example.invalid/MyProxy-windows-x64.zip",
                    "sha256": "0" * 64,
                    "mandatory": False,
                },
            )
            return
        if path == "/client/android/latest.json":
            self._json(
                200,
                {
                    "version": LATEST_VERSION,
                    "downloadUrl": "https://example.invalid/MyProxy-android.apk",
                    "sha256": "0" * 64,
                    "mandatory": False,
                },
            )
            return
        self._error("NotFound", "资源不存在", 404)

    def do_POST(self):
        global _claimed_instance_id, _claimed_platform, _claimed_at
        path = self.path.split("?", 1)[0]
        try:
            data = self._read_json()
        except ValueError:
            self._error("BadRequest", "请求参数错误", 400)
            return
        if path == "/api/device/heartbeat":
            auth = self.headers.get("Authorization", "")
            if auth == f"Bearer {DEVICE_TOKEN}":
                self._json(
                    200,
                    {
                        "ok": True,
                        "configVersion": CONFIG_VERSION,
                        "serverTime": utc_now_text(),
                    },
                )
            else:
                self._error("TokenInvalid", "设备已失效，请重新绑定", 401)
            return
        if path != "/api/device/claim":
            self._error("NotFound", "资源不存在", 404)
            return
        required_fields = ("deviceName", "platform", "clientVersion")
        if any(
            not isinstance(data.get(field), str) or not data[field].strip()
            for field in required_fields
        ):
            self._error("BadRequest", "请求参数错误", 400)
            return
        code = norm_pairing_code(data.get("pairingCode"))
        try:
            client_instance_id = norm_client_instance_id(
                data.get("clientInstanceId")
            )
        except ValueError:
            self._error("BadRequest", "请求参数错误", 400)
            return
        platform = data["platform"].strip()
        if code is None:
            self._error("BadRequest", "请求参数错误", 400)
            return
        if code == "MOCK-0001":
            now = datetime.now(timezone.utc)
            with _claim_lock:
                if _claimed_at is None:
                    _claimed_instance_id = client_instance_id
                    _claimed_platform = platform
                    _claimed_at = now
                elif not client_instance_id:
                    self._error("PairingInvalid", "配对码无效，请检查后重试", 400)
                    return
                elif (
                    client_instance_id != _claimed_instance_id
                    or platform != _claimed_platform
                    or now
                    > _claimed_at + timedelta(seconds=CLAIM_REPLAY_WINDOW_SECONDS)
                ):
                    self._error("PairingInvalid", "配对码无效，请检查后重试", 400)
                    return
            self._json(
                200,
                {
                    "deviceId": DEVICE_ID,
                    "deviceToken": DEVICE_TOKEN,
                    "configVersion": CONFIG_VERSION,
                    "config": PROFILE,
                },
            )
        elif code == "MOCK-0002":
            self._error("PairingExpired", "配对码已过期，请获取新的配对码", 400)
        else:
            self._error("PairingInvalid", "配对码无效，请检查后重试", 400)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print(f"Mock API listening on http://{HOST}:{PORT}")
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()
