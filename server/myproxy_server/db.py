"""SQLite schema and repository helpers for MyProxy Server.

The module intentionally uses only the standard library.  ``connect()``
returns a small :class:`sqlite3.Connection` subclass whose context manager
commits or rolls back the transaction opened by the caller. Connections use
foreign keys and a bounded busy timeout. The database is initialized in WAL
mode once by :func:`init_db`. Reads do not acquire an immediate write lock;
callers that need a serialized write must explicitly use ``BEGIN IMMEDIATE``
for the short database-only section.
"""

from __future__ import annotations

import json
import os
import secrets
import sqlite3
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import __version__


SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','deleted')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS binding_links (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    user_id TEXT NOT NULL REFERENCES users(id),
    device_template TEXT NOT NULL DEFAULT 'windows',
    status TEXT NOT NULL DEFAULT 'active'
        CHECK(status IN ('active','claimed','expired','revoked')),
    expires_at TEXT NOT NULL,
    max_attempts INTEGER NOT NULL DEFAULT 5,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    last_failed_at TEXT,
    created_at TEXT NOT NULL,
    claimed_at TEXT,
    claimed_device_id TEXT
);

CREATE INDEX IF NOT EXISTS idx_binding_code ON binding_links(code);
CREATE INDEX IF NOT EXISTS idx_binding_user ON binding_links(user_id);

CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES users(id),
    device_name TEXT NOT NULL,
    platform TEXT NOT NULL DEFAULT 'windows',
    client_version TEXT NOT NULL DEFAULT '',
    client_instance_id_hash TEXT,
    token_hash TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','disabled')),
    xray_client_email TEXT NOT NULL UNIQUE,
    token_created_at TEXT NOT NULL,
    token_expires_at TEXT,
    last_seen_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_devices_user ON devices(user_id);
CREATE INDEX IF NOT EXISTS idx_devices_token ON devices(token_hash);

