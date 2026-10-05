"""Real loopback TLS regressions for early errors and streamed package uploads.

Certificates and test private keys are generated temporarily. The tests need
either optional cryptography or an installed OpenSSL executable; no production
identity or external server is used.
"""
from __future__ import annotations

import datetime
import hashlib
import http.client
import importlib.util
import json
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

from test_api import FakeXuiAdapter, make_settings, stop_server
from test_update_plane import KEY_ID, SECRET, public_key, signed
from myproxy_server.app import MyProxyService
from myproxy_server import server as backend_module

try:
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID
except ImportError:
    x509 = None

OPENSSL = shutil.which("openssl")
SPEC = importlib.util.spec_from_file_location(
    "admin_browser_bridge_tls", Path(__file__).resolve().parents[1] / "deploy" / "admin_browser_bridge.py"
)
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


def ephemeral_certificate(root):
    cert_path, key_path = root / "certificate.pem", root / "key.pem"
    if x509 is not None:
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "ephemeral-loopback-test")])
        now = datetime.datetime.now(datetime.timezone.utc)
        certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
                       .public_key(private.public_key()).serial_number(x509.random_serial_number())
                       .not_valid_before(now - datetime.timedelta(minutes=1))
                       .not_valid_after(now + datetime.timedelta(minutes=10))
                       .sign(private, hashes.SHA256()))
        cert_path.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
        key_path.write_bytes(private.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                                  serialization.NoEncryption()))
    else:
        subprocess.run([OPENSSL, "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                        "-keyout", str(key_path), "-out", str(cert_path), "-days", "1",
                        "-subj", "/CN=ephemeral-loopback-test"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True, timeout=30)
    key_path.chmod(0o600)
    pin = hashlib.sha256(ssl.PEM_cert_to_DER_cert(cert_path.read_text())).hexdigest()
    return cert_path, key_path, pin


@unittest.skipUnless(x509 is not None or OPENSSL, "temporary TLS certificate generation needs cryptography or OpenSSL")
class BridgeRealTlsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        cert, key, self.pin = ephemeral_certificate(self.root)
        self.settings = make_settings(str(self.root / "test.db"), tls_cert=str(cert), tls_key=str(key),
                                      release_signing_keys=f"{KEY_ID}:{public_key(SECRET).hex()}")
        self.service = MyProxyService(self.settings, FakeXuiAdapter())
        self.service.initialize()
        self.log_patch = patch.object(backend_module, "_emit_structured")
        self.events = self.log_patch.start()
        self.backend = backend_module.make_server(self.service, self.settings)
        self.backend_thread = threading.Thread(target=lambda: self.backend.serve_forever(poll_interval=0.01), daemon=True)
        self.backend_thread.start()
        self.proxy = bridge.BridgeServer(0, self.backend.server_port, self.pin)
        self.proxy_thread = threading.Thread(target=lambda: self.proxy.serve_forever(poll_interval=0.01), daemon=True)
        self.proxy_thread.start()

    def tearDown(self):
        stop_server(self.proxy, self.proxy_thread)
        stop_server(self.backend, self.backend_thread)
        self.log_patch.stop()
        self.directory.cleanup()

    def request(self, method, path, *, body=b"", length=None, authorization=False, target=None):
        target = target or self.proxy
        connection = http.client.HTTPConnection("127.0.0.1", target.server_port, timeout=8)
        try:
            connection.putrequest(method, path, skip_host=True)
            connection.putheader("Host", target.expected_host)
            connection.putheader("Content-Type", "application/octet-stream")
            connection.putheader("Content-Length", str(len(body) if length is None else length))
            if authorization:
                connection.putheader("Authorization", "Bearer " + self.settings.admin_token)
            connection.endheaders(body or None)
            response = connection.getresponse()
            # Reading the whole body also rejects a truncated early TLS error.
            return response.status, response.read()
        finally:
            connection.close()

    def test_early_unauthorized_response_is_complete_with_small_or_absent_upload_body(self):
        path = "/api/admin/release/rel_test/artifact"
        # Repetition exposes the TLS scheduling race which plain HTTP mocks
        # cannot reproduce. No package bytes are required before authentication.
        for iteration in range(30):
            with self.subTest(iteration=iteration):
                status, raw = self.request("POST", path, body=b"1234")
                self.assertEqual(status, 401)
                self.assertEqual(json.loads(raw)["error"]["code"], "AdminUnauthorized")
        for iteration in range(5):
            with self.subTest(absent_body=iteration):
                status, raw = self.request("POST", path, length=bridge.MAX_BODY)
                self.assertEqual(status, 401)
                self.assertEqual(json.loads(raw)["error"]["code"], "AdminUnauthorized")

    def test_streamed_package_matches_registered_size_and_sha256(self):
        payload = bytes(range(256)) * (8 * 1024 * 1024 // 256)
        digest = hashlib.sha256(payload).hexdigest()
        for iteration in range(2):
            version = f"9.1.{iteration}"
            record = {"platform": "windows", "version": version, "artifactSha256": digest}
            manifest, signature = signed(version=version, artifact={
                "url": self.service.artifacts.url(record), "sha256": digest, "size": len(payload),
                "signature": {"type": "authenticode", "subjectSha256": "b" * 64},
            })
            release = self.service.admin_register_release(manifest, signature, KEY_ID)
            status, raw = self.request("POST", f"/api/admin/release/{release['id']}/artifact",
                                       body=payload, authorization=True)
            self.assertEqual(status, 200)
            self.assertTrue(json.loads(raw)["artifactReady"])
            artifact = self.service.artifacts.root / digest / f"myproxy-windows-{version}.zip"
            self.assertEqual(artifact.stat().st_size, len(payload))
            self.assertEqual(hashlib.sha256(artifact.read_bytes()).hexdigest(), digest)

    def test_wrong_certificate_pin_prevents_authenticated_http_request(self):
        wrong = bridge.BridgeServer(0, self.backend.server_port, "0" * 64)
        thread = threading.Thread(target=lambda: wrong.serve_forever(poll_interval=0.01), daemon=True)
        thread.start()
        before = self.events.call_count
        try:
            status, raw = self.request("POST", "/api/admin/release/rel_test/artifact", body=b"package",
                                       authorization=True, target=wrong)
            self.assertEqual(status, 502)
            self.assertEqual(json.loads(raw)["error"]["code"], "BridgeRejected")
            self.assertEqual(self.events.call_count, before)
        finally:
            stop_server(wrong, thread)


if __name__ == "__main__":
    unittest.main()
