"""XuiAdapter protocol and the 3x-ui SQLite adapter.

Only this module is allowed to touch the 3x-ui SQLite DB (or a local fake
copy of the same schema during tests).  Public error/log messages must never
contain the REALITY private key; the adapter therefore never includes raw JSON
or raw subprocess output in exceptions.

New 3x-ui data model support (``clients`` + ``client_inbounds``):
``add_client`` / ``remove_client`` / ``update_client`` / ``list_clients``
prefer the new tables when they exist and fall back to the legacy
``inbounds.settings.clients`` JSON for older 3x-ui versions.  The adapter also
cleans legacy ``inbounds.settings.clients`` entries with the same email so a
single logical client never appears in both places (``user1`` is never
touched by these cleanups).
"""

from __future__ import annotations

import json
import os
import re
import secrets
import socket
import sqlite3
import subprocess
import sys
import threading
import time
import uuid
from collections.abc import Sequence
from contextlib import closing
from typing import Protocol


# app.py provisions clients as ``myproxy-{device_id}-{token_hex(4)}``, where
# ``device_id`` is ``dev_`` plus 24 lowercase hex characters.  Keep the exact
# namespace definition here so the privileged helper never treats another
# panel client's merely similar display name as MyProxy-owned.
MYPROXY_EMAIL_PREFIX = "myproxy-"
_MYPROXY_EMAIL_RE = re.compile(r"^myproxy-dev_[0-9a-f]{24}-[0-9a-f]{8}$", re.ASCII)


def is_myproxy_email(value: object) -> bool:
    """Return whether *value* belongs to the clients provisioned by MyProxy."""
    return isinstance(value, str) and _MYPROXY_EMAIL_RE.fullmatch(value) is not None


def new_myproxy_email(device_id: str) -> str:
    """Create an x-ui email in the one namespace accepted by the helper."""
    if re.fullmatch(r"dev_[0-9a-f]{24}", device_id, re.ASCII) is None:
        raise XuiError("invalid MyProxy device id")
    return f"{MYPROXY_EMAIL_PREFIX}{device_id}-{secrets.token_hex(4)}"


def _is_legacy_myproxy_email(value: object) -> bool:
    """Recognize pre-boundary test/legacy names in the root-local adapter.

    The direct SQLite adapter is root-only in production.  It retains prefix
    compatibility for migrations and adapter-level tests, while both sides of
    the Unix-socket privilege boundary enforce :func:`is_myproxy_email`.
    """
    return (
        isinstance(value, str)
        and value.startswith(MYPROXY_EMAIL_PREFIX)
        and len(value) > len(MYPROXY_EMAIL_PREFIX)
    )


def _require_myproxy_email(email: str) -> str:
    if not isinstance(email, str) or not email:
        raise XuiError("email is required")
    if not is_myproxy_email(email):
        raise XuiError("email is outside the MyProxy namespace")
    return email


def _require_legacy_myproxy_email(email: str) -> str:
    if not isinstance(email, str) or not email:
        raise XuiError("email is required")
    if not _is_legacy_myproxy_email(email):
        raise XuiError("email is outside the MyProxy namespace")
    return email


def _emit_structured(event: str, **fields: object) -> None:
    """Emit adapter events as single-line JSON without subprocess output."""
    record = {
        "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "event": event,
    }
    record.update(fields)
    print(json.dumps(record, ensure_ascii=False, separators=(",", ":")), file=sys.stderr, flush=True)


def _serialized_xui_write(fn):
    def wrapper(self, *args, **kwargs):
        with self._xui_write_lock:
            return fn(self, *args, **kwargs)
    return wrapper


# The only error codes allowed to cross the privileged helper boundary.
# They are a closed vocabulary carrying no x-ui path, SQL or subprocess
# output, and exist so callers can branch on an outcome instead of matching
# error text -- messages are deliberately generic on the socket adapter and
# therefore unmatchable in production.
XUI_ERROR_CODES = frozenset({"client_not_found"})


