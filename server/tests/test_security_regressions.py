"""Regression tests for control-plane locking, validation and HTTP safety."""

from __future__ import annotations

import http.client
import importlib.util
import json
import os
import sys
import tempfile
import threading
import time
import unittest
import dataclasses
import socket
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
REPO_DIR = str(Path(SERVER_DIR).parent)
if REPO_DIR not in sys.path:
    sys.path.insert(0, REPO_DIR)

from myproxy_server.app import MyProxyService, ServiceError  # noqa: E402
from myproxy_server.config import Settings  # noqa: E402
from myproxy_server import db  # noqa: E402
from myproxy_server.server import (  # noqa: E402
    REQUEST_TIMEOUT_SECONDS,
    TLS_HANDSHAKE_TIMEOUT_SECONDS,
    _complete_tls_handshake,
    _Handler,
    _build_xui_adapter,
    _validate_runtime_security,
    make_server,
)
from myproxy_server.xui import UnixSocketXuiAdapter, XuiError  # noqa: E402
from tests.fake_xui import FakeXuiAdapter  # noqa: E402


class SlowAddXui(FakeXuiAdapter):
    def __init__(self) -> None:
        super().__init__()
        self.started = threading.Event()
        self.release = threading.Event()

    def add_client(self, email: str) -> str:
        self.started.set()
        if not self.release.wait(5):
            raise RuntimeError("test xui timeout")
        return super().add_client(email)


class DisableAfterCommitFailureXui(FakeXuiAdapter):
    def __init__(self) -> None:
        super().__init__()
        self.fail_next_disable = True

    def update_client(self, email: str, *, enable=None, **kwargs) -> None:
        super().update_client(email, enable=enable, **kwargs)
        if enable is False and self.fail_next_disable:
            self.fail_next_disable = False
            raise RuntimeError("restart failed after x-ui write")


class HelperRejectsDisableXui(FakeXuiAdapter):
    """Mimic the production socket adapter on a disable failure.

    ``UnixSocketXuiAdapter`` collapses every helper failure into one generic
    message, so only the structured code distinguishes outcomes.
    """

    def __init__(self, code: str | None) -> None:
        super().__init__()
        self.code = code

    def update_client(self, email, **kwargs):  # type: ignore[override]
        raise XuiError("x-ui helper rejected operation", code=self.code)


class ControlPlaneSafetyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self.tmp.name, "myproxy.db")
        self.settings = Settings(
            db_path=self.db_path,
            admin_token="test-admin-token",
            listen_host="127.0.0.1",
            listen_port=0,
            tls_cert="",
            tls_key="",
        )
        self.xui = SlowAddXui()
        self.service = MyProxyService(self.settings, self.xui)
        self.service.initialize()
        self.httpd = make_server(self.service, self.settings)
        self.port = self.httpd.server_address[1]
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self) -> None:
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join(timeout=5)
        self.tmp.cleanup()

    def test_claim_does_not_hold_sqlite_write_lock_during_xui(self) -> None:
        user = self.service.admin_create_user("lock-user")
        binding = self.service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        result: dict[str, object] = {}

        def claim() -> None:
            try:
                result["value"] = self.service.claim(
                    binding["code"], "LOCK-DESKTOP", "windows", "0.1.0"
                )
            except Exception as exc:  # pragma: no cover - assertion below
                result["error"] = exc

        worker = threading.Thread(target=claim)
        worker.start()
        self.assertTrue(self.xui.started.wait(2))

        started = time.perf_counter()
        listed = self.service.admin_list_users()
        elapsed = time.perf_counter() - started
        self.assertLess(elapsed, 1.0)
        self.assertEqual(len(listed["users"]), 1)

        self.xui.release.set()
        worker.join(timeout=5)
        self.assertFalse(worker.is_alive())
        self.assertNotIn("error", result)

    def test_wrong_json_types_are_bad_request(self) -> None:
        status, body = self._request(
            "POST",
            "/api/admin/user",
            {"username": 123},
            admin=True,
        )
        self.assertEqual(status, 400, body)
        self.assertEqual(body["error"]["code"], "BadRequest")

        status, body = self._request(
            "POST",
            "/api/admin/latest",
            {"version": "0.2.0", "mandatory": "false"},
            admin=True,
        )
        self.assertEqual(status, 400, body)
        self.assertEqual(body["error"]["code"], "BadRequest")

        status, body = self._request(
            "POST",
            "/api/admin/latest",
            {"version": "0.2.0", "mandatory": False},
            admin=True,
        )
        self.assertEqual(status, 200, body)
        self.assertFalse(body["mandatory"])

    def test_non_json_body_is_consumed_before_connection_reuse(self) -> None:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            raw = b'{"username":"ignored"}'
            conn.request(
                "POST",
                "/api/admin/user",
                body=raw,
                headers={
                    "Authorization": "Bearer test-admin-token",
                    "Content-Type": "text/plain",
                    "Content-Length": str(len(raw)),
                },
            )
            response = conn.getresponse()
            self.assertEqual(response.status, 400)
            response.read()

            conn.request("GET", "/healthz")
            response = conn.getresponse()
            self.assertEqual(response.status, 200)
            self.assertEqual(response.getheader("Server", "").strip(), "myproxy-api/0.1.0")
            self.assertTrue(json.loads(response.read())["ok"])
        finally:
            conn.close()

    def test_unmatched_route_body_is_consumed_before_connection_reuse(self) -> None:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            raw = b"unmatched request body"
            conn.request(
                "POST",
                "/not-a-route",
                body=raw,
                headers={"Content-Length": str(len(raw))},
            )
            response = conn.getresponse()
            self.assertEqual(response.status, 404)
            response.read()

            conn.request("GET", "/healthz")
            response = conn.getresponse()
            self.assertEqual(response.status, 200)
            self.assertTrue(json.loads(response.read())["ok"])
        finally:
            conn.close()

    def test_error_path_consumes_body_and_cannot_smuggle_a_second_request(self) -> None:
        """One request must produce exactly one response on every path.

        The body drain runs in ``_dispatch``'s ``finally``.  When it lived
        after ``_route`` inside the ``try``, an authentication failure skipped
        it and the unread body was parsed as the next request, desynchronising
        an nginx-pooled upstream connection and bypassing the exact-location
        allowlist.
        """
        smuggled = (
            b"GET /healthz HTTP/1.1\r\n"
            b"Host: 127.0.0.1\r\n"
            b"\r\n"
        )
        request = (
            b"GET /api/device/config HTTP/1.1\r\n"
            b"Host: 127.0.0.1\r\n"
            b"Authorization: Bearer tok_not_a_real_device_token\r\n"
            b"Content-Length: " + str(len(smuggled)).encode("ascii") + b"\r\n"
            b"\r\n"
        ) + smuggled

        raw = self._send_raw(request)
        self.assertEqual(raw.count(b"HTTP/1.1 "), 1, raw)
        self.assertTrue(raw.startswith(b"HTTP/1.1 401 "), raw)
        self.assertNotIn(b'"service":"myproxy-api"', raw)

    def test_error_path_body_drain_keeps_the_connection_reusable(self) -> None:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            body = b'{"ignored":"body"}'
            conn.request(
                "GET",
                "/api/device/config",
                body=body,
                headers={
                    "Authorization": "Bearer tok_not_a_real_device_token",
                    "Content-Type": "application/json",
                    "Content-Length": str(len(body)),
                },
            )
            response = conn.getresponse()
            self.assertEqual(response.status, 401)
            response.read()

            conn.request("GET", "/healthz")
            response = conn.getresponse()
            self.assertEqual(response.status, 200)
            self.assertTrue(json.loads(response.read())["ok"])
        finally:
            conn.close()

    def test_leading_slashes_in_raw_request_target_are_rejected(self) -> None:
        # BaseHTTPRequestHandler folds a leading // to / before setting
        # self.path.  Exercise real request lines, without a client library
        # normalising them before the handler's public-route guard runs.
        for method, path in (
            ("GET", "//api/device/config"),
            ("GET", "///api/device/config?ignored=1"),
            ("POST", "//api/device/heartbeat"),
            ("POST", "///api/device/heartbeat?ignored=1"),
        ):
            with self.subTest(method=method, path=path):
                request = (
                    f"{method} {path} HTTP/1.1\r\n"
                    "Host: 127.0.0.1\r\n"
                    "Connection: close\r\n"
                    "Content-Type: application/json\r\n"
                    "Content-Length: 2\r\n"
                    "\r\n"
                    "{}"
                ).encode("ascii")
                response = self._send_raw(request)
                self.assertTrue(response.startswith(b"HTTP/1.1 404 "), response)
                body = json.loads(response.split(b"\r\n\r\n", 1)[1])
                self.assertEqual(body["error"]["code"], "NotFound")

    def test_canonical_raw_request_target_keeps_query_and_authentication(self) -> None:
        for method, path in (
            ("GET", "/api/device/config?ignored=//value"),
            ("POST", "/api/device/heartbeat?ignored=//value"),
        ):
            with self.subTest(method=method, path=path):
                request = (
                    f"{method} {path} HTTP/1.1\r\n"
                    "Host: 127.0.0.1\r\n"
                    "Connection: close\r\n"
                    "Content-Type: application/json\r\n"
                    "Content-Length: 2\r\n"
                    "\r\n"
                    "{}"
                ).encode("ascii")
                response = self._send_raw(request)
                self.assertTrue(response.startswith(b"HTTP/1.1 401 "), response)
                body = json.loads(response.split(b"\r\n\r\n", 1)[1])
                self.assertEqual(body["error"]["code"], "TokenInvalid")

    def test_oversized_body_on_error_path_closes_instead_of_desyncing(self) -> None:
        """An undrainable body must close the connection, never leave bytes."""
        request = (
            b"GET /api/device/config HTTP/1.1\r\n"
            b"Host: 127.0.0.1\r\n"
            b"Authorization: Bearer tok_not_a_real_device_token\r\n"
            b"Content-Length: 99999999\r\n"
            b"\r\n"
        )
        raw = self._send_raw(request)
        self.assertEqual(raw.count(b"HTTP/1.1 "), 1, raw)
        self.assertIn(b"Connection: close", raw)

    def test_non_ascii_bearer_is_unauthorized_and_audited(self) -> None:
        """A non-ASCII admin token must 401 with an audit record, not 500.

        ``hmac.compare_digest`` raises ``TypeError`` for non-ASCII ``str``.
        That turned every such attempt into a 500 that skipped the
        ``admin_unauthorized`` record, so an attacker could probe the admin
        token without leaving failed-authentication traces.
        """
        events: list[tuple[str, dict]] = []

        def record(event: str, **fields: object) -> None:
            events.append((event, fields))

        with mock.patch("myproxy_server.server._emit_structured", record):
            status, body = self._request(
                "GET",
                "/api/admin/user",
                admin=False,
                headers={"Authorization": "Bearer üüü"},
            )

        self.assertEqual(status, 401, body)
        self.assertEqual(body["error"]["code"], "AdminUnauthorized")
        actions = [f.get("action") for name, f in events if name == "security"]
        self.assertIn("admin_unauthorized", actions)
        self.assertNotIn("server_error", [name for name, _ in events])

    def test_revoked_pairing_code_survives_its_ttl(self) -> None:
        """Revocation is terminal: a later TTL must not rewrite the status.

        ``mark_binding_expired`` used to overwrite any status unconditionally
        and ran before the revoked check, so an expired-but-revoked code
        answered "expired" and lost its revocation audit trail.
        """
        user = self.service.admin_create_user("revoked-then-expired")
        binding = self.service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        self.service.admin_delete_binding(binding["id"])

        with db.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE binding_links SET expires_at = ? WHERE id = ?",
                ("2000-01-01T00:00:00Z", binding["id"]),
            )
            conn.commit()

        with self.assertRaises(ServiceError) as caught:
            self.service.claim(binding["code"], "REVOKED-DESKTOP", "windows", "0.1.0")
        self.assertEqual(caught.exception.code, "PairingInvalid")

        with db.connect(self.db_path) as conn:
            row = conn.execute(
                "SELECT status FROM binding_links WHERE id = ?", (binding["id"],)
            ).fetchone()
        self.assertEqual(row["status"], "revoked")

    def test_expired_pairing_code_still_transitions_to_expired(self) -> None:
        user = self.service.admin_create_user("plain-expired")
        binding = self.service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        with db.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE binding_links SET expires_at = ? WHERE id = ?",
                ("2000-01-01T00:00:00Z", binding["id"]),
            )
            conn.commit()

        with self.assertRaises(ServiceError) as caught:
            self.service.claim(binding["code"], "EXPIRED-DESKTOP", "windows", "0.1.0")
        self.assertEqual(caught.exception.code, "PairingExpired")

        with db.connect(self.db_path) as conn:
            row = conn.execute(
                "SELECT status FROM binding_links WHERE id = ?", (binding["id"],)
            ).fetchone()
        self.assertEqual(row["status"], "expired")

    def _rebind_with_disable_failure(self, code: str | None):
        """Claim twice with the same device identity, failing the disable."""
        xui = HelperRejectsDisableXui(code)
        service = MyProxyService(self.settings, xui)
        service.initialize()
        user = service.admin_create_user(f"rebind-{code or 'generic'}")
        first = service.admin_create_binding(user["id"], "windows", expires_in_seconds=3600)
        service.claim(first["code"], "REBIND-DESKTOP", "windows", "0.1.0")
        second = service.admin_create_binding(user["id"], "windows", expires_in_seconds=3600)
        return service.claim(second["code"], "REBIND-DESKTOP", "windows", "0.1.0")

    def test_rebind_survives_an_old_client_deleted_out_of_band(self) -> None:
        """``client_not_found`` on the disable must not block the rebind.

        The branch used to match ``"not found"`` in the message.  Production
        messages never contain it, so a device whose x-ui client had been
        removed out of band could never rebind.
        """
        result = self._rebind_with_disable_failure("client_not_found")
        self.assertTrue(result["deviceToken"].startswith("tok_"))
        self.assertGreaterEqual(result["configVersion"], 1)

    def test_rebind_still_fails_on_a_generic_helper_error(self) -> None:
        """The reverse of the old match: an unrelated failure must not pass.

        ``"inbound not found"`` contains ``"not found"``, so the text match
        also swallowed a missing inbound and left the previous device's UUID
        enabled in xray while the database recorded it as revoked.
        """
        with self.assertRaises(XuiError):
            self._rebind_with_disable_failure(None)

    def test_revoking_a_claimed_code_stops_replay(self) -> None:
        """撤销必须对 claimed 生效，否则泄漏的码在窗口内仍可重放。

        admin_delete_binding 此前只在状态为 active 时才撤销，却无条件返回 ok；
        db.revoke_binding 也不论是否更新都返回 True。
        """
        # 该 TestCase 的默认适配器会阻塞 add_client，这里要一条正常的 claim。
        service = MyProxyService(self.settings, FakeXuiAdapter())
        service.initialize()
        user = service.admin_create_user("revoke-claimed")
        binding = service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        instance = "instance-" + "c" * 16
        claimed = service.claim(
            binding["code"], "LEAK-DESKTOP", "windows", "0.1.0", instance
        )

        # 撤销之前，同一身份可以重放。
        replay = service.claim(
            binding["code"], "LEAK-DESKTOP", "windows", "0.1.0", instance
        )
        self.assertEqual(replay["deviceToken"], claimed["deviceToken"])

        self.assertEqual(service.admin_delete_binding(binding["id"]), {"ok": True})

        with db.connect(self.db_path) as conn:
            row = conn.execute(
                "SELECT status FROM binding_links WHERE id = ?", (binding["id"],)
            ).fetchone()
        self.assertEqual(row["status"], "revoked")

        with self.assertRaises(ServiceError) as caught:
            service.claim(binding["code"], "LEAK-DESKTOP", "windows", "0.1.0", instance)
        self.assertEqual(caught.exception.code, "PairingInvalid")

    def test_deleting_a_missing_binding_is_not_reported_as_success(self) -> None:
        with self.assertRaises(ServiceError) as caught:
            self.service.admin_delete_binding("bl_does_not_exist")
        self.assertEqual(caught.exception.status, 404)

    def test_persisted_private_config_keys_are_not_returned(self) -> None:
        with db.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE config_versions SET config_json = ? WHERE version = 1",
                (
                    json.dumps(
                        {
                            "server": "127.0.0.1",
                            "privateKey": "must-not-leak",
                            "uuid": "stale-uuid",
                        }
                    ),
                ),
            )

        result = self.service.admin_get_config()
        self.assertNotIn("privateKey", result["config"])
        self.assertEqual(result["config"]["uuid"], "")

    def test_deleting_user_revokes_bound_device_and_xui_client(self) -> None:
        user = self.service.admin_create_user("delete-user")
        binding = self.service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        self.xui.release.set()
        claimed = self.service.claim(
            binding["code"], "DELETE-DESKTOP", "windows", "0.1.0"
        )

        self.service.admin_delete_user(user["id"])

        with self.assertRaises(ServiceError) as ctx:
            self.service.get_device_config(claimed["deviceToken"])
        self.assertEqual(ctx.exception.code, "TokenInvalid")
        self.assertFalse(self.xui.list_clients()[0]["enable"])

    def test_device_disable_failure_after_xui_write_is_compensated(self) -> None:
        flaky_xui = DisableAfterCommitFailureXui()
        service = MyProxyService(self.settings, flaky_xui)
        service.initialize()
        user = service.admin_create_user("disable-retry-user")
        binding = service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        claimed = service.claim(
            binding["code"], "DISABLE-DESKTOP", "windows", "0.1.0"
        )

        with self.assertRaises(RuntimeError):
            service.admin_delete_device(claimed["deviceId"])

        self.assertEqual(
            service.admin_list_devices(user["id"])["devices"][0]["status"],
            "active",
        )
        self.assertTrue(flaky_xui.list_clients()[0]["enable"])

    def test_deleting_a_device_whose_xui_client_is_gone_still_revokes_it(self) -> None:
        # The client was removed in the 3x-ui panel, or x-ui.db came back from
        # a backup.  The delete used to fail with client_not_found and leave
        # the device active with a Device Token that still worked.
        user = self.service.admin_create_user("gone-client-user")
        binding = self.service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        self.xui.release.set()
        claimed = self.service.claim(
            binding["code"], "GONE-DESKTOP", "windows", "0.1.0"
        )
        self.xui.remove_client(self.xui.list_clients()[0]["email"])

        self.service.admin_delete_device(claimed["deviceId"])

        self.assertEqual(
            self.service.admin_list_devices(user["id"])["devices"][0]["status"],
            "disabled",
        )
        with self.assertRaises(ServiceError) as ctx:
            self.service.get_device_config(claimed["deviceToken"])
        self.assertEqual(ctx.exception.code, "TokenInvalid")

    def test_deleting_a_user_with_one_missing_client_keeps_the_others_disabled(
        self,
    ) -> None:
        # One device's client is gone from x-ui.  The delete used to fail on
        # it and then "compensate" by re-enabling every device it had already
        # disabled -- leaving the user fully connected after a delete.
        user = self.service.admin_create_user("half-gone-user")
        self.xui.release.set()
        tokens = []
        for name in ("HALF-ONE", "HALF-TWO"):
            binding = self.service.admin_create_binding(
                user["id"], "windows", expires_in_seconds=3600
            )
            tokens.append(
                self.service.claim(binding["code"], name, "windows", "0.1.0")[
                    "deviceToken"
                ]
            )
        devices = self.service.admin_list_devices(user["id"])["devices"]
        # Remove the client of the device the delete visits last, so at least
        # one disable has already happened when the missing one is reached.
        self.xui.remove_client(devices[-1]["xrayClientEmail"])

        self.service.admin_delete_user(user["id"])

        remaining = self.xui.list_clients()
        self.assertEqual(len(remaining), 1)
        self.assertFalse(remaining[0]["enable"])
        for token in tokens:
            with self.assertRaises(ServiceError) as ctx:
                self.service.get_device_config(token)
            self.assertEqual(ctx.exception.code, "TokenInvalid")

    def test_claim_rotation_failure_after_xui_write_is_compensated(self) -> None:
        flaky_xui = DisableAfterCommitFailureXui()
        service = MyProxyService(self.settings, flaky_xui)
        service.initialize()
        user = service.admin_create_user("rotation-retry-user")
        first_binding = service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        instance_id = "27a63a2e-f967-4b53-a41d-2c49ae740665"
        first_claim = service.claim(
            first_binding["code"],
            "ROTATION-DESKTOP",
            "windows",
            "0.1.0",
            instance_id,
        )
        old_client = flaky_xui.list_clients()[0]

        second_binding = service.admin_create_binding(
            user["id"], "windows", expires_in_seconds=3600
        )
        with self.assertRaises(RuntimeError):
            service.claim(
                second_binding["code"],
                "ROTATION-DESKTOP",
                "windows",
                "0.1.0",
                instance_id,
            )

        devices = service.admin_list_devices(user["id"])["devices"]
        self.assertEqual(len(devices), 1)
        self.assertEqual(devices[0]["id"], first_claim["deviceId"])
        self.assertEqual(devices[0]["status"], "active")
        clients = flaky_xui.list_clients()
        self.assertEqual(len(clients), 1)
        self.assertEqual(clients[0]["email"], old_client["email"])
        self.assertTrue(clients[0]["enable"])

    def _request(
        self,
        method: str,
        path: str,
        body: dict | None = None,
        *,
        admin: bool = False,
        headers: dict[str, str] | None = None,
    ) -> tuple[int, dict]:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            sent = {"Content-Type": "application/json"}
            if admin:
                sent["Authorization"] = "Bearer test-admin-token"
            sent.update(headers or {})
            conn.request(
                method,
                path,
                body=None if body is None else json.dumps(body),
                headers=sent,
            )
            response = conn.getresponse()
            return response.status, json.loads(response.read())
        finally:
            conn.close()

    def _send_raw(self, request: bytes) -> bytes:
        """Send one hand-built request and read every byte the server writes."""
        sock = socket.create_connection(("127.0.0.1", self.port), timeout=5)
        try:
            sock.sendall(request)
            sock.settimeout(2)
            chunks: list[bytes] = []
            while True:
                try:
                    chunk = sock.recv(4096)
                except (TimeoutError, socket.timeout):
                    break
                if not chunk:
                    break
                chunks.append(chunk)
            return b"".join(chunks)
        finally:
            sock.close()


