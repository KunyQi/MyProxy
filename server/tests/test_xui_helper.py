"""Unit tests for the least-privilege x-ui Unix-socket boundary."""

from __future__ import annotations

import json
import os
import socketserver
import struct
import sys
import threading
import unittest
from types import SimpleNamespace
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from myproxy_server import xui_helper  # noqa: E402
from myproxy_server.xui import (  # noqa: E402
    XUI_ERROR_CODES,
    UnixSocketXuiAdapter,
    XuiError,
    is_myproxy_email,
    new_myproxy_email,
)

MANAGED_EMAIL = "myproxy-dev_111111111111111111111111-aaaaaaaa"


class _FakeAdapter:
    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def public_profile(self) -> dict:
        self.calls.append(("public_profile",))
        return {"server": "example.invalid"}

    def list_clients(self) -> list[dict]:
        self.calls.append(("list_clients",))
        return [
            {"email": "user1"},
            {"email": "myproxy-device@example.invalid"},
            {"email": MANAGED_EMAIL},
        ]

    def add_client(self, email: str) -> str:
        self.calls.append(("add_client", email))
        return "client-id"

    def remove_client(self, email: str) -> None:
        self.calls.append(("remove_client", email))

    def update_client(self, email: str, **values) -> None:
        self.calls.append(("update_client", email, values))


class _FakeSocket:
    def __init__(self, response: bytes) -> None:
        self.response = response
        self.sent = b""
        self.timeout = None
        self.connected = None

    def __enter__(self):
        return self

    def __exit__(self, *_args) -> None:
        return None

    def settimeout(self, value: float) -> None:
        self.timeout = value

    def connect(self, path: str) -> None:
        self.connected = path

    def sendall(self, payload: bytes) -> None:
        self.sent += payload

    def recv(self, _size: int) -> bytes:
        response, self.response = self.response, b""
        return response


