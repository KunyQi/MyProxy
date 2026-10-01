"""Update Plane tests: manifest signing, release registry, assignment, rollout.

Everything here runs against a temporary SQLite database, a FakeXuiAdapter and
a threaded HTTP server on 127.0.0.1.  No network, no real /etc/x-ui.

The test signer lives here rather than in ``myproxy_server`` on purpose: the
production package must not contain a signing routine at all.  "The server can
only verify, never sign" is the property that makes a stolen Admin token
insufficient to ship a binary, and the cheapest way to keep that property true
is to keep the private-key code out of the shipped package entirely.
"""

import base64
import hashlib
import json
import os
import sys
import tempfile
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from myproxy_server import db, release  # noqa: E402
from myproxy_server.app import MyProxyService  # noqa: E402
from myproxy_server.server import (  # noqa: E402
    _validate_runtime_security,
    make_server,
)

from test_api import (  # noqa: E402
    FakeXuiAdapter,
    make_settings,
    stop_server,
)


# ----------------------------------------------------------------------
# Test-only Ed25519 signer
# ----------------------------------------------------------------------

def _clamp(secret: bytes) -> tuple[int, bytes]:
    digest = hashlib.sha512(secret).digest()
    scalar = int.from_bytes(digest[:32], "little")
    scalar &= (1 << 254) - 8
    scalar |= 1 << 254
    return scalar, digest[32:]


def _compress(point) -> bytes:
    z_inv = pow(point[2], release._P - 2, release._P)
    x = point[0] * z_inv % release._P
    y = point[1] * z_inv % release._P
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def public_key(secret: bytes) -> bytes:
    scalar, _ = _clamp(secret)
    return _compress(release._point_mul(scalar, release._BASE))


def sign(secret: bytes, message: bytes) -> bytes:
    scalar, prefix = _clamp(secret)
    encoded_a = public_key(secret)
    r = int.from_bytes(hashlib.sha512(prefix + message).digest(), "little") % release._L
    encoded_r = _compress(release._point_mul(r, release._BASE))
    k = (
        int.from_bytes(
            hashlib.sha512(encoded_r + encoded_a + message).digest(), "little"
        )
        % release._L
    )
    return encoded_r + int.to_bytes((r + k * scalar) % release._L, 32, "little")


SECRET = bytes(range(32))
KEY_ID = "test-key"
OTHER_SECRET = bytes(range(32, 64))


def manifest_bytes(**overrides) -> bytes:
    doc = {
        "schemaVersion": 1,
        "platform": "windows",
        "version": "1.2.3",
        "channel": "stable",
        "mandatory": False,
        "issuedAt": "2026-09-19T00:00:00Z",
        "artifact": {
            "url": f"https://releases.example/MyProxy-{overrides.get('version', '1.2.3')}.exe",
            "sha256": "a" * 64,
            "size": 4096,
            "signature": {"type": "authenticode", "subjectSha256": "b" * 64},
        },
    }
    artifact_overrides = overrides.pop("artifact", None)
    doc.update(overrides)
    if artifact_overrides is not None:
        if artifact_overrides is _REMOVE:
            doc.pop("artifact")
        else:
            doc["artifact"].update(artifact_overrides)
    for key, value in list(doc.items()):
        if value is _REMOVE:
            doc.pop(key)
    return json.dumps(doc).encode("utf-8")


class _Remove:
    pass


_REMOVE = _Remove()


def signed(secret: bytes = SECRET, **overrides) -> tuple[str, str]:
    raw = manifest_bytes(**overrides)
    signature = sign(secret, release.SIGNING_DOMAIN + raw)
    return (
        base64.b64encode(raw).decode("ascii"),
        base64.b64encode(signature).decode("ascii"),
    )


# ----------------------------------------------------------------------