class XuiError(RuntimeError):
    """Raised for any 3x-ui adapter failure.

    ``code`` is either one of :data:`XUI_ERROR_CODES` or ``None``.  Anything
    outside the vocabulary is dropped rather than propagated, so an untrusted
    or newer helper cannot widen the boundary.
    """

    def __init__(self, message: str, *, code: str | None = None) -> None:
        super().__init__(message)
        self.code = code if code in XUI_ERROR_CODES else None


class XuiAdapter(Protocol):
    """The shape every 3x-ui adapter must implement."""

    def public_profile(self) -> dict: ...
    def add_client(self, email: str) -> str: ...
    @_serialized_xui_write
    def update_client(
        self,
        email: str,
        *,
        enable: bool | None = None,
        limit_ip: int | None = None,
        total_gb: int | None = None,
        expiry_time: int | None = None,
    ) -> None: ...
    def remove_client(self, email: str) -> None: ...
    def list_clients(self) -> list[dict]: ...


class UnixSocketXuiAdapter:
    """Least-privilege client for the root-owned x-ui helper.

    The API process only sends a small allow-listed JSON command over a local
    Unix socket.  It never opens the x-ui SQLite database or invokes
    ``systemctl``.  The helper intentionally returns generic errors so x-ui
    paths, SQL and subprocess output cannot cross the privilege boundary.
    """

    # list_clients returns every client, and clients only accumulate: a
    # rebind or a deleted device disables its old client rather than removing
    # it.  At ~164 bytes a client, 256 KiB ran out near 1,600 clients ever
    # issued, after which every device config fetch (it looks its own client
    # up in that list) failed with a 500 and the usage sweep stopped.  4 MiB
    # is ~25,000 clients; the bound still exists, it is just not the first
    # thing a growing deployment hits.
    _MAX_RESPONSE_BYTES = 4 * 1024 * 1024

    def __init__(self, socket_path: str, *, timeout: float = 15.0) -> None:
        if not socket_path or not os.path.isabs(socket_path):
            raise ValueError("x-ui helper socket must be an absolute path")
        if timeout <= 0:
            raise ValueError("x-ui helper timeout must be positive")
        self.socket_path = socket_path
        self.timeout = float(timeout)

    def _call(self, op: str, **params):
        request = {"op": op, **params}
        raw = (json.dumps(request, separators=(",", ":")) + "\n").encode("utf-8")
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as conn:
                conn.settimeout(self.timeout)
                conn.connect(self.socket_path)
                conn.sendall(raw)
                chunks: list[bytes] = []
                total = 0
                while True:
                    chunk = conn.recv(8192)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > self._MAX_RESPONSE_BYTES:
                        raise XuiError("x-ui helper response too large")
                    chunks.append(chunk)
                    if b"\n" in chunk:
                        break
            line = b"".join(chunks).split(b"\n", 1)[0]
            response = json.loads(line.decode("utf-8"))
            if not isinstance(response, dict) or response.get("ok") is not True:
                code = response.get("code") if isinstance(response, dict) else None
                raise XuiError(
                    "x-ui helper rejected operation",
                    code=code if isinstance(code, str) else None,
                )
            return response.get("result")
        except XuiError:
            raise
        except (OSError, UnicodeError, json.JSONDecodeError, TypeError):
            raise XuiError("x-ui helper unavailable") from None

    def public_profile(self) -> dict:
        result = self._call("public_profile")
        if not isinstance(result, dict):
            raise XuiError("x-ui helper returned invalid profile")
        return result

    def add_client(self, email: str) -> str:
        _require_myproxy_email(email)
        result = self._call("add_client", email=email)
        if not isinstance(result, str) or not result:
            raise XuiError("x-ui helper returned invalid client id")
        return result

    def update_client(self, email: str, *, enable: bool | None = None,
                      limit_ip: int | None = None, total_gb: int | None = None,
                      expiry_time: int | None = None) -> None:
        _require_myproxy_email(email)
        self._call(
            "update_client",
            email=email,
            enable=enable,
            limit_ip=limit_ip,
            total_gb=total_gb,
            expiry_time=expiry_time,
        )

    def remove_client(self, email: str) -> None:
        _require_myproxy_email(email)
        self._call("remove_client", email=email)

    def list_clients(self) -> list[dict]:
        result = self._call("list_clients")
        if not isinstance(result, list):
            raise XuiError("x-ui helper returned invalid clients")
        # Defence in depth: a compromised/misconfigured helper must not be
        # able to disclose panel-owned clients through the API adapter.
        return [
            client
            for client in result
            if isinstance(client, dict) and is_myproxy_email(client.get("email"))
        ]