CREATE TABLE IF NOT EXISTS config_versions (
    version INTEGER PRIMARY KEY,
    config_json TEXT NOT NULL,
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Update Plane -------------------------------------------------------------
-- A release row is a *record of something already signed elsewhere*, not a
-- thing this server can mint.  manifest_b64 and signature_b64 are stored and
-- served byte for byte; every other column is a denormalised copy that exists
-- only so the Admin API can list and resolve without re-parsing, and must
-- never be treated as authoritative by a client.
CREATE TABLE IF NOT EXISTS releases (
    id TEXT PRIMARY KEY,
    -- 平台取值与 release.PLATFORMS 必须一致：这里是第二道（弱一道）防线，
    -- 真正把关的是登记路径上的 release._require_str(..., PLATFORMS)。
    platform TEXT NOT NULL CHECK(platform IN ('windows','android','linux')),
    version TEXT NOT NULL,
    channel TEXT NOT NULL DEFAULT 'stable' CHECK(channel IN ('stable','beta')),
    status TEXT NOT NULL DEFAULT 'draft'
        CHECK(status IN ('draft','published','revoked')),
    mandatory INTEGER NOT NULL DEFAULT 0,
    artifact_url TEXT NOT NULL,
    artifact_sha256 TEXT NOT NULL,
    artifact_size INTEGER NOT NULL DEFAULT 0,
    platform_signature_type TEXT NOT NULL DEFAULT '',
    platform_signature_subject TEXT NOT NULL DEFAULT '',
    minimum_version TEXT NOT NULL DEFAULT '',
    manifest_b64 TEXT NOT NULL,
    signature_b64 TEXT NOT NULL,
    signing_key_id TEXT NOT NULL,
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_releases_platform_version
    ON releases(platform, version);
CREATE INDEX IF NOT EXISTS idx_releases_platform_status
    ON releases(platform, status);

-- An assignment says only "this target gets that release" plus opaque feature
-- flags.  It deliberately cannot carry a URL, a command or a payload: the
-- whole point of the Update Plane is that the Control Plane names a release
-- and nothing else.  release_id is nullable so an assignment can carry flags
-- alone without pinning a version.
CREATE TABLE IF NOT EXISTS release_assignments (
    id TEXT PRIMARY KEY,
    scope TEXT NOT NULL CHECK(scope IN ('device','user','platform')),
    target_id TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL CHECK(platform IN ('windows','android','linux')),
    release_id TEXT REFERENCES releases(id),
    feature_flags TEXT NOT NULL DEFAULT '{}',
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_assignment_target
    ON release_assignments(scope, target_id, platform);

CREATE TABLE IF NOT EXISTS release_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at TEXT NOT NULL,
    action TEXT NOT NULL,
    scope TEXT NOT NULL DEFAULT '',
    target_id TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    release_id TEXT NOT NULL DEFAULT '',
    previous_release_id TEXT NOT NULL DEFAULT '',
    detail TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_release_audit_at ON release_audit(at);
CREATE INDEX IF NOT EXISTS idx_release_audit_target
    ON release_audit(scope, target_id);

-- Observability Plane -------------------------------------------------------
-- There is deliberately no row here that records a single connection or a
-- hostname: see observability.py for the limits this schema exists to
-- enforce.  Client addresses, on the other hand, are stored in full
-- (device_activity.ip_address and the capped device_addresses history) since
-- the configured retention includes addresses. Treat database backups as sensitive.
CREATE TABLE IF NOT EXISTS usage_buckets (
    device_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    bucket_start TEXT NOT NULL,
    uplink_bytes INTEGER NOT NULL DEFAULT 0,
    downlink_bytes INTEGER NOT NULL DEFAULT 0,
    samples INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (device_id, bucket_start)
);

CREATE INDEX IF NOT EXISTS idx_usage_bucket_time ON usage_buckets(bucket_start);
CREATE INDEX IF NOT EXISTS idx_usage_bucket_user
    ON usage_buckets(user_id, bucket_start);

CREATE TABLE IF NOT EXISTS usage_categories (
    device_id TEXT NOT NULL,
    bucket_start TEXT NOT NULL,
    category TEXT NOT NULL,
    total_bytes INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (device_id, bucket_start, category)
);

CREATE INDEX IF NOT EXISTS idx_usage_category_time
    ON usage_categories(bucket_start);

-- Where the last reading of each cumulative x-ui counter got to.  Without it
-- every sweep would re-count the device's entire lifetime traffic.
CREATE TABLE IF NOT EXISTS usage_cursors (
    device_id TEXT PRIMARY KEY,
    last_uplink INTEGER NOT NULL DEFAULT 0,
    last_downlink INTEGER NOT NULL DEFAULT 0,
    observed_at TEXT NOT NULL
);

-- ip_address is the device's current egress address as the server saw it, and
-- ip_prefix is the /24 or /48 derived from it for the coarse location view.
-- country/region/city/asn/isp are supplied by an operator, never looked up:
-- the server has no GeoIP database and must not send user addresses to a
-- third-party geolocation service to obtain one.
CREATE TABLE IF NOT EXISTS device_activity (
    device_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    last_seen_at TEXT,
    ip_address TEXT NOT NULL DEFAULT '',
    ip_prefix TEXT NOT NULL DEFAULT '',
    country TEXT NOT NULL DEFAULT '',
    region TEXT NOT NULL DEFAULT '',
    city TEXT NOT NULL DEFAULT '',
    asn TEXT NOT NULL DEFAULT '',
    isp TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_device_activity_user ON device_activity(user_id);

-- The last few distinct addresses a device connected from, newest first.
-- One row per (device, address): a device that reconnects from the same
-- address for a month is one row whose last_seen_at moves, which is what
-- makes a capped history readable instead of a wall of duplicates.
--
-- There is deliberately no index on address alone.  Reverse lookup -- "which
-- devices used this address" -- is not a supported query, and an index is
-- how that capability arrives by accident.
CREATE TABLE IF NOT EXISTS device_addresses (
    device_id TEXT NOT NULL,
    address TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    PRIMARY KEY (device_id, address)
);

CREATE INDEX IF NOT EXISTS idx_device_addresses_recent
    ON device_addresses(device_id, last_seen_at DESC);
"""

REQUIRED_READINESS_TABLES = frozenset(
    {
        "users",
        "binding_links",
        "devices",
        "config_versions",
        "server_meta",
        "releases",
        "release_assignments",
        "release_audit",
        "usage_buckets",
        "usage_categories",
        "usage_cursors",
        "device_activity",
        "device_addresses",
    }
)


class _Connection(sqlite3.Connection):
    """Connection with safe commit/rollback context handling."""

    def __enter__(self) -> "_Connection":
        self.row_factory = sqlite3.Row
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> bool:
        try:
            if self.in_transaction:
                if exc_type is None:
                    self.commit()
                else:
                    self.rollback()
        finally:
            self.close()
        return False


def connect(db_path: str) -> sqlite3.Connection:
    """Open a database connection.

    ``with db.connect(path) as conn:`` commits/rolls back automatically. The
    connection is configured before it is returned so a failed context entry
    cannot leak an open handle.
    """
    conn = sqlite3.connect(db_path, factory=_Connection, timeout=5.0)
    conn.row_factory = sqlite3.Row
    try:
        conn.execute("PRAGMA busy_timeout=5000")
        conn.execute("PRAGMA foreign_keys=ON")
    except Exception:
        conn.close()
        raise
    return conn


def check_readiness(db_path: str) -> bool:
    """Return whether the control-plane DB is readable and fully initialized.

    Readiness must never create a missing database or mutate schema.  Use a
    read-only SQLite URI and only return a boolean so callers cannot expose
    filesystem paths or SQLite diagnostics over HTTP.
    """
    if not db_path or not os.path.isfile(db_path):
        return False
    try:
        uri = Path(db_path).resolve().as_uri() + "?mode=ro"
        with closing(sqlite3.connect(uri, uri=True, timeout=2.0)) as conn:
            conn.execute("PRAGMA busy_timeout=2000")
            rows = conn.execute(
                "SELECT name FROM sqlite_master WHERE type = 'table'"
            ).fetchall()
            tables = {str(row[0]) for row in rows}
            if not REQUIRED_READINESS_TABLES.issubset(tables):
                return False
            return (
                conn.execute("SELECT 1 FROM config_versions LIMIT 1").fetchone()
                is not None
            )
    except (OSError, sqlite3.Error, ValueError):
        return False


def utc_now() -> str:
    """Return current UTC time as ``YYYY-MM-DDTHH:MM:SSZ``."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def new_id(prefix: str) -> str:
    """Return ``<prefix>`` + 24 hex chars (``secrets.token_hex(12)``)."""
    return prefix + secrets.token_hex(12)


# ---------------------------------------------------------------------------
# Row conversion helpers
# ---------------------------------------------------------------------------

def _user_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "username": row["username"],
        "displayName": row["display_name"],
        "status": row["status"],
        "createdAt": row["created_at"],
        "updatedAt": row["updated_at"],
    }


def _binding_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "code": row["code"],
        "userId": row["user_id"],
        "deviceTemplate": row["device_template"],
        "status": row["status"],
        "expiresAt": row["expires_at"],
        "maxAttempts": row["max_attempts"],
        "failedAttempts": row["failed_attempts"],
        "lastFailedAt": row["last_failed_at"],
        "createdAt": row["created_at"],
        "claimedAt": row["claimed_at"],
        "claimedDeviceId": row["claimed_device_id"],
    }


def _device_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "userId": row["user_id"],
        "deviceName": row["device_name"],
        "platform": row["platform"],
        "clientVersion": row["client_version"],
        "status": row["status"],
        "xrayClientEmail": row["xray_client_email"],
        "lastSeenAt": row["last_seen_at"],
        "createdAt": row["created_at"],
        "updatedAt": row["updated_at"],
    }


# ---------------------------------------------------------------------------
# Initialization
# ---------------------------------------------------------------------------

def _widen_platform_checks(conn: sqlite3.Connection) -> None:
    """把两张发布表的 platform CHECK 扩到包含 linux。

    `CREATE TABLE IF NOT EXISTS` 不会改已有库的约束，而 SQLite 也 **不能** ALTER
    一个 CHECK——所以这是一次实实在在的「按需重建表」，只对已经存在、且建表语句
    里仍是两平台约束的库执行：

    1. 取出 `sqlite_master` 里存的原始建表语句，把约束与表名各替换一处；
    2. 建新表、按列名拷贝全部行、删旧表、改名回原名；
    3. 索引由随后的 SCHEMA 脚本用 `CREATE INDEX IF NOT EXISTS` 重建（所以本函数
       必须跑在 `executescript(SCHEMA)` **之前**）。

    外键：`release_assignments.release_id` 指向 `releases(id)`，重建期间必须关掉
    `PRAGMA foreign_keys`（该 PRAGMA 在事务里无效，所以整段在事务外先关、结束再开），
    并打开 `legacy_alter_table`，避免 RENAME 去改写别的表里对本表的引用。

    幂等：语句里已经含 `'linux'` 时直接跳过。
    """
    for table in ("releases", "release_assignments"):
        row = conn.execute(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = ?",
            (table,),
        ).fetchone()
        if row is None or not row["sql"]:
            continue

        original = row["sql"]
        if "linux" in original or "IN ('windows','android')" not in original:
            continue

        columns = [item["name"] for item in conn.execute(f"PRAGMA table_info({table})")]
        if not columns:
            continue

        rebuilt = original.replace(
            "IN ('windows','android')", "IN ('windows','android','linux')", 1
        )
        staging = f"{table}__platform_migration"
        rebuilt = rebuilt.replace(f"TABLE {table} ", f"TABLE {staging} ", 1)
        rebuilt = rebuilt.replace(f'TABLE "{table}" ', f'TABLE "{staging}" ', 1)
        if f"TABLE {staging} " not in rebuilt and f'TABLE "{staging}" ' not in rebuilt:
            # 建表语句的形状没见过：宁可不迁移，也不要写坏一个生产库。
            continue

        column_list = ", ".join(f'"{name}"' for name in columns)
        conn.execute("PRAGMA foreign_keys=OFF")
        conn.execute("PRAGMA legacy_alter_table=ON")
        try:
            conn.execute("BEGIN IMMEDIATE")
            try:
                conn.execute(rebuilt)
                conn.execute(
                    f'INSERT INTO "{staging}" ({column_list})'
                    f' SELECT {column_list} FROM "{table}"'
                )
                conn.execute(f'DROP TABLE "{table}"')
                conn.execute(f'ALTER TABLE "{staging}" RENAME TO "{table}"')
                conn.execute("COMMIT")
            except Exception:
                conn.execute("ROLLBACK")
                raise
        finally:
            conn.execute("PRAGMA legacy_alter_table=OFF")
            conn.execute("PRAGMA foreign_keys=ON")


def init_db(db_path: str) -> None:
    """Create the schema and seed initial data.  Idempotent."""
    with connect(db_path) as conn:
        conn.execute("PRAGMA journal_mode=WAL").fetchone()
        # 必须在建表/建索引之前：重建表会连带丢掉旧索引，随后 SCHEMA 脚本里的
        # CREATE INDEX IF NOT EXISTS 正好把它们补回来。
        _widen_platform_checks(conn)
        conn.executescript(SCHEMA)
        # executescript commits any pending transaction first, so start a
        # fresh one before seeding/updating data.
        conn.execute("BEGIN IMMEDIATE")

        # Existing V1 databases predate stable client instance identifiers.
        # SQLite's CREATE TABLE IF NOT EXISTS does not add new columns, so keep
        # this additive migration idempotent and preserve every existing row.
        device_columns = {
            row["name"] for row in conn.execute("PRAGMA table_info(devices)")
        }
        if "client_instance_id_hash" not in device_columns:
            conn.execute(
                "ALTER TABLE devices ADD COLUMN client_instance_id_hash TEXT"
            )
        # Only active rows own an installation identity.  A disabled device is
        # an audit record and must not make a later, administrator-authorised
        # rebind fail with a uniqueness error.
        conn.execute("DROP INDEX IF EXISTS idx_devices_user_instance")
        conn.execute(
            "CREATE UNIQUE INDEX idx_devices_user_instance"
            " ON devices(user_id, platform, client_instance_id_hash)"
            " WHERE client_instance_id_hash IS NOT NULL AND status = 'active'"
        )

        # device_activity originally stored only the truncated prefix.  Same
        # reason as above: the CREATE TABLE above will not add the column to a
        # table that already exists, so add it here and leave every row alone.
        # Rows written before this point keep an empty address until the
        # device is next heard from, which is correct -- the server genuinely
        # does not know what address they used.
        activity_columns = {
            row["name"] for row in conn.execute("PRAGMA table_info(device_activity)")
        }
        if "ip_address" not in activity_columns:
            conn.execute(
                "ALTER TABLE device_activity ADD COLUMN"
                " ip_address TEXT NOT NULL DEFAULT ''"
            )

        now = utc_now()
        cur = conn.execute("SELECT COUNT(*) FROM config_versions")
        if cur.fetchone()[0] == 0:
            conn.execute(
                "INSERT INTO config_versions(version, config_json, note, created_at)"
                " VALUES (1, '{}', '', ?)",
                (now,),
            )

        latest_defaults = (
            ("latest_version", __version__),
            ("latest_download_url", ""),
            ("latest_sha256", ""),
            ("latest_mandatory", "false"),
            ("android_latest_version", "0.1.0"),
            ("android_latest_download_url", ""),
            ("android_latest_sha256", ""),
            ("android_latest_mandatory", "false"),
        )
        for key, value in latest_defaults:
            conn.execute(
                "INSERT INTO server_meta(key, value, updated_at)"
                " VALUES (?, ?, ?) ON CONFLICT(key) DO NOTHING",
                (key, value, now),
            )


# ---------------------------------------------------------------------------
# Users
# ---------------------------------------------------------------------------

def create_user(conn: sqlite3.Connection, username: str, display_name: str) -> dict[str, Any]:
    username = (username or "").strip()[:64]
    if not username:
        raise ValueError("username must not be empty")
    display_name = (display_name or "").strip()
    if not display_name:
        display_name = username

    now = utc_now()
    user_id = new_id("usr_")
    conn.execute(
        "INSERT INTO users(id, username, display_name, status, created_at, updated_at)"
        " VALUES (?, ?, ?, 'active', ?, ?)",
        (user_id, username, display_name, now, now),
    )
    return get_user(conn, user_id)  # type: ignore[return-value]


def list_users(
    conn: sqlite3.Connection, username: str | None = None
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM users WHERE status != 'deleted'"
    params: list[Any] = []
    if username is not None:
        sql += " AND username = ?"
        params.append(username)
    sql += " ORDER BY created_at, id"
    rows = conn.execute(sql, params).fetchall()
    return [_user_to_dict(r) for r in rows]


def get_user(conn: sqlite3.Connection, user_id: str) -> dict[str, Any] | None:
    row = conn.execute("SELECT * FROM users WHERE id = ?", (user_id,)).fetchone()
    return _user_to_dict(row) if row else None


def soft_delete_user(conn: sqlite3.Connection, user_id: str) -> bool:
    row = conn.execute("SELECT id FROM users WHERE id = ?", (user_id,)).fetchone()
    if row is None:
        return False

    now = utc_now()
    conn.execute(
        "UPDATE users SET status = 'deleted', updated_at = ? WHERE id = ?",
        (now, user_id),
    )
    # Same transaction: invalidate all of this user's unused active links.
    conn.execute(
        "UPDATE binding_links SET status = 'revoked'"
        " WHERE user_id = ? AND status = 'active'",
        (user_id,),
    )
    # A deleted user must not retain a valid device token.  The service layer
    # disables the corresponding x-ui clients before entering this transaction;
    # keep the local revocation invariant here as well for every caller.
    conn.execute(
        "UPDATE devices SET status = 'disabled', updated_at = ?"
        " WHERE user_id = ? AND status = 'active'",
        (now, user_id),
    )
    return True


# ---------------------------------------------------------------------------
# Binding links
# ---------------------------------------------------------------------------

def _generate_binding_code() -> str:
    raw = secrets.token_hex(4).upper()
    return f"{raw[:4]}-{raw[4:]}"


def create_binding(
    conn: sqlite3.Connection,
    user_id: str,
    device_template: str,
    expires_at: str,
) -> dict[str, Any]:
    now = utc_now()
    binding_id = new_id("lnk_")
    code = _generate_binding_code()
    template = (device_template or "windows").strip()[:32] or "windows"
    conn.execute(
        "INSERT INTO binding_links"
        " (id, code, user_id, device_template, status, expires_at,"
        "  max_attempts, failed_attempts, last_failed_at, created_at,"
        "  claimed_at, claimed_device_id)"
        " VALUES (?, ?, ?, ?, 'active', ?, 5, 0, NULL, ?, NULL, NULL)",
        (binding_id, code, user_id, template, expires_at, now),
    )
    return get_binding_by_id(conn, binding_id)  # type: ignore[return-value]


def get_binding_by_id(
    conn: sqlite3.Connection, binding_id: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM binding_links WHERE id = ?", (binding_id,)
    ).fetchone()
    return _binding_to_dict(row) if row else None


def get_binding_by_code(conn: sqlite3.Connection, code: str) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM binding_links WHERE code = ?", (code,)
    ).fetchone()
    return _binding_to_dict(row) if row else None


def list_bindings(
    conn: sqlite3.Connection,
    user_id: str | None = None,
    now: str | None = None,
) -> list[dict[str, Any]]:
    if now is not None:
        conn.execute(
            "UPDATE binding_links SET status = 'expired'"
            " WHERE status = 'active' AND expires_at <= ?",
            (now,),
        )
    sql = "SELECT * FROM binding_links"
    params: list[Any] = []
    if user_id is not None:
        sql += " WHERE user_id = ?"
        params.append(user_id)
    sql += " ORDER BY created_at, id"
    rows = conn.execute(sql, params).fetchall()
    return [_binding_to_dict(r) for r in rows]


def mark_binding_claimed(
    conn: sqlite3.Connection, binding_id: str, device_id: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT id FROM binding_links WHERE id = ?", (binding_id,)
    ).fetchone()
    if row is None:
        return None

    now = utc_now()
    cursor = conn.execute(
        "UPDATE binding_links"
        " SET status = 'claimed', claimed_at = ?, claimed_device_id = ?"
        " WHERE id = ? AND status = 'active'",
        (now, device_id, binding_id),
    )
    if cursor.rowcount != 1:
        return None
    return get_binding_by_id(conn, binding_id)


def mark_binding_failed(
    conn: sqlite3.Connection, binding_id: str, now: str
) -> None:
    conn.execute(
        "UPDATE binding_links"
        " SET failed_attempts = failed_attempts + 1, last_failed_at = ?"
        " WHERE id = ?",
        (now, binding_id),
    )


def mark_binding_expired(conn: sqlite3.Connection, binding_id: str) -> None:
    """Expire a pairing code, but never overwrite a terminal status.

    Only an ``active`` link may become ``expired``.  Without the guard a
    revoked link that later reaches its TTL would be rewritten to ``expired``,
    destroying the revocation audit trail and answering the client with
    "code expired" instead of "code invalid".
    """
    conn.execute(
        "UPDATE binding_links SET status = 'expired'"
        " WHERE id = ? AND status = 'active'",
        (binding_id,),
    )


def revoke_binding(conn: sqlite3.Connection, binding_id: str) -> bool:
    """Revoke a pairing link.  Returns False only when no such link exists.

    Revocation applies to every non-revoked status.  A code leaked *after* it
    was claimed must stop being replayable within the claim window and must
    show as revoked in the admin list; leaving it ``claimed`` made the API
    answer ``ok`` while changing nothing.  Device rows are independent, so an
    already-bound device keeps working -- revoking a code revokes the code.
    """
    cursor = conn.execute(
        "UPDATE binding_links SET status = 'revoked'"
        " WHERE id = ? AND status != 'revoked'",
        (binding_id,),
    )
    if cursor.rowcount:
        return True
    return (
        conn.execute(
            "SELECT 1 FROM binding_links WHERE id = ?", (binding_id,)
        ).fetchone()
        is not None
    )


# ---------------------------------------------------------------------------
# Devices
# ---------------------------------------------------------------------------

def create_device(
    conn: sqlite3.Connection,
    user_id: str,
    device_name: str,
    platform: str,
    client_version: str,
    token_hash: str,
    xray_client_email: str,
    client_instance_id_hash: str | None = None,
) -> dict[str, Any]:
    device_name = (device_name or "")[:128]
    if not device_name:
        raise ValueError("device_name must not be empty")
    platform = (platform or "windows")[:32] or "windows"
    client_version = (client_version or "")[:32]

    now = utc_now()
    device_id = new_id("dev_")
    conn.execute(
        "INSERT INTO devices"
        " (id, user_id, device_name, platform, client_version,"
        "  client_instance_id_hash, token_hash,"
        "  status, xray_client_email, token_created_at, token_expires_at,"
        "  last_seen_at, created_at, updated_at)"
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
    return get_device(conn, device_id)  # type: ignore[return-value]


def get_device(conn: sqlite3.Connection, device_id: str) -> dict[str, Any] | None:
    row = conn.execute("SELECT * FROM devices WHERE id = ?", (device_id,)).fetchone()
    return _device_to_dict(row) if row else None


def get_device_by_token_hash(
    conn: sqlite3.Connection, token_hash: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM devices WHERE token_hash = ?", (token_hash,)
    ).fetchone()
    return _device_to_dict(row) if row else None


def find_active_device_by_name(
    conn: sqlite3.Connection,
    user_id: str,
    device_name: str,
    platform: str,
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM devices"
        " WHERE user_id = ? AND device_name = ? AND platform = ?"
        "   AND status = 'active'"
        " ORDER BY created_at, id LIMIT 1",
        (user_id, device_name, platform),
    ).fetchone()
    return _device_to_dict(row) if row else None


def find_active_device_by_instance(
    conn: sqlite3.Connection,
    user_id: str,
    platform: str,
    client_instance_id_hash: str,
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM devices"
        " WHERE user_id = ? AND platform = ? AND client_instance_id_hash = ?"
        "   AND status = 'active' LIMIT 1",
        (user_id, platform, client_instance_id_hash),
    ).fetchone()
    return _device_to_dict(row) if row else None


def get_device_instance_hash(
    conn: sqlite3.Connection, device_id: str
) -> str | None:
    row = conn.execute(
        "SELECT client_instance_id_hash FROM devices WHERE id = ?", (device_id,)
    ).fetchone()
    return row["client_instance_id_hash"] if row else None


def get_device_token_hash(
    conn: sqlite3.Connection, device_id: str
) -> str | None:
    row = conn.execute(
        "SELECT token_hash FROM devices WHERE id = ?", (device_id,)
    ).fetchone()
    return row["token_hash"] if row else None


def rotate_device_token(
    conn: sqlite3.Connection,
    device_id: str,
    token_hash: str,
    xray_client_email: str,
    now: str,
    client_instance_id_hash: str | None = None,
    device_name: str | None = None,
    client_version: str | None = None,
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT id, xray_client_email FROM devices WHERE id = ?", (device_id,)
    ).fetchone()
    if row is None:
        return None
    if row["xray_client_email"] != xray_client_email:
        # The cursor holds the *old* client's cumulative counters.  Kept, the
        # sweep would read the new client's first total minus the old
        # cursor, and record the old client's whole history as missing from
        # the new one.  A new client starts at zero: so does its cursor.
        conn.execute("DELETE FROM usage_cursors WHERE device_id = ?", (device_id,))

    conn.execute(
        "UPDATE devices"
        " SET token_hash = ?, xray_client_email = ?, token_created_at = ?,"
        "     client_instance_id_hash = COALESCE(?, client_instance_id_hash),"
        "     device_name = COALESCE(?, device_name),"
        "     client_version = COALESCE(?, client_version),"
        "     updated_at = ?"
        " WHERE id = ?",
        (
            token_hash,
            xray_client_email,
            now,
            client_instance_id_hash,
            device_name,
            client_version,
            now,
            device_id,
        ),
    )
    return get_device(conn, device_id)


def list_devices(
    conn: sqlite3.Connection, user_id: str | None = None
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM devices"
    params: list[Any] = []
    if user_id is not None:
        sql += " WHERE user_id = ?"
        params.append(user_id)
    sql += " ORDER BY created_at, id"
    rows = conn.execute(sql, params).fetchall()
    return [_device_to_dict(r) for r in rows]


def set_device_disabled(conn: sqlite3.Connection, device_id: str) -> bool:
    row = conn.execute(
        "SELECT id FROM devices WHERE id = ?", (device_id,)
    ).fetchone()
    if row is None:
        return False

    conn.execute(
        "UPDATE devices SET status = 'disabled', updated_at = ? WHERE id = ?",
        (utc_now(), device_id),
    )
    return True


def touch_device_seen(conn: sqlite3.Connection, device_id: str, now: str) -> None:
    conn.execute(
        "UPDATE devices SET last_seen_at = ? WHERE id = ?", (now, device_id)
    )


# ---------------------------------------------------------------------------
# Config versions
# ---------------------------------------------------------------------------

def latest_config(conn: sqlite3.Connection) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT version, config_json, created_at FROM config_versions"
        " ORDER BY version DESC LIMIT 1"
    ).fetchone()
    if row is None:
        return None

    try:
        config = json.loads(row["config_json"])
    except (json.JSONDecodeError, TypeError):
        config = {}
    if not isinstance(config, dict):
        config = {}

    return {
        "version": int(row["version"]),
        "config": config,
        "createdAt": row["created_at"],
    }


def insert_config(
    conn: sqlite3.Connection,
    config_dict: dict[str, Any],
    note: str,
    now: str,
) -> dict[str, Any]:
    row = conn.execute(
        "SELECT COALESCE(MAX(version), 0) AS max_version FROM config_versions"
    ).fetchone()
    version = int(row["max_version"]) + 1
    config_json = json.dumps(config_dict, ensure_ascii=False)
    conn.execute(
        "INSERT INTO config_versions(version, config_json, note, created_at)"
        " VALUES (?, ?, ?, ?)",
        (version, config_json, note, now),
    )
    return {"version": version, "config": config_dict, "createdAt": now}


# ---------------------------------------------------------------------------
# Server metadata
# ---------------------------------------------------------------------------

def get_meta(conn: sqlite3.Connection, key: str) -> str | None:
    row = conn.execute(
        "SELECT value FROM server_meta WHERE key = ?", (key,)
    ).fetchone()
    return row["value"] if row else None


def set_meta(conn: sqlite3.Connection, key: str, value: str, now: str) -> None:
    conn.execute(
        "INSERT INTO server_meta(key, value, updated_at)"
        " VALUES (?, ?, ?)"
        " ON CONFLICT(key) DO UPDATE SET value = excluded.value,"
        "     updated_at = excluded.updated_at",
        (key, value, now),
    )


# ---------------------------------------------------------------------------
# Update Plane: releases, assignments, audit
# ---------------------------------------------------------------------------

def _release_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "platform": row["platform"],
        "version": row["version"],
        "channel": row["channel"],
        "status": row["status"],
        "mandatory": bool(row["mandatory"]),
        "artifactUrl": row["artifact_url"],
        "artifactSha256": row["artifact_sha256"],
        "artifactSize": row["artifact_size"],
        "platformSignatureType": row["platform_signature_type"],
        "platformSignatureSubjectSha256": row["platform_signature_subject"],
        "minimumVersion": row["minimum_version"],
        "signingKeyId": row["signing_key_id"],
        "note": row["note"],
        "createdAt": row["created_at"],
        "updatedAt": row["updated_at"],
    }


def _assignment_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "scope": row["scope"],
        "targetId": row["target_id"],
        "platform": row["platform"],
        "releaseId": row["release_id"] or "",
        "featureFlags": row["feature_flags"],
        "note": row["note"],
        "createdAt": row["created_at"],
        "updatedAt": row["updated_at"],
    }