class Ed25519Test(unittest.TestCase):
    """RFC 8032 conformance, not just self-consistency.

    A round trip against our own signer would also pass with a subtly wrong
    curve, so pin the published test vector: the same secret must produce the
    same public key and the same signature bytes as the RFC.
    """

    RFC_SECRET = bytes.fromhex(
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"
    )
    RFC_PUBLIC = bytes.fromhex(
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"
    )
    RFC_SIGNATURE = bytes.fromhex(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155"
        "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
    )

    def test_rfc8032_vector_1(self):
        self.assertEqual(public_key(self.RFC_SECRET), self.RFC_PUBLIC)
        self.assertEqual(sign(self.RFC_SECRET, b""), self.RFC_SIGNATURE)
        self.assertTrue(
            release.ed25519_verify(self.RFC_PUBLIC, b"", self.RFC_SIGNATURE)
        )

    def test_malformed_inputs_return_false_instead_of_raising(self):
        good = sign(SECRET, b"payload")
        cases = {
            "short key": (public_key(SECRET)[:31], b"payload", good),
            "short signature": (public_key(SECRET), b"payload", good[:63]),
            "wrong message": (public_key(SECRET), b"other", good),
            "wrong key": (public_key(OTHER_SECRET), b"payload", good),
            "flipped bit": (
                public_key(SECRET),
                b"payload",
                bytes([good[0] ^ 1]) + good[1:],
            ),
        }
        for name, (key, message, signature) in cases.items():
            with self.subTest(name):
                self.assertFalse(release.ed25519_verify(key, message, signature))

    def test_non_canonical_s_is_rejected(self):
        # S >= L is the classic malleability trick: it encodes an equivalent
        # scalar, so an implementation that reduces instead of rejecting will
        # accept two distinct byte strings for one signature.
        good = sign(SECRET, b"payload")
        s = int.from_bytes(good[32:], "little")
        mutated = good[:32] + int.to_bytes(s + release._L, 32, "little")
        self.assertFalse(
            release.ed25519_verify(public_key(SECRET), b"payload", mutated)
        )

    def test_signing_domain_prevents_cross_structure_replay(self):
        raw = manifest_bytes()
        signature = sign(SECRET, raw)  # signed WITHOUT the domain prefix
        self.assertFalse(
            release.ed25519_verify(
                public_key(SECRET), release.SIGNING_DOMAIN + raw, signature
            )
        )


