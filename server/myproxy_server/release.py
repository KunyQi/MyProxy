"""Update Plane: release manifest parsing, validation and signature checking.

This is a *leaf* module in the same sense as :mod:`auth`, :mod:`db` and
:mod:`xui`: it reads no configuration, touches no database and imports nothing
from the rest of the package.  ``app.py`` injects everything it needs.

Two decisions here are load bearing and must not be "simplified" later:

1.  **The manifest is signed as opaque bytes, never as a re-serialised
    object.**  Three independent implementations (Python server, C# Windows
    client, Kotlin Android client) have to agree bit for bit on what was
    signed.  Any canonical-JSON scheme makes that agreement depend on three
    separate serialisers matching on key order, float formatting, Unicode
    escaping and separator whitespace -- a standing source of "signature
    valid on one platform, invalid on another" bugs.  So the signer produces
    the manifest bytes once, and those exact bytes travel base64-encoded
    through every hop.  Everyone verifies first and parses second; nobody
    ever re-encodes.

2.  **The server only verifies, it never signs.**  There is no private key on
    the VPS.  Signing happens offline in the release workflow.  That is what
    makes the acceptance criterion "Control Plane cannot ship arbitrary
    executables" true even against a fully compromised Admin token: an
    attacker holding the Admin token can still only register a release whose
    manifest already carries a valid signature from a key they do not have.

The signature covers ``SIGNING_DOMAIN + manifest_bytes``.  The domain prefix
follows the same convention as the claim message in :mod:`auth`: it keeps a
signature over a release manifest from ever being replayable as a signature
over some other MyProxy structure.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import json
import re
import urllib.parse
from typing import Any


SIGNING_DOMAIN = b"myproxy-release-manifest-v1\x00"

SCHEMA_VERSION = 1
PLATFORMS = ("windows", "android", "linux")
CHANNELS = ("stable", "beta")

# Which platform signature each artifact kind must carry.  The client checks
# the real thing (Authenticode / apksigner / a detached Ed25519 signature)
# after download; the manifest only states what it is supposed to find, so that
# a mismatch is a verification failure rather than a silent install.
#
# Linux has no OS-level signing story for a tarball, so the Linux client
# verifies a detached Ed25519 signature over the artifact bytes against a
# built-in public key -- the same Ed25519 implementation the manifest
# signature already uses, which is why no new crypto dependency appears here.
PLATFORM_SIGNATURE_TYPES = {
    "windows": "authenticode",
    "android": "apksigner",
    "linux": "ed25519",
}

INSTALL_STATES = ("installed", "failed", "rolledback")

_VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")
_HEX64_RE = re.compile(r"^[0-9a-f]{64}$")
_KEY_ID_RE = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
_TIMESTAMP_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$")

MAX_MANIFEST_BYTES = 8192


class ManifestError(Exception):
    """A manifest was malformed, unsigned, or signed by an unknown key.

    The message is for the server log only.  Like ``xui.XuiError``, the HTTP
    layer must not forward it to callers: a precise "which field was wrong"
    reply would let anyone holding the Admin token probe the validator.
    """


# ----------------------------------------------------------------------
# Ed25519 verification (RFC 8032), pure standard library.
# ----------------------------------------------------------------------
# Implemented here rather than pulled from ``cryptography`` because the API
# process runs with a stdlib-only dependency set, and because verification is
# the only half we need -- there is no key generation and no signing on this
# host.  Constants and point arithmetic follow RFC 8032 section 5.1.

_P = 2**255 - 19
_L = 2**252 + 27742317777372353535851937790883648493
_D = -121665 * pow(121666, _P - 2, _P) % _P
_SQRT_M1 = pow(2, (_P - 1) // 4, _P)


def _recover_x(y: int, sign: int) -> int | None:
    if y >= _P:
        return None
    x2 = (y * y - 1) * pow(_D * y * y + 1, _P - 2, _P) % _P
    if x2 == 0:
        return None if sign else 0
    x = pow(x2, (_P + 3) // 8, _P)
    if (x * x - x2) % _P != 0:
        x = x * _SQRT_M1 % _P
    if (x * x - x2) % _P != 0:
        return None
    if (x & 1) != sign:
        x = _P - x
    return x


_BASE_Y = 4 * pow(5, _P - 2, _P) % _P
_BASE_X = _recover_x(_BASE_Y, 0) or 0
# Extended homogeneous coordinates (X, Y, Z, T) with x = X/Z, y = Y/Z.
_BASE = (_BASE_X, _BASE_Y, 1, _BASE_X * _BASE_Y % _P)
_IDENTITY = (0, 1, 1, 0)


def _point_add(p: tuple, q: tuple) -> tuple:
    a = (p[1] - p[0]) * (q[1] - q[0]) % _P
    b = (p[1] + p[0]) * (q[1] + q[0]) % _P
    c = 2 * p[3] * q[3] * _D % _P
    d = 2 * p[2] * q[2] % _P
    e, f, g, h = b - a, d - c, d + c, b + a
    return (e * f % _P, g * h % _P, f * g % _P, e * h % _P)


def _point_mul(scalar: int, point: tuple) -> tuple:
    result = _IDENTITY
    while scalar > 0:
        if scalar & 1:
            result = _point_add(result, point)
        point = _point_add(point, point)
        scalar >>= 1
    return result


def _point_equal(p: tuple, q: tuple) -> bool:
    # Projective coordinates are not unique, so cross-multiply instead of
    # comparing components directly.
    if (p[0] * q[2] - q[0] * p[2]) % _P != 0:
        return False
    return (p[1] * q[2] - q[1] * p[2]) % _P == 0


def _point_decompress(data: bytes) -> tuple | None:
    if len(data) != 32:
        return None
    value = int.from_bytes(data, "little")
    sign = value >> 255
    y = value & ((1 << 255) - 1)
    x = _recover_x(y, sign)
    if x is None:
        return None
    return (x, y, 1, x * y % _P)


def ed25519_verify(public_key: bytes, message: bytes, signature: bytes) -> bool:
    """Return True when ``signature`` is a valid Ed25519 signature.

    Returns False for every malformed input instead of raising: a caller that
    cannot tell "bad key encoding" from "wrong signature" cannot leak the
    difference either.
    """
    if len(public_key) != 32 or len(signature) != 64:
        return False
    point_a = _point_decompress(public_key)
    if point_a is None:
        return False
    encoded_r = signature[:32]
    point_r = _point_decompress(encoded_r)
    if point_r is None:
        return False
    s = int.from_bytes(signature[32:], "little")
    if s >= _L:
        # Non-canonical S; rejecting it keeps signatures non-malleable.
        return False
    k = (
        int.from_bytes(
            hashlib.sha512(encoded_r + public_key + message).digest(), "little"
        )
        % _L
    )
    return _point_equal(
        _point_mul(s, _BASE), _point_add(point_r, _point_mul(k, point_a))
    )


# ----------------------------------------------------------------------
# Signing keys
# ----------------------------------------------------------------------


def parse_public_keys(spec: str) -> dict[str, bytes]:
    """Parse ``keyId:hex,keyId2:hex`` into ``{key_id: raw_public_key}``.

    Raises ValueError on anything malformed so that a typo in the deployment
    environment fails at start-up rather than silently disabling the gate.
    """
    keys: dict[str, bytes] = {}
    for item in (spec or "").split(","):
        text = item.strip()
        if not text:
            continue
        key_id, _, hex_key = text.partition(":")
        key_id = key_id.strip()
        hex_key = hex_key.strip().lower()
        if not _KEY_ID_RE.fullmatch(key_id):
            raise ValueError("release signing key id is invalid")
        if len(hex_key) != 64:
            raise ValueError(f"release signing key {key_id} must be 32 bytes of hex")
        try:
            raw = bytes.fromhex(hex_key)
        except ValueError as exc:
            raise ValueError(f"release signing key {key_id} is not hex") from exc
        if key_id in keys:
            raise ValueError(f"release signing key {key_id} is declared twice")
        keys[key_id] = raw
    return keys


# ----------------------------------------------------------------------
# Manifest
# ----------------------------------------------------------------------


def _decode_b64(value: Any, what: str, limit: int) -> bytes:
    if not isinstance(value, str) or not value:
        raise ManifestError(f"{what} is missing")
    if len(value) > limit * 2:
        raise ManifestError(f"{what} is too large")
    try:
        # validate=True rejects whitespace and stray characters, so the bytes
        # that were signed are the only bytes this can ever produce.
        raw = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ManifestError(f"{what} is not valid base64") from exc
    if len(raw) > limit:
        raise ManifestError(f"{what} is too large")
    return raw


def _require_str(doc: dict, key: str, allowed: tuple[str, ...] | None = None) -> str:
    value = doc.get(key)
    if not isinstance(value, str) or not value:
        raise ManifestError(f"manifest field {key} is missing")
    if allowed is not None and value not in allowed:
        raise ManifestError(f"manifest field {key} is not allowed")
    return value


def validate_manifest(doc: Any) -> dict[str, Any]:
    """Validate a parsed manifest and return a normalised summary.

    The summary is what the Server stores in indexed columns.  The manifest
    bytes themselves stay authoritative; these columns exist only so the Admin
    API can list and resolve releases without re-parsing every row.
    """
    if not isinstance(doc, dict):
        raise ManifestError("manifest is not an object")
    if doc.get("schemaVersion") != SCHEMA_VERSION:
        raise ManifestError("manifest schemaVersion is unsupported")

    platform = _require_str(doc, "platform", PLATFORMS)
    version = _require_str(doc, "version")
    if not _VERSION_RE.fullmatch(version):
        raise ManifestError("manifest version is not a valid version string")
    channel = _require_str(doc, "channel", CHANNELS)

    mandatory = doc.get("mandatory", False)
    if not isinstance(mandatory, bool):
        raise ManifestError("manifest mandatory must be a boolean")

    issued_at = _require_str(doc, "issuedAt")
    if not _TIMESTAMP_RE.fullmatch(issued_at):
        raise ManifestError("manifest issuedAt is not an ISO-8601 UTC timestamp")

    artifact = doc.get("artifact")
    if not isinstance(artifact, dict):
        raise ManifestError("manifest artifact is missing")

    url = _require_str(artifact, "url")
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https" or not parsed.hostname:
        raise ManifestError("artifact url must be https with a host")
    if parsed.username or parsed.password:
        raise ManifestError("artifact url must not carry credentials")

    sha256 = _require_str(artifact, "sha256")
    if not _HEX64_RE.fullmatch(sha256):
        raise ManifestError("artifact sha256 must be 64 lowercase hex characters")

    size = artifact.get("size")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise ManifestError("artifact size must be a positive integer")

    signature = artifact.get("signature")
    if not isinstance(signature, dict):
        raise ManifestError("artifact signature block is missing")
    sig_type = _require_str(signature, "type")
    if sig_type != PLATFORM_SIGNATURE_TYPES[platform]:
        raise ManifestError("artifact signature type does not match the platform")
    subject = _require_str(signature, "subjectSha256")
    if not _HEX64_RE.fullmatch(subject):
        raise ManifestError(
            "artifact signature subjectSha256 must be 64 hex characters"
        )

    minimum_version = doc.get("minimumVersion")
    if minimum_version is not None:
        if not isinstance(minimum_version, str) or not _VERSION_RE.fullmatch(
            minimum_version
        ):
            raise ManifestError("manifest minimumVersion is not a valid version string")

    return {
        "platform": platform,
        "version": version,
        "channel": channel,
        "mandatory": mandatory,
        "artifactUrl": url,
        "artifactSha256": sha256,
        "artifactSize": size,
        "platformSignatureType": sig_type,
        "platformSignatureSubjectSha256": subject,
        "issuedAt": issued_at,
        "minimumVersion": minimum_version or "",
    }


def verify_and_parse(
    manifest_b64: Any,
    signature_b64: Any,
    key_id: Any,
    keys: dict[str, bytes],
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Verify a submitted manifest and return ``(summary, parsed_document)``.

    Order matters: the signature is checked over the raw bytes *before* the
    JSON is parsed, so a malformed or hostile document never reaches the
    parser on the strength of an unverified claim.
    """
    if not keys:
        # Fail closed.  A deployment without configured signing keys must not
        # be able to register releases at all -- silently skipping the check
        # would turn the Update Plane into exactly the arbitrary-payload
        # channel this design exists to prevent.
        raise ManifestError("no release signing keys are configured")
    if not isinstance(key_id, str) or key_id not in keys:
        raise ManifestError("release signing key id is unknown")

    manifest_bytes = _decode_b64(manifest_b64, "manifest", MAX_MANIFEST_BYTES)
    signature = _decode_b64(signature_b64, "signature", 64)
    if len(signature) != 64:
        raise ManifestError("signature must be 64 bytes")

    if not ed25519_verify(keys[key_id], SIGNING_DOMAIN + manifest_bytes, signature):
        raise ManifestError("release manifest signature is invalid")

    try:
        doc = json.loads(manifest_bytes.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ManifestError("manifest is not valid UTF-8 JSON") from exc

    return validate_manifest(doc), doc
