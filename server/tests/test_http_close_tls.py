"""Bounded TLS close regressions using only an ephemeral loopback server."""
from __future__ import annotations

import http.client
import json
from pathlib import Path
import queue
import ssl
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from test_api import FakeXuiAdapter, make_settings, stop_server
from test_admin_browser_bridge_tls import OPENSSL, ephemeral_certificate, x509
from myproxy_server.app import MyProxyService
from myproxy_server import server as backend


@unittest.skipUnless(x509 is not None or OPENSSL, "temporary TLS certificates need cryptography or OpenSSL")
class TlsCloseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        cert, key, _ = ephemeral_certificate(self.root)
        settings = make_settings(str(self.root / "test.db"), tls_cert=str(cert), tls_key=str(key))
        self.service = MyProxyService(settings, FakeXuiAdapter())
        self.service.initialize()
        self.logs = patch.object(backend, "_emit_structured")
        self.logs.start()
        self.httpd = backend.make_server(self.service, settings)
        self.drains = queue.Queue()
        observations = self.drains
        bound_handler = self.httpd.RequestHandlerClass

        class ObservedHandler(bound_handler):
            def _linger_unread_tls_body(handler):
                reader = handler.rfile
                sample = {"bytes": 0}

                class CountingReader:
                    def read1(self, size):
                        chunk = reader.read1(size)
                        sample["bytes"] += len(chunk)
                        return chunk

                handler.rfile = CountingReader()
                started = time.monotonic()
                try:
                    super()._linger_unread_tls_body()
                finally:
                    handler.rfile = reader
                    sample["seconds"] = time.monotonic() - started
                    observations.put(sample)

        self.httpd.RequestHandlerClass = ObservedHandler
        self.thread = threading.Thread(target=lambda: self.httpd.serve_forever(poll_interval=0.01), daemon=True)
        self.thread.start()

    def tearDown(self):
        stop_server(self.httpd, self.thread)
        self.logs.stop()
        self.directory.cleanup()

    def connection(self):
        return http.client.HTTPSConnection("127.0.0.1", self.httpd.server_port, timeout=3,
                                          context=ssl._create_unverified_context())

    def upload_headers(self, connection, length):
        connection.putrequest("POST", "/api/admin/release/rel_test/artifact")
        connection.putheader("Content-Type", "application/octet-stream")
        connection.putheader("Content-Length", str(length))
        connection.endheaders()

    def test_short_body_arriving_after_early_error_does_not_truncate_tls_response(self):
        for iteration in range(15):
            with self.subTest(iteration=iteration):
                connection = self.connection()
                try:
                    self.upload_headers(connection, 4)
                    time.sleep(0.01)
                    connection.send(b"late")
                    response = connection.getresponse()
                    self.assertEqual(response.status, 401)
                    self.assertEqual(response.getheader("Connection"), "close")
                    self.assertEqual(json.loads(response.read())["error"]["code"], "AdminUnauthorized")
                    drained = self.drains.get(timeout=1)
                    self.assertEqual(drained["bytes"], 4)
                    self.assertLess(drained["seconds"], 0.5)
                finally:
                    connection.close()

    def test_missing_large_body_gets_response_before_bounded_close_wait_ends(self):
        connection = self.connection()
        try:
            self.upload_headers(connection, 512 * 1024 * 1024)
            # Keep the peer socket alive after reading the close response, so
            # EOF cannot shorten the server's idle-body wait in this test.
            response = http.client.HTTPResponse(connection.sock)
            response.begin()
            self.assertEqual(response.status, 401)
            self.assertEqual(json.loads(response.read())["error"]["code"], "AdminUnauthorized")
            drained = self.drains.get(timeout=1)
            self.assertEqual(drained["bytes"], 0)
            self.assertGreaterEqual(drained["seconds"], 0.1)
            self.assertLess(drained["seconds"], 0.75)
        finally:
            connection.close()

    def test_large_rejected_body_is_discarded_only_up_to_byte_limit(self):
        connection = self.connection()
        try:
            self.upload_headers(connection, 512 * 1024 * 1024)
            # Enough bytes are already in TLS records to reach the discard
            # limit. The advertised package is never read in full.
            try:
                connection.send(b"x" * (2 * backend.TLS_BODY_LINGER_BYTES))
            except OSError:
                # The server may reach its byte budget while sendall still
                # writes the deliberately larger tail.
                pass
            drained = self.drains.get(timeout=1)
            self.assertEqual(drained["bytes"], backend.TLS_BODY_LINGER_BYTES)
            self.assertLess(drained["seconds"], 0.75)
        finally:
            connection.close()

    def test_bodyless_get_keeps_the_same_tls_connection_and_needs_no_linger(self):
        connection = self.connection()
        try:
            for _ in range(2):
                connection.request("GET", "/healthz")
                response = connection.getresponse()
                self.assertEqual(response.status, 200)
                response.read()
                if _ == 0:
                    socket = connection.sock
                else:
                    self.assertIs(connection.sock, socket)
                self.assertNotEqual(response.getheader("Connection"), "close")
        finally:
            connection.close()
        self.assertTrue(self.drains.empty())


if __name__ == "__main__":
    unittest.main()