class XuiSqliteAdapter:
    """Reads and writes a 3x-ui SQLite database.

    Read-only operations open the DB with ``mode=ro``; write operations use a
    normal connection, write back the relevant table(s) and then run the
    restart command.  Writes are committed before the restart: if the restart
    fails, callers are responsible for compensation.
    """

    def __init__(
        self,
        db_path: str,
        inbound_id: int,
        *,
        server_host: str | None = None,
        server_port: int | None = None,
        public_key: str | None = None,
        short_id: str | None = None,
        sni: str | None = None,
        fingerprint: str | None = None,
        flow: str | None = None,
        spider_x: str | None = None,
        restart_command: Sequence[str] = ("systemctl", "restart", "x-ui"),
        restart_timeout: float = 2.0,
        restart_attempts: int = 3,
    ) -> None:
        self.db_path = str(db_path)
        self.inbound_id = int(inbound_id)
        self.server_host = server_host
        self.server_port = int(server_port) if server_port is not None else None
        self.public_key = public_key
        self.short_id = short_id
        self.sni = sni
        self.fingerprint = fingerprint
        self.flow = flow
        self.spider_x = spider_x
        self.restart_command = tuple(str(part) for part in restart_command)
        if restart_timeout <= 0 or restart_attempts < 1:
            raise ValueError("x-ui restart budget is invalid")
        self.restart_timeout = float(restart_timeout)
        self.restart_attempts = int(restart_attempts)
        self._xui_write_lock = threading.Lock()

    # ------------------------------------------------------------------
    # Read-only helpers
    # ------------------------------------------------------------------
    def _connect_ro(self) -> sqlite3.Connection:
        return sqlite3.connect(f"file:{self.db_path}?mode=ro", uri=True)

    @staticmethod
    def _parse_json_object(text: str, label: str) -> dict:
        try:
            value = json.loads(text)
        except (json.JSONDecodeError, TypeError) as exc:
            raise XuiError(f"invalid x-ui {label} json") from exc
        if not isinstance(value, dict):
            raise XuiError(f"invalid x-ui {label} json")
        return value

    def _fetch_inbound(self) -> dict | None:
        try:
            with closing(self._connect_ro()) as conn:
                conn.row_factory = sqlite3.Row
                row = conn.execute(
                    "SELECT port, settings, stream_settings FROM inbounds WHERE id = ?",
                    (self.inbound_id,),
                ).fetchone()
                return dict(row) if row is not None else None
        except XuiError:
            raise
        except Exception as exc:
            raise XuiError("failed to read x-ui inbound") from exc

    def _has_new_client_tables(self) -> bool:
        """Return True when the 3x-ui DB has the new ``clients`` tables.

        A failed probe must never be reported as "this is the legacy schema".
        Right after ``_restart_xui()`` the database can answer ``database is
        locked``; downgrading that to the legacy branch writes the client into
        ``inbounds.settings.clients`` instead of the tables xray actually
        reads, so claim commits and returns 200 while the device is rejected at
        handshake with nothing in the logs.
        """
        try:
            with closing(self._connect_ro()) as conn:
                rows = conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('clients','client_inbounds')"
                ).fetchall()
        except Exception as exc:
            raise XuiError("failed to probe x-ui schema") from exc
        names = {row[0] for row in rows}
        return {"clients", "client_inbounds"} <= names

    def _traffic_map(self) -> dict[str, dict[str, int]]:
        try:
            with closing(self._connect_ro()) as conn:
                conn.row_factory = sqlite3.Row
                rows = conn.execute(
                    "SELECT email, up, down, total FROM client_traffics WHERE inbound_id = ?",
                    (self.inbound_id,),
                ).fetchall()
        except Exception as exc:
            raise XuiError("failed to read x-ui client traffic") from exc

        traffic: dict[str, dict[str, int]] = {}
        for row in rows:
            traffic[row["email"]] = {
                "up": int(row["up"] or 0),
                "down": int(row["down"] or 0),
                "total": int(row["total"] or 0),
            }
        return traffic

    # ------------------------------------------------------------------
    # Write helpers
    # ------------------------------------------------------------------
    def _save_settings(self, settings: dict, expected_raw: str | None = None) -> None:
        serialized = json.dumps(settings, ensure_ascii=False)
        try:
            with closing(sqlite3.connect(self.db_path)) as conn:
                if expected_raw is None:
                    cursor = conn.execute(
                        "UPDATE inbounds SET settings = ? WHERE id = ?",
                        (serialized, self.inbound_id),
                    )
                else:
                    # Legacy 3x-ui stores the whole client list in one JSON
                    # blob.  Refuse to overwrite a blob changed by another
                    # writer after our read; silent last-write-wins can
                    # delete an unrelated client.
                    cursor = conn.execute(
                        "UPDATE inbounds SET settings = ?"
                        " WHERE id = ? AND settings = ?",
                        (serialized, self.inbound_id, expected_raw),
                    )
                if cursor.rowcount != 1:
                    raise XuiError("x-ui settings changed concurrently")
                conn.commit()
        except XuiError:
            raise
        except Exception as exc:
            raise XuiError("failed to write x-ui settings") from exc

    def _restart_xui(self) -> None:
        last_exc: Exception | None = None
        delays = (0.25, 0.5)
        for attempt in range(1, self.restart_attempts + 1):
            try:
                proc = subprocess.run(
                    list(self.restart_command),
                    timeout=self.restart_timeout,
                    capture_output=True,
                    text=True,
                )
                _emit_structured(
                    "xui_restart",
                    attempt=attempt,
                    return_code=proc.returncode,
                )
                if proc.returncode == 0:
                    return
                # systemd can transiently reject a restart when a previous job
                # is settling; reset the failed state and back off.
                if (
                    self.restart_command
                    and os.path.basename(self.restart_command[0]) == "systemctl"
                ):
                    try:
                        subprocess.run(
                            [
                                self.restart_command[0],
                                "reset-failed",
                                self.restart_command[-1],
                            ],
                            timeout=min(0.5, self.restart_timeout),
                            capture_output=True,
                        )
                    except Exception:
                        pass
            except (OSError, subprocess.SubprocessError) as exc:
                last_exc = exc
                _emit_structured(
                    "xui_restart",
                    attempt=attempt,
                    error_type=type(exc).__name__,
                )
            if attempt < self.restart_attempts:
                time.sleep(delays[min(attempt - 1, len(delays) - 1)])
        if last_exc is not None:
            raise XuiError("x-ui restart command failed") from last_exc
        raise XuiError("x-ui restart command failed")

    def _remove_legacy_inbound_client(self, email: str) -> bool:
        """Remove ``email`` from the legacy ``inbounds.settings.clients`` JSON.

        Returns True when a row was removed.  Non-matching rows (including
        ``user1``) are always preserved.
        """
        row = self._fetch_inbound()
        if row is None:
            return False
        settings = self._parse_json_object(row["settings"], "settings")
        clients = settings.get("clients")
        if not isinstance(clients, list):
            return False

        remaining = [c for c in clients if c.get("email") != email]
        if len(remaining) == len(clients):
            return False

        settings["clients"] = remaining
        self._save_settings(settings, row["settings"])
        return True

    @staticmethod
    def _now_ms() -> int:
        return int(time.time() * 1000)

    # ------------------------------------------------------------------
    # New-table helpers (clients + client_inbounds)
    # ------------------------------------------------------------------
    def _find_new_client(
        self,
        conn: sqlite3.Connection,
        email: str,
        *,
        inbound_id: int | None = None,
    ) -> sqlite3.Row | None:
        """Find a client, optionally requiring a link to one inbound.

        ``clients`` is a shared table in newer 3x-ui schemas.  A lookup used
        by update/remove must therefore be constrained through
        ``client_inbounds``; otherwise an email belonging only to another
        inbound can be modified or deleted by this adapter.  Add keeps the
        historical global duplicate check by omitting ``inbound_id``.
        """
        conn.row_factory = sqlite3.Row
        if inbound_id is None:
            return conn.execute(
                "SELECT id, email, uuid, enable, limit_ip, total_gb, expiry_time, flow"
                " FROM clients WHERE email = ? ORDER BY id LIMIT 1",
                (email,),
            ).fetchone()
        return conn.execute(
            "SELECT c.id, c.email, c.uuid, c.enable, c.limit_ip, c.total_gb,"
            " c.expiry_time, c.flow"
            " FROM clients c"
            " JOIN client_inbounds ci ON ci.client_id = c.id"
            " WHERE c.email = ? AND ci.inbound_id = ?"
            " ORDER BY c.id LIMIT 1",
            (email, int(inbound_id)),
        ).fetchone()

    def _require_inbound(self, conn: sqlite3.Connection) -> None:
        if conn.execute("SELECT 1 FROM inbounds WHERE id = ?", (self.inbound_id,)).fetchone() is None:
            # Infrastructure loss must not trigger the recoverable missing-client path.
            raise XuiError("inbound not found")

    def _insert_new_client(self, email: str) -> tuple[int, str]:
        """Insert a new row into ``clients`` + ``client_inbounds``.

        Returns ``(client_pk, client_uuid)``.
        """
        client_uuid = str(uuid.uuid4())
        sub_id = secrets.token_hex(8)
        now_ms = self._now_ms()

        try:
            with closing(sqlite3.connect(self.db_path)) as conn:
                conn.execute("BEGIN IMMEDIATE")
                self._require_inbound(conn)
                cursor = conn.execute(
                    "INSERT INTO clients ("
                    " email, sub_id, uuid, password, auth, flow, security, reverse,"
                    " wg_private_key, wg_public_key, wg_allowed_ips, wg_pre_shared_key,"
                    " wg_keep_alive, secret, ad_tag, limit_ip, total_gb, expiry_time,"
                    " enable, tg_id, group_name, comment, reset, created_at, updated_at"
                    ") VALUES (?, ?, ?, '', '', 'xtls-rprx-vision', '', '', '', '', '', '',"
                    " 0, '', '', 0, 0, 0, 1, 0, '', '', 0, ?, ?)",
                    (email, sub_id, client_uuid, now_ms, now_ms),
                )
                client_pk = int(cursor.lastrowid)
                conn.execute(
                    "INSERT INTO client_inbounds"
                    " (client_id, inbound_id, flow_override, created_at)"
                    " VALUES (?, ?, 'xtls-rprx-vision', ?)",
                    (client_pk, self.inbound_id, now_ms),
                )
                conn.commit()
        except XuiError:
            raise
        except Exception as exc:
            raise XuiError("failed to write x-ui client tables") from exc

        return client_pk, client_uuid

    def _delete_new_client(self, conn: sqlite3.Connection, client_pk: int) -> None:
        """Unlink this inbound and delete the shared client only when orphaned."""
        cursor = conn.execute(
            "DELETE FROM client_inbounds WHERE client_id = ? AND inbound_id = ?",
            (client_pk, self.inbound_id),
        )
        if cursor.rowcount != 1:
            raise XuiError("client not found", code="client_not_found")

        linked_elsewhere = conn.execute(
            "SELECT 1 FROM client_inbounds WHERE client_id = ? LIMIT 1",
            (client_pk,),
        ).fetchone()
        if linked_elsewhere is None:
            conn.execute("DELETE FROM clients WHERE id = ?", (client_pk,))

    def _update_new_client(
        self,
        conn: sqlite3.Connection,
        client_pk: int,
        *,
        enable: bool | None = None,
        limit_ip: int | None = None,
        total_gb: int | None = None,
        expiry_time: int | None = None,
    ) -> None:
        sets: list[str] = []
        params: list[object] = []

        if enable is not None:
            sets.append("enable = ?")
            params.append(1 if enable else 0)
        if limit_ip is not None:
            sets.append("limit_ip = ?")
            params.append(int(limit_ip))
        if total_gb is not None:
            sets.append("total_gb = ?")
            params.append(int(total_gb))
        if expiry_time is not None:
            sets.append("expiry_time = ?")
            params.append(int(expiry_time))

        if not sets:
            return
        sets.append("updated_at = ?")
        params.append(self._now_ms())
        params.append(client_pk)
        conn.execute(f"UPDATE clients SET {', '.join(sets)} WHERE id = ?", params)

    def _read_new_clients(self) -> list[dict]:
        try:
            with closing(self._connect_ro()) as conn:
                conn.row_factory = sqlite3.Row
                rows = conn.execute(
                    "SELECT c.id AS client_pk, c.email, c.uuid, c.enable"
                    " FROM clients c"
                    " JOIN client_inbounds ci ON ci.client_id = c.id"
                    " WHERE ci.inbound_id = ?"
                    " ORDER BY c.id",
                    (self.inbound_id,),
                ).fetchall()
        except Exception as exc:
            raise XuiError("failed to read x-ui client tables") from exc

        traffic = self._traffic_map()
        result: list[dict] = []
        for row in rows:
            email = row["email"]
            if not _is_legacy_myproxy_email(email):
                continue
            t = traffic.get(email, {"up": 0, "down": 0, "total": 0})
            result.append(
                {
                    "email": email,
                    "id": row["uuid"] or "",
                    "enable": bool(row["enable"]),
                    "up": int(t["up"]),
                    "down": int(t["down"]),
                    "total": int(t["total"]),
                }
            )
        return result

    # ------------------------------------------------------------------
    # XuiAdapter implementation
    # ------------------------------------------------------------------
    def public_profile(self) -> dict:
        """Return the public profile without a uuid or private key."""
        row = self._fetch_inbound()
        if row is None:
            raise XuiError("inbound not found")

        stream = self._parse_json_object(row["stream_settings"], "stream_settings")
        if str(stream.get("security", "")).strip().lower() != "reality":
            raise XuiError("x-ui inbound security must be reality")
        reality = stream.get("realitySettings")
        if not isinstance(reality, dict):
            reality = {}
        reality_settings = reality.get("settings")
        if not isinstance(reality_settings, dict):
            reality_settings = {}

        server_host = self.server_host
        if server_host is None or not str(server_host).strip():
            raise XuiError("server_host is required")
        server_host = str(server_host).strip()

        server_port = self.server_port
        if server_port is None or server_port == 0:
            server_port = row.get("port")
        if server_port is None or server_port == 0:
            raise XuiError("server_port is required")
        server_port = int(server_port)
        if not 1 <= server_port <= 65535:
            raise XuiError("server_port is invalid")

        short_ids = reality.get("shortIds")
        if not isinstance(short_ids, list) or not short_ids:
            raise XuiError("shortIds is missing")
        enabled = [str(item).strip() for item in short_ids if str(item).strip()]
        if not enabled:
            raise XuiError("shortIds is invalid")
        short_id = str(self.short_id or "").strip() or enabled[0]
        if not short_id or short_id not in enabled:
            raise XuiError("shortId is not in realitySettings.shortIds")

        sni = self.sni or None
        if sni is None:
            server_names = reality.get("serverNames")
            if isinstance(server_names, list) and server_names:
                sni = server_names[0]
        if not sni:
            raise XuiError("sni is missing")
        sni = str(sni)
        if not sni.strip():
            raise XuiError("sni is invalid")

        public_key = self.public_key or None
        if public_key is None:
            public_key = reality_settings.get("publicKey")
        if not public_key:
            raise XuiError("publicKey is missing")
        public_key = str(public_key)
        if not public_key.strip():
            raise XuiError("publicKey is invalid")

        fingerprint = self.fingerprint or None
        if fingerprint is None:
            fingerprint = reality_settings.get("fingerprint") or "chrome"
        fingerprint = str(fingerprint)

        flow = self.flow or None
        if flow is None:
            flow = "xtls-rprx-vision"
        flow = str(flow)

        spider_x = self.spider_x or None
        if spider_x is None:
            spider_x = reality_settings.get("spiderX") or "/"
        spider_x = str(spider_x)

        return {
            "server": server_host,
            "port": server_port,
            "security": "reality",
            "publicKey": public_key,
            "shortId": short_id,
            "sni": sni,
            "fingerprint": fingerprint,
            "flow": flow,
            "spiderX": spider_x,
        }

    @_serialized_xui_write
    def add_client(self, email: str) -> str:
        """Add a new client and restart x-ui.

        New 3x-ui: insert into ``clients`` + ``client_inbounds``.  Legacy
        3x-ui: append to ``inbounds.settings.clients``.  When the new tables
        are in use, a stale legacy JSON row with the same email is removed so
        the client only exists once.
        """
        _require_legacy_myproxy_email(email)

        if self._has_new_client_tables():
            with closing(sqlite3.connect(self.db_path)) as conn:
                if self._find_new_client(conn, email) is not None:
                    raise XuiError("client already exists")

            client_uuid = self._insert_new_client(email)[1]

            # New-model DBs may still contain stale legacy rows.  Clean the
            # same-email legacy row (never touches user1 or other emails).
            try:
                self._remove_legacy_inbound_client(email)
            except Exception:
                pass

            self._restart_xui()
            return client_uuid

        # Legacy 3x-ui fallback -------------------------------------------------
        row = self._fetch_inbound()
        if row is None:
            raise XuiError("inbound not found")
        expected_settings = row["settings"]
        settings = self._parse_json_object(row["settings"], "settings")
        clients = settings.get("clients")
        if clients is None:
            clients = []
            settings["clients"] = clients
        if not isinstance(clients, list):
            raise XuiError("invalid clients in settings")

        if any(c.get("email") == email for c in clients):
            raise XuiError("client already exists")

        client_id = str(uuid.uuid4())
        clients.append(
            {
                "id": client_id,
                "flow": "xtls-rprx-vision",
                "email": email,
                "limitIp": 0,
                "totalGB": 0,
                "expiryTime": 0,
                "enable": True,
                "tgId": 0,
                "subId": secrets.token_hex(8),
                "reset": 0,
            }
        )

        self._save_settings(settings, expected_settings)
        self._restart_xui()
        return client_id

    @_serialized_xui_write
    def update_client(
        self,
        email: str,
        *,
        enable: bool | None = None,
        limit_ip: int | None = None,
        total_gb: int | None = None,
        expiry_time: int | None = None,
    ) -> None:
        """Update only the supplied non-``None`` client fields."""
        _require_legacy_myproxy_email(email)

        if self._has_new_client_tables():
            with closing(sqlite3.connect(self.db_path)) as conn:
                conn.execute("BEGIN IMMEDIATE")
                self._require_inbound(conn)
                client = self._find_new_client(conn, email, inbound_id=self.inbound_id)
                if client is None:
                    raise XuiError("client not found", code="client_not_found")
                self._update_new_client(
                    conn,
                    int(client["id"]),
                    enable=enable,
                    limit_ip=limit_ip,
                    total_gb=total_gb,
                    expiry_time=expiry_time,
                )
                conn.commit()

            try:
                self._remove_legacy_inbound_client(email)
            except Exception:
                pass

            self._restart_xui()
            return

        # Legacy fallback -------------------------------------------------------
        row = self._fetch_inbound()
        if row is None:
            raise XuiError("inbound not found")
        expected_settings = row["settings"]
        settings = self._parse_json_object(row["settings"], "settings")
        clients = settings.get("clients")
        if clients is None:
            raise XuiError("client not found", code="client_not_found")
        if not isinstance(clients, list):
            raise XuiError("invalid clients in settings")

        for client in clients:
            if client.get("email") == email:
                break
        else:
            raise XuiError("client not found", code="client_not_found")

        if enable is not None:
            # A JSON boolean, as add_client writes it: 3x-ui models the field
            # as a Go bool, and when it builds the Xray config it skips a
            # disabled client only if the value asserts to bool.  An integer
            # 0 fails that assertion, so the client stayed live -- revocation
            # silently did nothing on legacy JSON deployments.
            client["enable"] = bool(enable)
        if limit_ip is not None:
            client["limitIp"] = int(limit_ip)
        if total_gb is not None:
            client["totalGB"] = int(total_gb)
        if expiry_time is not None:
            client["expiryTime"] = int(expiry_time)

        self._save_settings(settings, expected_settings)
        self._restart_xui()

    @_serialized_xui_write
    def remove_client(self, email: str) -> None:
        """Delete a client by email and restart x-ui."""
        _require_legacy_myproxy_email(email)

        if self._has_new_client_tables():
            with closing(sqlite3.connect(self.db_path)) as conn:
                conn.execute("BEGIN IMMEDIATE")
                self._require_inbound(conn)
                client = self._find_new_client(conn, email, inbound_id=self.inbound_id)
                if client is None:
                    raise XuiError("client not found", code="client_not_found")
                self._delete_new_client(conn, int(client["id"]))
                conn.commit()

            try:
                self._remove_legacy_inbound_client(email)
            except Exception:
                pass

            self._restart_xui()
            return

        # Legacy fallback -
        row = self._fetch_inbound()
        if row is None:
            raise XuiError("inbound not found")
        expected_settings = row["settings"]
        settings = self._parse_json_object(row["settings"], "settings")
        clients = settings.get("clients")
        if clients is None:
            raise XuiError("client not found", code="client_not_found")
        if not isinstance(clients, list):
            raise XuiError("invalid clients in settings")

        remaining = [c for c in clients if c.get("email") != email]
        if len(remaining) == len(clients):
            raise XuiError("client not found", code="client_not_found")

        settings["clients"] = remaining
        self._save_settings(settings, expected_settings)
        self._restart_xui()

    def list_clients(self) -> list[dict]:
        """Return clients linked to this inbound, joined with traffic data."""
        if self._has_new_client_tables():
            return self._read_new_clients()

        # Legacy fallback -------------------------------------------------------
        row = self._fetch_inbound()
        if row is None:
            raise XuiError("inbound not found")
        settings = self._parse_json_object(row["settings"], "settings")
        clients = settings.get("clients")
        if clients is None:
            clients = []
        if not isinstance(clients, list):
            raise XuiError("invalid clients in settings")

        traffic = self._traffic_map()
        result: list[dict] = []
        for client in clients:
            email = client.get("email", "")
            if not _is_legacy_myproxy_email(email):
                continue
            t = traffic.get(email, {"up": 0, "down": 0, "total": 0})
            result.append(
                {
                    "email": email,
                    "id": client.get("id", ""),
                    "enable": bool(client.get("enable", True)),
                    "up": int(t["up"]),
                    "down": int(t["down"]),
                    "total": int(t["total"]),
                }
            )
        return result
