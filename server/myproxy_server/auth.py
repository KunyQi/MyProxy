"""Token generation, hashing, Bearer parsing and pairing-code helpers."""

from __future__ import annotations

import base64
import hashlib
import hmac
import re
import secrets


def generate_device_token() -> str:
    """Return a new device token: ``tok_`` + 32 url-safe base64 chars."""
    return "tok_" + secrets.token_urlsafe(32)


def hash_token(token: str) -> str:
    """Return the SHA-256 hex digest of *token*."""
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def derive_device_token(
    secret: str,
    binding_id: str,
    device_id: str,
    client_instance_id: str,
) -> str:
    """Derive a replayable device token without persisting its plaintext.

    ``client_instance_id`` is a client-generated high-entropy stable value.
    The server secret lives outside SQLite, so a database-only disclosure still
    exposes token hashes rather than replayable device tokens.
    """
    if not secret:
        raise ValueError("device token secret must not be empty")
    message = "\0".join(
        ("myproxy-claim-v1", binding_id, device_id, client_instance_id)
    ).encode("utf-8")
    digest = hmac.new(secret.encode("utf-8"), message, hashlib.sha256).digest()
    encoded = base64.urlsafe_b64encode(digest).decode("ascii").rstrip("=")
    return "tok_" + encoded


def extract_bearer(authorization: str | None) -> str | None:
    """Extract a bearer token from an Authorization header value.

    Returns ``None`` for missing/malformed values or non-Bearer schemes.
    """
    if not authorization:
        return None
    match = re.fullmatch(r"Bearer\s+(\S+)", authorization.strip(), re.IGNORECASE)
    if not match:
        return None
    return match.group(1)


def timing_safe_equal(a: str, b: str) -> bool:
    """Constant-time string comparison.

    ``hmac.compare_digest`` raises ``TypeError`` for ``str`` arguments outside
    ASCII.  Comparing the UTF-8 encodings keeps the comparison constant time
    while letting a non-ASCII candidate produce a plain mismatch, so it is
    rejected with the normal unauthorized response and audit log instead of a
    500 that leaves no trace.
    """
    if not isinstance(a, str) or not isinstance(b, str):
        return False
    return hmac.compare_digest(a.encode("utf-8"), b.encode("utf-8"))


def normalize_pairing_code(raw: str | None) -> str | None:
    """Normalize a pairing code to the canonical form ``XXXX-XXXX``.

    The accepted inputs are, after trimming whitespace, any combination of
    exactly 8 alphanumeric characters optionally separated by ``-`` or spaces.
    Examples: ``A7K9M2QF``, ``a7k9 m2qf`` and ``A7K9-M2QF`` all normalize to
    ``A7K9-M2QF``.  Invalid input returns ``None``.
    """
    if raw is None or not isinstance(raw, str):
        return None
    stripped = raw.strip()
    if not stripped:
        return None
    compact = stripped.replace("-", "").replace(" ", "")
    if len(compact) != 8 or not re.fullmatch(r"[A-Za-z0-9]{8}", compact):
        return None
    upper = compact.upper()
    return f"{upper[:4]}-{upper[4:]}"
