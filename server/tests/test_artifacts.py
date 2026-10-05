"""Release uploads use temporary storage and local HTTP, never system paths."""

import base64
from dataclasses import replace
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import re
import socket
import sys
import tempfile
import threading
import unittest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from myproxy_server import artifact
from myproxy_server.app import MyProxyService, ServiceError
from myproxy_server.server import make_server
from test_api import FakeXuiAdapter, make_settings, stop_server
import test_update_plane as vectors
from test_update_plane import KEY_ID, OTHER_SECRET, SECRET, public_key, sign, signed


class ArtifactFixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.settings = make_settings(
            str(Path(self.tmp.name) / "test.db"),
            release_signing_keys=f"{KEY_ID}:{public_key(SECRET).hex()}",
        )
        self.service = MyProxyService(self.settings, FakeXuiAdapter())
        self.service.initialize()

    def register(self, data=b"signed package bytes", platform="windows", version="1.2.3", **overrides):
        digest = hashlib.sha256(data).hexdigest()
        record = {"platform": platform, "version": version, "artifactSha256": digest}
        url = self.service.artifacts.url(record)
        signature_type = {"windows": "authenticode", "android": "apksigner", "linux": "ed25519"}[platform]
        subject = hashlib.sha256(public_key(SECRET)).hexdigest() if platform == "linux" else "b" * 64
        values = {"url": url, "sha256": digest, "size": len(data),
                  "signature": {"type": signature_type, "subjectSha256": subject}}
        values.update(overrides)
        manifest, signature = signed(platform=platform, version=version, artifact=values)
        return self.service.admin_register_release(manifest, signature, KEY_ID)

    def assert_error(self, code, callback, *args, **kwargs):
        with self.assertRaises(ServiceError) as caught:
            callback(*args, **kwargs)
        self.assertEqual(caught.exception.code, code)