class TlsListenerTests(unittest.TestCase):
    """TLS 握手必须发生在 worker 线程，而不是 accept() 内联。"""

    class _FakeTlsSocket:
        def __init__(self, fail: bool = False) -> None:
            self.fail = fail
            self.timeouts: list[float] = []
            self.handshakes = 0

        def settimeout(self, value: float) -> None:
            self.timeouts.append(value)

        def do_handshake(self) -> None:
            self.handshakes += 1
            if self.fail:
                raise TimeoutError("peer never sent a ClientHello")

    def test_listener_defers_the_handshake_out_of_accept(self) -> None:
        # wrap_socket 默认 do_handshake_on_connect=True，使 SSLSocket.accept()
        # 内联完成握手 —— 早于 get_request 的超时与 process_request 的信号量，
        # 一个连上却不发 ClientHello 的对端即可让整个 API 停止接受连接。
        with tempfile.TemporaryDirectory() as tmp:
            settings = Settings(
                db_path=os.path.join(tmp, "myproxy.db"),
                admin_token="a" * 64,
                device_token_secret="b" * 64,
                listen_host="127.0.0.1",
                listen_port=0,
                tls_cert=os.path.join(tmp, "server.crt"),
                tls_key=os.path.join(tmp, "server.key"),
            )
            with mock.patch("myproxy_server.server.ssl.SSLContext") as context_cls:
                context = context_cls.return_value
                httpd = make_server(FakeXuiAdapter(), settings)
                try:
                    context.load_cert_chain.assert_called_once()
                    _, kwargs = context.wrap_socket.call_args
                    self.assertIs(kwargs["do_handshake_on_connect"], False)
                    self.assertIs(kwargs["server_side"], True)
                finally:
                    httpd.server_close()

    def test_handshake_is_bounded_and_failure_only_drops_that_peer(self) -> None:
        good = self._FakeTlsSocket()
        self.assertTrue(_complete_tls_handshake(good))
        self.assertEqual(good.handshakes, 1)
        self.assertEqual(
            good.timeouts, [TLS_HANDSHAKE_TIMEOUT_SECONDS, REQUEST_TIMEOUT_SECONDS]
        )

        bad = self._FakeTlsSocket(fail=True)
        self.assertFalse(_complete_tls_handshake(bad))
        self.assertEqual(bad.timeouts, [TLS_HANDSHAKE_TIMEOUT_SECONDS])

        # 明文监听（本地测试）没有 do_handshake，必须原样放行。
        self.assertTrue(_complete_tls_handshake(object()))