def create_release(
    conn: sqlite3.Connection,
    summary: dict[str, Any],
    manifest_b64: str,
    signature_b64: str,
    signing_key_id: str,
    note: str,
    now: str,
) -> dict[str, Any]:
    release_id = new_id("rel_")
    conn.execute(
        "INSERT INTO releases(id, platform, version, channel, status, mandatory,"
        " artifact_url, artifact_sha256, artifact_size, platform_signature_type,"
        " platform_signature_subject, minimum_version, manifest_b64, signature_b64,"
        " signing_key_id, note, created_at, updated_at)"
        " VALUES (?, ?, ?, ?, 'draft', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (
            release_id,
            summary["platform"],
            summary["version"],
            summary["channel"],
            1 if summary["mandatory"] else 0,
            summary["artifactUrl"],
            summary["artifactSha256"],
            summary["artifactSize"],
            summary["platformSignatureType"],
            summary["platformSignatureSubjectSha256"],
            summary["minimumVersion"],
            manifest_b64,
            signature_b64,
            signing_key_id,
            note,
            now,
            now,
        ),
    )
    return get_release(conn, release_id) or {}


def get_release(conn: sqlite3.Connection, release_id: str) -> dict[str, Any] | None:
    row = conn.execute("SELECT * FROM releases WHERE id = ?", (release_id,)).fetchone()
    return _release_to_dict(row) if row else None