class ManifestValidationTest(unittest.TestCase):
    def setUp(self):
        self.keys = {KEY_ID: public_key(SECRET)}

    def test_accepts_a_well_formed_manifest(self):
        manifest, signature = signed()
        summary, doc = release.verify_and_parse(
            manifest, signature, KEY_ID, self.keys
        )
        self.assertEqual(summary["version"], "1.2.3")
        self.assertEqual(summary["platform"], "windows")
        self.assertEqual(summary["artifactSize"], 4096)
        self.assertEqual(doc["schemaVersion"], 1)

    def test_linux_manifest_requires_its_own_signature_type(self):
        # Linux 的 tarball 没有 Authenticode 那样的平台签名可查，客户端验的是
        # 对产物字节的独立 Ed25519 签名。登记时就必须是这个类型，否则拒绝——
        # 让一份「说是 ed25519 却带着 authenticode 字段」的 manifest 进库，
        # 等于把不一致留给客户端在安装那一刻去发现。
        linux_artifact = {
            "url": "https://releases.example/MyProxy-linux-x64.tar.gz",
            "signature": {"type": "ed25519", "subjectSha256": "c" * 64},
        }
        manifest, signature = signed(platform="linux", artifact=linux_artifact)
        summary, _ = release.verify_and_parse(manifest, signature, KEY_ID, self.keys)
        self.assertEqual(summary["platform"], "linux")

        wrong = dict(linux_artifact)
        wrong["signature"] = {"type": "authenticode", "subjectSha256": "c" * 64}
        manifest, signature = signed(platform="linux", artifact=wrong)
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(manifest, signature, KEY_ID, self.keys)

    def test_no_configured_keys_fails_closed(self):
        manifest, signature = signed()
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(manifest, signature, KEY_ID, {})

    def test_unknown_key_id_is_rejected(self):
        manifest, signature = signed()
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(manifest, signature, "nope", self.keys)

    def test_signature_from_an_untrusted_key_is_rejected(self):
        manifest, signature = signed(OTHER_SECRET)
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(manifest, signature, KEY_ID, self.keys)

    def test_tampered_manifest_is_rejected(self):
        manifest, signature = signed()
        raw = bytearray(base64.b64decode(manifest))
        raw[-1] = raw[-1] ^ 0x01
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(
                base64.b64encode(bytes(raw)).decode("ascii"),
                signature,
                KEY_ID,
                self.keys,
            )

    def test_reserialised_manifest_does_not_verify(self):
        # Guards the "opaque bytes" decision: re-encoding the same document
        # with different separators must break the signature, which is exactly
        # why no hop is allowed to re-serialise.
        manifest, signature = signed()
        doc = json.loads(base64.b64decode(manifest))
        reserialised = json.dumps(doc, separators=(",", ":")).encode("utf-8")
        self.assertNotEqual(reserialised, base64.b64decode(manifest))
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(
                base64.b64encode(reserialised).decode("ascii"),
                signature,
                KEY_ID,
                self.keys,
            )

    def test_schema_rejections(self):
        cases = {
            "http url": {"artifact": {"url": "http://releases.example/a.exe"}},
            "url with credentials": {
                "artifact": {"url": "https://u:p@releases.example/a.exe"}
            },
            "bad sha256": {"artifact": {"sha256": "z" * 64}},
            "uppercase sha256": {"artifact": {"sha256": "A" * 64}},
            "zero size": {"artifact": {"size": 0}},
            "size as bool": {"artifact": {"size": True}},
            "wrong signature type": {
                "artifact": {"signature": {"type": "apksigner",
                                           "subjectSha256": "b" * 64}}
            },
            "missing artifact": {"artifact": _REMOVE},
            "bad version": {"version": "1.2"},
            "bad channel": {"channel": "nightly"},
            # linux 现在是合法平台（它有自己的 latest.json 与 ed25519 产物签名），
            # 所以这条用的是真正不存在的平台名。
            "bad platform": {"platform": "macos"},
            "bad schema version": {"schemaVersion": 2},
            "bad timestamp": {"issuedAt": "2026-09-19 00:00:00"},
            "mandatory not bool": {"mandatory": "yes"},
            "bad minimumVersion": {"minimumVersion": "one"},
        }
        keys = self.keys
        for name, override in cases.items():
            with self.subTest(name):
                manifest, signature = signed(**override)
                with self.assertRaises(release.ManifestError):
                    release.verify_and_parse(manifest, signature, KEY_ID, keys)

    def test_non_json_payload_is_rejected_after_signature_check(self):
        raw = b"not json at all"
        signature = sign(SECRET, release.SIGNING_DOMAIN + raw)
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(
                base64.b64encode(raw).decode("ascii"),
                base64.b64encode(signature).decode("ascii"),
                KEY_ID,
                self.keys,
            )

    def test_base64_must_be_strict(self):
        manifest, signature = signed()
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(
                manifest[:4] + "\n" + manifest[4:], signature, KEY_ID, self.keys
            )

    def test_oversized_manifest_is_rejected(self):
        manifest, signature = signed(note="x" * (release.MAX_MANIFEST_BYTES + 10))
        with self.assertRaises(release.ManifestError):
            release.verify_and_parse(manifest, signature, KEY_ID, self.keys)


class SigningKeyConfigTest(unittest.TestCase):
    def test_parse_and_reject(self):
        hex_key = public_key(SECRET).hex()
        parsed = release.parse_public_keys(f"{KEY_ID}:{hex_key}")
        self.assertEqual(parsed, {KEY_ID: public_key(SECRET)})
        self.assertEqual(release.parse_public_keys(""), {})
        self.assertEqual(release.parse_public_keys("  "), {})
        for bad in (
            f"{KEY_ID}:{hex_key[:-2]}",
            f"{KEY_ID}:zz{hex_key[2:]}",
            f":{hex_key}",
            f"bad key id:{hex_key}",
            f"{KEY_ID}:{hex_key},{KEY_ID}:{hex_key}",
        ):
            with self.subTest(bad):
                with self.assertRaises(ValueError):
                    release.parse_public_keys(bad)

    def test_startup_rejects_an_invalid_key_list(self):
        settings = make_settings(
            "unused.db",
            admin_token="a" * 64,
            device_token_secret="b" * 64,
            release_signing_keys="broken",
        )
        with self.assertRaises(RuntimeError):
            _validate_runtime_security(settings)

    def test_startup_accepts_an_empty_key_list(self):
        settings = make_settings(
            "unused.db",
            admin_token="a" * 64,
            device_token_secret="b" * 64,
            release_signing_keys="",
        )
        _validate_runtime_security(settings)