class ServiceValidationTests(unittest.TestCase):
    def test_http_server_rejects_connections_before_unbounded_thread_creation(self) -> None:
        settings = Settings(
            admin_token="a" * 64,
            device_token_secret="b" * 64,
            listen_host="127.0.0.1",
            listen_port=0,
            tls_cert="",
            tls_key="",
        )
        httpd = make_server(FakeXuiAdapter(), settings, max_concurrent_requests=1)
        first, first_peer = socket.socketpair()
        second, second_peer = socket.socketpair()
        try:
            with mock.patch.object(ThreadingHTTPServer, "process_request") as parent:
                httpd.process_request(first, None)
                httpd.process_request(second, None)
                parent.assert_called_once_with(first, None)
                self.assertEqual(second.fileno(), -1)
            with mock.patch.object(ThreadingHTTPServer, "process_request_thread"):
                httpd.process_request_thread(first, None)
            self.assertTrue(httpd._request_slots.acquire(blocking=False))
            httpd._request_slots.release()
        finally:
            for stream in (first, first_peer, second, second_peer):
                stream.close()
            httpd.server_close()

    def test_service_rejects_non_string_username(self) -> None:
        with self.assertRaises(ServiceError) as ctx:
            MyProxyService.__new__(MyProxyService).admin_create_user(123)  # type: ignore[arg-type]
        self.assertEqual(ctx.exception.code, "BadRequest")

    def test_runtime_rejects_public_plaintext_or_missing_admin_token(self) -> None:
        settings = Settings(
            listen_host="0.0.0.0",
            tls_cert="",
            tls_key="",
            admin_token="configured",
            device_token_secret="a" * 64,
        )
        with self.assertRaises(RuntimeError):
            _validate_runtime_security(settings)

        no_admin = dataclasses.replace(settings, listen_host="127.0.0.1", admin_token="")
        with self.assertRaises(RuntimeError):
            _validate_runtime_security(no_admin)

        invalid_device_secret = dataclasses.replace(
            settings,
            listen_host="127.0.0.1",
            tls_cert="/tmp/server.crt",
            tls_key="/tmp/server.key",
            device_token_secret="",
        )
        with self.assertRaises(RuntimeError):
            _validate_runtime_security(invalid_device_secret)

        weak_production_admin = dataclasses.replace(
            settings,
            listen_host="127.0.0.1",
            tls_cert="/tmp/server.crt",
            tls_key="/tmp/server.key",
            admin_token="short-admin-token",
        )
        with self.assertRaises(RuntimeError):
            _validate_runtime_security(weak_production_admin)

    def test_forwarded_claim_ip_is_used_only_for_trusted_proxy(self) -> None:
        handler = _Handler.__new__(_Handler)
        handler.client_address = ("127.0.0.1", 12345)
        handler.headers = {"X-Forwarded-For": "203.0.113.7, 127.0.0.1"}
        handler.settings = Settings(trusted_proxy_ips=("127.0.0.1",))
        self.assertEqual(handler._claim_source_ip(), "203.0.113.7")

        handler.settings = Settings(trusted_proxy_ips=())
        self.assertEqual(handler._claim_source_ip(), "127.0.0.1")

        handler.settings = Settings(trusted_proxy_ips=("127.0.0.1",))
        handler.headers = {"X-Forwarded-For": "not-an-ip"}
        self.assertEqual(handler._claim_source_ip(), "127.0.0.1")

    def test_deployment_gateway_keeps_admin_api_private(self) -> None:
        deploy_dir = Path(SERVER_DIR) / "deploy"
        service = (deploy_dir / "myproxy-api.service").read_text(encoding="utf-8")
        self.assertIn("MYPROXY_LISTEN_HOST=127.0.0.1", service)
        self.assertIn("MYPROXY_LISTEN_PORT=1820", service)
        self.assertIn("MYPROXY_TRUSTED_PROXY_IPS=127.0.0.1,::1", service)
        self.assertIn("ProtectSystem=strict", service)
        self.assertIn("User=myproxy", service)
        self.assertIn("PYTHONDONTWRITEBYTECODE=1", service)
        self.assertIn("MYPROXY_DB_PATH=/var/lib/myproxy-api/myproxy.db", service)
        self.assertIn("MYPROXY_TLS_CERT=/etc/myproxy/tls/server.crt", service)
        self.assertIn("MYPROXY_TLS_KEY=/etc/myproxy/tls/server.key", service)
        self.assertIn("MYPROXY_XUI_HELPER_SOCKET=/run/myproxy/xui-helper.sock", service)
        self.assertIn("ReadWritePaths=/var/lib/myproxy-api", service)
        self.assertIn("SupplementaryGroups=", service)
        self.assertNotIn("ReadWritePaths=/etc/x-ui", service)
        self.assertNotIn("MYPROXY_XUI_RESTART_COMMAND", service)
        self.assertNotIn("MYPROXY_XUI_DB_PATH", service)

        helper_unit = (deploy_dir / "myproxy-xui-helper.service").read_text(
            encoding="utf-8"
        )
        self.assertIn("User=root", helper_unit)
        self.assertIn("EnvironmentFile=/etc/myproxy-api/xui-helper.env", helper_unit)
        self.assertIn("ReadWritePaths=/etc/x-ui /run/myproxy", helper_unit)
        self.assertIn("RestrictAddressFamilies=AF_UNIX", helper_unit)
        self.assertNotIn("EnvironmentFile=-", helper_unit)
        helper_env = (deploy_dir / "myproxy-xui-helper.env").read_text(encoding="utf-8")
        self.assertFalse(any(line.startswith("MYPROXY_SERVER_HOST=") for line in helper_env.splitlines()))
        self.assertIn("MYPROXY_XUI_DB_PATH=/etc/x-ui/x-ui.db", helper_env)

        gateway = (deploy_dir / "nginx-device-api.conf.template").read_text(
            encoding="utf-8"
        )
        allowed = {
            line.strip().split()[2]
            for line in gateway.splitlines()
            if line.strip().startswith("location = ")
        }
        self.assertEqual(
            allowed,
            {
                "/healthz",
                "/connectivity-check",
                "/api/device/claim",
                "/api/device/config",
                "/api/device/heartbeat",
                "/api/device/update",
                "/api/device/update/report",
                "/api/device/usage",
                "/client/windows/latest.json",
                "/client/android/latest.json",
                "/client/linux/latest.json",
            },
        )
        default_location = gateway.split("location / {", 1)[1].split("}", 1)[0]
        self.assertIn("return 404", default_location)
        self.assertNotIn("proxy_pass", default_location)

        installer = (deploy_dir / "install-device-api-proxy.sh").read_text(
            encoding="utf-8"
        )
        self.assertIn("MYPROXY_DEPLOY_CONFIRM", installer)
        self.assertIn("MYPROXY_DEVICE_API_SERVER_NAME", installer)
        self.assertIn("MYPROXY_DEVICE_API_UPSTREAM_PORT", installer)
        self.assertIn('TLS_MODE" != "public-ca', installer)
        self.assertNotIn("CERT_SHA256", installer)
        self.assertIn("curl -sSf --resolve", installer)
        self.assertIn("systemctl reload nginx || true", installer)
        self.assertIn("sites-enabled/default", installer)
        self.assertIn("listen[[:space:]]+([^[:space:];]+:)?80", installer)

        self.assertFalse((deploy_dir / "myproxy-xui-restart").exists())
        self.assertIn("myproxy_device_api_claim", gateway)
        self.assertIn("limit_conn myproxy_device_api_conn 20", gateway)
        self.assertIn("limit_req_status 429", gateway)
        # 十一个精确代理路由，含空 204 检测端点。
        # 这个数字是有意锁死的：每多一条 location 都是一次对外暴露面的扩大，
        # 必须有人明确改这个断言，而不是顺手加一条路由。
        self.assertEqual(gateway.count("proxy_set_header X-Request-ID $request_id;"), 11)
        self.assertIn("proxy_read_timeout 35s", gateway)

    def test_deploy_transaction_snapshots_and_rolls_back_before_ufw(self) -> None:
        deploy = (
            Path(SERVER_DIR) / "deploy" / "deploy.sh"
        ).read_text(encoding="utf-8")
        for required in (
            'MYPROXY_DEPLOY_CONFIRM=YES',
            'VPS_HOST="${MYPROXY_VPS_HOST:-}"',
            'SSH_KEY="${MYPROXY_SSH_KEY:-}"',
            "远程预检现有 Admin Token 形状",
            "尚未停止服务",
            'ROLLBACK_DIR="$REMOTE_TMP/.rollback"',
            "trap finish_remote EXIT",
            "rollback_remote()",
            'tar czf "$ROLLBACK_DIR/remote-dir.tar.gz"',
            'cp -a -- "$UNIT_PATH" "$ROLLBACK_DIR/myproxy-api.service"',
            'cp -a -- "$NGINX_CONFIG_PATH" "$ROLLBACK_DIR/nginx.conf"',
            'cp -a -- "$ADMIN_ENV_PATH" "$ROLLBACK_DIR/admin.env"',
            'restore_snapshot_file "$UNIT_EXISTED"',
            'restore_snapshot_file "$ADMIN_ENV_EXISTED"',
            'restore_snapshot_file "$XUI_HELPER_ENV_EXISTED"',
            'tar czf "$ROLLBACK_DIR/state-dir.tar.gz"',
            'source.backup(destination)',
            'PRAGMA integrity_check',
            'src_dir_fd=directory_fd',
            'dst_dir_fd=directory_fd',
            'install -d -m 0700 -o root -g root "$STATE_DIR"',
            'chown myproxy:myproxy "$STATE_DIR"',
            'chown root:myproxy /etc/myproxy /etc/myproxy/tls',
            'TLS_ROOT_MODE=',
            "MYPROXY_WAS_ACTIVE",
            "MYPROXY_WAS_ENABLED",
            "NGINX_WAS_ACTIVE",
            "NGINX_WAS_ENABLED",
            "UFW_RULE_ADDED",
            "NGINX_DEFAULT_SITE_PATH",
            "NGINX_DEFAULT_SITE_EXISTED",
            "ufw-820.before",
            "UFW_V4_820_BEFORE",
            "UFW_V6_820_BEFORE",
            "UFW_V4_820_ADDED",
            "UFW_V6_820_ADDED",
            "id -Gn myproxy",
            "MYPROXY_EXTRA_GROUPS",
            "MYPROXY_DEVICE_TOKEN_SECRET=",
            "admin.env contains a forbidden assignment",
            "DB_MIGRATED=0",
            'if [ "$DB_MIGRATED" -eq 1 ]',
            '0.0.0.0/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp',
            '::/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp',
            "nginx -T",
            "HTTP :80",
            "LC_ALL=C ufw status",
            "MYPROXY_DEPLOY_CONFIRM=YES",
            "MYPROXY_DEVICE_API_UPSTREAM_PORT=1820",
            "require_configured_deployment()",
            "command -v python3",
            "SERVER_MODULE_FILES=(",
            'SERVER_UPLOADS+=("$module_path")',
            '"${SERVER_UPLOADS[@]}"',
            'DEPLOY_UPLOADS=(',
            '[ -L "$SERVER_DIR/myproxy_server" ]',
            '[ -L "$SERVER_DIR/deploy" ]',
            '[ -L "$SERVER_DIR/run_local.py" ]',
        ):
            self.assertIn(required, deploy)
        self.assertNotIn(
            'ufw --force delete allow "${DEVICE_API_PUBLIC_PORT}/tcp"',
            deploy,
        )
        self.assertIn('DEPLOYMENT_CONFIG="${MYPROXY_DEPLOYMENT_CONFIG:-', deploy)
        self.assertNotIn("$HOME/.ssh", deploy)
        self.assertNotIn("CERT_SHA256", deploy)
        self.assertNotIn('scp "${SSH_OPTS[@]}" -r "$SERVER_DIR/myproxy_server"', deploy)
        for module_name in (
            "__init__.py", "__main__.py", "admin_ui.py", "app.py", "auth.py",
            "config.py", "db.py", "observability.py", "release.py",
            "server.py", "xui.py",
            "xui_helper.py",
        ):
            self.assertIn(module_name, deploy)

        snapshot_ready = deploy.rindex("SNAPSHOT_READY=1")
        install_code = deploy.index('rm -rf "$REMOTE_DIR/myproxy_server"')
        acceptance = deploy.index('if [ "$HEALTH_STATUS" != "200" ]')
        for status_variable in (
            "ADMIN_UI_STATUS",
            "ADMIN_UI_SLASH_STATUS",
            "ADMIN_UI_JS_STATUS",
            "READY_PUBLIC_STATUS",
            "ADMIN_DOTDOT_STATUS",
        ):
            with self.subTest(status_variable=status_variable):
                self.assertIn(status_variable, deploy)
                self.assertIn(f'[ "${status_variable}" != "404" ]', deploy)
        for public_path in (
            "/admin", "/admin/", "/admin/app.js", "/readyz", "/api/admin/../device/claim"
        ):
            with self.subTest(public_path=public_path):
                self.assertIn(
                    f'"https://${{DEVICE_API_SERVER_NAME}}:${{DEVICE_API_PUBLIC_PORT}}{public_path}"',
                    deploy,
                )
        commit = deploy.index("DEPLOY_COMMITTED=1", acceptance)
        ufw = deploy.index(
            'ufw allow from 0.0.0.0/0 to any port '
            '"$DEVICE_API_PUBLIC_PORT" proto tcp'
        )
        self.assertLess(snapshot_ready, install_code)
        self.assertLess(install_code, acceptance)
        self.assertLess(acceptance, ufw)
        self.assertLess(ufw, commit)

    def test_deploy_rollback_restores_tls_before_start_and_uses_legacy_health(self) -> None:
        deploy = (Path(SERVER_DIR) / "deploy" / "deploy.sh").read_text(
            encoding="utf-8"
        )
        rollback = deploy[deploy.index("rollback_remote()") : deploy.index("finish_remote()")]
        restore = rollback.index("restore_tls_metadata")
        helper_start = rollback.index("systemctl start myproxy-xui-helper")
        api_start = rollback.index("systemctl start myproxy-api")
        self.assertLess(restore, helper_start)
        self.assertLess(restore, api_start)
        self.assertIn("https://127.0.0.1:1820/healthz", rollback)
        self.assertNotIn("https://127.0.0.1:1820/readyz", rollback)
        tls_restore = deploy[
            deploy.index("restore_tls_metadata()") : deploy.index("ufw_rule_present()")
        ]
        link_guard = tls_restore.index('if [ "$restore_status" -ne 0 ]')
        self.assertLess(link_guard, tls_restore.index('chown "$TLS_ROOT_OWNER"'))

    def test_deploy_keeps_state_root_only_until_migration_finishes(self) -> None:
        deploy = (Path(SERVER_DIR) / "deploy" / "deploy.sh").read_text(
            encoding="utf-8"
        )
        root_only = deploy.index('install -d -m 0700 -o root -g root "$STATE_DIR"')
        migration = deploy.index('OLD_DB="$REMOTE_DIR/myproxy.db"')
        cleanup = deploy.index(
            'rm -f -- "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-shm" "$REMOTE_DIR/myproxy.db-wal"'
        )
        handoff = deploy.index('chown myproxy:myproxy "$STATE_DIR"', migration)
        self.assertLess(root_only, migration)
        self.assertLess(migration, cleanup)
        self.assertLess(cleanup, handoff)

    def test_deploy_legacy_db_cleanup_is_after_durable_migration(self) -> None:
        deploy = (Path(SERVER_DIR) / "deploy" / "deploy.sh").read_text(
            encoding="utf-8"
        )
        migration = deploy.index('OLD_DB="$REMOTE_DIR/myproxy.db"')
        cleanup = deploy.index(
            'rm -f -- "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-shm" "$REMOTE_DIR/myproxy.db-wal"'
        )
        self.assertLess(migration, cleanup)
        for marker in (
            "source.backup(destination)",
            'PRAGMA integrity_check',
            "destination.commit()",
            "os.fsync(fd)",
            "src_dir_fd=directory_fd",
            "dst_dir_fd=directory_fd",
            "os.fsync(directory_fd)",
        ):
            self.assertLess(deploy.index(marker, migration), cleanup)
        self.assertLess(
            deploy.index('if [ "$DB_MIGRATED" -eq 1 ]'),
            cleanup,
        )

    def test_existing_admin_env_validation_never_rewrites_device_secret(self) -> None:
        deploy = (Path(SERVER_DIR) / "deploy" / "deploy.sh").read_text(
            encoding="utf-8"
        )
        start = deploy.index(
            'echo "==> [远程] 校验独立设备签名密钥'
        )
        end = deploy.index('if [ "$XUI_HELPER_ENV_CREATED" -eq 1 ]', start)
        validation = deploy[start:end]
        self.assertNotIn("import secrets", validation)
        self.assertNotIn("import tempfile", validation)
        self.assertNotIn("os.replace", validation)
        self.assertNotIn("mkstemp", validation)
        self.assertIn("raise SystemExit", validation)
        self.assertIn("admin.env contains a forbidden assignment", validation)
        self.assertIn(
            'allowed = {"MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET"}',
            validation,
        )
        self.assertIn("MYPROXY_DEVICE_TOKEN_SECRET=", deploy[:start])
        self.assertIn('DEVICE_TOKEN_SECRET="$(', deploy[:start])

    def test_nginx_listener_guard_covers_ipv4_ipv6_port_forms(self) -> None:
        installer = (
            Path(SERVER_DIR) / "deploy" / "install-device-api-proxy.sh"
        ).read_text(encoding="utf-8")
        self.assertIn(
            "listen[[:space:]]+([^[:space:];]+:)?80([[:space:];]|$)",
            installer,
        )
        # Exercise the equivalent ERE against the forms emitted by nginx -T.
        import re

        listener = re.compile(r"^\s*listen\s+([^\s;]+:)?80(?:[\s;]|$)")
        for line in (
            "listen 80;",
            "listen 0.0.0.0:80 default_server;",
            "listen 127.0.0.1:80;",
            "listen [::]:80 ssl;",
        ):
            with self.subTest(line=line):
                self.assertIsNotNone(listener.search(line))
        for line in ("listen 8080;", "listen 127.0.0.1:8080;", "listen [::]:8080;"):
            with self.subTest(line=line):
                self.assertIsNone(listener.search(line))

    def test_nginx_installer_arms_exit_restore_before_first_mutation(self) -> None:
        installer = (
            Path(SERVER_DIR) / "deploy" / "install-device-api-proxy.sh"
        ).read_text(encoding="utf-8")
        arm = installer.index("RESTORE_NEEDED=1\nrm -f -- \"$DEFAULT_SITE\"")
        first_mutation = installer.index('rm -f -- "$DEFAULT_SITE"', arm)
        self.assertLess(arm, first_mutation)
        self.assertLess(
            installer.index("if [ -f \"$TARGET\" ]; then"),
            arm,
        )
        cleanup = installer[installer.index("cleanup()") : arm]
        self.assertIn("set +e", cleanup)
        self.assertIn('cp -a -- "$BACKUP" "$TARGET"', cleanup)
        self.assertIn("systemctl reload nginx", cleanup)
        self.assertNotIn('install -m 0644 "$BACKUP"', installer)
        self.assertIn('if [ -L "$TARGET" ]; then', installer)
        self.assertIn(
            'if [ -e "$TARGET" ] && [ ! -f "$TARGET" ]; then',
            installer,
        )

    def test_run_selects_nonroot_socket_boundary(self) -> None:
        settings = Settings(
            admin_token="a" * 64,
            device_token_secret="b" * 64,
            xui_helper_socket="/run/myproxy/xui-helper.sock",
        )
        adapter = _build_xui_adapter(settings)
        self.assertIsInstance(adapter, UnixSocketXuiAdapter)
        self.assertEqual(adapter.socket_path, "/run/myproxy/xui-helper.sock")

    def test_run_rejects_direct_xui_fallback(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "XUI_HELPER_SOCKET"):
            _build_xui_adapter(Settings(admin_token="a" * 64, device_token_secret="b" * 64))

    def test_release_helpers_fail_closed_on_target_and_tls(self) -> None:
        repo_root = Path(SERVER_DIR).parent
        release_check = (repo_root / "scripts" / "release_api_test.py").read_text(
            encoding="utf-8"
        )
        pairing_helper = (repo_root / "scripts" / "create_pairing_code.py").read_text(
            encoding="utf-8"
        )

        self.assertIn("MYPROXY_DEPLOYMENT_CONFIG", release_check)
        self.assertIn("MYPROXY_RELEASE_TEST_CONFIRM", release_check)
        self.assertIn("def verified_connection", release_check)
        self.assertIn("ssl.create_default_context", release_check)
        self.assertIn("def require", release_check)
        self.assertIn("def validate_public_config", release_check)
        self.assertIn("def validate_config_response", release_check)
        self.assertIn("def validate_claim_response", release_check)
        self.assertIn("def validate_latest_response", release_check)
        self.assertIn("MYPROXY_CLIENT_VERSION", release_check)
        self.assertNotIn("CERT_SHA256", release_check)
        self.assertNotIn("assert ", release_check)
        self.assertLess(release_check.index("conn.connect()"), release_check.index("conn.request("))
        self.assertIn("verified_connection()", release_check)
        self.assertIn("MYPROXY_DEPLOY_CONFIRM", pairing_helper)
        self.assertIn("MYPROXY_VPS_HOST", pairing_helper)
        self.assertIn("MYPROXY_SSH_KEY", pairing_helper)
        self.assertIn("--admin-url", pairing_helper)
        self.assertIn("--username", pairing_helper)
        self.assertIn("ssl.create_default_context", pairing_helper)
        self.assertNotIn("_create_unverified_context", pairing_helper)

        spec = importlib.util.spec_from_file_location("release_api_test_behavior", repo_root / "scripts" / "release_api_test.py")
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        release_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(release_module)
        claim = {
            "deviceId": "dev_test",
            "deviceToken": "tok_test",
            "configVersion": 1,
            "config": {
                "server": "203.0.113.10",
                "port": 443,
                "uuid": "1f56c0eb-80aa-40c7-a89f-b0310369949d",
                "security": "reality",
                "publicKey": "public-key",
                "shortId": "0123456789abcdef",
                "sni": "example.com",
                "fingerprint": "chrome",
                "flow": "xtls-rprx-vision",
                "spiderX": "/",
            },
        }
        release_module.validate_claim_response(claim)
        invalid_claim = dict(claim)
        invalid_claim["deviceToken"] = ""
        with self.assertRaises(RuntimeError):
            release_module.validate_claim_response(invalid_claim)
        invalid_claim = dict(claim)
        invalid_claim["configVersion"] = True
        with self.assertRaises(RuntimeError):
            release_module.validate_claim_response(invalid_claim)
        claim["config"]["privateKey"] = "must-fail"
        with self.assertRaises(RuntimeError):
            release_module.validate_claim_response(claim)
        config_response = {
            "configVersion": 1,
            "config": dict(claim["config"]),
        }
        config_response["config"].pop("privateKey")
        release_module.validate_config_response(config_response, 1)
        config_response["config"]["privateKey"] = "must-fail"
        with self.assertRaises(RuntimeError):
            release_module.validate_config_response(config_response, 1)
        self.assertTrue(release_module.valid_host("203.0.113.10"))
        self.assertTrue(release_module.valid_host("api.example.test"))
        self.assertFalse(release_module.valid_host("bad host"))
        self.assertFalse(release_module.valid_host("api/example"))
        self.assertTrue(release_module.valid_version("0.1.0"))
        self.assertTrue(release_module.valid_version("12.3.40"))
        self.assertFalse(release_module.valid_version("0.1"))
        self.assertFalse(release_module.valid_version("01.2.3"))
        release_module.validate_latest_response(
            {"version": "0.1.0", "downloadUrl": "", "sha256": "", "mandatory": False}
        )
        with self.assertRaises(RuntimeError):
            release_module.validate_latest_response(
                {"version": "0.1.0", "downloadUrl": "", "sha256": "ABC", "mandatory": False}
            )

    def test_pairing_helper_gate_precedes_ssh_and_writes_secret_portably(self) -> None:
        script_path = Path(SERVER_DIR).parent / "scripts" / "create_pairing_code.py"
        spec = importlib.util.spec_from_file_location("create_pairing_code_test", script_path)
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        calls: list[list[str]] = []

        def fake_runner(command, **kwargs):
            calls.append(command)
            return type("Result", (), {"returncode": 0, "stdout": "ABCD-1234\n", "stderr": ""})()

        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "pairing_code.txt"
            with self.assertRaises(SystemExit):
                module.main(
                    [str(target), "60"],
                    env={"MYPROXY_VPS_HOST": "example.test", "MYPROXY_SSH_KEY": "key"},
                    runner=lambda *args, **kwargs: self.fail("SSH must not run before confirmation"),
                )
            self.assertEqual(calls, [])

            result = module.main(
                ["--output", str(target), "--ttl", "60", "--username", "test-user", "--admin-url", "https://127.0.0.1:1820"],
                env={
                    "MYPROXY_DEPLOY_CONFIRM": "YES",
                    "MYPROXY_VPS_HOST": "example.test",
                    "MYPROXY_SSH_KEY": "key",
                },
                runner=fake_runner,
            )
            self.assertEqual(result, 0)
            self.assertEqual(target.read_text(encoding="utf-8"), "ABCD-1234\n")
            self.assertIn("root@example.test", calls[0])
            self.assertIn("127.0.0.1:1820", module._remote_script(60, "test-user", "Test User", "windows", "https://127.0.0.1:1820", None))


if __name__ == "__main__":
    unittest.main()