class ArtifactServiceTest(ArtifactFixture):
    def test_settings_returns_public_keys_and_no_filesystem_or_private_secret(self):
        value = self.service.admin_release_settings()
        self.assertEqual(value["artifactBaseUrl"], "https://api.example.invalid/client/releases")
        self.assertEqual(value["maxArtifactBytes"], 512 * 1024 * 1024)
        self.assertEqual(value["trustedSigningKeys"], [{"keyId": KEY_ID, "publicKeyHex": public_key(SECRET).hex()}])
        self.assertTrue(value["artifactUploadEnabled"])
        self.assertNotIn(self.tmp.name, json.dumps(value))
        self.assertNotIn(SECRET.hex(), json.dumps(value))

    def test_separate_https_download_origin_controls_managed_namespace_and_settings(self):
        self.settings = replace(
            self.settings, api_base_url="https://203.0.113.20:820", api_host="203.0.113.20", api_port=820,
            release_artifact_origin="https://downloads.example.com:8443/",
        )
        self.service = MyProxyService(self.settings, FakeXuiAdapter())
        self.service.initialize()
        base_url = "https://downloads.example.com:8443/client/releases"
        self.assertEqual(self.service.admin_release_settings()["artifactBaseUrl"], base_url)
        self.assertEqual(self.settings.api_base_url, "https://203.0.113.20:820")
        record = self.register(b"package")
        self.assertTrue(record["artifactManaged"])
        self.assertTrue(self.service.artifacts.is_local(record))
        self.assertTrue(self.service.artifacts.uses_local_namespace(record))
        old_origin = {**record, "artifactUrl": record["artifactUrl"].replace("https://downloads.example.com:8443", self.settings.api_base_url)}
        self.assertFalse(self.service.artifacts.is_local(old_origin))
        self.assertFalse(self.service.artifacts.uses_local_namespace(old_origin))
        for alias in (
            record["artifactUrl"].replace("downloads.example.com", "DOWNLOADS.EXAMPLE.COM"),
            record["artifactUrl"].replace("/client/releases/", "/client/%72eleases/"),
            record["artifactUrl"].replace("/client/releases/", "/client/other/../releases/"),
        ):
            with self.subTest(alias=alias):
                self.assertTrue(self.service.artifacts.uses_local_namespace({**record, "artifactUrl": alias}))
                self.assert_error("ManifestRejected", self.register, b"package", url=alias)
        self.assert_error("Conflict", self.service.admin_set_release_status, record["id"], "published")
        self.service.admin_upload_artifact(record["id"], io.BytesIO(b"package"), 7)
        self.service.admin_set_release_status(record["id"], "published")
        stream, size = self.service.open_release_artifact(record["artifactSha256"], artifact.filename(record))
        with stream:
            self.assertEqual((size, stream.read()), (7, b"package"))

    def test_streamed_upload_is_idempotent_and_published_is_immutable(self):
        data = b"z" * (artifact.CHUNK_BYTES + 13)
        record = self.register(data)

        class BoundedReader(io.BytesIO):
            def read(self, length=-1):
                if not 0 <= length <= artifact.CHUNK_BYTES:
                    raise AssertionError("unbounded artifact read")
                return super().read(length)

        result = self.service.admin_upload_artifact(record["id"], BoundedReader(data), len(data))
        self.assertTrue(result["artifactReady"])
        self.assertTrue(result["platformSignatureReady"])
        self.service.admin_upload_artifact(record["id"], BoundedReader(data), len(data))
        self.service.admin_set_release_status(record["id"], "published")
        self.assert_error("Conflict", self.service.admin_upload_artifact, record["id"], io.BytesIO(data), len(data))
        stream, size = self.service.open_release_artifact(record["artifactSha256"], artifact.filename(record))
        with stream:
            self.assertEqual(size, len(data))
            self.assertEqual(stream.read(), data)
        self.service.admin_set_release_status(record["id"], "revoked")
        self.assert_error("NotFound", self.service.open_release_artifact, record["artifactSha256"], artifact.filename(record))
        self.assert_error("Conflict", self.service.admin_upload_artifact, record["id"], io.BytesIO(data), len(data))

    def test_slow_draft_upload_does_not_block_published_downloads(self):
        published = self.register(b"published")
        self.service.admin_upload_artifact(published["id"], io.BytesIO(b"published"), 9)
        self.service.admin_set_release_status(published["id"], "published")
        draft = self.register(b"new draft", version="1.2.4")
        started, proceed, downloaded = threading.Event(), threading.Event(), threading.Event()
        failures = []

        class SlowReader(io.BytesIO):
            def read(self, length=-1):
                started.set()
                if not proceed.wait(3):
                    raise AssertionError("upload did not resume")
                return super().read(length)

        def upload():
            try:
                self.service.admin_upload_artifact(draft["id"], SlowReader(b"new draft"), 9)
            except Exception as exc:
                failures.append(exc)

        def download():
            try:
                stream, _ = self.service.open_release_artifact(published["artifactSha256"], artifact.filename(published))
                with stream:
                    self.assertEqual(stream.read(), b"published")
                downloaded.set()
            except Exception as exc:
                failures.append(exc)

        worker = threading.Thread(target=upload)
        worker.start()
        self.assertTrue(started.wait(2))
        reader = threading.Thread(target=download)
        reader.start()
        try:
            self.assertTrue(downloaded.wait(1), "published downloads waited for a different draft upload")
        finally:
            proceed.set()
            worker.join(3)
            reader.join(3)
        self.assertEqual(failures, [])

    def test_wrong_digest_short_read_and_size_leave_no_temporary_files(self):
        data = b"package"
        record = self.register(data)
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact, record["id"], io.BytesIO(b"corrupt"), len(data))
        self.assert_error("BadRequest", self.service.admin_upload_artifact, record["id"], io.BytesIO(b"p"), len(data))
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact, record["id"], io.BytesIO(data), len(data) + 1)
        self.assertEqual(list(Path(self.tmp.name).rglob(".upload-*")), [])
        self.assertFalse(self.service.admin_list_releases()["releases"][0]["artifactReady"])
        self.assert_error("Conflict", self.service.admin_set_release_status, record["id"], "published")

    def test_linux_signature_is_for_raw_artifact_same_key_and_required_for_publish(self):
        data = b"Linux tar.gz content"
        record = self.register(data, "linux")
        signature = sign(SECRET, data)
        self.assert_error("Conflict", self.service.admin_upload_artifact_signature, record["id"], signature)
        self.service.admin_upload_artifact(record["id"], io.BytesIO(data), len(data))
        self.assert_error("Conflict", self.service.admin_set_release_status, record["id"], "published")
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact_signature, record["id"], sign(OTHER_SECRET, data))
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact_signature, record["id"], sign(SECRET, b"different bytes"))
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact_signature, record["id"], signature[:63])
        result = self.service.admin_upload_artifact_signature(record["id"], signature)
        self.assertTrue(result["platformSignatureReady"])
        self.service.admin_upload_artifact_signature(record["id"], signature)
        self.service.admin_set_release_status(record["id"], "published")
        stream, size = self.service.open_release_artifact(record["artifactSha256"], artifact.filename(record) + ".sig")
        with stream:
            self.assertEqual(size, 89)
            self.assertEqual(base64.b64decode(stream.read(), validate=False), signature)
        self.assert_error("Conflict", self.service.admin_upload_artifact_signature, record["id"], signature)

    def test_linux_subject_must_be_hash_of_manifest_signing_public_key(self):
        data = b"tarball"
        record = self.register(data, "linux", signature={"type": "ed25519", "subjectSha256": "a" * 64})
        self.service.admin_upload_artifact(record["id"], io.BytesIO(data), len(data))
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact_signature, record["id"], sign(SECRET, data))

    def test_manual_external_release_is_compatible_but_cannot_upload(self):
        record = self.register(url="https://releases.example/manual.zip")
        self.assertFalse(record["artifactManaged"])
        self.service.admin_set_release_status(record["id"], "published")
        self.assert_error("Conflict", self.service.admin_upload_artifact, record["id"], io.BytesIO(b"signed package bytes"), 20)
        self.assert_error("NotFound", self.service.open_release_artifact, record["artifactSha256"], artifact.filename(record))

    def test_registered_local_url_must_match_digest_platform_and_version(self):
        for suffix in ("../../escape", "wrong.zip", "myproxy-windows-2.0.0.zip"):
            with self.subTest(suffix=suffix):
                self.assert_error("ManifestRejected", self.register, url=self.service.artifacts.base_url + "/" + "a" * 64 + "/" + suffix)

    def test_equivalent_origin_or_path_alias_cannot_skip_upload_checks(self):
        data = b"signed package bytes"
        canonical = self.service.artifacts.url({"platform": "windows", "version": "1.2.3", "artifactSha256": hashlib.sha256(data).hexdigest()})
        for alias in (canonical.replace("api.example.invalid", "API.EXAMPLE.INVALID"),
                      canonical.replace("api.example.invalid", "api.example.invalid:443"),
                      canonical.replace("/client/releases/", "/client/%72eleases/"),
                      canonical.replace("/client/releases/", "/client/other/../releases/")):
            with self.subTest(alias=alias):
                self.assert_error("ManifestRejected", self.register, url=alias)

    def test_publish_rehashes_artifact_and_does_not_accept_local_tampering(self):
        record = self.register(b"good")
        self.service.admin_upload_artifact(record["id"], io.BytesIO(b"good"), 4)
        path = Path(self.tmp.name) / "releases" / record["artifactSha256"] / artifact.filename(record)
        path.write_bytes(b"evil")
        self.assert_error("Conflict", self.service.admin_set_release_status, record["id"], "published")
        self.assert_error("Conflict", self.service.admin_upload_artifact, record["id"], io.BytesIO(b"good"), 4)
        self.assertEqual(path.read_bytes(), b"evil")
        self.assertEqual(list(path.parent.glob(".upload-*")), [])

    def test_limit_and_disable_apply_before_stream_read(self):
        record = self.register(b"1234")

        class NeverRead:
            def read(self, _):
                raise AssertionError("body read before limit validation")

        self.service.artifacts.max_bytes = 3
        self.assert_error("ManifestRejected", self.service.admin_upload_artifact, record["id"], NeverRead(), 4)
        self.service.artifacts.max_bytes = 0
        self.assertFalse(self.service.admin_release_settings()["artifactUploadEnabled"])
        self.assert_error("Conflict", self.service.admin_upload_artifact, record["id"], NeverRead(), 4)

    def test_streaming_signature_verifier_matches_rfc8032_and_rejects_noncanonical_s(self):
        path = Path(self.tmp.name) / "empty"
        path.write_bytes(b"")
        self.assertTrue(artifact.verify_file_signature(path, vectors.Ed25519Test.RFC_PUBLIC, vectors.Ed25519Test.RFC_SIGNATURE))
        mutated = vectors.Ed25519Test.RFC_SIGNATURE[:32] + int.to_bytes(release_order(), 32, "little")
        self.assertFalse(artifact.verify_file_signature(path, vectors.Ed25519Test.RFC_PUBLIC, mutated))