class UpdatePlaneApiTest(unittest.TestCase):
    """End-to-end over real HTTP, so routing and auth are covered too."""

    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory()
        cls.db_path = os.path.join(cls._tmpdir.name, "update.db")
        cls.admin_token = "test-admin-token"
        cls.xui = FakeXuiAdapter()
        cls.settings = make_settings(
            cls.db_path,
            release_signing_keys=f"{KEY_ID}:{public_key(SECRET).hex()}",
            # Every test claims a fresh pairing code from 127.0.0.1, which the
            # production per-IP cap (20) would throttle partway through the
            # class.  Rate limiting has its own coverage in test_api.
            claim_max_per_ip=10000,
        )
        cls.service = MyProxyService(cls.settings, cls.xui)
        cls.service.initialize()
        cls.httpd = make_server(cls.service, cls.settings)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        stop_server(cls.httpd, cls.thread)
        cls._tmpdir.cleanup()

    def setUp(self):
        # Each test gets its own user/device so assignment state cannot leak
        # between tests through the shared database.
        self.user = self._admin(
            "POST", "/api/admin/user", {"username": f"u{os.urandom(6).hex()}"}
        )[1]
        binding = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": self.user["id"], "deviceTemplate": "windows"},
        )[1]
        claim = self._api(
            "POST",
            "/api/device/claim",
            {
                "pairingCode": binding["code"],
                "deviceName": f"DEV-{os.urandom(4).hex()}",
                "platform": "windows",
                "clientVersion": "1.0.0",
            },
        )[1]
        self.device_token = claim["deviceToken"]
        self.device_id = claim["deviceId"]
        self.addCleanup(self._clear_platform_assignment)

    def _clear_platform_assignment(self):
        _, listing = self._admin("GET", "/api/admin/assignment?scope=platform")
        for assignment in listing.get("assignments", []):
            self._admin("DELETE", f"/api/admin/assignment/{assignment['id']}")

    # -- helpers -------------------------------------------------------
    @classmethod
    def _api(cls, method, path, body=None, token=None):
        from test_api import ApiTest

        ApiTest.port = cls.port
        return ApiTest._api(method, path, body, token)

    @classmethod
    def _admin(cls, method, path, body=None):
        return cls._api(method, path, body, cls.admin_token)

    def _device(self, method, path, body=None):
        return self._api(method, path, body, self.device_token)

    def _register(self, version="1.2.3", publish=True, **overrides):
        manifest, signature = signed(version=version, **overrides)
        status, created = self._admin(
            "POST",
            "/api/admin/release",
            {"manifest": manifest, "signature": signature, "signingKeyId": KEY_ID},
        )
        self.assertEqual(status, 200, created)
        if publish:
            status, created = self._admin(
                "POST", f"/api/admin/release/{created['id']}/publish"
            )
            self.assertEqual(status, 200, created)
        return created, manifest, signature

    def _assign(self, scope, target_id, release_id=None, flags=None, expect=200):
        body = {"scope": scope, "targetId": target_id, "platform": "windows"}
        if release_id is not None:
            body["releaseId"] = release_id
        if flags is not None:
            body["featureFlags"] = flags
        status, payload = self._admin("POST", "/api/admin/assignment", body)
        self.assertEqual(status, expect, payload)
        return payload

    # -- registration --------------------------------------------------
    def test_register_returns_draft_and_publish_flips_status(self):
        created, _, _ = self._register(version="2.0.0", publish=False)
        self.assertEqual(created["status"], "draft")
        self.assertEqual(created["artifactSha256"], "a" * 64)
        status, published = self._admin(
            "POST", f"/api/admin/release/{created['id']}/publish"
        )
        self.assertEqual(status, 200)
        self.assertEqual(published["status"], "published")

    def test_release_listing_never_leaks_manifest_bytes(self):
        self._register(version="2.1.0")
        _, listing = self._admin("GET", "/api/admin/release?platform=windows")
        self.assertTrue(listing["releases"])
        for item in listing["releases"]:
            self.assertNotIn("manifest", item)
            self.assertNotIn("signature", item)

    def test_unsigned_or_wrongly_signed_release_is_rejected_generically(self):
        manifest, _ = signed(version="3.0.0")
        _, forged = signed(OTHER_SECRET, version="3.0.0")
        for name, body in {
            "wrong key": {
                "manifest": manifest,
                "signature": forged,
                "signingKeyId": KEY_ID,
            },
            "unknown key id": {
                "manifest": manifest,
                "signature": forged,
                "signingKeyId": "ghost",
            },
            "missing signature": {"manifest": manifest, "signingKeyId": KEY_ID},
        }.items():
            with self.subTest(name):
                status, payload = self._admin("POST", "/api/admin/release", body)
                self.assertEqual(status, 400)
                # Generic on purpose: the reply must not let a caller map the
                # validator one field at a time.
                self.assertEqual(payload.get("error", {}).get("code"), "ManifestRejected")

    def test_same_platform_and_version_cannot_be_registered_twice(self):
        self._register(version="4.0.0")
        manifest, signature = signed(version="4.0.0", mandatory=True)
        status, payload = self._admin(
            "POST",
            "/api/admin/release",
            {"manifest": manifest, "signature": signature, "signingKeyId": KEY_ID},
        )
        self.assertEqual(status, 409, payload)

    def test_revoked_release_cannot_be_republished(self):
        created, _, _ = self._register(version="4.1.0")
        self._admin("POST", f"/api/admin/release/{created['id']}/revoke")
        status, _ = self._admin("POST", f"/api/admin/release/{created['id']}/publish")
        self.assertEqual(status, 409)

    def test_registration_requires_admin_auth(self):
        manifest, signature = signed(version="4.2.0")
        status, payload = self._api(
            "POST",
            "/api/admin/release",
            {"manifest": manifest, "signature": signature, "signingKeyId": KEY_ID},
        )
        self.assertEqual(status, 401)
        self.assertEqual(payload.get("error", {}).get("code"), "AdminUnauthorized")

    # -- assignment ----------------------------------------------------
    def test_device_assignment_is_served_verbatim(self):
        created, manifest, signature = self._register(version="5.0.0")
        self._assign("device", self.device_id, created["id"])

        status, payload = self._device("GET", "/api/device/update")
        self.assertEqual(status, 200, payload)
        update = payload["update"]
        self.assertEqual(update["releaseId"], created["id"])
        self.assertEqual(update["source"], "device")
        # Byte-for-byte: this is the whole contract.
        self.assertEqual(update["manifest"], manifest)
        self.assertEqual(update["signature"], signature)
        self.assertTrue(
            release.ed25519_verify(
                public_key(SECRET),
                release.SIGNING_DOMAIN + base64.b64decode(update["manifest"]),
                base64.b64decode(update["signature"]),
            )
        )

    def test_resolution_order_is_device_over_user_over_platform(self):
        platform_release, _, _ = self._register(version="6.0.0")
        user_release, _, _ = self._register(version="6.1.0")
        device_release, _, _ = self._register(version="6.2.0")

        self._assign("platform", "", platform_release["id"])
        self.assertEqual(self._resolved()["version"], "6.0.0")

        self._assign("user", self.user["id"], user_release["id"])
        self.assertEqual(self._resolved()["version"], "6.1.0")

        self._assign("device", self.device_id, device_release["id"])
        resolved = self._resolved()
        self.assertEqual(resolved["version"], "6.2.0")
        self.assertEqual(resolved["source"], "device")

    def _resolved(self):
        _, payload = self._device("GET", "/api/device/update")
        return payload["update"]

    def test_revoking_a_release_falls_back_to_the_broader_scope(self):
        platform_release, _, _ = self._register(version="7.0.0")
        device_release, _, _ = self._register(version="7.1.0")
        self._assign("platform", "", platform_release["id"])
        self._assign("device", self.device_id, device_release["id"])
        self.assertEqual(self._resolved()["version"], "7.1.0")

        # One action, and every device pinned to the bad build rolls back.
        self._admin("POST", f"/api/admin/release/{device_release['id']}/revoke")
        resolved = self._resolved()
        self.assertEqual(resolved["version"], "7.0.0")
        self.assertEqual(resolved["source"], "platform")

    def test_no_assignment_yields_a_null_update(self):
        status, payload = self._device("GET", "/api/device/update")
        self.assertEqual(status, 200)
        self.assertIsNone(payload["update"])
        self.assertEqual(payload["featureFlags"], {})

    def test_feature_flags_merge_from_broad_to_narrow(self):
        self._assign("platform", "", flags={"beta": False, "banner": "hello"})
        self._assign("user", self.user["id"], flags={"beta": True})
        self._assign("device", self.device_id, flags={"banner": "device"})
        _, payload = self._device("GET", "/api/device/update")
        self.assertEqual(
            payload["featureFlags"], {"beta": True, "banner": "device"}
        )

    def test_flags_reject_nesting_and_oversized_values(self):
        for name, flags in {
            "nested object": {"a": {"b": 1}},
            "nested list": {"a": [1, 2]},
            "bad key": {"1bad": True},
            "long string": {"a": "x" * 200},
        }.items():
            with self.subTest(name):
                self._assign(
                    "device", self.device_id, flags=flags, expect=400
                )

    def test_assigning_an_unpublished_or_cross_platform_release_fails(self):
        draft, _, _ = self._register(version="8.0.0", publish=False)
        self._assign("device", self.device_id, draft["id"], expect=409)

        android_manifest, android_signature = signed(
            platform="android",
            version="8.1.0",
            artifact={
                "url": "https://releases.example/MyProxy-8.1.0.apk",
                "signature": {"type": "apksigner", "subjectSha256": "c" * 64},
            },
        )
        _, android = self._admin(
            "POST",
            "/api/admin/release",
            {
                "manifest": android_manifest,
                "signature": android_signature,
                "signingKeyId": KEY_ID,
            },
        )
        self._admin("POST", f"/api/admin/release/{android['id']}/publish")
        self._assign("device", self.device_id, android["id"], expect=400)

    def test_assignment_to_a_missing_target_is_404(self):
        created, _, _ = self._register(version="8.2.0")
        self._assign("device", "dev_missing", created["id"], expect=404)
        self._assign("user", "usr_missing", created["id"], expect=404)

    def test_deleting_a_device_clears_its_assignment(self):
        created, _, _ = self._register(version="9.0.0")
        self._assign("device", self.device_id, created["id"])
        status, _ = self._admin("DELETE", f"/api/admin/device/{self.device_id}")
        self.assertEqual(status, 200)
        _, listing = self._admin(
            "GET", f"/api/admin/assignment?scope=device&targetId={self.device_id}"
        )
        self.assertEqual(listing["assignments"], [])

    # -- device-facing behaviour ---------------------------------------
    def test_update_requires_a_valid_device_token(self):
        for token in (None, "not-a-token"):
            with self.subTest(token=token):
                status, payload = self._api("GET", "/api/device/update", None, token)
                self.assertEqual(status, 401)
                self.assertEqual(payload.get("error", {}).get("code"), "TokenInvalid")

    def test_heartbeat_reports_the_assigned_release_and_flags(self):
        created, _, _ = self._register(version="10.0.0")
        self._assign("device", self.device_id, created["id"], flags={"neat": True})
        status, payload = self._device("POST", "/api/device/heartbeat", {})
        self.assertEqual(status, 200, payload)
        self.assertEqual(payload["release"]["version"], "10.0.0")
        self.assertEqual(payload["release"]["source"], "device")
        self.assertEqual(payload["featureFlags"], {"neat": True})
        # Existing fields must survive: clients in the field read these.
        self.assertTrue(payload["ok"])
        self.assertIn("configVersion", payload)
        self.assertIn("serverTime", payload)

    def test_heartbeat_release_is_null_without_an_assignment(self):
        _, payload = self._device("POST", "/api/device/heartbeat", {})
        self.assertIsNone(payload["release"])

    def test_latest_json_serves_the_platform_default_manifest(self):
        created, manifest, signature = self._register(version="11.0.0")
        self._assign("platform", "", created["id"])
        status, payload = self._api("GET", "/client/windows/latest.json")
        self.assertEqual(status, 200)
        self.assertEqual(payload["version"], "11.0.0")
        self.assertEqual(payload["manifest"], manifest)
        self.assertEqual(payload["signature"], signature)
        self.assertEqual(payload["sha256"], "a" * 64)
        self.assertEqual(
            payload["downloadUrl"], "https://releases.example/MyProxy-11.0.0.exe"
        )

    def test_latest_json_never_exposes_a_per_device_assignment(self):
        device_release, _, _ = self._register(version="12.0.0")
        self._assign("device", self.device_id, device_release["id"])
        _, payload = self._api("GET", "/client/windows/latest.json")
        self.assertNotIn("manifest", payload)
        self.assertNotEqual(payload.get("version"), "12.0.0")

    def test_latest_json_keeps_the_legacy_shape_without_a_platform_release(self):
        _, payload = self._api("GET", "/client/windows/latest.json")
        self.assertEqual(
            set(payload), {"version", "downloadUrl", "sha256", "mandatory"}
        )

    def test_latest_json_serves_the_linux_platform_default_manifest(self):
        # Linux 是第三个平台：它有自己的公开路由、自己的 server_meta 键，
        # 产物签名类型是 ed25519。这三样必须同时成立，否则 Linux 客户端
        # 要么取不到更新，要么取到一份它按自己平台的规则验不过的 manifest。
        created, manifest, signature = self._register(
            version="14.0.0",
            platform="linux",
            artifact={
                "url": "https://releases.example/MyProxy-linux-x64.tar.gz",
                "signature": {"type": "ed25519", "subjectSha256": "c" * 64},
            },
        )
        status, assigned = self._admin(
            "POST",
            "/api/admin/assignment",
            {
                "scope": "platform",
                "targetId": "",
                "platform": "linux",
                "releaseId": created["id"],
            },
        )
        self.assertEqual(status, 200, assigned)

        status, payload = self._api("GET", "/client/linux/latest.json")
        self.assertEqual(status, 200, payload)
        self.assertEqual(payload["version"], "14.0.0")
        self.assertEqual(payload["manifest"], manifest)
        self.assertEqual(payload["signature"], signature)
        self.assertEqual(payload["sha256"], "a" * 64)
        self.assertEqual(
            payload["downloadUrl"],
            "https://releases.example/MyProxy-linux-x64.tar.gz",
        )

        # 三端互不串台：linux 的平台指派不出现在 windows 的响应里。
        _, windows_payload = self._api("GET", "/client/windows/latest.json")
        self.assertNotEqual(windows_payload.get("version"), "14.0.0")
        self.assertNotIn("manifest", windows_payload)

    # -- install reporting ---------------------------------------------
    def test_install_report_updates_version_and_writes_audit(self):
        created, _, _ = self._register(version="13.0.0")
        status, payload = self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": created["id"], "status": "installed"},
        )
        self.assertEqual(status, 200, payload)
        _, devices = self._admin("GET", f"/api/admin/device?userId={self.user['id']}")
        self.assertEqual(devices["devices"][0]["clientVersion"], "13.0.0")
        _, audit = self._admin(
            "GET", f"/api/admin/release-audit?targetId={self.device_id}"
        )
        self.assertEqual(audit["entries"][0]["action"], "install_installed")

    def test_a_failed_report_does_not_change_the_installed_version(self):
        created, _, _ = self._register(version="14.0.0")
        self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": created["id"], "status": "failed", "detail": "hash"},
        )
        _, devices = self._admin("GET", f"/api/admin/device?userId={self.user['id']}")
        self.assertEqual(devices["devices"][0]["clientVersion"], "1.0.0")

    def test_report_cannot_move_the_device_onto_another_release(self):
        assigned, _, _ = self._register(version="15.0.0")
        other, _, _ = self._register(version="15.1.0")
        self._assign("device", self.device_id, assigned["id"])
        self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": other["id"], "status": "installed"},
        )
        # Reporting is audit only: the assignment is untouched.
        self.assertEqual(self._resolved()["version"], "15.0.0")

    def test_report_on_a_release_the_device_could_not_run_changes_nothing(self):
        draft, _, _ = self._register(version="41.0.0", publish=False)
        android, _, _ = self._register(
            platform="android",
            version="41.1.0",
            artifact={
                "url": "https://releases.example/MyProxy-41.1.0.apk",
                "signature": {"type": "apksigner", "subjectSha256": "c" * 64},
            },
        )
        for release_id in (draft["id"], android["id"]):
            status, _ = self._device(
                "POST",
                "/api/device/update/report",
                {"releaseId": release_id, "status": "installed"},
            )
            self.assertEqual(status, 404, release_id)

        _, devices = self._admin("GET", f"/api/admin/device?userId={self.user['id']}")
        self.assertEqual(devices["devices"][0]["clientVersion"], "1.0.0")
        _, audit = self._admin(
            "GET", f"/api/admin/release-audit?targetId={self.device_id}"
        )
        self.assertEqual(audit["entries"], [])

    def test_a_revoked_release_can_still_be_reported(self):
        # It may have been installed before it was pulled; the report is the
        # evidence an administrator needs.
        created, _, _ = self._register(version="41.2.0")
        status, _ = self._admin(
            "POST", f"/api/admin/release/{created['id']}/revoke"
        )
        self.assertEqual(status, 200)
        status, _ = self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": created["id"], "status": "failed", "detail": "pulled"},
        )
        self.assertEqual(status, 200)

    def test_a_draft_revoked_without_ever_being_published_is_not_reportable(self):
        # "revoked" alone is not evidence that any device was offered it: a
        # draft can be revoked straight away.
        draft, _, _ = self._register(version="41.3.0", publish=False)
        status, _ = self._admin("POST", f"/api/admin/release/{draft['id']}/revoke")
        self.assertEqual(status, 200)

        status, _ = self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": draft["id"], "status": "installed"},
        )
        self.assertEqual(status, 404)
        _, devices = self._admin("GET", f"/api/admin/device?userId={self.user['id']}")
        self.assertEqual(devices["devices"][0]["clientVersion"], "1.0.0")

    def test_report_rejects_unknown_status_and_release(self):
        created, _, _ = self._register(version="16.0.0")
        status, _ = self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": created["id"], "status": "exploded"},
        )
        self.assertEqual(status, 400)
        status, _ = self._device(
            "POST",
            "/api/device/update/report",
            {"releaseId": "rel_missing", "status": "installed"},
        )
        self.assertEqual(status, 404)

    # -- admin visibility ----------------------------------------------
    def test_effective_release_explains_where_the_pin_came_from(self):
        created, _, _ = self._register(version="17.0.0")
        self._assign("user", self.user["id"], created["id"], flags={"x": 1})
        status, payload = self._admin(
            "GET", f"/api/admin/device/{self.device_id}/release"
        )
        self.assertEqual(status, 200, payload)
        self.assertEqual(payload["release"]["version"], "17.0.0")
        self.assertEqual(payload["source"], "user")
        self.assertEqual(payload["featureFlags"], {"x": 1})
        self.assertEqual(payload["installedVersion"], "1.0.0")

    def test_audit_records_assignment_changes_with_the_previous_release(self):
        first, _, _ = self._register(version="18.0.0")
        second, _, _ = self._register(version="18.1.0")
        self._assign("device", self.device_id, first["id"])
        self._assign("device", self.device_id, second["id"])
        _, audit = self._admin(
            "GET", f"/api/admin/release-audit?targetId={self.device_id}"
        )
        latest = audit["entries"][0]
        self.assertEqual(latest["action"], "assignment_set")
        self.assertEqual(latest["releaseId"], second["id"])
        self.assertEqual(latest["previousReleaseId"], first["id"])

    def test_admin_update_plane_routes_reject_a_device_token(self):
        for method, path in (
            ("GET", "/api/admin/release"),
            ("GET", "/api/admin/assignment"),
            ("GET", "/api/admin/release-audit"),
            ("GET", f"/api/admin/device/{self.device_id}/release"),
        ):
            with self.subTest(path):
                status, payload = self._api(method, path, None, self.device_token)
                self.assertEqual(status, 401)
                self.assertEqual(payload.get("error", {}).get("code"), "AdminUnauthorized")


