"""Local-only security and streaming checks for the optional browser bridge."""
from __future__ import annotations

from email.message import Message
import hashlib
import http.client
import importlib.util
import io
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import socket
import ssl
import subprocess
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


_SPEC = importlib.util.spec_from_file_location(
    "admin_browser_bridge", Path(__file__).resolve().parents[1] / "deploy" / "admin_browser_bridge.py"
)
bridge = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(bridge)


def headers(*pairs):
    result = Message()
    for name, value in pairs:
        result[name] = value
    return result


class FakeResponse:
    status = 200

    def __init__(self, body=b"local response", extra_headers=()):
        self.body = io.BytesIO(body)
        self.headers = [("Content-Type", "text/plain"), ("Content-Length", str(len(body))),
                        ("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none'"),
                        ("X-Frame-Options", "DENY"), ("Referrer-Policy", "no-referrer"),
                        ("Cache-Control", "no-store"), *extra_headers]

    def read(self, count):
        return self.body.read(count)

    def getheaders(self):
        return self.headers


class FakeConnection:
    def __init__(self, response=None, reject_pin=False):
        self.response = response or FakeResponse()
        self.reject_pin = reject_pin
        self.events = []
        self.headers = {}
        self.chunks = []
        self.sock = SimpleNamespace(sendall=self.send, shutdown=lambda _: None, settimeout=lambda _: None)
        self.body_finished = threading.Event()
        self.closed = threading.Event()

    def connect(self):
        self.events.append("pin")
        if self.reject_pin:
            raise ssl.SSLCertVerificationError("test pin rejected")

    def putrequest(self, method, path, **kwargs):
        self.events.append("request")
        self.method, self.path = method, path
        self.assert_encoding = kwargs["skip_accept_encoding"]

    def putheader(self, name, value):
        self.headers[name] = value

    def endheaders(self):
        self.events.append("headers")

    def send(self, data):
        self.events.append("body")
        self.chunks.append(data)
        if sum(map(len, self.chunks)) == int(self.headers.get("Content-Length", "0")):
            self.body_finished.set()

    def request(self, method, path, body, headers):
        self.events.append("request")
        self.method, self.path, self.body, self.headers = method, path, body, headers

    def getresponse(self):
        if int(self.headers.get("Content-Length", "0")):
            if not self.body_finished.wait(3):
                raise TimeoutError("Test body was not sent")
        return self.response

    def close(self):
        self.events.append("close")
        self.closed.set()


