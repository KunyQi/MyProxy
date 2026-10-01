"""HTTP API tests for MyProxy Server V1.

Uses a temporary SQLite database, a FakeXuiAdapter and a threaded HTTP server
on 127.0.0.1 with a random port.  No real /etc/x-ui access, no network, no
systemctl.
"""

import json
import http.client
import os
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from myproxy_server import db  # noqa: E402
from myproxy_server.app import MyProxyService, ServiceError  # noqa: E402
from myproxy_server.config import Settings  # noqa: E402
from myproxy_server.server import make_server  # noqa: E402

try:
    from tests.fake_xui import FakeXuiAdapter
except ImportError:  # pragma: no cover - standalone test fallback
    class FakeXuiAdapter:
        """In-memory adapter equivalent to tests/fake_xui.py."""

        def __init__(self):
            self._clients = {}

        def public_profile(self):
            return {
                "server": "127.0.0.1",
                "port": 443,
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


class FailingAddXui(FakeXuiAdapter):
    """Fake adapter whose add_client always fails."""

    def add_client(self, email):
        raise RuntimeError("xui add_client failed")


def make_settings(db_path, *, admin_token="test-admin-token", **overrides):
    values = dict(
        db_path=db_path,
        admin_token=admin_token,
        listen_host="127.0.0.1",
        listen_port=0,
        tls_cert="",
        tls_key="",
    )
    values.update(overrides)
    return Settings(**values)


def start_server(settings, xui):
    service = MyProxyService(settings, xui)
    service.initialize()
    httpd = make_server(service, settings)
    port = httpd.server_address[1]
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    return httpd, thread, port


def stop_server(httpd, thread):
    httpd.shutdown()
    httpd.server_close()
    thread.join(timeout=5)


class ApiTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory()
        cls.db_path = os.path.join(cls._tmpdir.name, "myproxy.db")
        cls.admin_token = "test-admin-token"
        cls.xui = FakeXuiAdapter()
        cls.settings = make_settings(cls.db_path)
        cls.service = MyProxyService(cls.settings, cls.xui)
        cls.service.initialize()
        cls.httpd = make_server(cls.service, cls.settings)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

        # Common fixture: one user, one binding, one claimed device.
        status, cls.fixture_user = cls._api(
            "POST", "/api/admin/user",
            {"username": "fixture_user", "displayName": "Fixture"},
            cls.admin_token,
        )
        assert status == 200, cls.fixture_user
        status, cls.fixture_binding = cls._api(
            "POST", "/api/admin/binding",
            {"userId": cls.fixture_user["id"], "deviceTemplate": "windows", "expiresInSeconds": 3600},
            cls.admin_token,
        )
        assert status == 200, cls.fixture_binding
        status, cls.fixture_claim = cls._api(
            "POST", "/api/device/claim",
            {
                "pairingCode": cls.fixture_binding["code"],
                "deviceName": "FIXTURE-DESKTOP",
                "platform": "windows",
                "clientVersion": "0.1.0",
            },
        )
        assert status == 200, cls.fixture_claim
        cls.device_token = cls.fixture_claim["deviceToken"]
        cls.device_id = cls.fixture_claim["deviceId"]
        cls.binding_code = cls.fixture_binding["code"]

    @classmethod
    def tearDownClass(cls):
        stop_server(cls.httpd, cls.thread)
        cls._tmpdir.cleanup()

    # ------------------------------------------------------------------
    # helpers
    # ------------------------------------------------------------------
    @classmethod
    def _api(cls, method, path, body=None, token=None):
        url = f"http://127.0.0.1:{cls.port}{path}"
        data = None
        headers = {}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                raw = resp.read().decode("utf-8")
                return resp.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8")
            return exc.code, (json.loads(raw) if raw else {})

    @classmethod
    def _admin(cls, method, path, body=None):
        return cls._api(method, path, body, cls.admin_token)

    def assert_profile_keys(self, config):
        for key in (
            "server", "port", "uuid", "security", "publicKey",
            "shortId", "sni", "fingerprint", "flow", "spiderX",
        ):
            self.assertIn(key, config)
        uuid.UUID(config["uuid"])

    # ------------------------------------------------------------------
    # tests
    # ------------------------------------------------------------------
    def test_healthz(self):
        status, data = self._api("GET", "/healthz")
        self.assertEqual(status, 200)
        self.assertTrue(data["ok"])
        self.assertEqual(data["service"], "myproxy-api")
        self.assertEqual(data["version"], "0.1.0")

    def test_connectivity_check_returns_an_exact_empty_204(self):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            conn.request("GET", "/connectivity-check")
            response = conn.getresponse()
            self.assertEqual(response.status, 204)
            self.assertIsNone(response.getheader("Content-Type"))
            self.assertIsNone(response.getheader("Content-Length"))
            self.assertEqual(response.read(), b"")
        finally:
            conn.close()

        for method, path in (
            ("POST", "/connectivity-check"),
            ("DELETE", "/connectivity-check"),
            ("GET", "/connectivity-check?"),
            ("GET", "/connectivity-check?probe=1"),
            ("GET", "/connectivity-check/"),
        ):
            with self.subTest(method=method, path=path):
                status, _ = self._api(method, path)
                self.assertEqual(status, 404)

    def test_readyz_reports_database_and_xui_checks(self):
        status, data = self._api("GET", "/readyz")
        self.assertEqual(status, 200)
        self.assertTrue(data["ok"])
        self.assertEqual(data["checks"], {"db": True, "xui": True})
        self.assertNotIn("path", data)
        self.assertNotIn("error", data)

    def test_responses_return_a_safe_request_id(self):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            conn.request("GET", "/healthz", headers={"X-Request-ID": "test.req-1"})
            response = conn.getresponse()
            self.assertEqual(response.status, 200)
            self.assertEqual(response.getheader("X-Request-ID"), "test.req-1")
            response.read()
        finally:
            conn.close()

    def test_claim_success_shape(self):
        claim = self.fixture_claim
        self.assertEqual(claim["deviceId"], self.device_id)
        self.assertTrue(claim["deviceId"].startswith("dev_"))
        self.assertTrue(claim["deviceToken"].startswith("tok_"))
        self.assertGreaterEqual(claim["configVersion"], 1)
        self.assert_profile_keys(claim["config"])
        self.assertEqual(claim["config"]["server"], "127.0.0.1")

    def test_pairing_expired(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "expired_user"}
        )
        self.assertEqual(status, 200, user)
        status, binding = self._admin(
            "POST", "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows", "expiresInSeconds": 60},
        )
        self.assertEqual(status, 200, binding)
        with db.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE binding_links SET expires_at = ? WHERE id = ?",
                ("2020-01-01T00:00:00Z", binding["id"]),
            )
        status, data = self._api(
            "POST", "/api/device/claim",
            {
                "pairingCode": binding["code"],
                "deviceName": "EXPIRED-DESKTOP",
                "platform": "windows",
                "clientVersion": "0.1.0",
            },
        )
        self.assertEqual(status, 400, data)
        self.assertEqual(data["error"]["code"], "PairingExpired")
        with db.connect(self.db_path) as conn:
            row = conn.execute(
                "SELECT status FROM binding_links WHERE id = ?", (binding["id"],)
            ).fetchone()
        self.assertEqual(row[0], "expired")

    def test_invalid_or_used_code(self):
        status, data = self._api(
            "POST", "/api/device/claim",
            {
                "pairingCode": "A7K9-M2QF",
                "deviceName": "DESKTOP-ABC",
                "platform": "windows",
                "clientVersion": "0.1.0",
            },
        )
        self.assertEqual(status, 400, data)
        self.assertEqual(data["error"]["code"], "PairingInvalid")

        status, data = self._api(
            "POST", "/api/device/claim",
            {
                "pairingCode": self.binding_code,
                "deviceName": "FIXTURE-DESKTOP",
                "platform": "windows",
                "clientVersion": "0.1.0",
            },
        )
        self.assertEqual(status, 400, data)
        self.assertEqual(data["error"]["code"], "PairingInvalid")

    def test_claim_with_instance_id_is_safely_replayable(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "replay_user"}
        )
        self.assertEqual(status, 200, user)
        status, binding = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows"},
        )
        self.assertEqual(status, 200, binding)
        request = {
            "pairingCode": binding["code"],
            "deviceName": "REPLAY-DESKTOP",
            "platform": "windows",
            "clientVersion": "0.2.0",
            "clientInstanceId": "c18ac0c1-2cb0-444a-a61c-b47fc8617843",
        }
        before_clients = len(self.xui.list_clients())
        status, first = self._api("POST", "/api/device/claim", request)
        self.assertEqual(status, 200, first)
        status, replay = self._api("POST", "/api/device/claim", request)
        self.assertEqual(status, 200, replay)
        self.assertEqual(replay["deviceId"], first["deviceId"])
        self.assertEqual(replay["deviceToken"], first["deviceToken"])
        self.assertEqual(len(self.xui.list_clients()), before_clients + 1)

        wrong_instance = dict(request)
        wrong_instance["clientInstanceId"] = (
            "6dd40f55-ac04-43ba-a991-b1752fe282cd"
        )
        status, error = self._api(
            "POST", "/api/device/claim", wrong_instance
        )
        self.assertEqual(status, 400, error)
        self.assertEqual(error["error"]["code"], "PairingInvalid")

    def test_stable_instance_ids_separate_same_named_devices(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "same_name_user"}
        )
        self.assertEqual(status, 200, user)
        claims = []
        for instance_id in (
            "67f4af5e-c138-4b76-b245-106cba4bcab1",
            "55f57930-8368-448f-9828-62fa91b29fd2",
        ):
            status, binding = self._admin(
                "POST",
                "/api/admin/binding",
                {"userId": user["id"], "deviceTemplate": "android"},
            )
            self.assertEqual(status, 200, binding)
            status, claim = self._api(
                "POST",
                "/api/device/claim",
                {
                    "pairingCode": binding["code"],
                    "deviceName": "Pixel 9",
                    "platform": "android",
                    "clientVersion": "0.2.0",
                    "clientInstanceId": instance_id,
                },
            )
            self.assertEqual(status, 200, claim)
            claims.append(claim)

        self.assertNotEqual(claims[0]["deviceId"], claims[1]["deviceId"])
        self.assertNotEqual(claims[0]["deviceToken"], claims[1]["deviceToken"])
        status, devices = self._admin(
            "GET", f"/api/admin/device?userId={user['id']}"
        )
        self.assertEqual(status, 200, devices)
        self.assertEqual(len(devices["devices"]), 2)
        for claim in claims:
            status, config = self._api(
                "GET", "/api/device/config", token=claim["deviceToken"]
            )
            self.assertEqual(status, 200, config)

    def test_old_claim_cannot_resurrect_token_after_new_binding_rotates_it(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "rotation_replay_user"}
        )
        self.assertEqual(status, 200, user)
        instance_id = "41c5f4a4-7e4a-4b19-b610-18f08ad14658"
        bindings = []
        claims = []
        for _ in range(2):
            status, binding = self._admin(
                "POST",
                "/api/admin/binding",
                {"userId": user["id"], "deviceTemplate": "windows"},
            )
            self.assertEqual(status, 200, binding)
            bindings.append(binding)
            status, claim = self._api(
                "POST",
                "/api/device/claim",
                {
                    "pairingCode": binding["code"],
                    "deviceName": "ROTATED-DESKTOP",
                    "platform": "windows",
                    "clientVersion": "0.2.0",
                    "clientInstanceId": instance_id,
                },
            )
            self.assertEqual(status, 200, claim)
            claims.append(claim)

        self.assertEqual(claims[0]["deviceId"], claims[1]["deviceId"])
        self.assertNotEqual(claims[0]["deviceToken"], claims[1]["deviceToken"])
        status, error = self._api(
            "POST",
            "/api/device/claim",
            {
                "pairingCode": bindings[0]["code"],
                "deviceName": "ROTATED-DESKTOP",
                "platform": "windows",
                "clientVersion": "0.2.0",
                "clientInstanceId": instance_id,
            },
        )
        self.assertEqual(status, 400, error)
        self.assertEqual(error["error"]["code"], "PairingInvalid")
        status, old_error = self._api(
            "GET", "/api/device/config", token=claims[0]["deviceToken"]
        )
        self.assertEqual(status, 401, old_error)
        status, current = self._api(
            "GET", "/api/device/config", token=claims[1]["deviceToken"]
        )
        self.assertEqual(status, 200, current)

    def test_disabled_stable_device_can_be_authorized_to_rebind(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "disabled_rebind_user"}
        )
        self.assertEqual(status, 200, user)
        instance_id = "6211b1bb-2daf-4dd6-8049-93f75c081fc6"
        claimed = []
        for attempt in range(2):
            status, binding = self._admin(
                "POST",
                "/api/admin/binding",
                {"userId": user["id"], "deviceTemplate": "windows"},
            )
            self.assertEqual(status, 200, binding)
            status, result = self._api(
                "POST",
                "/api/device/claim",
                {
                    "pairingCode": binding["code"],
                    "deviceName": "REBOUND-DESKTOP",
                    "platform": "windows",
                    "clientVersion": "0.2.0",
                    "clientInstanceId": instance_id,
                },
            )
            self.assertEqual(status, 200, result)
            claimed.append(result)
            if attempt == 0:
                status, deleted = self._admin(
                    "DELETE", f"/api/admin/device/{result['deviceId']}"
                )
                self.assertEqual(status, 200, deleted)
        self.assertNotEqual(claimed[0]["deviceId"], claimed[1]["deviceId"])

    def test_invalid_client_instance_id_is_bad_request(self):
        status, error = self._api(
            "POST",
            "/api/device/claim",
            {
                "pairingCode": "A7K9-M2QF",
                "deviceName": "DESKTOP-ABC",
                "platform": "windows",
                "clientVersion": "0.2.0",
                "clientInstanceId": "too-short",
            },
        )
        self.assertEqual(status, 400, error)
        self.assertEqual(error["error"]["code"], "BadRequest")

    def test_config_and_heartbeat_auth(self):
        status, data = self._api("GET", "/api/device/config")
        self.assertEqual(status, 401, data)
        self.assertEqual(data["error"]["code"], "TokenInvalid")

        status, data = self._api(
            "GET", "/api/device/config", token="tok_wrong_token"
        )
        self.assertEqual(status, 401, data)
        self.assertEqual(data["error"]["code"], "TokenInvalid")

        status, data = self._api(
            "GET", "/api/device/config", token=self.device_token
        )
        self.assertEqual(status, 200, data)
        self.assertGreaterEqual(data["configVersion"], 1)
        self.assert_profile_keys(data["config"])

        status, data = self._api(
            "POST", "/api/device/heartbeat", body={}, token=self.device_token
        )
        self.assertEqual(status, 200, data)
        self.assertTrue(data["ok"])
        self.assertGreaterEqual(data["configVersion"], 1)
        self.assertIn("serverTime", data)

        status, data = self._api(
            "POST", "/api/device/heartbeat", token="tok_wrong_token"
        )
        self.assertEqual(status, 401, data)
        self.assertEqual(data["error"]["code"], "TokenInvalid")

    def test_config_version_increases_after_rotate(self):
        status, before = self._admin("GET", "/api/admin/config")
        self.assertEqual(status, 200, before)
        status, rotated = self._admin(
            "POST", "/api/admin/config/version", {"note": "rotate"}
        )
        self.assertEqual(status, 200, rotated)
        self.assertEqual(rotated["configVersion"], before["configVersion"] + 1)

        status, data = self._api(
            "GET", "/api/device/config", token=self.device_token
        )
        self.assertEqual(status, 200, data)
        self.assertEqual(data["configVersion"], rotated["configVersion"])

        status, heartbeat = self._api(
            "POST", "/api/device/heartbeat", body={}, token=self.device_token
        )
        self.assertEqual(status, 200, heartbeat)
        self.assertEqual(
            heartbeat["configVersion"], rotated["configVersion"]
        )

    def test_latest_json_and_admin_latest(self):
        status, latest = self._api("GET", "/client/windows/latest.json")
        self.assertEqual(status, 200, latest)
        self.assertEqual(latest["version"], "0.1.0")
        self.assertEqual(latest["downloadUrl"], "")
        self.assertEqual(latest["sha256"], "")
        self.assertFalse(latest["mandatory"])

        status, latest = self._admin("GET", "/api/admin/latest")
        self.assertEqual(status, 200, latest)

        status, updated = self._admin(
            "POST", "/api/admin/latest",
            {
                "version": "0.2.0",
                "downloadUrl": "https://example.com/myproxy.zip",
                "sha256": "a" * 64,
                "mandatory": True,
            },
        )
        self.assertEqual(status, 200, updated)
        self.assertEqual(updated["version"], "0.2.0")
        self.assertTrue(updated["mandatory"])

        status, latest = self._api("GET", "/client/windows/latest.json")
        self.assertEqual(status, 200, latest)
        self.assertEqual(latest["version"], "0.2.0")

    def test_latest_sha256_rejects_nonempty_non_lowercase_hex(self):
        for invalid_sha256 in ("not-a-sha", "a" * 63, "a" * 65, "A" * 64):
            status, data = self._admin(
                "POST",
                "/api/admin/latest",
                {
                    "version": "0.3.0",
                    "downloadUrl": "https://example.com/myproxy.zip",
                    "sha256": invalid_sha256,
                    "mandatory": False,
                },
            )
            self.assertEqual(status, 400, (invalid_sha256, data))
            self.assertEqual(data["error"]["code"], "BadRequest")

    def test_conflict_error_maps_to_409(self):
        class ConflictService:
            def claim(self, *args, **kwargs):
                raise ServiceError("Conflict", 409)

        settings = make_settings(self.db_path, claim_max_per_ip=100)
        httpd = make_server(ConflictService(), settings)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        try:
            status, data = self._api_on(
                httpd.server_address[1],
                "POST",
                "/api/device/claim",
                {
                    "pairingCode": "A7K9-M2QF",
                    "deviceName": "CONFLICT-DESKTOP",
                    "platform": "windows",
                    "clientVersion": "0.1.0",
                },
            )
            self.assertEqual(status, 409, data)
            self.assertEqual(data["error"]["code"], "Conflict")
        finally:
            stop_server(httpd, thread)

    def test_android_latest_is_available_and_independent(self):
        status, latest = self._api("GET", "/client/android/latest.json")
        self.assertEqual(status, 200, latest)
        self.assertEqual(latest["version"], "0.1.0")

        status, updated = self._admin(
            "POST",
            "/api/admin/latest/android",
            {
                "version": "1.2.3",
                "downloadUrl": "https://example.com/myproxy.apk",
                "sha256": "b" * 64,
                "mandatory": False,
            },
        )
        self.assertEqual(status, 200, updated)
        status, latest = self._api("GET", "/client/android/latest.json")
        self.assertEqual(status, 200, latest)
        self.assertEqual(latest["version"], "1.2.3")
        status, android_admin = self._admin(
            "GET", "/api/admin/latest/android"
        )
        self.assertEqual(status, 200, android_admin)
        self.assertEqual(android_admin, latest)

        status, windows = self._api("GET", "/client/windows/latest.json")
        self.assertEqual(status, 200, windows)
        self.assertNotEqual(windows["version"], "1.2.3")

    def test_binding_list_materializes_natural_expiry(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "list_expiry_user"}
        )
        self.assertEqual(status, 200, user)
        status, binding = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows"},
        )
        self.assertEqual(status, 200, binding)
        with db.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE binding_links SET expires_at = ? WHERE id = ?",
                ("2020-01-01T00:00:00Z", binding["id"]),
            )

        status, response = self._admin(
            "GET", f"/api/admin/binding?userId={user['id']}"
        )
        self.assertEqual(status, 200, response)
        listed = next(
            item for item in response["bindings"] if item["id"] == binding["id"]
        )
        self.assertEqual(listed["status"], "expired")
        self.assertNotIn("code", listed)

    def test_binding_code_is_returned_only_at_creation(self):
        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "one_time_code_user"}
        )
        self.assertEqual(status, 200, user)
        status, created = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows"},
        )
        self.assertEqual(status, 200, created)
        self.assertRegex(created["code"], r"^[0-9A-F]{4}-[0-9A-F]{4}$")

        status, listed = self._admin(
            "GET", f"/api/admin/binding?userId={user['id']}"
        )
        self.assertEqual(status, 200, listed)
        self.assertEqual(len(listed["bindings"]), 1)
        self.assertNotIn("code", listed["bindings"][0])

        status, extended = self._admin(
            "POST",
            f"/api/admin/binding/{created['id']}/extend",
            {"extendsSeconds": 3600},
        )
        self.assertEqual(status, 200, extended)
        self.assertNotIn("code", extended)

    def test_admin_full_chain(self):
        status, data = self._api("GET", "/api/admin/user", token="wrong")
        self.assertEqual(status, 401, data)
        self.assertEqual(data["error"]["code"], "AdminUnauthorized")

        status, user = self._admin(
            "POST", "/api/admin/user", {"username": "alice_full"}
        )
        self.assertEqual(status, 200, user)
        self.assertEqual(user["username"], "alice_full")
        self.assertEqual(user["status"], "active")

        status, binding = self._admin(
            "POST", "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows", "expiresInSeconds": 3600},
        )
        self.assertEqual(status, 200, binding)

        status, claim = self._api(
            "POST", "/api/device/claim",
            {
                "pairingCode": binding["code"],
                "deviceName": "ALICE-DESKTOP",
                "platform": "windows",
                "clientVersion": "0.1.0",
            },
        )
        self.assertEqual(status, 200, claim)

        status, devices = self._admin("GET", f"/api/admin/device?userId={user['id']}")
        self.assertEqual(status, 200, devices)
        self.assertEqual(len(devices["devices"]), 1)
        self.assertEqual(devices["devices"][0]["id"], claim["deviceId"])

        status, data = self._admin("DELETE", f"/api/admin/device/{claim['deviceId']}")
        self.assertEqual(status, 200, data)
        self.assertTrue(data["ok"])

        status, devices = self._admin("GET", f"/api/admin/device?userId={user['id']}")
        self.assertEqual(status, 200, devices)
        self.assertEqual(devices["devices"][0]["status"], "disabled")

        status, data = self._admin("DELETE", f"/api/admin/binding/{binding['id']}")
        self.assertEqual(status, 200, data)
        status, data = self._admin("DELETE", f"/api/admin/binding/{binding['id']}")
        self.assertEqual(status, 200, data)

        status, data = self._admin("DELETE", f"/api/admin/user/{user['id']}")
        self.assertEqual(status, 200, data)

        status, users = self._admin("GET", "/api/admin/user?username=alice_full")
        self.assertEqual(status, 200, users)
        self.assertEqual(users["users"], [])

    def test_429_claim_per_ip(self):
        settings = make_settings(
            self.db_path,
            claim_max_per_ip=3,
            claim_window_seconds=600,
        )
        httpd, thread, port = start_server(settings, FakeXuiAdapter())
        try:
            results = []
            for _ in range(4):
                status, data = self._api_on(
                    port,
                    "POST",
                    "/api/device/claim",
                    {
                        "pairingCode": "A7K9-M2QF",
                        "deviceName": "DESKTOP-ABC",
                        "platform": "windows",
                        "clientVersion": "0.1.0",
                    },
                )
                results.append((status, data))
            self.assertEqual([r[0] for r in results[:3]], [400, 400, 400])
            self.assertEqual(results[3][0], 429)
            self.assertEqual(results[3][1]["error"]["code"], "RateLimited")
        finally:
            stop_server(httpd, thread)

    def _make_binding(self, username):
        status, user = self._admin("POST", "/api/admin/user", {"username": username})
        self.assertEqual(status, 200, user)
        status, binding = self._admin(
            "POST", "/api/admin/binding",
            {"userId": user["id"], "deviceTemplate": "windows", "expiresInSeconds": 3600},
        )
        self.assertEqual(status, 200, binding)
        return binding

    def _failed_attempts(self, binding_id):
        with db.connect(self.db_path) as conn:
            row = conn.execute(
                "SELECT failed_attempts FROM binding_links WHERE id = ?",
                (binding_id,),
            ).fetchone()
        return row[0]

    def test_429_per_binding_failure_window(self):
        """A rejected replay counts, and the lockout also guards the replay path.

        The threshold used to be checked only after the 'claimed' branch, which
        returns via _replay_claim first -- so replay guesses were never counted
        and never locked out.
        """
        binding = self._make_binding("ratelimit_user")
        settings = make_settings(
            self.db_path,
            claim_max_failures_per_code=2,
            claim_max_per_ip=100,
            claim_window_seconds=600,
        )
        httpd, thread, port = start_server(settings, FakeXuiAdapter())
        claim = {
            "pairingCode": binding["code"],
            "deviceName": "RATELIMIT-DESKTOP",
            "platform": "windows",
            "clientVersion": "0.1.0",
            "clientInstanceId": "instance-" + "a" * 16,
        }
        try:
            status, data = self._api_on(port, "POST", "/api/device/claim", claim)
            self.assertEqual(status, 200, data)

            # Replaying with a different installation identity is a client-side
            # rejection and must count against the code.
            wrong = dict(claim, clientInstanceId="instance-" + "b" * 16)
            for _ in range(2):
                status, data = self._api_on(port, "POST", "/api/device/claim", wrong)
                self.assertEqual(status, 400, data)
                self.assertEqual(data["error"]["code"], "PairingInvalid")

            status, data = self._api_on(port, "POST", "/api/device/claim", wrong)
            self.assertEqual(status, 429, data)
            self.assertEqual(data["error"]["code"], "RateLimited")

            # The lockout must hold even for the identity that legitimately
            # claimed the code, otherwise it is trivially bypassed.
            status, data = self._api_on(port, "POST", "/api/device/claim", claim)
            self.assertEqual(status, 429, data)
        finally:
            stop_server(httpd, thread)

        self.assertEqual(self._failed_attempts(binding["id"]), 2)

    def test_xui_outage_does_not_lock_the_pairing_code(self):
        """Infrastructure failures must not consume the per-code budget.

        x-ui or the helper being briefly unavailable used to spend the budget,
        so five honest retries locked a freshly issued code for the whole
        window and only an administrator could recover it.
        """
        binding = self._make_binding("outage_user")
        settings = make_settings(
            self.db_path,
            claim_max_failures_per_code=2,
            claim_max_per_ip=100,
            claim_window_seconds=600,
        )
        claim = {
            "pairingCode": binding["code"],
            "deviceName": "OUTAGE-DESKTOP",
            "platform": "windows",
            "clientVersion": "0.1.0",
        }

        httpd, thread, port = start_server(settings, FailingAddXui())
        try:
            for _ in range(3):
                status, data = self._api_on(port, "POST", "/api/device/claim", claim)
                self.assertEqual(status, 500, data)
                self.assertEqual(data["error"]["code"], "ServerError")
        finally:
            stop_server(httpd, thread)

        self.assertEqual(self._failed_attempts(binding["id"]), 0)

        # Once x-ui recovers, the same code still works.
        httpd, thread, port = start_server(settings, FakeXuiAdapter())
        try:
            status, data = self._api_on(port, "POST", "/api/device/claim", claim)
            self.assertEqual(status, 200, data)
        finally:
            stop_server(httpd, thread)

    @staticmethod
    def _api_on(port, method, path, body=None, token=None):
        url = f"http://127.0.0.1:{port}{path}"
        data = None
        headers = {}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                raw = resp.read().decode("utf-8")
                return resp.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8")
            return exc.code, (json.loads(raw) if raw else {})


if __name__ == "__main__":
    unittest.main()