def release_was_published(conn: sqlite3.Connection, release: dict[str, Any]) -> bool:
    """Whether a release was ever offered to devices.

    ``published`` says so directly.  ``revoked`` does not: a draft may be
    revoked without ever being published.  The publish transition always
    writes a ``release_published`` audit row, so that row is the record.
    """
    if release.get("status") == "published":
        return True
    if release.get("status") != "revoked":
        return False
    row = conn.execute(
        "SELECT 1 FROM release_audit WHERE release_id = ? AND action = 'release_published'"
        " LIMIT 1",
        (release.get("id") or "",),
    ).fetchone()
    return row is not None


def get_release_by_version(
    conn: sqlite3.Connection, platform: str, version: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM releases WHERE platform = ? AND version = ?",
        (platform, version),
    ).fetchone()
    return _release_to_dict(row) if row else None


def get_release_payload(
    conn: sqlite3.Connection, release_id: str
) -> dict[str, Any] | None:
    """Return the signed bytes for a release, exactly as they were stored.

    Kept separate from :func:`get_release` so that listing endpoints cannot
    accidentally splash multi-kilobyte manifests into an Admin table view.
    """
    row = conn.execute(
        "SELECT manifest_b64, signature_b64, signing_key_id FROM releases"
        " WHERE id = ?",
        (release_id,),
    ).fetchone()
    if row is None:
        return None
    return {
        "manifest": row["manifest_b64"],
        "signature": row["signature_b64"],
        "signingKeyId": row["signing_key_id"],
    }