def release_order():
    from myproxy_server import release
    return release._L


class ArtifactHttpTest(ArtifactFixture):
    def setUp(self):
        super().setUp()
        self.httpd = make_server(self.service, self.settings)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(stop_server, self.httpd, self.thread)
        self.port = self.httpd.server_address[1]

    def request(self, method, path, data=None, authorized=True, **headers):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3)
        if authorized:
            headers["Authorization"] = "Bearer " + self.settings.admin_token
        if data is not None:
            headers.setdefault("Content-Type", "application/octet-stream")
        try:
            conn.request(method, path, data, headers)
            response = conn.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            conn.close()

    def test_full_http_lifecycle_and_exact_public_path(self):
        data = b"Android APK test content"
        record = self.register(data, "android", "1.2.3-beta.1+build.5")
        path = "/client/releases/" + record["artifactSha256"] + "/" + artifact.filename(record)
        self.assertEqual(self.request("GET", path)[0], 404)
        status, _, body = self.request("POST", "/api/admin/release/" + record["id"] + "/artifact", data)
        self.assertEqual(status, 200, body)
        self.assertTrue(json.loads(body)["artifactReady"])
        status, _, body = self.request("POST", "/api/admin/release/" + record["id"] + "/publish")
        self.assertEqual(status, 200, body)
        status, headers, body = self.request("GET", path, authorized=False)
        self.assertEqual((status, body), (200, data))
        self.assertEqual(headers["Cache-Control"], "no-store")
        for invalid in (path + "?x=1", path + "?", path + "/", path + ".sig", path.replace("/myproxy", "/%6dyproxy"),
                        path.replace("/myproxy", "/../myproxy"), path.replace("/myproxy", "/%2e%2e/myproxy"),
                        path.replace("/myproxy", "/%2fmyproxy")):
            with self.subTest(path=invalid):
                self.assertEqual(self.request("GET", invalid, authorized=False)[0], 404)
        self.assertEqual(self.request("POST", path, data)[0], 404)
        self.assertEqual(self.request("POST", "/api/admin/release/" + record["id"] + "/artifact", data)[0], 409)

    def test_authentication_happens_before_body_read_even_for_small_uploads(self):
        record = self.register(b"1234")
        with socket.create_connection(("127.0.0.1", self.port), timeout=2) as conn:
            conn.sendall((f"POST /api/admin/release/{record['id']}/artifact HTTP/1.1\r\n"
                          "Host: localhost\r\nContent-Type: application/octet-stream\r\n"
                          "Content-Length: 4\r\n\r\n").encode("ascii"))
            response = conn.recv(4096)
            self.assertIn(b" 401 ", response)
            self.assertIn(b"Connection: close", response)
        self.assertEqual(list(Path(self.tmp.name).rglob(".upload-*")), [])

    def test_content_type_size_and_signature_framing(self):
        record = self.register(b"1234", "linux")
        endpoint = "/api/admin/release/" + record["id"]
        self.assertEqual(self.request("POST", endpoint + "/artifact", b"1234", **{"Content-Type": "application/json"})[0], 400)
        self.assertEqual(self.request("POST", endpoint + "/artifact", b"123", **{"Content-Length": "3"})[0], 400)
        self.assertEqual(self.request("POST", endpoint + "/artifact-signature", b"a" * 63)[0], 400)
        self.assertEqual(self.request("GET", "/api/admin/release-settings", authorized=False)[0], 401)
        status, _, body = self.request("GET", "/api/admin/release-settings")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["artifactExtensions"]["linux"], "tar.gz")

    def test_private_crypto_static_asset_has_same_csp(self):
        status, headers, body = self.request("GET", "/admin/release-signing.js", authorized=False)
        self.assertEqual(status, 200)
        self.assertIn("script-src 'self'", headers["Content-Security-Policy"])
        self.assertIn(b"MyProxyRelease", body)