class UnconfiguredSigningKeyTest(unittest.TestCase):
    """A deployment with no signing keys must not be able to publish at all."""

    def setUp(self):
        self._tmpdir = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmpdir.cleanup)
        db_path = os.path.join(self._tmpdir.name, "nokeys.db")
        settings = make_settings(db_path, release_signing_keys="")
        self.service = MyProxyService(settings, FakeXuiAdapter())
        self.service.initialize()

    def test_register_release_fails_closed(self):
        from myproxy_server.app import ServiceError

        manifest, signature = signed()
        with self.assertRaises(ServiceError) as ctx:
            self.service.admin_register_release(manifest, signature, KEY_ID)
        self.assertEqual(ctx.exception.code, "ManifestRejected")


class SchemaMigrationTest(unittest.TestCase):
    def test_init_db_is_idempotent_and_adds_update_plane_tables(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "m.db")
            db.init_db(path)
            db.init_db(path)
            with db.connect(path) as conn:
                names = {
                    row[0]
                    for row in conn.execute(
                        "SELECT name FROM sqlite_master WHERE type='table'"
                    )
                }
            self.assertTrue(
                {"releases", "release_assignments", "release_audit"} <= names
            )
            self.assertTrue(db.check_readiness(path))


if __name__ == "__main__":
    unittest.main()