def list_releases(
    conn: sqlite3.Connection,
    platform: str | None = None,
    status: str | None = None,
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM releases"
    clauses: list[str] = []
    params: list[Any] = []
    if platform:
        clauses.append("platform = ?")
        params.append(platform)
    if status:
        clauses.append("status = ?")
        params.append(status)
    if clauses:
        sql += " WHERE " + " AND ".join(clauses)
    sql += " ORDER BY created_at DESC, id DESC"
    return [_release_to_dict(row) for row in conn.execute(sql, params)]


def set_release_status(
    conn: sqlite3.Connection, release_id: str, status: str, now: str
) -> bool:
    cur = conn.execute(
        "UPDATE releases SET status = ?, updated_at = ? WHERE id = ?",
        (status, now, release_id),
    )
    return cur.rowcount > 0


def upsert_assignment(
    conn: sqlite3.Connection,
    scope: str,
    target_id: str,
    platform: str,
    release_id: str | None,
    feature_flags: str,
    note: str,
    now: str,
) -> dict[str, Any]:
    conn.execute(
        "INSERT INTO release_assignments(id, scope, target_id, platform,"
        " release_id, feature_flags, note, created_at, updated_at)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"
        " ON CONFLICT(scope, target_id, platform) DO UPDATE SET"
        "     release_id = excluded.release_id,"
        "     feature_flags = excluded.feature_flags,"
        "     note = excluded.note,"
        "     updated_at = excluded.updated_at",
        (
            new_id("asg_"),
            scope,
            target_id,
            platform,
            release_id,
            feature_flags,
            note,
            now,
            now,
        ),
    )
    return get_assignment(conn, scope, target_id, platform) or {}


def get_assignment(
    conn: sqlite3.Connection, scope: str, target_id: str, platform: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM release_assignments"
        " WHERE scope = ? AND target_id = ? AND platform = ?",
        (scope, target_id, platform),
    ).fetchone()
    return _assignment_to_dict(row) if row else None


def get_assignment_by_id(
    conn: sqlite3.Connection, assignment_id: str
) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM release_assignments WHERE id = ?", (assignment_id,)
    ).fetchone()
    return _assignment_to_dict(row) if row else None