class ArtifactOriginHttpTest(ArtifactFixture):
    request = ArtifactHttpTest.request

    def test_server_settings_returns_separate_download_origin(self):
        self.settings = replace(
            self.settings, api_base_url="https://203.0.113.20:820", api_host="203.0.113.20", api_port=820,
            release_artifact_origin="https://downloads.example.com:8443",
        )
        self.service = MyProxyService(self.settings, FakeXuiAdapter())
        self.service.initialize()
        self.httpd = make_server(self.service, self.settings)
        thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(stop_server, self.httpd, thread)
        self.port = self.httpd.server_address[1]
        status, _, body = self.request("GET", "/api/admin/release-settings")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["artifactBaseUrl"], "https://downloads.example.com:8443/client/releases")
        self.assertEqual(self.settings.api_base_url, "https://203.0.113.20:820")


class ArtifactDeploymentTest(unittest.TestCase):
    def test_nginx_only_exposes_digest_downloads_with_get(self):
        template = (HERE.parent / "deploy" / "nginx-device-api.conf.template").read_text(encoding="utf-8")
        line = next(line.strip() for line in template.splitlines() if line.strip().startswith("location ~"))
        pattern = line.split('"')[1]
        matcher = re.compile(pattern)
        for platform, extension in artifact.EXTENSIONS.items():
            path = f"/client/releases/{'a' * 64}/myproxy-{platform}-1.2.3-beta.1+build.5.{extension}"
            self.assertIsNotNone(matcher.fullmatch(path))
        for path in ("/api/admin/release-settings", "/api/admin/release/rel_x/artifact",
                     "/client/releases/../secret", "/client/releases/" + "a" * 63 + "/myproxy-linux-1.2.3.tar.gz"):
            self.assertIsNone(matcher.fullmatch(path))
        section = template.split(line, 1)[1].split("    # Optional root-managed", 1)[0]
        self.assertIn("limit_except GET { deny all; }", section)
        self.assertIn("proxy_buffering off;", section)
        self.assertNotIn("location /client/releases", template)


if __name__ == "__main__":
    unittest.main()
