"""MyProxyService business orchestration.

This module deliberately does not touch HTTP.  Expected errors are raised as
:class:`ServiceError`; unknown exceptions are allowed to propagate so the HTTP
layer can map them to ``500 ServerError``.
"""

from __future__ import annotations

import json
import re
import secrets
import sqlite3
import threading
import time
import hashlib
from datetime import datetime, timedelta
from typing import Any

from . import __version__, artifact, auth, db, observability, release
from .config import Settings
from .xui import XuiAdapter, XuiError, new_myproxy_email

_PUBLIC_PROFILE_KEYS = (
    "server",
    "port",
    "security",
    "publicKey",
    "shortId",
    "sni",
    "fingerprint",
    "flow",
    "spiderX",
)

_CLIENT_INSTANCE_ID_RE = re.compile(r"^[A-Za-z0-9._~-]{16,128}$")
_FEATURE_FLAG_KEY_RE = re.compile(r"^[a-zA-Z][A-Za-z0-9._-]{0,63}$")
_RELEASE_STATUSES = ("draft", "published", "revoked")
_ASSIGNMENT_SCOPES = ("device", "user", "platform")
_META_USAGE_LAST_SWEEP = "usage_last_sweep_at"

# Persistent key names for each platform's latest release metadata.
_META_LATEST_VERSION = "latest_version"
_META_LATEST_DOWNLOAD_URL = "latest_download_url"
_META_LATEST_SHA256 = "latest_sha256"
_META_LATEST_MANDATORY = "latest_mandatory"
_META_ANDROID_LATEST_VERSION = "android_latest_version"
_META_ANDROID_LATEST_DOWNLOAD_URL = "android_latest_download_url"
_META_ANDROID_LATEST_SHA256 = "android_latest_sha256"
_META_ANDROID_LATEST_MANDATORY = "android_latest_mandatory"
_META_LINUX_LATEST_VERSION = "linux_latest_version"
_META_LINUX_LATEST_DOWNLOAD_URL = "linux_latest_download_url"
_META_LINUX_LATEST_SHA256 = "linux_latest_sha256"
_META_LINUX_LATEST_MANDATORY = "linux_latest_mandatory"

_LATEST_META_KEYS = {
    "windows": (
        _META_LATEST_VERSION,
        _META_LATEST_DOWNLOAD_URL,
        _META_LATEST_SHA256,
        _META_LATEST_MANDATORY,
    ),
    "android": (
        _META_ANDROID_LATEST_VERSION,
        _META_ANDROID_LATEST_DOWNLOAD_URL,
        _META_ANDROID_LATEST_SHA256,
        _META_ANDROID_LATEST_MANDATORY,
    ),
    "linux": (
        _META_LINUX_LATEST_VERSION,
        _META_LINUX_LATEST_DOWNLOAD_URL,
        _META_LINUX_LATEST_SHA256,
        _META_LINUX_LATEST_MANDATORY,
    ),
}


class ServiceError(Exception):
    """An expected, user-visible service error."""

    def __init__(self, code: str, status: int = 400) -> None:
        super().__init__(code)
        self.code = code
        self.status = status


# Only a rejection that says "this attempt was not legitimate for this code"
# counts against the per-code lockout.  Counting infrastructure failures let a
# brief x-ui/helper outage turn five honest retries into a 600-second lockout
# on a freshly issued pairing code, recoverable only by re-issuing it.
_CLAIM_FAILURE_CODES = frozenset({"PairingInvalid"})


def _counts_as_claim_failure(exc: BaseException) -> bool:
    return isinstance(exc, ServiceError) and exc.code in _CLAIM_FAILURE_CODES


def _parse_utc(value: str) -> datetime:
    """Parse a UTC timestamp (``YYYY-MM-DDTHH:MM:SSZ``) to datetime."""
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    return datetime.fromisoformat(text)