def list_assignments(
    conn: sqlite3.Connection,
    scope: str | None = None,
    target_id: str | None = None,
    platform: str | None = None,
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM release_assignments"
    clauses: list[str] = []
    params: list[Any] = []
    if scope:
        clauses.append("scope = ?")
        params.append(scope)
    if target_id:
        clauses.append("target_id = ?")
        params.append(target_id)
    if platform:
        clauses.append("platform = ?")
        params.append(platform)
    if clauses:
        sql += " WHERE " + " AND ".join(clauses)
    sql += " ORDER BY scope, target_id, platform"
    return [_assignment_to_dict(row) for row in conn.execute(sql, params)]


def delete_assignment(conn: sqlite3.Connection, assignment_id: str) -> bool:
    cur = conn.execute("DELETE FROM release_assignments WHERE id = ?", (assignment_id,))
    return cur.rowcount > 0


def clear_assignments_for_target(
    conn: sqlite3.Connection, scope: str, target_id: str
) -> None:
    """Drop every assignment belonging to a target that is going away.

    Called when a device or user is deleted.  Without this, a later device
    holding a recycled identifier could inherit a stale pin.
    """
    conn.execute(
        "DELETE FROM release_assignments WHERE scope = ? AND target_id = ?",
        (scope, target_id),
    )


def append_release_audit(
    conn: sqlite3.Connection,
    now: str,
    action: str,
    scope: str = "",
    target_id: str = "",
    platform: str = "",
    release_id: str = "",
    previous_release_id: str = "",
    detail: str = "",
) -> None:
    conn.execute(
        "INSERT INTO release_audit(at, action, scope, target_id, platform,"
        " release_id, previous_release_id, detail)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
        (
            now,
            action,
            scope,
            target_id,
            platform,
            release_id,
            previous_release_id,
            detail,
        ),
    )


def list_release_audit(
    conn: sqlite3.Connection,
    target_id: str | None = None,
    limit: int = 100,
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM release_audit"
    params: list[Any] = []
    if target_id:
        sql += " WHERE target_id = ?"
        params.append(target_id)
    sql += " ORDER BY id DESC LIMIT ?"
    params.append(max(1, min(limit, 1000)))
    return [
        {
            "at": row["at"],
            "action": row["action"],
            "scope": row["scope"],
            "targetId": row["target_id"],
            "platform": row["platform"],
            "releaseId": row["release_id"],
            "previousReleaseId": row["previous_release_id"],
            "detail": row["detail"],
        }
        for row in conn.execute(sql, params)
    ]


# ---------------------------------------------------------------------------
# Observability Plane: usage buckets, cursors, activity
# ---------------------------------------------------------------------------

def add_usage(
    conn: sqlite3.Connection,
    device_id: str,
    user_id: str,
    bucket_start: str,
    uplink: int,
    downlink: int,
    now: str,
) -> None:
    """Accumulate bytes into an hourly bucket.

    Additive rather than last-writer-wins: a bucket is filled by several
    independent sources (the x-ui counter sweep and the device's own report),
    and whichever lands second must not erase the first.
    """
    if uplink <= 0 and downlink <= 0:
        return
    conn.execute(
        "INSERT INTO usage_buckets(device_id, user_id, bucket_start,"
        " uplink_bytes, downlink_bytes, samples, updated_at)"
        " VALUES (?, ?, ?, ?, ?, 1, ?)"
        " ON CONFLICT(device_id, bucket_start) DO UPDATE SET"
        "     uplink_bytes = uplink_bytes + excluded.uplink_bytes,"
        "     downlink_bytes = downlink_bytes + excluded.downlink_bytes,"
        "     samples = samples + 1,"
        "     updated_at = excluded.updated_at",
        (device_id, user_id, bucket_start, max(0, uplink), max(0, downlink), now),
    )


def add_usage_category(
    conn: sqlite3.Connection,
    device_id: str,
    bucket_start: str,
    category: str,
    total_bytes: int,
    now: str,
) -> None:
    if total_bytes <= 0:
        return
    conn.execute(
        "INSERT INTO usage_categories(device_id, bucket_start, category,"
        " total_bytes, updated_at) VALUES (?, ?, ?, ?, ?)"
        " ON CONFLICT(device_id, bucket_start, category) DO UPDATE SET"
        "     total_bytes = total_bytes + excluded.total_bytes,"
        "     updated_at = excluded.updated_at",
        (device_id, bucket_start, category, total_bytes, now),
    )


def list_usage(
    conn: sqlite3.Connection,
    device_id: str | None,
    user_id: str | None,
    start: str,
    end: str,
) -> list[dict[str, Any]]:
    sql = (
        "SELECT device_id, user_id, bucket_start, uplink_bytes, downlink_bytes,"
        " samples FROM usage_buckets WHERE bucket_start >= ? AND bucket_start <= ?"
    )
    params: list[Any] = [start, end]
    if device_id:
        sql += " AND device_id = ?"
        params.append(device_id)
    if user_id:
        sql += " AND user_id = ?"
        params.append(user_id)
    sql += " ORDER BY bucket_start"
    return [
        {
            "deviceId": row["device_id"],
            "userId": row["user_id"],
            "bucketStart": row["bucket_start"],
            "uplinkBytes": row["uplink_bytes"],
            "downlinkBytes": row["downlink_bytes"],
            "samples": row["samples"],
        }
        for row in conn.execute(sql, params)
    ]


def list_usage_categories(
    conn: sqlite3.Connection,
    device_id: str | None,
    start: str,
    end: str,
    user_id: str | None = None,
) -> list[dict[str, Any]]:
    sql = (
        "SELECT category, SUM(total_bytes) AS total FROM usage_categories"
        " WHERE bucket_start >= ? AND bucket_start <= ?"
    )
    params: list[Any] = [start, end]
    if device_id:
        sql += " AND device_id = ?"
        params.append(device_id)
    if user_id:
        # The table has no user column; a user's categories are their
        # devices' categories.  Without this filter a per-user view showed
        # the whole site's categories next to that user's own series.
        sql += " AND device_id IN (SELECT id FROM devices WHERE user_id = ?)"
        params.append(user_id)
    sql += " GROUP BY category ORDER BY total DESC"
    return [
        {"category": row["category"], "totalBytes": row["total"] or 0}
        for row in conn.execute(sql, params)
    ]


def get_usage_cursor(conn: sqlite3.Connection, device_id: str) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT device_id, last_uplink, last_downlink, observed_at"
        " FROM usage_cursors WHERE device_id = ?",
        (device_id,),
    ).fetchone()
    if row is None:
        return None
    return {
        "deviceId": row["device_id"],
        "lastUplink": row["last_uplink"],
        "lastDownlink": row["last_downlink"],
        "observedAt": row["observed_at"],
    }


def list_usage_cursors(conn: sqlite3.Connection) -> dict[str, dict[str, Any]]:
    return {
        row["device_id"]: {
            "lastUplink": row["last_uplink"],
            "lastDownlink": row["last_downlink"],
            "observedAt": row["observed_at"],
        }
        for row in conn.execute(
            "SELECT device_id, last_uplink, last_downlink, observed_at"
            " FROM usage_cursors"
        )
    }


def set_usage_cursor(
    conn: sqlite3.Connection,
    device_id: str,
    uplink: int,
    downlink: int,
    now: str,
) -> None:
    conn.execute(
        "INSERT INTO usage_cursors(device_id, last_uplink, last_downlink,"
        " observed_at) VALUES (?, ?, ?, ?)"
        " ON CONFLICT(device_id) DO UPDATE SET"
        "     last_uplink = excluded.last_uplink,"
        "     last_downlink = excluded.last_downlink,"
        "     observed_at = excluded.observed_at",
        (device_id, uplink, downlink, now),
    )


def upsert_activity(
    conn: sqlite3.Connection,
    device_id: str,
    user_id: str,
    now: str,
    ip_prefix: str = "",
    geo: dict[str, str] | None = None,
    ip_address: str = "",
) -> None:
    """Record that a device was heard from, and optionally where.

    ``ip_address`` and ``ip_prefix`` are both produced by
    :mod:`observability` before they reach here, which is what keeps loopback
    and malformed input out of the table.  An empty address, prefix or geo
    field leaves the stored value alone rather than blanking it, so one
    request arriving without a trusted forwarding header cannot wipe the
    address the server already knew or the location an operator entered.
    """
    geo = geo or {}
    conn.execute(
        "INSERT INTO device_activity(device_id, user_id, last_seen_at,"
        " ip_address, ip_prefix, country, region, city, asn, isp, updated_at)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
        " ON CONFLICT(device_id) DO UPDATE SET"
        "     user_id = excluded.user_id,"
        "     last_seen_at = excluded.last_seen_at,"
        "     ip_address = CASE WHEN excluded.ip_address = '' THEN ip_address"
        "                       ELSE excluded.ip_address END,"
        "     ip_prefix = CASE WHEN excluded.ip_prefix = '' THEN ip_prefix"
        "                      ELSE excluded.ip_prefix END,"
        "     country = CASE WHEN excluded.country = '' THEN country"
        "                    ELSE excluded.country END,"
        "     region = CASE WHEN excluded.region = '' THEN region"
        "                   ELSE excluded.region END,"
        "     city = CASE WHEN excluded.city = '' THEN city"
        "                 ELSE excluded.city END,"
        "     asn = CASE WHEN excluded.asn = '' THEN asn ELSE excluded.asn END,"
        "     isp = CASE WHEN excluded.isp = '' THEN isp ELSE excluded.isp END,"
        "     updated_at = excluded.updated_at",
        (
            device_id,
            user_id,
            now,
            ip_address,
            ip_prefix,
            geo.get("country", ""),
            geo.get("region", ""),
            geo.get("city", ""),
            geo.get("asn", ""),
            geo.get("isp", ""),
            now,
        ),
    )


def set_activity_geo(
    conn: sqlite3.Connection, device_id: str, geo: dict[str, str], now: str
) -> bool:
    """Overwrite the operator-supplied location fields.

    Unlike :func:`upsert_activity` this one does blank a field that is passed
    as an empty string, because an operator clearing a wrong value has to be
    able to actually clear it.
    """
    assignments = ", ".join(f"{key} = ?" for key in geo)
    if not assignments:
        return False
    params = list(geo.values()) + [now, device_id]
    cur = conn.execute(
        f"UPDATE device_activity SET {assignments}, updated_at = ?"
        " WHERE device_id = ?",
        params,
    )
    return cur.rowcount > 0


def get_activity(conn: sqlite3.Connection, device_id: str) -> dict[str, Any] | None:
    row = conn.execute(
        "SELECT * FROM device_activity WHERE device_id = ?", (device_id,)
    ).fetchone()
    return _activity_to_dict(row) if row else None


def list_activity(
    conn: sqlite3.Connection, user_id: str | None = None
) -> list[dict[str, Any]]:
    sql = "SELECT * FROM device_activity"
    params: list[Any] = []
    if user_id:
        sql += " WHERE user_id = ?"
        params.append(user_id)
    sql += " ORDER BY last_seen_at DESC"
    return [_activity_to_dict(row) for row in conn.execute(sql, params)]


def record_device_address(
    conn: sqlite3.Connection, device_id: str, address: str, now: str, limit: int
) -> None:
    """Add an address to a device's history and drop anything past ``limit``.

    The eviction runs on every write because this process has no scheduler.
    It deletes by ``last_seen_at``, so the addresses that survive are the ones
    used most recently rather than the ones seen first -- a device that has
    been on the same connection for a year does not pin the whole history.

    ``limit`` of 0 or less keeps no history at all; the current address still
    lives on ``device_activity``.
    """
    if not address:
        return
    if limit <= 0:
        conn.execute("DELETE FROM device_addresses WHERE device_id = ?", (device_id,))
        return
    conn.execute(
        "INSERT INTO device_addresses(device_id, address, first_seen_at,"
        " last_seen_at) VALUES (?, ?, ?, ?)"
        " ON CONFLICT(device_id, address) DO UPDATE SET"
        "     last_seen_at = excluded.last_seen_at",
        (device_id, address, now, now),
    )
    conn.execute(
        "DELETE FROM device_addresses WHERE device_id = ? AND address NOT IN ("
        "    SELECT address FROM device_addresses WHERE device_id = ?"
        "    ORDER BY last_seen_at DESC, address ASC LIMIT ?"
        ")",
        (device_id, device_id, limit),
    )


def list_device_addresses(
    conn: sqlite3.Connection, device_id: str
) -> list[dict[str, Any]]:
    """The device's recent addresses, most recently used first."""
    rows = conn.execute(
        "SELECT address, first_seen_at, last_seen_at FROM device_addresses"
        " WHERE device_id = ? ORDER BY last_seen_at DESC, address ASC",
        (device_id,),
    )
    return [
        {
            "address": row["address"],
            "firstSeenAt": row["first_seen_at"],
            "lastSeenAt": row["last_seen_at"],
        }
        for row in rows
    ]


def _activity_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "deviceId": row["device_id"],
        "userId": row["user_id"],
        "lastSeenAt": row["last_seen_at"],
        "ipAddress": row["ip_address"],
        "ipPrefix": row["ip_prefix"],
        "country": row["country"],
        "region": row["region"],
        "city": row["city"],
        "asn": row["asn"],
        "isp": row["isp"],
        "updatedAt": row["updated_at"],
    }


def prune_usage(conn: sqlite3.Connection, cutoff: str) -> int:
    """Drop buckets older than the retention cutoff.

    Runs on write because this process has no scheduler; the alternative is a
    table that grows without bound for the lifetime of the deployment.
    """
    removed = conn.execute(
        "DELETE FROM usage_buckets WHERE bucket_start < ?", (cutoff,)
    ).rowcount
    conn.execute(
        "DELETE FROM usage_categories WHERE bucket_start < ?", (cutoff,)
    )
    return removed


def clear_observability_for_device(conn: sqlite3.Connection, device_id: str) -> None:
    """Forget everything this plane knows about a device.

    Called when a device is deleted.  Usage history outliving the device it
    describes would be a record about a person that no longer maps to
    anything they can see or revoke.
    """
    conn.execute("DELETE FROM usage_buckets WHERE device_id = ?", (device_id,))
    conn.execute("DELETE FROM usage_categories WHERE device_id = ?", (device_id,))
    conn.execute("DELETE FROM usage_cursors WHERE device_id = ?", (device_id,))
    conn.execute("DELETE FROM device_activity WHERE device_id = ?", (device_id,))
    conn.execute("DELETE FROM device_addresses WHERE device_id = ?", (device_id,))