class BridgeValidationTests(unittest.TestCase):
    def test_host_origin_and_body_framing_are_strict(self):
        expected = "127.0.0.1:32100"
        self.assertEqual(bridge.request_length(headers(("Host", expected)), "GET", expected), 0)
        self.assertEqual(bridge.request_length(headers(("Host", expected), ("Origin", "http://" + expected),
                                                      ("Content-Length", str(bridge.MAX_BODY))), "POST", expected),
                         bridge.MAX_BODY)
        invalid = [
            [], [("Host", "localhost:32100")], [("Host", "127.0.0.1:32101")],
            [("Host", expected), ("Host", expected)],
            [("Host", expected), ("Origin", "https://evil.example")],
            [("Host", expected), ("Origin", "null")],
            [("Host", expected), ("Origin", "http://" + expected), ("Origin", "http://" + expected)],
            [("Host", expected), ("Transfer-Encoding", "chunked")],
            [("Host", expected), ("Expect", "100-continue")],
            [("Host", expected), ("Content-Length", "1"), ("Content-Length", "1")],
            [("Host", expected), ("Content-Length", "-1")],
            [("Host", expected), ("Content-Length", "01")],
            [("Host", expected), ("Content-Length", str(bridge.MAX_BODY + 1))],
            [("Host", expected), ("Authorization", "Bearer one"), ("Authorization", "Bearer two")],
            [("Host", expected), ("Authorization", "Bearer bad\r\nInjected: value")],
        ]
        for pairs in invalid:
            with self.subTest(pairs=pairs), self.assertRaises(ValueError):
                bridge.request_length(headers(*pairs), "POST", expected)
        with self.assertRaises(ValueError):
            bridge.request_length(headers(("Host", expected), ("Content-Length", "1")), "GET", expected)

    def test_only_canonical_admin_and_health_paths_are_allowed(self):
        for path in [*bridge.PUBLIC_PATHS, "/api/admin/release/7/artifact", "/api/admin/device/list?page=2",
                     "/api/admin/releases?q=1.2.3%2Bbuild"]:
            with self.subTest(path=path):
                self.assertTrue(bridge.safe_path(path))
        for path in ["http://evil.example/admin", "//evil.example/admin", "/api/device/list", "/", "/api/admin",
                     "/admin/other.js", "/api/admin/../device", "/api/admin/./list", "/api/admin//list",
                     "/api/admin/%2e%2e/device", "/api/admin/%252e/list", "/admin\\session",
                     "/admin/#fragment", "/api/admin/list?x=%0d%0aHost", "/api/admin/list?x=%oops",
                     "/api/admin/list\x00", "/api/admin/" + "x" * 8192]:
            with self.subTest(path=path):
                self.assertFalse(bridge.safe_path(path))

    def test_certificate_pin_is_checked_on_each_tls_connection(self):
        certificate = b"test certificate DER"
        pin = hashlib.sha256(certificate).hexdigest()
        for received, accepted in [(certificate, True), (b"different DER", False), (None, False)]:
            connection = bridge.PinnedConnection(1234, pin.upper())
            sock = Mock()
            sock.getpeercert.return_value = received
            def connected(instance):
                instance.sock = sock
            with self.subTest(accepted=accepted), patch.object(http.client.HTTPSConnection, "connect", connected):
                if accepted:
                    connection.connect()
                    sock.getpeercert.assert_called_once_with(binary_form=True)
                    self.assertIs(connection.sock, sock)
                    connection.close()
                else:
                    with self.assertRaises(ssl.SSLCertVerificationError):
                        connection.connect()
                    self.assertIsNone(connection.sock)
                sock.close.assert_called_once()

    def test_upload_response_checks_complete_length_and_bounded_size(self):
        cases = [(b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 4\r\n\r\nx", http.client.IncompleteRead),
                 (b"HTTP/1.1 200 OK\r\n\r\n" + b"x" * bridge.MAX_UPLOAD_RESPONSE, http.client.HTTPException)]
        for raw_response, expected_error in cases:
            upstream, backend = socket.socketpair()
            local, client = socket.socketpair()
            def respond():
                try:
                    backend.sendall(raw_response)
                except OSError:
                    pass
                finally:
                    backend.close()
            thread = threading.Thread(target=respond, daemon=True)
            thread.start()
            try:
                with self.subTest(expected_error=expected_error), self.assertRaises(expected_error):
                    bridge.upload_response(upstream, io.BytesIO(b"package"), local, 7, "POST")
            finally:
                upstream.close()
                local.close()
                client.close()
                thread.join(3)

    def test_ssh_uses_only_the_restricted_token_command_and_strict_identity(self):
        args = SimpleNamespace(host="server.example", ssh_user="myproxy-admin", ssh_key=Path("key"),
                               known_hosts=Path("hosts"))
        token = b"test-administrator-token"
        with patch.object(bridge.shutil, "which", return_value="ssh"), patch.object(bridge.subprocess, "run") as run:
            run.return_value = subprocess.CompletedProcess([], 0, token, b"ignored")
            self.assertEqual(bridge.ssh_token(args), token.decode())
            command = run.call_args.args[0]
            self.assertEqual(command[-2:], ["myproxy-admin@server.example", "token"])
            self.assertIn("StrictHostKeyChecking=yes", command)
            self.assertIn("IdentitiesOnly=yes", command)
            self.assertIn("UserKnownHostsFile=hosts", command)
            self.assertIn("GlobalKnownHostsFile=none", command)
            self.assertIn("PasswordAuthentication=no", command)
            self.assertEqual(command[1:4], ["-F", "none", "-T"])
            self.assertNotIn("-L", command)
            self.assertNotIn("-N", command)
            self.assertEqual(run.call_args.kwargs, {"capture_output": True, "timeout": 30, "check": False})

    def test_ui_ticket_bootstrap_pins_first_and_returns_only_ticket(self):
        ticket = "A" * 43
        connection = FakeConnection(FakeResponse(json.dumps({"ticket": ticket}).encode()))
        with patch.object(bridge, "PinnedConnection", return_value=connection):
            self.assertEqual(bridge.ui_ticket(1234, "0" * 64, "test-bearer"), ticket)
        self.assertEqual(connection.events, ["pin", "request", "close"])
        self.assertEqual((connection.method, connection.path), ("POST", "/api/admin/ui-ticket"))
        self.assertEqual(connection.headers["Authorization"], "Bearer test-bearer")
        for document in [[], {}, {"ticket": "bad"}]:
            connection = FakeConnection(FakeResponse(json.dumps(document).encode()))
            with patch.object(bridge, "PinnedConnection", return_value=connection), self.assertRaises(RuntimeError):
                bridge.ui_ticket(1234, "0" * 64, "test-bearer")
            self.assertEqual(connection.events[-1], "close")

    def test_no_browser_main_prints_ticket_url_without_bearer_or_tunnel_process(self):
        args = SimpleNamespace(listen_port=0, upstream_port=1234, tls_sha256="0" * 64, no_browser=True)
        server = Mock(expected_host="127.0.0.1:32100")
        server.serve_forever.side_effect = KeyboardInterrupt
        output = io.StringIO()
        with patch.object(bridge, "arguments", return_value=args), patch.object(bridge, "BridgeServer", return_value=server), \
             patch.object(bridge, "ssh_token", return_value="secret-admin-bearer"), \
             patch.object(bridge, "ui_ticket", return_value="A" * 43), \
             patch.object(bridge.webbrowser, "open") as browser, patch("sys.stdout", output):
            self.assertEqual(bridge.main([]), 0)
        self.assertEqual(output.getvalue(), "http://127.0.0.1:32100/admin/#ticket=" + "A" * 43 + "\n")
        self.assertNotIn("secret-admin-bearer", output.getvalue())
        browser.assert_not_called()
        server.server_close.assert_called_once()

    def test_cli_checks_key_metadata_without_reading_private_key(self):
        with tempfile.TemporaryDirectory() as directory:
            key, hosts = Path(directory) / "key", Path(directory) / "hosts"
            key.write_bytes(b"private placeholder")
            hosts.write_bytes(b"known host placeholder")
            argv = ["--host", "server.example", "--ssh-user", "myproxy-admin", "--ssh-key", str(key),
                    "--known-hosts", str(hosts), "--upstream-port", "31820", "--tls-sha256", "a" * 64,
                    "--no-browser"]
            with patch.object(Path, "read_bytes", side_effect=AssertionError("private key must not be read")), \
                 patch.object(Path, "read_text", side_effect=AssertionError("private key must not be read")):
                args = bridge.arguments(argv)
            self.assertEqual(args.listen_port, 0)
            self.assertTrue(args.no_browser)


class BridgeRelayTests(unittest.TestCase):
    def setUp(self):
        self.connections = []
        self.reject_pin = False
        self.server = bridge.BridgeServer(0, 1234, "0" * 64)
        def upstream():
            connection = FakeConnection(reject_pin=self.reject_pin)
            self.connections.append(connection)
            return connection
        self.server.upstream = upstream
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.01), daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def test_anonymous_requests_stay_anonymous_and_security_headers_survive(self):
        status, response_headers, body = self.request("GET", "/admin/")
        self.assertEqual(status, 200)
        self.assertEqual(body, b"local response")
        upstream = self.connections[0]
        self.assertNotIn("Authorization", upstream.headers)
        self.assertEqual(upstream.events, ["pin", "request", "headers", "close"])
        self.assertEqual(response_headers["Content-Security-Policy"], "default-src 'self'; frame-ancestors 'none'")
        self.assertEqual(response_headers["Referrer-Policy"], "no-referrer")
        self.assertEqual(response_headers["X-Frame-Options"], "DENY")

    def test_browser_authorization_is_forwarded_but_origin_and_other_headers_are_not(self):
        self.request("GET", "/api/admin/releases", headers={
            "Authorization": "Bearer browser-session", "Origin": "http://" + self.server.expected_host,
            "Cookie": "do-not-forward", "X-Forwarded-Host": "evil.example"})
        upstream = self.connections[0]
        self.assertEqual(upstream.headers["Authorization"], "Bearer browser-session")
        for name in ("Origin", "Cookie", "X-Forwarded-Host"):
            self.assertNotIn(name, upstream.headers)

    def test_upload_is_streamed_and_content_length_is_canonical(self):
        body = b"x" * (bridge.CHUNK_SIZE * 2 + 53)
        chunks, received_headers = [], []
        class ReceiveUpload(BaseHTTPRequestHandler):
            def do_POST(self):
                received_headers.append(self.headers)
                remaining = int(self.headers["Content-Length"])
                while remaining:
                    data = self.rfile.read(min(bridge.CHUNK_SIZE, remaining))
                    chunks.append(data)
                    remaining -= len(data)
                self.send_response(200)
                self.send_header("Content-Length", "0")
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True
            def log_message(self, *args):
                pass
        backend = ThreadingHTTPServer(("127.0.0.1", 0), ReceiveUpload)
        backend_thread = threading.Thread(target=lambda: backend.serve_forever(poll_interval=0.01), daemon=True)
        backend_thread.start()
        self.server.upstream = lambda: http.client.HTTPConnection("127.0.0.1", backend.server_port, timeout=3)
        try:
            self.assertEqual(self.request("POST", "/api/admin/release/7/artifact", body,
                                         {"Content-Type": "application/octet-stream"})[0], 200)
            self.assertEqual(b"".join(chunks), body)
            self.assertLessEqual(max(map(len, chunks)), bridge.CHUNK_SIZE)
            self.assertGreater(len(chunks), 1)
            self.assertEqual(received_headers[0]["Content-Length"], str(len(body)))
        finally:
            backend.shutdown()
            backend.server_close()
            backend_thread.join(3)

    def test_rejected_requests_do_not_contact_upstream(self):
        requests = [("GET", "/admin/", {"Host": "localhost:" + str(self.server.server_port)}),
                    ("GET", "/admin/", {"Origin": "https://evil.example"}),
                    ("GET", "/api/device/list", {}), ("POST", "/admin/app.js", {}),
                    ("GET", "/api/admin/%2e%2e/list", {})]
        for method, path, request_headers in requests:
            with self.subTest(path=path, headers=request_headers):
                self.assertEqual(self.request(method, path, headers=request_headers)[0], 400)
        self.assertEqual(self.connections, [])

    def test_duplicate_length_and_chunked_are_rejected_before_upstream(self):
        for framing in [b"Content-Length: 1\r\nContent-Length: 1", b"Transfer-Encoding: chunked"]:
            with socket.create_connection(("127.0.0.1", self.server.server_port), timeout=3) as client:
                client.sendall(b"POST /api/admin/release/1/artifact HTTP/1.1\r\nHost: " +
                               self.server.expected_host.encode() + b"\r\n" + framing + b"\r\n\r\n")
                response = client.recv(4096)
                self.assertTrue(response.startswith(b"HTTP/1.1 400"), response)
        self.assertEqual(self.connections, [])

    def test_pin_failure_never_sends_http_headers_or_artifact_bytes(self):
        self.reject_pin = True
        self.assertEqual(self.request("POST", "/api/admin/release/7/artifact", b"sensitive-package")[0], 502)
        upstream = self.connections[0]
        self.assertTrue(upstream.closed.wait(1))
        self.assertEqual(upstream.events, ["pin", "close"])
        self.assertEqual(upstream.headers, {})
        self.assertEqual(upstream.chunks, [])

    def test_early_backend_401_is_relayed_before_upload_body_arrives(self):
        class EarlyAuthenticationFailure(BaseHTTPRequestHandler):
            def do_POST(self):
                self.send_response(401)
                self.send_header("Content-Length", "0")
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True
            def log_message(self, *args):
                pass
        backend = ThreadingHTTPServer(("127.0.0.1", 0), EarlyAuthenticationFailure)
        backend_thread = threading.Thread(target=lambda: backend.serve_forever(poll_interval=0.01), daemon=True)
        backend_thread.start()
        self.server.upstream = lambda: http.client.HTTPConnection("127.0.0.1", backend.server_port, timeout=3)
        try:
            for partial_body in (b"", b"x" * bridge.CHUNK_SIZE):
                with self.subTest(bytes_sent=len(partial_body)), \
                     socket.create_connection(("127.0.0.1", self.server.server_port), timeout=3) as client:
                    client.sendall(b"POST /api/admin/release/7/artifact HTTP/1.1\r\nHost: " +
                                   self.server.expected_host.encode() + b"\r\nContent-Length: 536870912\r\n\r\n" + partial_body)
                    # An absent or partial body cannot delay the early response
                    # until a large package has been buffered or drained.
                    response = client.recv(4096)
                    self.assertTrue(response.startswith(b"HTTP/1.1 401"), response)
        finally:
            backend.shutdown()
            backend.server_close()
            backend_thread.join(3)


if __name__ == "__main__":
    unittest.main()