def _format_utc(dt: datetime) -> str:
    """Format a datetime as a UTC timestamp (second precision)."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=None)  # db.utc_now is UTC by contract
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


class MyProxyService:
    """Business orchestration for the MyProxy control plane."""

    def __init__(self, settings: Settings, xui: XuiAdapter) -> None:
        self.settings = settings
        self.artifacts = artifact.ArtifactStore(settings)
        # Serialize mutations of one release. Streaming a new large draft
        # must not hold up downloads of already published versions.
        self._release_artifact_locks_guard = threading.Lock()
        self._release_artifact_locks: dict[str, threading.RLock] = {}
        self.xui = xui
        self._claim_lock = threading.Lock()
        # 可观测性扫描的单飞锁：见 _sweep_xui_counters。
        self._sweep_lock = threading.Lock()
        self._ui_lock = threading.Lock()
        self._ui_tickets: dict[str, float] = {}
        self._ui_sessions: dict[str, float] = {}

    def _prune_ui_access(self) -> None:
        now = time.monotonic()
        for store in (self._ui_tickets, self._ui_sessions):
            for key, deadline in list(store.items()):
                if deadline <= now:
                    del store[key]

    @staticmethod
    def _ui_digest(value: str) -> str:
        return hashlib.sha256(value.encode("utf-8")).hexdigest()

    def issue_ui_ticket(self) -> dict:
        with self._ui_lock:
            self._prune_ui_access()
            if len(self._ui_tickets) >= 128:
                raise ServiceError("RateLimited", 429)
            ticket = secrets.token_urlsafe(32)
            self._ui_tickets[self._ui_digest(ticket)] = time.monotonic() + 60
            return {"ticket": ticket, "expiresIn": 60}

    def redeem_ui_ticket(self, ticket: str) -> dict:
        with self._ui_lock:
            self._prune_ui_access()
            digest = self._ui_digest(ticket)
            if digest not in self._ui_tickets:
                raise ServiceError("AdminUnauthorized", 401)
            if len(self._ui_sessions) >= 128:
                raise ServiceError("RateLimited", 429)
            del self._ui_tickets[digest]
            token = secrets.token_urlsafe(32)
            self._ui_sessions[self._ui_digest(token)] = time.monotonic() + 3600
            return {"token": token, "expiresIn": 3600}

    def ui_session_authorized(self, token: str) -> bool:
        with self._ui_lock:
            self._prune_ui_access()
            return self._ui_digest(token) in self._ui_sessions

    def revoke_ui_session(self, token: str) -> None:
        with self._ui_lock:
            self._ui_sessions.pop(self._ui_digest(token), None)

    def _with_claim_lock(self, fn, *args, **kwargs):
        """Serialize data-plane mutations with claim provisioning in-process."""
        with self._claim_lock:
            return fn(*args, **kwargs)

    # ------------------------------------------------------------------
    # lifecycle / helpers
    # ------------------------------------------------------------------
    def initialize(self) -> None:
        """Initialise SQLite schema and make sure config version 1 exists."""
        db.init_db(self.settings.db_path)
        self.ensure_current_profile()

    def readiness(self) -> dict[str, bool]:
        """Return non-sensitive DB/x-ui readiness checks.

        ``healthz`` remains a cheap liveness endpoint.  This method is used by
        the private ``readyz`` endpoint and deliberately suppresses adapter
        exceptions and all diagnostic details; operators can inspect the
        structured service log or systemd status locally instead.
        """
        db_ready = db.check_readiness(self.settings.db_path)
        xui_ready = False
        try:
            # public_profile performs a read-only inbound/profile query.  It
            # is the smallest x-ui probe that proves the configured inbound is
            # available without returning profile data in the readiness body.
            self.xui.public_profile()
            xui_ready = True
        except Exception:
            xui_ready = False
        return {"db": db_ready, "xui": xui_ready, "ok": db_ready and xui_ready}

    def ensure_current_profile(self) -> None:
        """Create/refresh config_versions version 1 if it is missing or ``{}``.

        An existing, non-empty profile (a real profile) is never overwritten.
        The public profile template stores ``uuid`` as an empty string.
        """
        with db.connect(self.settings.db_path) as conn:
            row = conn.execute(
                "SELECT config_json FROM config_versions WHERE version = 1"
            ).fetchone()
            if row is not None:
                try:
                    current = json.loads(row[0])
                except Exception:
                    current = {}
                if isinstance(current, dict) and current:
                    return
            # Only call the xui adapter when version 1 is absent or empty.
            profile = self._public_profile_template(self.xui.public_profile())
            if row is None:
                conn.execute(
                    "INSERT INTO config_versions (version, config_json, note, created_at)"
                    " VALUES (1, ?, '', ?)",
                    (json.dumps(profile, ensure_ascii=False), db.utc_now()),
                )
            else:
                conn.execute(
                    "UPDATE config_versions SET config_json = ?, note = '', created_at = ?"
                    " WHERE version = 1",
                    (json.dumps(profile, ensure_ascii=False), db.utc_now()),
                )

    def _now(self, now: str | None) -> str:
        return now or db.utc_now()

    def _public_profile_template(self, profile: dict[str, Any]) -> dict[str, Any]:
        """Return a public ServerProfile template (uuid empty).

        Only public profile keys are kept; a malicious/accidental
        ``privateKey`` returned by an adapter is never copied.
        """
        defaults = {
            "server": self.settings.server_host,
            "port": self.settings.server_port or 443,
            "security": "reality",
            "publicKey": self.settings.public_key,
            "shortId": self.settings.short_id,
            "sni": self.settings.sni,
            "fingerprint": self.settings.fingerprint,
            "flow": self.settings.flow,
            "spiderX": self.settings.spider_x,
        }
        template = {}
        for key in _PUBLIC_PROFILE_KEYS:
            value = profile.get(key)
            template[key] = defaults[key] if value is None else value
        template["uuid"] = ""
        return template

    def _config_from_template(self, template: Any, uuid: str) -> dict[str, Any]:
        if isinstance(template, str):
            try:
                template = json.loads(template)
            except Exception:
                template = {}
        if not isinstance(template, dict):
            template = {}
        # Never echo arbitrary persisted keys.  In particular, a malformed or
        # manually edited config_versions row must not become a private-key
        # exfiltration path.
        config = {key: template[key] for key in _PUBLIC_PROFILE_KEYS if key in template}
        config["uuid"] = uuid
        return config

    @staticmethod
    def _truncate(value: Any, limit: int) -> str:
        if value is None:
            return ""
        if not isinstance(value, str):
            raise ServiceError("BadRequest", 400)
        return str(value)[:limit]

    @staticmethod
    def _add_seconds(timestamp: str, seconds: int) -> str:
        return _format_utc(_parse_utc(timestamp) + timedelta(seconds=seconds))

    @staticmethod
    def _failure_within_window(last_failed_at: str | None, now: str, window_seconds: int) -> bool:
        if not last_failed_at:
            return False
        try:
            last = _parse_utc(last_failed_at)
            current = _parse_utc(now)
        except Exception:
            return False
        return last + timedelta(seconds=window_seconds) >= current

    def _record_failure(self, binding_id: str, now: str) -> None:
        """Persist a per-binding failure outside the rolled-back claim tx."""
        try:
            with db.connect(self.settings.db_path) as conn:
                db.mark_binding_failed(conn, binding_id, now)
        except Exception:
            pass

    def _find_client_uuid(self, email: str) -> str:
        for client in self.xui.list_clients():
            if client.get("email") == email:
                return client.get("id") or ""
        raise ServiceError("ServerError", 500)

    def _normalize_client_instance_id(self, value: Any) -> str:
        """Validate the optional installation-scoped claim identity."""
        if value is None or value == "":
            return ""
        if not isinstance(value, str):
            raise ServiceError("BadRequest", 400)
        normalized = value.strip()
        if not _CLIENT_INSTANCE_ID_RE.fullmatch(normalized):
            raise ServiceError("BadRequest", 400)
        return normalized

    @staticmethod
    def _client_instance_hash(client_instance_id: str) -> str | None:
        if not client_instance_id:
            return None
        return auth.hash_token("myproxy-client-instance-v1\0" + client_instance_id)

    def _device_token_secret(self) -> str:
        # ``run()`` requires a dedicated production secret.  The Admin Token
        # fallback exists only for older direct service constructors in tests
        # and embedders that do not use the production entry point.
        secret = self.settings.device_token_secret or self.settings.admin_token
        if not secret:
            raise ServiceError("ServerError", 500)
        return secret

    @staticmethod
    def _find_device_for_claim(
        conn,
        user_id: str,
        device_name: str,
        platform: str,
        client_instance_id_hash: str | None,
    ) -> dict[str, Any] | None:
        if client_instance_id_hash:
            device = db.find_active_device_by_instance(
                conn, user_id, platform, client_instance_id_hash
            )
            if device is not None:
                return device

            # Upgrade an existing legacy row in-place on the first claim made
            # by a client version that has gained a stable instance id.
            legacy = db.find_active_device_by_name(
                conn, user_id, device_name, platform
            )
            if legacy is not None:
                legacy_id = legacy.get("id") or legacy.get("deviceId") or ""
                if legacy_id and db.get_device_instance_hash(conn, legacy_id) is None:
                    return legacy
            return None
        return db.find_active_device_by_name(conn, user_id, device_name, platform)

    def _replay_claim(
        self,
        link: dict[str, Any],
        client_instance_id: str,
        client_instance_id_hash: str | None,
        platform: str,
        now: str,
    ) -> dict[str, Any]:
        """Replay a completed claim without provisioning another client."""
        if not client_instance_id or not client_instance_id_hash:
            raise ServiceError("PairingInvalid", 400)
        claimed_at = link.get("claimedAt")
        device_id = link.get("claimedDeviceId") or ""
        if not claimed_at or not device_id:
            raise ServiceError("PairingInvalid", 400)
        try:
            replay_deadline = _parse_utc(claimed_at) + timedelta(
                seconds=max(1, self.settings.claim_window_seconds)
            )
            if _parse_utc(now) > replay_deadline:
                raise ServiceError("PairingInvalid", 400)
        except ServiceError:
            raise
        except Exception:
            raise ServiceError("PairingInvalid", 400)

        with db.connect(self.settings.db_path) as conn:
            device = db.get_device(conn, device_id)
            stored_instance_hash = db.get_device_instance_hash(conn, device_id)
            stored_token_hash = db.get_device_token_hash(conn, device_id)
            latest = db.latest_config(conn)
        if (
            device is None
            or device.get("status") != "active"
            or device.get("userId") != link.get("userId")
            or device.get("platform") != platform
            or not stored_instance_hash
            or not stored_token_hash
            or not auth.timing_safe_equal(
                stored_instance_hash, client_instance_id_hash
            )
            or latest is None
        ):
            raise ServiceError("PairingInvalid", 400)

        token = auth.derive_device_token(
            self._device_token_secret(),
            link["id"],
            device_id,
            client_instance_id,
        )
        if not auth.timing_safe_equal(auth.hash_token(token), stored_token_hash):
            # A later rebind has already rotated this device.  Replaying an old
            # link must never resurrect the superseded credential.
            raise ServiceError("PairingInvalid", 400)

        email = device.get("xrayClientEmail") or ""
        xray_uuid = self._find_client_uuid(email)
        config = self._config_from_template(latest.get("config"), xray_uuid)
        with db.connect(self.settings.db_path) as conn:
            db.touch_device_seen(conn, device_id, now)
        return {
            "deviceId": device_id,
            "deviceToken": token,
            "configVersion": latest["version"],
            "config": config,
        }

    # ------------------------------------------------------------------
    # device-facing methods
    # ------------------------------------------------------------------
    def claim(
        self,
        pairing_code: str | None,
        device_name: str | None,
        platform: str | None,
        client_version: str | None,
        client_instance_id: str | None = None,
        now: str | None = None,
    ) -> dict[str, Any]:
        if pairing_code is not None and not isinstance(pairing_code, str):
            raise ServiceError("BadRequest", 400)
        code = auth.normalize_pairing_code(pairing_code)
        if code is None:
            raise ServiceError("BadRequest", 400)

        device_name = self._truncate(device_name, 128).strip()
        platform = self._truncate(platform, 32).strip() or "windows"
        client_version = self._truncate(client_version, 32).strip()
        client_instance_id = self._normalize_client_instance_id(client_instance_id)
        client_instance_id_hash = self._client_instance_hash(client_instance_id)
        if not device_name:
            raise ServiceError("BadRequest", 400)
        now = self._now(now)

        active_binding_id: str | None = None
        counted_binding_id: str | None = None
        old_email = ""
        new_email = ""
        device_id = ""
        new_client_added = False
        old_client_disable_attempted = False
        token = ""

        # The lock prevents two claim workflows in this process from
        # provisioning competing x-ui clients.  The conditional DB update
        # below remains the cross-process guard.
        with self._claim_lock:
            try:
                # Read and validate only.  No database transaction is held
                # while x-ui writes or restarts.
                with db.connect(self.settings.db_path) as conn:
                    link = db.get_binding_by_code(conn, code)
                    if link is None:
                        raise ServiceError("PairingInvalid", 400)

                    binding_id = link["id"]

                    # The per-code lockout is checked before any status branch.
                    # Behind the 'claimed' branch it never applied to replay
                    # attempts at all, because _replay_claim returns first.
                    if (
                        link.get("failedAttempts", 0)
                        >= self.settings.claim_max_failures_per_code
                        and self._failure_within_window(
                            link.get("lastFailedAt"),
                            now,
                            self.settings.claim_window_seconds,
                        )
                    ):
                        raise ServiceError("RateLimited", 429)

                    # Every rejected attempt from here on counts against this
                    # code -- including a failed replay.
                    counted_binding_id = binding_id

                    # Revocation is terminal and outranks both the TTL and the
                    # replay window: a revoked link must answer "invalid",
                    # must never be rewritten to 'expired', and must not be
                    # replayable for the rest of its claim window.
                    if link["status"] == "revoked":
                        raise ServiceError("PairingInvalid", 400)
                    if link["status"] == "claimed":
                        return self._replay_claim(
                            link,
                            client_instance_id,
                            client_instance_id_hash,
                            platform,
                            now,
                        )
                    if link["status"] == "expired" or link["expiresAt"] <= now:
                        db.mark_binding_expired(conn, binding_id)
                        conn.commit()
                        raise ServiceError("PairingExpired", 400)

                    active_binding_id = binding_id
                    existing = self._find_device_for_claim(
                        conn,
                        link["userId"],
                        device_name,
                        platform,
                        client_instance_id_hash,
                    )
                    if existing is None:
                        device_id = db.new_id("dev_")
                    else:
                        device_id = existing.get("id") or existing.get("deviceId") or ""
                        old_email = existing.get("xrayClientEmail") or ""
                        if not device_id:
                            raise ServiceError("ServerError", 500)
                    if client_instance_id:
                        token = auth.derive_device_token(
                            self._device_token_secret(),
                            binding_id,
                            device_id,
                            client_instance_id,
                        )
                    else:
                        token = auth.generate_device_token()
                    token_hash = auth.hash_token(token)
                    new_email = new_myproxy_email(device_id)

                # Provision x-ui outside the SQLite transaction.
                # The adapter may commit the client before a later restart
                # failure is reported, so compensation must be attempted even
                # when add_client itself raises.
                new_client_added = True
                xray_uuid = self.xui.add_client(new_email)
                if not isinstance(xray_uuid, str) or not xray_uuid:
                    raise XuiError("x-ui returned an invalid client id")

                if old_email and old_email != new_email:
                    try:
                        # x-ui persists the mutation before restarting Xray.
                        # A restart failure can therefore be reported after the
                        # old client was already disabled.  Arm compensation
                        # before calling the adapter, just as add_client does.
                        old_client_disable_attempted = True
                        self.xui.update_client(old_email, enable=False)
                    except XuiError as exc:
                        # The old client is already gone from x-ui, so the
                        # rebind can proceed.  Branch on the structured code:
                        # the production adapter collapses every helper failure
                        # into one generic message, and matching that text
                        # would both never fire here and wrongly swallow a
                        # missing inbound.
                        if exc.code != "client_not_found":
                            raise

                # Commit only the short database section after x-ui is ready.
                with db.connect(self.settings.db_path) as conn:
                    # Re-check the code *and its owner* under the write lock:
                    # the user may have been deleted while x-ui was being
                    # called.  Failing here runs the compensation below, which
                    # removes the client that was just added.
                    conn.execute("BEGIN IMMEDIATE")
                    current = db.get_binding_by_code(conn, code)
                    if current is None or current["status"] != "active":
                        raise ServiceError("PairingInvalid", 400)
                    if current["expiresAt"] <= now:
                        db.mark_binding_expired(conn, current["id"])
                        conn.commit()
                        raise ServiceError("PairingExpired", 400)
                    owner = db.get_user(conn, current["userId"])
                    if owner is None or owner.get("status") != "active":
                        raise ServiceError("PairingInvalid", 400)

                    current_device = self._find_device_for_claim(
                        conn,
                        current["userId"],
                        device_name,
                        platform,
                        client_instance_id_hash,
                    )
                    if current_device is None:
                        if old_email:
                            raise ServiceError("Conflict", 409)
                        self._insert_device(
                            conn,
                            device_id=device_id,
                            user_id=current["userId"],
                            device_name=device_name,
                            platform=platform,
                            client_version=client_version,
                            token_hash=token_hash,
                            xray_client_email=new_email,
                            client_instance_id_hash=client_instance_id_hash,
                            now=now,
                        )
                    else:
                        if current_device.get("id") != device_id:
                            raise ServiceError("Conflict", 409)
                        rotated = db.rotate_device_token(
                            conn,
                            device_id,
                            token_hash,
                            new_email,
                            now,
                            client_instance_id_hash,
                            device_name,
                            client_version,
                        )
                        if rotated is None:
                            raise ServiceError("ServerError", 500)

                    claimed = db.mark_binding_claimed(
                        conn, current["id"], device_id
                    )
                    if claimed is None:
                        raise ServiceError("PairingInvalid", 400)
                    conn.execute(
                        "UPDATE binding_links SET failed_attempts = 0,"
                        " last_failed_at = NULL WHERE id = ?",
                        (current["id"],),
                    )
                    db.touch_device_seen(conn, device_id, now)
                    latest = db.latest_config(conn)
                    if latest is None:
                        raise ServiceError("ServerError", 500)
                    config_version = latest["version"]
                    config = self._config_from_template(
                        latest.get("config"), xray_uuid
                    )
            except Exception as exc:
                if counted_binding_id is not None and _counts_as_claim_failure(exc):
                    self._record_failure(counted_binding_id, now)
                if old_client_disable_attempted and old_email:
                    try:
                        self.xui.update_client(old_email, enable=True)
                    except Exception:
                        pass
                if new_client_added and new_email:
                    try:
                        self.xui.remove_client(new_email)
                    except Exception:
                        pass
                raise

        return {
            "deviceId": device_id,
            "deviceToken": token,
            "configVersion": config_version,
            "config": config,
        }

    def _insert_device(
        self,
        conn: sqlite3.Connection,
        *,
        device_id: str,
        user_id: str,
        device_name: str,
        platform: str,
        client_version: str,
        token_hash: str,
        xray_client_email: str,
        client_instance_id_hash: str | None,
        now: str,
    ) -> None:
        conn.execute(
            "INSERT INTO devices ("
            " id, user_id, device_name, platform, client_version,"
            " client_instance_id_hash, token_hash,"
            " status, xray_client_email, token_created_at, token_expires_at,"
            " last_seen_at, created_at, updated_at)"
            " VALUES (?, ?, ?, ?, ?, ?, ?, 'active', ?, ?, NULL, NULL, ?, ?)",
            (
                device_id,
                user_id,
                device_name,
                platform,
                client_version,
                client_instance_id_hash,
                token_hash,
                xray_client_email,
                now,
                now,
                now,
            ),
        )

    def get_device_config(self, token: str, now: str | None = None) -> dict:
        if not token:
            raise ServiceError("TokenInvalid", 401)
        now = self._now(now)

        with db.connect(self.settings.db_path) as conn:
            device = db.get_device_by_token_hash(conn, auth.hash_token(token))
            if device is None or device.get("status") != "active":
                raise ServiceError("TokenInvalid", 401)
            device_id = device.get("id") or device.get("deviceId")
            email = device.get("xrayClientEmail") or ""
            latest = db.latest_config(conn)
            if latest is None:
                raise ServiceError("ServerError", 500)
            config_version = latest["version"]
            template = latest.get("config")

        uuid = self._find_client_uuid(email)
        config = self._config_from_template(template, uuid)

        with db.connect(self.settings.db_path) as conn:
            db.touch_device_seen(conn, device_id, now)

        return {"configVersion": config_version, "config": config}

    def heartbeat(
        self, token: str, now: str | None = None, client_ip: str = ""
    ) -> dict:
        if not token:
            raise ServiceError("TokenInvalid", 401)
        now = self._now(now)
        # Best effort and throttled: a heartbeat must not get slower or start
        # failing because the data plane is unhappy, so the sweep swallows its
        # own errors and runs at most once per configured interval.
        self._sweep_xui_counters(now)

        with db.connect(self.settings.db_path) as conn:
            # One write transaction from the status check to the last write.
            # Read outside it, a device deleted in between -- whose rows the
            # deletion has just cleared -- got its full address written
            # straight back into device_activity and device_addresses, and
            # nothing ever clears those again.
            conn.execute("BEGIN IMMEDIATE")
            device = db.get_device_by_token_hash(conn, auth.hash_token(token))
            if device is None or device.get("status") != "active":
                raise ServiceError("TokenInvalid", 401)
            device_id = device.get("id") or device.get("deviceId")
            latest = db.latest_config(conn)
            if latest is None:
                raise ServiceError("ServerError", 500)
            config_version = latest["version"]
            # Resolved on every beat so that an assignment change reaches a
            # running client within one heartbeat interval without the client
            # having to poll a second endpoint.  Only the identity of the
            # release travels here; the signed manifest still has to be
            # fetched from /api/device/update and verified there.
            picked, flags, source = self._resolve_update(conn, device)
            db.touch_device_seen(conn, device_id, now)
            self._note_client_address(
                conn, device_id, device.get("userId") or "", now, client_ip
            )

        return {
            "ok": True,
            "configVersion": config_version,
            "serverTime": now,
            "release": (
                None
                if picked is None
                else {
                    "releaseId": picked["id"],
                    "version": picked["version"],
                    "channel": picked["channel"],
                    "mandatory": picked["mandatory"],
                    "source": source,
                }
            ),
            "featureFlags": flags,
        }

    @staticmethod
    def _latest_meta_keys(platform: str) -> tuple[str, str, str, str]:
        try:
            return _LATEST_META_KEYS[platform]
        except KeyError:
            raise ServiceError("BadRequest", 400)

    def latest_info(self, platform: str = "windows") -> dict:
        version_key, url_key, sha_key, mandatory_key = self._latest_meta_keys(
            platform
        )
        with db.connect(self.settings.db_path) as conn:
            default_version = __version__ if platform == "windows" else "0.1.0"
            version = db.get_meta(conn, version_key) or default_version
            download_url = db.get_meta(conn, url_key) or ""
            sha256 = db.get_meta(conn, sha_key) or ""
            mandatory = (
                db.get_meta(conn, mandatory_key) or "false"
            ).lower() == "true"
            # The platform-scope assignment is the Update Plane's answer to
            # "update checks must not depend on the Control Plane being
            # reachable": this route needs no device token, so a client that
            # cannot authenticate can still fetch and verify a signed
            # manifest.  Per-device pins deliberately never appear here --
            # that would leak one device's assignment to anyone who asks.
            assignment = db.get_assignment(conn, "platform", "", platform)
            default_release = None
            payload = None
            release_id = (assignment or {}).get("releaseId") or ""
            if release_id:
                candidate = db.get_release(conn, release_id)
                if candidate is not None and candidate["status"] == "published":
                    default_release = candidate
                    payload = db.get_release_payload(conn, release_id)

        if default_release is not None and payload is not None:
            # When a signed release exists it is authoritative: leaving the
            # legacy server_meta values in place would let the two disagree,
            # and an older client reads exactly those fields.
            return {
                "version": default_release["version"],
                "downloadUrl": default_release["artifactUrl"],
                "sha256": default_release["artifactSha256"],
                "mandatory": default_release["mandatory"],
                "releaseId": default_release["id"],
                "manifest": payload["manifest"],
                "signature": payload["signature"],
                "signingKeyId": payload["signingKeyId"],
            }
        return {
            "version": version,
            "downloadUrl": download_url,
            "sha256": sha256,
            "mandatory": mandatory,
        }

    # ------------------------------------------------------------------
    # admin methods (authentication is handled by the HTTP layer)
    # ------------------------------------------------------------------
    def admin_create_user(
        self,
        username: str | None,
        display_name: str | None = None,
        now: str | None = None,
    ) -> dict:
        if username is not None and not isinstance(username, str):
            raise ServiceError("BadRequest", 400)
        if display_name is not None and not isinstance(display_name, str):
            raise ServiceError("BadRequest", 400)
        username = (username or "").strip()
        if not username or len(username) > 64:
            raise ServiceError("BadRequest", 400)
        display_name = self._truncate((display_name or "").strip() or username, 64)
        now = self._now(now)

        with db.connect(self.settings.db_path) as conn:
            if db.list_users(conn, username=username):
                raise ServiceError("Conflict", 409)
            try:
                user = db.create_user(conn, username, display_name)
            except sqlite3.IntegrityError:
                raise ServiceError("Conflict", 409)
            if user is None:
                raise ServiceError("ServerError", 500)
        return user

    def admin_list_users(self, username: str | None = None) -> dict:
        with db.connect(self.settings.db_path) as conn:
            users = db.list_users(conn, username=username or None)
        return {"users": users}

    def admin_delete_user(self, user_id: str) -> dict:
        return self._with_claim_lock(self._admin_delete_user, user_id)

    def _admin_delete_user(self, user_id: str) -> dict:
        with db.connect(self.settings.db_path) as conn:
            user = db.get_user(conn, user_id)
            if user is None:
                raise ServiceError("NotFound", 404)
            if user.get("status") == "deleted":
                return {"ok": True}

            devices = db.list_devices(conn, user_id=user_id)
        active_devices = [
            device for device in devices if device.get("status") == "active"
        ]
        attempted_emails: list[str] = []

        try:
            # x-ui writes are deliberately outside the Server DB transaction.
            # Record the attempt before calling the adapter because the adapter
            # can commit a disable before a later restart failure is reported.
            for device in active_devices:
                email = device.get("xrayClientEmail") or ""
                attempted_emails.append(email)
                try:
                    self.xui.update_client(email, enable=False)
                except XuiError as exc:
                    # The client is already gone from x-ui (removed in the
                    # panel, or x-ui.db restored from a backup).  The data
                    # plane can no longer admit this device, which is what the
                    # disable was for, so the revocation proceeds.  Failing
                    # here instead would roll back the *other* devices'
                    # disables below and leave the user fully connected.
                    # Same structured-code rule as the claim path.
                    if exc.code != "client_not_found":
                        raise
                    # Nothing was disabled, so there is nothing to restore.
                    attempted_emails.pop()

            with db.connect(self.settings.db_path) as conn:
                current = db.get_user(conn, user_id)
                if current is None:
                    raise ServiceError("NotFound", 404)
                if current.get("status") == "active" and not db.soft_delete_user(
                    conn, user_id
                ):
                    raise ServiceError("ServerError", 500)
                db.clear_assignments_for_target(conn, "user", user_id)
                for device in devices:
                    device_id = device.get("id") or ""
                    db.clear_assignments_for_target(conn, "device", device_id)
                    db.clear_observability_for_device(conn, device_id)
        except Exception:
            # If the local transaction could not complete, restore any x-ui
            # disables best-effort so an active user is not half-revoked.
            for email in reversed(attempted_emails):
                try:
                    self.xui.update_client(email, enable=True)
                except Exception:
                    pass
            raise
        return {"ok": True}

    def admin_create_binding(
        self,
        user_id: str | None,
        device_template: str | None,
        expires_in_seconds: int | None = None,
        now: str | None = None,
    ) -> dict:
        if user_id is not None and not isinstance(user_id, str):
            raise ServiceError("BadRequest", 400)
        if device_template is not None and not isinstance(device_template, str):
            raise ServiceError("BadRequest", 400)
        user_id = (user_id or "").strip()
        if not user_id:
            raise ServiceError("BadRequest", 400)
        device_template = self._truncate(
            (device_template or "").strip() or "windows", 32
        )
        if expires_in_seconds is None:
            expires_in_seconds = self.settings.binding_default_ttl_seconds
        if not isinstance(expires_in_seconds, int) or isinstance(
            expires_in_seconds, bool
        ):
            raise ServiceError("BadRequest", 400)
        if expires_in_seconds < 60 or expires_in_seconds > 604800:
            raise ServiceError("BadRequest", 400)
        now = self._now(now)
        expires_at = self._add_seconds(now, expires_in_seconds)

        with db.connect(self.settings.db_path) as conn:
            # Check and insert under one write lock.  A user deleted between
            # the two used to get a fresh active pairing code after deletion
            # had already revoked all of theirs -- and claiming it produced a
            # working device for a deleted user.
            conn.execute("BEGIN IMMEDIATE")
            user = db.get_user(conn, user_id)
            if user is None or user.get("status") != "active":
                raise ServiceError("BadRequest", 400)

            for _ in range(5):
                code = self._generate_pairing_code()
                if db.get_binding_by_code(conn, code) is None:
                    binding_id = db.new_id("lnk_")
                    try:
                        conn.execute(
                            "INSERT INTO binding_links ("
                            " id, code, user_id, device_template, status, expires_at,"
                            " max_attempts, failed_attempts, last_failed_at, created_at,"
                            " claimed_at, claimed_device_id)"
                            " VALUES (?, ?, ?, ?, 'active', ?, ?, 0, NULL, ?, NULL, NULL)",
                            (
                                binding_id,
                                code,
                                user_id,
                                device_template,
                                expires_at,
                                self.settings.claim_max_failures_per_code,
                                now,
                            ),
                        )
                    except sqlite3.IntegrityError:
                        # Lost a code-uniqueness race; try the next code.
                        continue
                    return {
                        "id": binding_id,
                        "code": code,
                        "userId": user_id,
                        "deviceTemplate": device_template,
                        "status": "active",
                        "expiresAt": expires_at,
                        "createdAt": now,
                    }
            raise ServiceError("ServerError", 500)

    @staticmethod
    def _generate_pairing_code() -> str:
        raw = secrets.token_hex(4).upper()
        return f"{raw[:4]}-{raw[4:]}"

    def admin_list_bindings(
        self, user_id: str | None = None, now: str | None = None
    ) -> dict:
        now = self._now(now)
        with db.connect(self.settings.db_path) as conn:
            bindings = db.list_bindings(conn, user_id=user_id or None, now=now)
        return {
            "bindings": [self._binding_without_code(binding) for binding in bindings]
        }

    @staticmethod
    def _binding_without_code(binding: dict) -> dict:
        result = dict(binding)
        result.pop("code", None)
        return result

    def admin_delete_binding(self, binding_id: str) -> dict:
        with db.connect(self.settings.db_path) as conn:
            if not db.revoke_binding(conn, binding_id):
                raise ServiceError("NotFound", 404)
        return {"ok": True}

    def admin_list_devices(self, user_id: str | None = None) -> dict:
        with db.connect(self.settings.db_path) as conn:
            devices = db.list_devices(conn, user_id=user_id or None)
        return {"devices": devices}

    def admin_delete_device(self, device_id: str) -> dict:
        return self._with_claim_lock(self._admin_delete_device, device_id)

    def _admin_delete_device(self, device_id: str) -> dict:
        with db.connect(self.settings.db_path) as conn:
            device = db.get_device(conn, device_id)
        if device is None:
            raise ServiceError("NotFound", 404)
        if device.get("status") == "disabled":
            return {"ok": True}

        email = device.get("xrayClientEmail") or ""
        xui_disable_attempted = True
        try:
            # The adapter can commit the disable before a later x-ui restart
            # failure is reported, so compensation must cover this call too.
            try:
                self.xui.update_client(email, enable=False)
            except XuiError as exc:
                # The client is already gone from x-ui: the data plane cannot
                # admit this device any more, so record the revocation instead
                # of failing it.  Failing left the device active in this
                # database with a Device Token that still passed heartbeat,
                # config and update -- a delete that silently did nothing.
                # Same structured-code rule as the claim path.
                if exc.code != "client_not_found":
                    raise
                # Nothing was disabled, so there is nothing to restore.
                xui_disable_attempted = False
            with db.connect(self.settings.db_path) as conn:
                current = db.get_device(conn, device_id)
                if current is None:
                    raise ServiceError("NotFound", 404)
                if current.get("status") != "disabled" and not db.set_device_disabled(
                    conn, device_id
                ):
                    raise ServiceError("ServerError", 500)
                # A device identifier that no longer names an active device
                # must not keep a release pin: a later rebind that recycled
                # the row would silently inherit it.
                db.clear_assignments_for_target(conn, "device", device_id)
                db.clear_observability_for_device(conn, device_id)
        except Exception:
            # The x-ui write is committed before its restart returns.  If the
            # local state commit fails, restore network access best-effort so
            # the database and data plane do not silently diverge.
            if xui_disable_attempted:
                try:
                    self.xui.update_client(email, enable=True)
                except Exception:
                    pass
            raise
        return {"ok": True}

    def admin_get_config(self) -> dict:
        with db.connect(self.settings.db_path) as conn:
            latest = db.latest_config(conn)
            if latest is None:
                raise ServiceError("ServerError", 500)
            config = self._config_from_template(latest.get("config"), "")
            return {
                "configVersion": latest["version"],
                "config": config,
                "createdAt": latest.get("createdAt") or db.utc_now(),
            }

    def admin_bump_config_version(
        self, note: str | None = None, now: str | None = None
    ) -> dict:
        if note is not None and not isinstance(note, str):
            raise ServiceError("BadRequest", 400)
        note = (note or "").strip()
        now = self._now(now)
        profile = self._public_profile_template(self.xui.public_profile())

        with db.connect(self.settings.db_path) as conn:
            latest = db.latest_config(conn)
            if latest is None:
                raise ServiceError("ServerError", 500)
            inserted = db.insert_config(conn, profile, note, now)
            if inserted is None:
                raise ServiceError("ServerError", 500)
            config = self._config_from_template(inserted.get("config"), "")
            return {
                "configVersion": inserted.get("version")
                if inserted.get("version")
                else latest["version"] + 1,
                "config": config,
                "createdAt": inserted.get("createdAt") or now,
            }

    def admin_get_latest(self, platform: str = "windows") -> dict:
        return self.latest_info(platform)

    def admin_set_latest(
        self,
        version: str | None,
        download_url: str | None,
        sha256: str | None,
        mandatory: bool | None,
        now: str | None = None,
        platform: str = "windows",
    ) -> dict:
        for value in (version, download_url, sha256):
            if value is not None and not isinstance(value, str):
                raise ServiceError("BadRequest", 400)
        if mandatory is not None and not isinstance(mandatory, bool):
            raise ServiceError("BadRequest", 400)

        version = (version or "").strip()
        if not re.match(r"^\d+\.\d+\.\d+$", version):
            raise ServiceError("BadRequest", 400)
        download_url = self._truncate(download_url or "", 2048)
        sha256 = self._truncate((sha256 or "").strip(), 128)
        if sha256 and not re.fullmatch(r"[0-9a-f]{64}", sha256):
            raise ServiceError("BadRequest", 400)
        mandatory = mandatory if mandatory is not None else False
        now = self._now(now)
        version_key, url_key, sha_key, mandatory_key = self._latest_meta_keys(
            platform
        )

        with db.connect(self.settings.db_path) as conn:
            db.set_meta(conn, version_key, version, now)
            db.set_meta(conn, url_key, download_url, now)
            db.set_meta(conn, sha_key, sha256, now)
            db.set_meta(
                conn,
                mandatory_key,
                "true" if mandatory else "false",
                now,
            )
        return {
            "version": version,
            "downloadUrl": download_url,
            "sha256": sha256,
            "mandatory": mandatory,
        }

    def admin_extend_binding(
        self,
        binding_id: str,
        extends_seconds: int | None,
        now: str | None = None,
    ) -> dict:
        if not isinstance(extends_seconds, int) or isinstance(extends_seconds, bool):
            raise ServiceError("BadRequest", 400)
        if extends_seconds <= 0 or extends_seconds > 604800:
            raise ServiceError("BadRequest", 400)
        now = self._now(now)

        with db.connect(self.settings.db_path) as conn:
            binding = db.get_binding_by_id(conn, binding_id)
            if binding is None:
                raise ServiceError("NotFound", 404)
            if binding.get("status") != "active":
                raise ServiceError("BadRequest", 400)
            old_expires = binding.get("expiresAt") or now
            base = old_expires if old_expires > now else now
            new_expires = self._add_seconds(base, extends_seconds)
            conn.execute(
                "UPDATE binding_links SET expires_at = ? WHERE id = ?",
                (new_expires, binding_id),
            )
            updated = db.get_binding_by_id(conn, binding_id)
            if updated is None:
                raise ServiceError("ServerError", 500)
        return self._binding_without_code(updated)

    # ------------------------------------------------------------------
    # Update Plane
    # ------------------------------------------------------------------
    # Resolution is device -> user -> platform, first hit wins, and a hit only
    # counts when the release it names is *published*.  A revoked release
    # therefore falls through to the next scope instead of resolving to
    # nothing, which is what makes "revoke the bad build" a one-action
    # rollback for every device pinned to it.

    def _signing_keys(self) -> dict[str, bytes]:
        """Parse and cache the trusted release signing keys.

        Cached on the instance rather than re-parsed per request: the parse
        does hex decoding on every key and the value only changes when the
        process restarts.
        """
        cached = getattr(self, "_signing_keys_cache", None)
        if cached is None:
            cached = release.parse_public_keys(self.settings.release_signing_keys)
            self._signing_keys_cache = cached
        return cached

    @staticmethod
    def _require_platform(platform: Any) -> str:
        if platform not in release.PLATFORMS:
            raise ServiceError("BadRequest", 400)
        return platform

    @staticmethod
    def _parse_flags(value: Any) -> str:
        """Validate feature flags and return them as a compact JSON string.

        Flags are a flat map of string keys to JSON scalars.  Nesting is
        rejected on purpose: a nested object is the shape an attacker would
        reach for to smuggle structured instructions through a channel that is
        only supposed to carry switches.
        """
        if value is None:
            return "{}"
        if not isinstance(value, dict):
            raise ServiceError("BadRequest", 400)
        if len(value) > 64:
            raise ServiceError("BadRequest", 400)
        for key, item in value.items():
            if not isinstance(key, str) or not _FEATURE_FLAG_KEY_RE.fullmatch(key):
                raise ServiceError("BadRequest", 400)
            if isinstance(item, bool) or item is None:
                continue
            if isinstance(item, (int, float)):
                continue
            if isinstance(item, str) and len(item) <= 128:
                continue
            raise ServiceError("BadRequest", 400)
        return json.dumps(value, separators=(",", ":"), sort_keys=True)

    @staticmethod
    def _load_flags(raw: Any) -> dict[str, Any]:
        if not isinstance(raw, str) or not raw:
            return {}
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError:
            return {}
        return parsed if isinstance(parsed, dict) else {}

    def _resolve_update(
        self, conn: sqlite3.Connection, device: dict
    ) -> tuple[dict | None, dict[str, Any], str]:
        """Return ``(release, feature_flags, source_scope)`` for one device.

        Flags merge the other way round from the release pick: platform first,
        then user, then device, so a narrow scope refines a broad default
        instead of having to restate it.
        """
        platform = device.get("platform") or "windows"
        if platform not in release.PLATFORMS:
            return None, {}, ""
        device_id = device.get("id") or ""
        user_id = device.get("userId") or ""

        lookups = (
            ("platform", ""),
            ("user", user_id),
            ("device", device_id),
        )
        flags: dict[str, Any] = {}
        picked: dict | None = None
        source = ""
        for scope, target in lookups:
            if scope != "platform" and not target:
                continue
            assignment = db.get_assignment(conn, scope, target, platform)
            if assignment is None:
                continue
            flags.update(self._load_flags(assignment.get("featureFlags")))
            release_id = assignment.get("releaseId") or ""
            if not release_id:
                continue
            candidate = db.get_release(conn, release_id)
            if candidate is None or candidate.get("status") != "published":
                continue
            picked, source = candidate, scope
        return picked, flags, source

    def device_update(self, token: str, now: str | None = None) -> dict:
        """Device API: the release this device is assigned, if any.

        Returns the signed manifest bytes verbatim.  The client verifies the
        signature itself; this endpoint is a transport, not an authority.
        """
        if not token:
            raise ServiceError("TokenInvalid", 401)
        now = self._now(now)
        with db.connect(self.settings.db_path) as conn:
            device = db.get_device_by_token_hash(conn, auth.hash_token(token))
            if device is None or device.get("status") != "active":
                raise ServiceError("TokenInvalid", 401)
            picked, flags, source = self._resolve_update(conn, device)
            payload = (
                db.get_release_payload(conn, picked["id"]) if picked else None
            )
            db.touch_device_seen(conn, device.get("id") or "", now)

        if picked is None or payload is None:
            return {"update": None, "featureFlags": flags, "serverTime": now}
        return {
            "update": {
                "releaseId": picked["id"],
                "version": picked["version"],
                "channel": picked["channel"],
                "mandatory": picked["mandatory"],
                "source": source,
                "manifest": payload["manifest"],
                "signature": payload["signature"],
                "signingKeyId": payload["signingKeyId"],
            },
            "featureFlags": flags,
            "serverTime": now,
        }

    def device_update_report(
        self,
        token: str,
        release_id: Any,
        status: Any,
        detail: Any = None,
        now: str | None = None,
    ) -> dict:
        """Device API: record how an install ended.

        This is the Server half of "an install failure can be rolled back":
        the client performs the rollback locally, and reporting it here is
        what lets an administrator see that a release is failing in the field
        and revoke it.  The report is audit data only -- it is never allowed
        to change which release is assigned, or a device could talk itself
        onto a different build.
        """
        if not token:
            raise ServiceError("TokenInvalid", 401)
        if status not in release.INSTALL_STATES:
            raise ServiceError("BadRequest", 400)
        if not isinstance(release_id, str) or not release_id:
            raise ServiceError("BadRequest", 400)
        if detail is not None and not isinstance(detail, str):
            raise ServiceError("BadRequest", 400)
        now = self._now(now)

        with db.connect(self.settings.db_path) as conn:
            device = db.get_device_by_token_hash(conn, auth.hash_token(token))
            if device is None or device.get("status") != "active":
                raise ServiceError("TokenInvalid", 401)
            reported = db.get_release(conn, release_id)
            # A device can only report on a build it could have been offered:
            # one for its own platform that was published at some point.  A
            # revoked release stays reportable -- it may have been installed
            # before it was pulled -- but only if it *was* published: a draft
            # can be revoked straight away, and "revoked" alone does not mean
            # any device ever saw it.  Accepting those would let any device
            # write an arbitrary version into the installed-version column.
            if (
                reported is None
                or reported.get("platform") != device.get("platform")
                or not db.release_was_published(conn, reported)
            ):
                raise ServiceError("NotFound", 404)
            device_id = device.get("id") or ""
            conn.execute("BEGIN IMMEDIATE")
            if status == "installed":
                # The device is now running this build; keep the Control Plane
                # view of the installed version honest for the Admin UI.
                conn.execute(
                    "UPDATE devices SET client_version = ?, updated_at = ?"
                    " WHERE id = ?",
                    (reported["version"], now, device_id),
                )
            db.append_release_audit(
                conn,
                now,
                f"install_{status}",
                scope="device",
                target_id=device_id,
                platform=device.get("platform") or "",
                release_id=release_id,
                detail=self._truncate(detail or "", 256),
            )
        return {"ok": True, "serverTime": now}

    # --- admin: releases ------------------------------------------------

    @staticmethod
    def _artifact_call(callback, *args):
        try:
            return callback(*args)
        except artifact.ArtifactError as exc:
            raise ServiceError(exc.code, exc.status) from exc

    def _artifact_lock_for(self, release_id: str):
        with self._release_artifact_locks_guard:
            return self._release_artifact_locks.setdefault(release_id, threading.RLock())

    def admin_release_settings(self) -> dict:
        keys = self._signing_keys()
        return {
            "artifactUploadEnabled": bool(keys) and self.artifacts.max_bytes > 0,
            "artifactBaseUrl": self.artifacts.base_url,
            "maxArtifactBytes": max(0, self.artifacts.max_bytes),
            "trustedSigningKeys": [
                {"keyId": key_id, "publicKeyHex": key.hex()}
                for key_id, key in sorted(keys.items())
            ],
            "artifactFilenameTemplate": "myproxy-{platform}-{version}.{extension}",
            "artifactExtensions": dict(artifact.EXTENSIONS),
        }

    def _artifact_result(self, record: dict) -> dict:
        return {
            "releaseId": record["id"],
            "artifactUrl": record["artifactUrl"],
            "artifactSha256": record["artifactSha256"],
            "artifactSize": record["artifactSize"],
            **self._artifact_call(self.artifacts.ready, record),
        }

    def admin_upload_artifact(self, release_id: str, stream, length: int) -> dict:
        with self._artifact_lock_for(release_id):
            with db.connect(self.settings.db_path) as conn:
                record = db.get_release(conn, release_id)
            if record is None:
                raise ServiceError("NotFound", 404)
            self._artifact_call(self.artifacts.upload, record, stream, length)
            with db.connect(self.settings.db_path) as conn:
                db.append_release_audit(conn, self._now(None), "release_artifact_uploaded",
                                        platform=record["platform"], release_id=release_id,
                                        detail=record["artifactSha256"])
            return self._artifact_result(record)

    def admin_upload_artifact_signature(self, release_id: str, signature: bytes) -> dict:
        with self._artifact_lock_for(release_id):
            with db.connect(self.settings.db_path) as conn:
                record = db.get_release(conn, release_id)
            if record is None:
                raise ServiceError("NotFound", 404)
            self._artifact_call(self.artifacts.upload_signature, record, signature, self._signing_keys())
            with db.connect(self.settings.db_path) as conn:
                db.append_release_audit(conn, self._now(None), "release_artifact_signature_uploaded",
                                        platform=record["platform"], release_id=release_id)
            return self._artifact_result(record)

    def open_release_artifact(self, digest: str, basename: str):
        if not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ServiceError("NotFound", 404)
        with db.connect(self.settings.db_path) as conn:
            records = db.list_releases(conn, status="published")
        for record in records:
            if record["artifactSha256"] == digest and self.artifacts.is_local(record):
                expected = artifact.filename(record)
                if basename == expected or (record["platform"] == "linux" and basename == expected + ".sig"):
                    with self._artifact_lock_for(record["id"]):
                        # Recheck after taking this release's lock: revoke may
                        # have won the race since the initial registry query.
                        with db.connect(self.settings.db_path) as conn:
                            current = db.get_release(conn, record["id"])
                        if current is not None:
                            return self._artifact_call(self.artifacts.open_download, current, basename)
        raise ServiceError("NotFound", 404)

    def admin_register_release(
        self,
        manifest: Any,
        signature: Any,
        signing_key_id: Any,
        note: Any = None,
        now: str | None = None,
    ) -> dict:
        if note is not None and not isinstance(note, str):
            raise ServiceError("BadRequest", 400)
        now = self._now(now)
        try:
            summary, _ = release.verify_and_parse(
                manifest, signature, signing_key_id, self._signing_keys()
            )
        except release.ManifestError:
            # Deliberately generic, exactly like the xui helper boundary: a
            # caller must not be able to use the reply to map the validator.
            raise ServiceError("ManifestRejected", 400)

        if self.artifacts.uses_local_namespace(summary) and not self.artifacts.is_local(summary):
            raise ServiceError("ManifestRejected", 400)

        with db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            existing = db.get_release_by_version(
                conn, summary["platform"], summary["version"]
            )
            if existing is not None:
                # A published version is immutable.  Re-registering it would
                # let the same version string point at different bytes, which
                # is the one thing a client cannot detect.
                raise ServiceError("Conflict", 409)
            created = db.create_release(
                conn,
                summary,
                manifest,
                signature,
                signing_key_id,
                self._truncate(note or "", 256),
                now,
            )
            db.append_release_audit(
                conn,
                now,
                "release_registered",
                platform=summary["platform"],
                release_id=created.get("id", ""),
                detail=summary["version"],
            )
        return {**created, **self._artifact_call(self.artifacts.ready, created)}

    def admin_list_releases(
        self, platform: str | None = None, status: str | None = None
    ) -> dict:
        if platform and platform not in release.PLATFORMS:
            raise ServiceError("BadRequest", 400)
        if status and status not in _RELEASE_STATUSES:
            raise ServiceError("BadRequest", 400)
        with db.connect(self.settings.db_path) as conn:
            releases = db.list_releases(conn, platform=platform, status=status)
        return {"releases": [
            {**record, **self._artifact_call(self.artifacts.ready, record)} for record in releases
        ]}

    def admin_set_release_status(
        self, release_id: str, status: Any, now: str | None = None
    ) -> dict:
        if status not in ("published", "revoked"):
            raise ServiceError("BadRequest", 400)
        now = self._now(now)
        with self._artifact_lock_for(release_id), db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            current = db.get_release(conn, release_id)
            if current is None:
                raise ServiceError("NotFound", 404)
            if current["status"] == "revoked" and status == "published":
                # Un-revoking would silently resurrect a build that was pulled
                # for a reason.  Register a new version instead.
                raise ServiceError("Conflict", 409)
            if status == "published":
                self._artifact_call(self.artifacts.require_publishable, current, self._signing_keys())
            db.set_release_status(conn, release_id, status, now)
            db.append_release_audit(
                conn,
                now,
                f"release_{status}",
                platform=current["platform"],
                release_id=release_id,
                detail=current["version"],
            )
            updated = db.get_release(conn, release_id)
        if updated is None:
            raise ServiceError("ServerError", 500)
        return {**updated, **self._artifact_call(self.artifacts.ready, updated)}

    # --- admin: assignments ---------------------------------------------

    def admin_set_assignment(
        self,
        scope: Any,
        target_id: Any,
        platform: Any,
        release_id: Any = None,
        feature_flags: Any = None,
        note: Any = None,
        now: str | None = None,
    ) -> dict:
        if scope not in _ASSIGNMENT_SCOPES:
            raise ServiceError("BadRequest", 400)
        platform = self._require_platform(platform)
        if note is not None and not isinstance(note, str):
            raise ServiceError("BadRequest", 400)
        if release_id is not None and not isinstance(release_id, str):
            raise ServiceError("BadRequest", 400)
        target_id = (target_id or "") if scope != "platform" else ""
        if scope != "platform" and (
            not isinstance(target_id, str) or not target_id
        ):
            raise ServiceError("BadRequest", 400)
        flags = self._parse_flags(feature_flags)
        now = self._now(now)

        with db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            if scope == "device":
                device = db.get_device(conn, target_id)
                if device is None or device.get("status") != "active":
                    raise ServiceError("NotFound", 404)
                if device.get("platform") != platform:
                    # Pinning a Windows build onto an Android device would
                    # resolve to a manifest the client must reject anyway.
                    raise ServiceError("BadRequest", 400)
            elif scope == "user":
                user = db.get_user(conn, target_id)
                if user is None or user.get("status") != "active":
                    raise ServiceError("NotFound", 404)

            resolved_release_id: str | None = None
            if release_id:
                target_release = db.get_release(conn, release_id)
                if target_release is None:
                    raise ServiceError("NotFound", 404)
                if target_release["platform"] != platform:
                    raise ServiceError("BadRequest", 400)
                if target_release["status"] != "published":
                    # Assigning a draft or revoked release would create a pin
                    # that silently resolves to nothing.
                    raise ServiceError("Conflict", 409)
                resolved_release_id = release_id

            previous = db.get_assignment(conn, scope, target_id, platform)
            assignment = db.upsert_assignment(
                conn,
                scope,
                target_id,
                platform,
                resolved_release_id,
                flags,
                self._truncate(note or "", 256),
                now,
            )
            db.append_release_audit(
                conn,
                now,
                "assignment_set",
                scope=scope,
                target_id=target_id,
                platform=platform,
                release_id=resolved_release_id or "",
                previous_release_id=(previous or {}).get("releaseId", ""),
                detail=flags,
            )
        return assignment

    def admin_list_assignments(
        self,
        scope: str | None = None,
        target_id: str | None = None,
        platform: str | None = None,
    ) -> dict:
        if scope and scope not in _ASSIGNMENT_SCOPES:
            raise ServiceError("BadRequest", 400)
        if platform and platform not in release.PLATFORMS:
            raise ServiceError("BadRequest", 400)
        with db.connect(self.settings.db_path) as conn:
            assignments = db.list_assignments(
                conn, scope=scope, target_id=target_id, platform=platform
            )
        return {"assignments": assignments}

    def admin_delete_assignment(
        self, assignment_id: str, now: str | None = None
    ) -> dict:
        now = self._now(now)
        with db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            existing = db.get_assignment_by_id(conn, assignment_id)
            if existing is None:
                raise ServiceError("NotFound", 404)
            db.delete_assignment(conn, assignment_id)
            db.append_release_audit(
                conn,
                now,
                "assignment_cleared",
                scope=existing["scope"],
                target_id=existing["targetId"],
                platform=existing["platform"],
                previous_release_id=existing.get("releaseId", ""),
            )
        return {"ok": True}

    def admin_device_effective_release(self, device_id: str) -> dict:
        """What a given device would be told right now, and why.

        Exists so that "which version is actually in force for this device"
        is answerable without replaying the resolution rules by hand.
        """
        with db.connect(self.settings.db_path) as conn:
            device = db.get_device(conn, device_id)
            if device is None:
                raise ServiceError("NotFound", 404)
            picked, flags, source = self._resolve_update(conn, device)
        return {
            "deviceId": device_id,
            "platform": device.get("platform", ""),
            "installedVersion": device.get("clientVersion", ""),
            "release": picked,
            "source": source,
            "featureFlags": flags,
        }

    def admin_release_audit(
        self, target_id: str | None = None, limit: int = 100
    ) -> dict:
        with db.connect(self.settings.db_path) as conn:
            entries = db.list_release_audit(
                conn, target_id=target_id or None, limit=limit
            )
        return {"entries": entries}

    # ------------------------------------------------------------------
    # Observability Plane
    # ------------------------------------------------------------------
    # Two independent sources fill the same hourly buckets:
    #
    #   * a sweep of x-ui's cumulative per-client counters, which is
    #     authoritative because it measures the data plane rather than
    #     believing a client, and
    #   * the device's own report, which is the only source that can attribute
    #     bytes to a service category, because the Server never sees traffic.
    #
    # The sweep is therefore the number an administrator should trust for
    # "how much", and the report is only trusted for "roughly what kind".

    def _ingest_interval(self) -> int:
        return max(60, int(self.settings.usage_ingest_interval_seconds))

    def _sweep_xui_counters(self, now: str, force: bool = False) -> int:
        """Fold x-ui's cumulative counters into hourly buckets.

        Called opportunistically from heartbeat and from Admin usage queries
        rather than from a scheduler, because this process does not have one.
        A ``server_meta`` timestamp throttles it so a hundred heartbeats in a
        minute still produce one x-ui call.

        The x-ui call happens **outside every SQLite transaction**, like the
        claim path: holding a write lock across an adapter call is the one
        thing this codebase never does.

        Only one sweep runs at a time.  Two sweeps that both read the cursors
        before either wrote them used to add the same delta twice, and every
        figure built on top of it -- the only authoritative "how much" this
        plane has -- came out doubled.  Heartbeats skip when a sweep is already
        running (a heartbeat must never wait on the data plane); a forced sweep
        (Admin refresh) waits for it to finish.
        """
        if not self._sweep_lock.acquire(blocking=force):
            return 0
        try:
            return self._sweep_xui_counters_locked(now, force)
        finally:
            self._sweep_lock.release()

    def _sweep_xui_counters_locked(self, now: str, force: bool) -> int:
        # Claim this round before calling x-ui.  The timestamp throttles
        # *attempts*, not successes: while the helper is unwell, letting every
        # heartbeat retry would make each one wait out the helper's timeout,
        # which is the one thing observability must never do to a heartbeat.
        with db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            last = db.get_meta(conn, _META_USAGE_LAST_SWEEP) or ""
            if not force and last:
                try:
                    elapsed = (
                        observability.parse_timestamp(now)
                        - observability.parse_timestamp(last)
                    ).total_seconds()
                except observability.ObservabilityError:
                    elapsed = self._ingest_interval() + 1
                if 0 <= elapsed < self._ingest_interval():
                    return 0
            db.set_meta(conn, _META_USAGE_LAST_SWEEP, now, now)
            if not self._active_devices_by_email(conn):
                return 0

        try:
            clients = self.xui.list_clients()
        except XuiError:
            # Observability is best-effort by design: a data-plane hiccup must
            # never fail a heartbeat or an Admin page load.
            return 0
        except Exception:
            return 0

        bucket = observability.hour_bucket(now)
        updated = 0
        with db.connect(self.settings.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            # Devices and cursors are read inside this write transaction, not
            # carried over from before the x-ui call: a device deleted in the
            # meantime has had its cursor cleared, and writing to it now would
            # resurrect the usage the delete was meant to forget.
            by_email = self._active_devices_by_email(conn)
            cursors = db.list_usage_cursors(conn)
            for client in clients:
                if not isinstance(client, dict):
                    continue
                device = by_email.get(client.get("email") or "")
                if device is None:
                    continue
                device_id = device.get("id") or ""
                cursor = cursors.get(device_id) or {}
                up_now = client.get("up") or 0
                down_now = client.get("down") or 0
                if not isinstance(up_now, int) or isinstance(up_now, bool):
                    up_now = 0
                if not isinstance(down_now, int) or isinstance(down_now, bool):
                    down_now = 0
                up_delta = observability.counter_delta(
                    cursor.get("lastUplink", 0), up_now
                )
                down_delta = observability.counter_delta(
                    cursor.get("lastDownlink", 0), down_now
                )
                db.set_usage_cursor(conn, device_id, up_now, down_now, now)
                if up_delta or down_delta:
                    db.add_usage(
                        conn,
                        device_id,
                        device.get("userId") or "",
                        bucket,
                        up_delta,
                        down_delta,
                        now,
                    )
                    updated += 1
            db.prune_usage(
                conn,
                observability.retention_cutoff(
                    now, self.settings.usage_retention_days
                ),
            )
        return updated

    @staticmethod
    def _active_devices_by_email(conn: Any) -> dict[str, dict]:
        """Devices whose x-ui counters still belong in usage, keyed by email.

        Only active ones.  Deleting a device disables its x-ui client rather
        than removing it, and clears the device's cursor; counting disabled
        devices made the next sweep read the client's whole cumulative counter
        as a fresh delta, so a deleted device reappeared in usage with
        everything it had ever transferred. Deleted devices must stay excluded.
        """
        return {
            device.get("xrayClientEmail") or "": device
            for device in db.list_devices(conn)
            if device.get("xrayClientEmail") and device.get("status") == "active"
        }

    def _note_client_address(
        self,
        conn: Any,
        device_id: str,
        user_id: str,
        now: str,
        client_ip: str,
    ) -> None:
        """Record where a device was heard from.

        Both device-facing paths go through here so they cannot drift: if only
        the heartbeat recorded the address, a device's history would depend on
        which endpoint it happened to call.

        ``client_ip`` is whatever ``server._claim_source_ip`` resolved, which
        is the peer address unless a *trusted* proxy forwarded one.  A request
        that arrives without a trusted forwarding header therefore normalises
        to the empty string and leaves the stored address alone, rather than
        overwriting a real address with the gateway's.
        """
        address = observability.normalise_address(client_ip)
        db.upsert_activity(
            conn,
            device_id,
            user_id,
            now,
            observability.ip_prefix(client_ip),
            ip_address=address,
        )
        db.record_device_address(
            conn, device_id, address, now, self.settings.device_address_history
        )

    def device_report_usage(
        self,
        token: str,
        body: Any,
        client_ip: str = "",
        now: str | None = None,
    ) -> dict:
        """Device API: a device's own view of what it moved, by category.

        Accepts aggregate byte counts against a closed vocabulary and nothing
        else.  There is no field here for a hostname, a URL or a connection
        record, and adding one would change what this product is.

        It also cannot move the "how much" number: totals come from the x-ui
        sweep, which measures the data plane.  A client can say what kind of
        traffic it moved, never how much.
        """
        if not token:
            raise ServiceError("TokenInvalid", 401)
        if body is not None and not isinstance(body, dict):
            raise ServiceError("BadRequest", 400)
        body = body or {}
        now = self._now(now)

        try:
            categories = observability.normalise_categories(body.get("categories"))
            bucket = (
                observability.hour_bucket(body["bucketStart"])
                if isinstance(body.get("bucketStart"), str)
                else observability.hour_bucket(now)
            )
        except observability.ObservabilityError:
            raise ServiceError("BadRequest", 400)

        # A device may not backfill history: it can report the current hour or
        # the one before it (a report crossing an hour boundary in flight),
        # and nothing else.  Otherwise a single device could rewrite a month
        # of somebody's usage trend.
        allowed = {
            observability.hour_bucket(now),
            observability.format_timestamp(
                observability.parse_timestamp(observability.hour_bucket(now))
                - timedelta(hours=1)
            ),
        }
        if bucket not in allowed:
            raise ServiceError("BadRequest", 400)

        with db.connect(self.settings.db_path) as conn:
            # The status check belongs to the write transaction, as in
            # heartbeat(): otherwise a deletion landing between the two leaves
            # category rows and a full address behind for a deleted device.
            conn.execute("BEGIN IMMEDIATE")
            device = db.get_device_by_token_hash(conn, auth.hash_token(token))
            if device is None or device.get("status") != "active":
                raise ServiceError("TokenInvalid", 401)
            device_id = device.get("id") or ""
            user_id = device.get("userId") or ""
            # Attribution only.  A device's own total would double-count
            # against the x-ui sweep, which measures the same bytes at the
            # data plane and is the authoritative figure; there is deliberately
            # no way for a client to move the "how much" number at all.
            for category, value in categories.items():
                db.add_usage_category(conn, device_id, bucket, category, value, now)
            self._note_client_address(conn, device_id, user_id, now, client_ip)
            db.touch_device_seen(conn, device_id, now)
        return {"ok": True, "serverTime": now}

    # --- admin queries --------------------------------------------------

    def _resolve_window(
        self, start: Any, end: Any, granularity: Any, now: str
    ) -> tuple[str, str, str, list[str]]:
        granularity = granularity or "hour"
        if granularity not in ("hour", "day"):
            raise ServiceError("BadRequest", 400)
        try:
            if end is None:
                end = now
            if start is None:
                span = timedelta(hours=24) if granularity == "hour" else timedelta(days=30)
                start = observability.format_timestamp(
                    observability.parse_timestamp(end) - span
                )
            labels = observability.bucket_range(start, end, granularity)
        except observability.ObservabilityError:
            raise ServiceError("BadRequest", 400)
        # Hourly rows are the only thing stored, so the query window is always
        # expressed in hourly bucket labels even when the caller asked for
        # days.  A daily label names midnight, so the window has to be widened
        # to that day's last hour -- otherwise a daily query silently returns
        # only whatever happened between 00:00 and 01:00.
        first_hour = labels[0] if labels else observability.hour_bucket(start)
        last_label = labels[-1] if labels else observability.hour_bucket(end)
        if granularity == "day":
            last_hour = observability.format_timestamp(
                observability.parse_timestamp(last_label) + timedelta(hours=23)
            )
        else:
            last_hour = last_label
        return first_hour, last_hour, granularity, labels

    def admin_usage(
        self,
        device_id: str | None = None,
        user_id: str | None = None,
        start: str | None = None,
        end: str | None = None,
        granularity: str | None = None,
        now: str | None = None,
    ) -> dict:
        now = self._now(now)
        self._sweep_xui_counters(now)
        first, last, granularity, labels = self._resolve_window(
            start, end, granularity, now
        )
        with db.connect(self.settings.db_path) as conn:
            if device_id and db.get_device(conn, device_id) is None:
                raise ServiceError("NotFound", 404)
            rows = db.list_usage(conn, device_id, user_id, first, last)
            categories = db.list_usage_categories(conn, device_id, first, last, user_id)
        series = observability.build_series(rows, labels, granularity)
        return {
            "deviceId": device_id or "",
            "userId": user_id or "",
            "granularity": granularity,
            "rangeStart": labels[0] if labels else first,
            "rangeEnd": labels[-1] if labels else last,
            "series": series,
            "categories": categories,
            "totals": {
                "uplinkBytes": sum(item["uplinkBytes"] for item in series),
                "downlinkBytes": sum(item["downlinkBytes"] for item in series),
                "totalBytes": sum(item["totalBytes"] for item in series),
            },
            "retentionDays": self.settings.usage_retention_days,
        }

    def admin_device_activity(self, device_id: str, now: str | None = None) -> dict:
        now = self._now(now)
        with db.connect(self.settings.db_path) as conn:
            device = db.get_device(conn, device_id)
            if device is None:
                raise ServiceError("NotFound", 404)
            activity = db.get_activity(conn, device_id) or {}
            addresses = db.list_device_addresses(conn, device_id)
        last_seen = activity.get("lastSeenAt") or device.get("lastSeenAt")
        return {
            "deviceId": device_id,
            "userId": device.get("userId", ""),
            "deviceName": device.get("deviceName", ""),
            "platform": device.get("platform", ""),
            "status": device.get("status", ""),
            "lastSeenAt": last_seen,
            "state": observability.activity_state(last_seen, now),
            "location": {
                "ipAddress": activity.get("ipAddress", ""),
                "ipPrefix": activity.get("ipPrefix", ""),
                "country": activity.get("country", ""),
                "region": activity.get("region", ""),
                "city": activity.get("city", ""),
                "asn": activity.get("asn", ""),
                "isp": activity.get("isp", ""),
            },
            # Only the detail view carries the history: the list view would
            # need one query per device to build it, and the question it
            # answers -- "has this account been moving around" -- is one an
            # operator asks about a specific device.
            "recentAddresses": addresses,
            "addressHistoryLimit": self.settings.device_address_history,
        }

    def admin_list_activity(
        self, user_id: str | None = None, now: str | None = None
    ) -> dict:
        now = self._now(now)
        with db.connect(self.settings.db_path) as conn:
            devices = db.list_devices(conn, user_id=user_id or None)
            activity = {
                item["deviceId"]: item
                for item in db.list_activity(conn, user_id=user_id or None)
            }
        entries = []
        for device in devices:
            device_id = device.get("id") or ""
            record = activity.get(device_id, {})
            last_seen = record.get("lastSeenAt") or device.get("lastSeenAt")
            entries.append(
                {
                    "deviceId": device_id,
                    "userId": device.get("userId", ""),
                    "deviceName": device.get("deviceName", ""),
                    "platform": device.get("platform", ""),
                    "status": device.get("status", ""),
                    "lastSeenAt": last_seen,
                    "state": observability.activity_state(last_seen, now),
                    "location": {
                        "ipAddress": record.get("ipAddress", ""),
                        "ipPrefix": record.get("ipPrefix", ""),
                        "country": record.get("country", ""),
                        "region": record.get("region", ""),
                        "city": record.get("city", ""),
                        "asn": record.get("asn", ""),
                        "isp": record.get("isp", ""),
                    },
                }
            )
        return {"devices": entries}

    def admin_set_device_geo(
        self, device_id: str, geo: Any, now: str | None = None
    ) -> dict:
        """Store operator-supplied location enrichment for one device.

        This exists because the Server deliberately does not geolocate: it has
        no GeoIP database, and shipping user addresses to a third-party
        lookup service to get one would leak exactly the data the rest of this
        plane is careful not to keep.
        """
        now = self._now(now)
        try:
            fields = observability.validate_geo(geo)
        except observability.ObservabilityError:
            raise ServiceError("BadRequest", 400)
        if not fields:
            raise ServiceError("BadRequest", 400)
        with db.connect(self.settings.db_path) as conn:
            device = db.get_device(conn, device_id)
            if device is None:
                raise ServiceError("NotFound", 404)
            conn.execute("BEGIN IMMEDIATE")
            db.upsert_activity(
                conn,
                device_id,
                device.get("userId") or "",
                device.get("lastSeenAt") or now,
            )
            db.set_activity_geo(conn, device_id, fields, now)
        return self.admin_device_activity(device_id, now)

    def admin_refresh_usage(self, now: str | None = None) -> dict:
        """Force an x-ui counter sweep, bypassing the throttle."""
        now = self._now(now)
        updated = self._sweep_xui_counters(now, force=True)
        return {"ok": True, "devicesUpdated": updated, "serverTime": now}