class XuiHelperBoundaryTests(unittest.TestCase):
    def test_generated_email_matches_only_the_exact_managed_namespace(self) -> None:
        with mock.patch("myproxy_server.xui.secrets.token_hex", return_value="abcdef01"):
            generated = new_myproxy_email("dev_0123456789abcdef01234567")
        self.assertEqual(
            generated,
            "myproxy-dev_0123456789abcdef01234567-abcdef01",
        )
        self.assertTrue(is_myproxy_email(generated))
        for outside in (
            "user1",
            "myproxy-user1",
            "myproxy-device@example.invalid",
            "myproxy-dev_0123456789abcdef01234567-ABCDEF01",
        ):
            with self.subTest(outside=outside):
                self.assertFalse(is_myproxy_email(outside))
        with self.assertRaises(XuiError):
            new_myproxy_email("dev_not-a-real-id")

    def test_dispatch_exposes_only_allowlisted_operations(self) -> None:
        adapter = _FakeAdapter()
        self.assertEqual(
            xui_helper._dispatch(adapter, {"op": "public_profile"}),
            {"server": "example.invalid"},
        )
        self.assertEqual(
            xui_helper._dispatch(adapter, {"op": "list_clients"}),
            [{"email": MANAGED_EMAIL}],
        )
        self.assertEqual(
            xui_helper._dispatch(adapter, {"op": "add_client", "email": MANAGED_EMAIL}),
            "client-id",
        )
        xui_helper._dispatch(
            adapter,
            {
                "op": "update_client",
                "email": MANAGED_EMAIL,
                "enable": False,
                "limit_ip": 1,
                "total_gb": 2,
                "expiry_time": 3,
            },
        )
        xui_helper._dispatch(adapter, {"op": "remove_client", "email": MANAGED_EMAIL})
        self.assertEqual(
            [call[0] for call in adapter.calls],
            ["public_profile", "list_clients", "add_client", "update_client", "remove_client"],
        )

    def test_error_code_vocabulary_is_closed(self) -> None:
        """Only allow-listed outcome codes may cross the privilege boundary."""
        self.assertEqual(XuiError("x", code="client_not_found").code, "client_not_found")
        self.assertIsNone(XuiError("x").code)
        self.assertIsNone(XuiError("x", code="restart_failed").code)
        self.assertIsNone(XuiError("x", code="/etc/x-ui/x-ui.db").code)
        self.assertEqual(XUI_ERROR_CODES, frozenset({"client_not_found"}))

    def test_helper_forwards_only_allowlisted_error_codes(self) -> None:
        self.assertEqual(
            xui_helper._safe_error_code(XuiError("client not found", code="client_not_found")),
            "client_not_found",
        )
        self.assertIsNone(xui_helper._safe_error_code(XuiError("inbound not found")))
        self.assertIsNone(xui_helper._safe_error_code(ValueError("sqlite3: no such table")))

        replies: list[bytes] = []
        handler = xui_helper._Handler.__new__(xui_helper._Handler)
        handler.wfile = SimpleNamespace(
            write=replies.append, flush=lambda: None
        )
        handler._reply(False, code="client_not_found")
        handler._reply(False)
        handler._reply(True, "client-id")
        payloads = [json.loads(raw.decode("utf-8")) for raw in replies]
        self.assertEqual(payloads[0], {"ok": False, "code": "client_not_found"})
        self.assertEqual(payloads[1], {"ok": False})
        self.assertEqual(payloads[2], {"ok": True, "result": "client-id"})

    def test_socket_adapter_restores_the_code_from_a_rejected_reply(self) -> None:
        """The generic message is unmatchable; the code is what callers use."""
        adapter = UnixSocketXuiAdapter.__new__(UnixSocketXuiAdapter)
        adapter.socket_path = "/run/myproxy/xui-helper.sock"
        adapter.timeout = 1.0

        def reply(payload: dict):
            # AF_UNIX is absent on Windows hosts; the protocol under test is
            # the JSON reply, not the transport.
            with mock.patch("socket.AF_UNIX", 1, create=True),                     mock.patch("socket.socket") as factory:
                factory.return_value = _FakeSocket(
                    json.dumps(payload).encode("utf-8") + b"\n"
                )
                with self.assertRaises(XuiError) as caught:
                    adapter._call("update_client", email=MANAGED_EMAIL)
            return caught.exception

        self.assertEqual(reply({"ok": False, "code": "client_not_found"}).code, "client_not_found")
        self.assertIsNone(reply({"ok": False, "code": "inbound_not_found"}).code)
        self.assertIsNone(reply({"ok": False}).code)
        self.assertIsNone(reply({"ok": False, "code": 42}).code)

    def test_dispatch_rejects_unknown_or_malformed_parameters(self) -> None:
        adapter = _FakeAdapter()
        invalid = (
            {"op": "restart_xui"},
            {"op": "add_client", "email": ""},
            {"op": "add_client", "email": "x" * 257},
            {"op": "add_client", "email": "user1"},
            {"op": "add_client", "email": "myproxy-a@example.invalid"},
            {"op": "update_client", "email": "user1", "enable": False},
            {"op": "update_client", "email": "myproxy-dev_short-deadbeef", "enable": False},
            {"op": "remove_client", "email": "user1"},
            {"op": "remove_client", "email": "myproxy-device@example.invalid"},
            {"op": "update_client", "email": "a", "enable": "yes"},
            {"op": "update_client", "email": "a", "limit_ip": True},
            {"op": "update_client", "email": "a", "total_gb": -1},
        )
        for request in invalid:
            with self.subTest(request=request), self.assertRaises(ValueError):
                xui_helper._dispatch(adapter, request)
        self.assertEqual(adapter.calls, [])

    def test_half_sent_request_times_out_without_dispatch(self) -> None:
        class TimedOutReader:
            def readline(self, _limit: int) -> bytes:
                raise TimeoutError("simulated half packet")

        adapter = _FakeAdapter()
        handler = xui_helper._Handler.__new__(xui_helper._Handler)
        handler.adapter = adapter
        handler.operation_lock = threading.Lock()
        handler.rfile = TimedOutReader()
        handler._authorized_peer = lambda: True

        handler.handle()

        self.assertEqual(adapter.calls, [])

    def test_handler_setup_applies_finite_read_timeout(self) -> None:
        handler = xui_helper._Handler.__new__(xui_helper._Handler)
        request = mock.Mock()
        handler.request = request
        with mock.patch.object(socketserver.StreamRequestHandler, "setup"):
            handler.setup()
        request.settimeout.assert_called_once_with(xui_helper.REQUEST_READ_TIMEOUT_SECONDS)

    def test_server_rejects_connections_over_bound_before_thread_creation(self) -> None:
        if not hasattr(socketserver, "ThreadingUnixStreamServer"):
            self.skipTest("Unix stream server unavailable")
        server = xui_helper._Server.__new__(xui_helper._Server)
        server.max_concurrent = 1
        server._connection_slots = threading.BoundedSemaphore(1)
        first = mock.Mock()
        second = mock.Mock()

        with mock.patch.object(socketserver.ThreadingMixIn, "process_request") as parent:
            server.process_request(first, None)
            server.process_request(second, None)
            self.assertEqual(parent.call_count, 1)
            second.close.assert_called_once_with()

            # A completed worker releases its slot, allowing the next request
            # through without increasing the configured bound.
            with mock.patch.object(socketserver.ThreadingMixIn, "process_request_thread"):
                server.process_request_thread(first, None)
            server.process_request(second, None)
            self.assertEqual(parent.call_count, 2)

    def test_peer_authorization_requires_exact_myproxy_uid(self) -> None:
        handler = xui_helper._Handler.__new__(xui_helper._Handler)
        expected_uid = 1234
        fake_pwd = SimpleNamespace(
            getpwnam=lambda name: SimpleNamespace(pw_uid=expected_uid)
            if name == "myproxy"
            else None
        )

        class Peer:
            uid = expected_uid

            def getsockopt(self, *_args):
                return struct.pack("3i", 99, self.uid, 5678)

        handler.request = Peer()
        with mock.patch.object(xui_helper, "pwd", fake_pwd), mock.patch.object(
            xui_helper.socket, "SO_PEERCRED", 17, create=True
        ):
            self.assertTrue(handler._authorized_peer())
            handler.request.uid = expected_uid + 1
            self.assertFalse(handler._authorized_peer())

    def test_socket_adapter_sends_one_bounded_json_operation(self) -> None:
        connection = _FakeSocket(b'{"ok":true,"result":"client-id"}\n')
        with mock.patch.object(
            sys.modules["myproxy_server.xui"].socket, "AF_UNIX", 1, create=True
        ), mock.patch("myproxy_server.xui.socket.socket", return_value=connection):
            adapter = UnixSocketXuiAdapter("/run/myproxy/xui-helper.sock", timeout=4.0)
            self.assertEqual(adapter.add_client(MANAGED_EMAIL), "client-id")
        self.assertEqual(connection.connected, "/run/myproxy/xui-helper.sock")
        self.assertEqual(connection.timeout, 4.0)
        self.assertTrue(connection.sent.endswith(b"\n"))
        self.assertEqual(
            json.loads(connection.sent.decode("utf-8")),
            {"op": "add_client", "email": MANAGED_EMAIL},
        )


if __name__ == "__main__":
    unittest.main()
