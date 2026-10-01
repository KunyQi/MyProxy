"""HTTP server and routing for MyProxy Server V1.

The handler only parses/validates/serialises; all business logic lives in
:class:`myproxy_server.app.MyProxyService`.
"""

from __future__ import annotations

import ipaddress
import json
import re
import secrets
import ssl
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import __version__, auth, release
from .admin_ui import ASSETS as ADMIN_UI_ASSETS
from .app import ServiceError


_REQUEST_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")


def _emit_structured(event: str, **fields: object) -> None:
    """Write one JSON log record without secrets or multiline diagnostics."""
    record = {
        "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "event": event,
    }
    record.update(fields)
    try:
        line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
    except (TypeError, ValueError):
        line = json.dumps(
            {"ts": record["ts"], "event": "log_serialization_error"},
            separators=(",", ":"),
        )
    print(line, file=sys.stderr, flush=True)

ERROR_MESSAGES = {
    "PairingInvalid": "配对码无效，请检查后重试",
    "PairingExpired": "配对码已过期，请获取新的配对码",
    "TokenInvalid": "设备已失效，请重新绑定",
    "DeviceNotFound": "设备已失效，请重新绑定",
    "AdminUnauthorized": "管理员校验失败",
    "RateLimited": "请求过于频繁，请稍后重试",
    "BadRequest": "请求参数错误",
    "NotFound": "资源不存在",
    "Conflict": "资源冲突",
    # Deliberately says nothing about which check failed: a precise reply would
    # let anyone holding the Admin token map the manifest validator one field
    # at a time.  Callers must not match on this text.
    "ManifestRejected": "更新清单校验失败",
    "ServerError": "服务器内部错误",
}

MAX_JSON_BODY_BYTES = 64 * 1024
REQUEST_TIMEOUT_SECONDS = 30
TLS_HANDSHAKE_TIMEOUT_SECONDS = 10
MAX_CONCURRENT_REQUESTS = 64
MAX_TRACKED_CLAIM_IPS = 10_000


def _complete_tls_handshake(request) -> bool:
    """Finish a deferred TLS handshake, returning False if the peer did not.

    Wrapping the *listening* socket with ``do_handshake_on_connect`` (the
    default) makes ``SSLSocket.accept()`` complete the handshake inline, ahead
    of the per-socket timeout, the concurrency semaphore and
    ``request_queue_size``.  One peer that connects and never sends a
    ClientHello would then stop the listener from accepting at all.  Running it
    here puts the handshake on a bounded worker thread instead.
    """
    handshake = getattr(request, "do_handshake", None)
    if handshake is None:
        return True
    try:
        request.settimeout(TLS_HANDSHAKE_TIMEOUT_SECONDS)
        handshake()
        request.settimeout(REQUEST_TIMEOUT_SECONDS)
        return True
    except OSError:
        return False


class _ClaimIpTracker:
    """Thread-safe per-IP claim rate limiter."""

    def __init__(self, window_seconds: int, max_per_ip: int) -> None:
        self.window_seconds = window_seconds
        self.max_per_ip = max_per_ip
        self._lock = threading.Lock()
        self._attempts: dict[str, list[float]] = {}

    def allow(self, ip: str) -> bool:
        if self.max_per_ip <= 0:
            return True
        now = time.time()
        with self._lock:
            if ip not in self._attempts and len(self._attempts) >= MAX_TRACKED_CLAIM_IPS:
                cutoff = now - self.window_seconds
                stale = [
                    key
                    for key, values in self._attempts.items()
                    if not values or values[-1] <= cutoff
                ]
                for key in stale:
                    self._attempts.pop(key, None)
                if len(self._attempts) >= MAX_TRACKED_CLAIM_IPS:
                    # Keep the limiter bounded even when every tracked IP is
                    # still active; evicting one old bucket is safer than
                    # allowing attacker-controlled memory growth.
                    oldest = min(
                        self._attempts,
                        key=lambda key: self._attempts[key][-1]
                        if self._attempts[key]
                        else 0,
                    )
                    self._attempts.pop(oldest, None)
            attempts = self._attempts.setdefault(ip, [])
            while attempts and now - attempts[0] > self.window_seconds:
                attempts.pop(0)
            if len(attempts) >= self.max_per_ip:
                return False
            attempts.append(now)
            return True


class _Handler(BaseHTTPRequestHandler):
    """Request handler created by :func:`make_server`.

    ``service``, ``settings`` and ``claim_tracker`` are class attributes set
    by ``make_server``.
    """

    server_version = f"myproxy-api/{__version__}"
    sys_version = ""
    protocol_version = "HTTP/1.1"

    service = None
    settings = None
    claim_tracker = None

    # ------------------------------------------------------------------
    # BaseHTTPRequestHandler overrides
    # ------------------------------------------------------------------
    def do_GET(self) -> None:
        self._dispatch("GET")

    def do_POST(self) -> None:
        self._dispatch("POST")

    def do_DELETE(self) -> None:
        self._dispatch("DELETE")

    def do_PUT(self) -> None:
        self.close_connection = True
        self._send_error(404, "NotFound")

    def do_PATCH(self) -> None:
        self.close_connection = True
        self._send_error(404, "NotFound")

    def log_message(self, format: str, *args) -> None:
        """Suppress default BaseHTTPRequestHandler logging."""

    # ------------------------------------------------------------------
    # dispatch / routing
    # ------------------------------------------------------------------
    def _dispatch(self, method: str) -> None:
        start = time.perf_counter()
        status_code = 500
        self._body_consumed = False
        self._request_id = self._request_id_from_header()
        if self._declared_body_length() is None:
            # An undrainable body cannot be followed by another request on
            # this connection.  Decide before routing so every response --
            # including the error ones -- advertises the close.
            self.close_connection = True
        try:
            status_code = self._route(method)
        except ServiceError as exc:
            status_code = exc.status
            self._send_error(exc.status, exc.code)
        except Exception as exc:
            # Never put exception text, paths or adapter output into the
            # service log.  It may contain credentials or private config.
            _emit_structured(
                "server_error",
                request_id=self._request_id,
                error_type=type(exc).__name__,
            )
            self._send_error(500, "ServerError")
        finally:
            # Unknown routes, bodyless methods and *every* error path must
            # still consume a bounded request body before HTTP/1.1 connection
            # reuse.  Leftover bytes would otherwise be parsed as the next
            # request on this connection, desynchronising responses and
            # bypassing the nginx exact-location allowlist.
            self._discard_unparsed_body()
            duration_ms = int((time.perf_counter() - start) * 1000)
            safe_path = urllib.parse.urlsplit(self.path).path
            _emit_structured(
                "request",
                request_id=self._request_id,
                method=method,
                path=safe_path,
                status=status_code,
                duration_ms=duration_ms,
                client_ip=self._claim_source_ip(),
            )

    def _route(self, method: str) -> int:
        # BaseHTTPRequestHandler folds leading // to / in self.path.  Reject
        # that noncanonical request target from the original request line,
        # before the parsed path can turn it into a public allowlisted route.
        raw_target = self.requestline.split()[1]
        if raw_target.startswith("//"):
            return self._send_error(404, "NotFound")
        parsed = urllib.parse.urlsplit(self.path)
        if not _is_canonical_path(parsed.path):
            return self._send_error(404, "NotFound")
        segments = [
            urllib.parse.unquote(seg) for seg in parsed.path.split("/") if seg
        ]
        query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)

        # Tunnel egress probes use one exact, unauthenticated path. Keep the
        # request-target check here so even an empty query or an alternate
        # spelling is rejected before any route can treat it as equivalent.
        if parsed.path == "/connectivity-check":
            if method == "GET" and raw_target == "/connectivity-check":
                return self._send_empty_204()
            return self._send_error(404, "NotFound")

        # Private administration UI ------------------------------------
        # These assets contain no credentials or server data.  The page
        # obtains all state from the existing Bearer-protected Admin API and
        # is reachable only on the same loopback listener as that API.
        if method == "GET" and segments == ["admin"]:
            content_type, data = ADMIN_UI_ASSETS["html"]
            return self._send_static(200, content_type, data)

        if method == "GET" and segments == ["admin", "app.css"]:
            content_type, data = ADMIN_UI_ASSETS["css"]
            return self._send_static(200, content_type, data)

        if method == "GET" and segments == ["admin", "app.js"]:
            content_type, data = ADMIN_UI_ASSETS["js"]
            return self._send_static(200, content_type, data)

        # Public routes -------------------------------------------------
        # Private ticket exchange; public nginx does not expose /admin/*.
        if method == "POST" and segments == ["admin", "session"]:
            body = self._read_json_body()
            ticket = body.get("ticket")
            if not isinstance(ticket, str) or not re.fullmatch(r"[A-Za-z0-9_-]{43}", ticket):
                return self._send_error(401, "AdminUnauthorized")
            return self._send_json(200, self.service.redeem_ui_ticket(ticket))

        if method == "GET" and segments == ["healthz"]:
            return self._send_json(
                200,
                {"ok": True, "service": "myproxy-api", "version": __version__},
            )

        if method == "GET" and segments == ["readyz"]:
            checks = self.service.readiness()
            status = 200 if checks["ok"] else 503
            _emit_structured(
                "readiness",
                request_id=self._request_id,
                status=status,
                db=checks["db"],
                xui=checks["xui"],
            )
            return self._send_json(
                status,
                {
                    "ok": checks["ok"],
                    "service": "myproxy-api",
                    "version": __version__,
                    "checks": {"db": checks["db"], "xui": checks["xui"]},
                },
            )

        if method == "GET" and segments == ["client", "windows", "latest.json"]:
            return self._send_json(200, self.service.latest_info())

        if method == "GET" and segments == ["client", "android", "latest.json"]:
            return self._send_json(200, self.service.latest_info("android"))

        if method == "GET" and segments == ["client", "linux", "latest.json"]:
            return self._send_json(200, self.service.latest_info("linux"))

        if method == "POST" and segments == ["api", "device", "claim"]:
            if not self.claim_tracker.allow(self._claim_source_ip()):
                _emit_structured(
                    "security",
                    request_id=self._request_id,
                    action="claim_rate_limited",
                    client_ip=self._claim_source_ip(),
                )
                return self._send_error(429, "RateLimited")
            body = self._read_json_body()
            result = self.service.claim(
                body.get("pairingCode"),
                body.get("deviceName"),
                body.get("platform"),
                body.get("clientVersion"),
                body.get("clientInstanceId"),
            )
            return self._send_json(200, result)

        if method == "GET" and segments == ["api", "device", "config"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            result = self.service.get_device_config(token)
            return self._send_json(200, result)

        if method == "POST" and segments == ["api", "device", "heartbeat"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            self._read_json_body()  # body is optional and ignored
            result = self.service.heartbeat(
                token, client_ip=self._claim_source_ip()
            )
            return self._send_json(200, result)

        if method == "GET" and segments == ["api", "device", "update"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            result = self.service.device_update(token)
            return self._send_json(200, result)

        if method == "POST" and segments == ["api", "device", "update", "report"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            body = self._read_json_body()
            result = self.service.device_update_report(
                token,
                body.get("releaseId"),
                body.get("status"),
                body.get("detail"),
            )
            return self._send_json(200, result)

        if method == "POST" and segments == ["api", "device", "usage"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            body = self._read_json_body()
            result = self.service.device_report_usage(
                token, body, client_ip=self._claim_source_ip()
            )
            return self._send_json(200, result)

        # Admin routes --------------------------------------------------
        if len(segments) >= 2 and segments[0] == "api" and segments[1] == "admin":
            if not self._admin_authorized():
                _emit_structured(
                    "security",
                    request_id=self._request_id,
                    action="admin_unauthorized",
                    client_ip=self._claim_source_ip(),
                    path=urllib.parse.urlsplit(self.path).path,
                )
                return self._send_error(401, "AdminUnauthorized")
            return self._route_admin(method, segments[2:], query)

        return self._send_error(404, "NotFound")

    def _route_admin(self, method: str, rest: list[str], query: dict) -> int:
        if method == "POST" and rest == ["ui-ticket"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            if not auth.timing_safe_equal(self.settings.admin_token, token):
                return self._send_error(401, "AdminUnauthorized")
            return self._send_json(200, self.service.issue_ui_ticket())
        if method == "DELETE" and rest == ["ui-session"]:
            token = auth.extract_bearer(self.headers.get("Authorization")) or ""
            self.service.revoke_ui_session(token)
            return self._send_json(200, {"ok": True})
        # POST /api/admin/user
        if method == "POST" and rest == ["user"]:
            body = self._read_json_body()
            result = self.service.admin_create_user(
                body.get("username"), body.get("displayName")
            )
            return self._send_json(200, result)

        # GET /api/admin/user?username=
        if method == "GET" and rest == ["user"]:
            username = _first_query(query, "username")
            return self._send_json(200, self.service.admin_list_users(username))

        # DELETE /api/admin/user/<id>
        if method == "DELETE" and len(rest) == 2 and rest[0] == "user":
            return self._send_json(200, self.service.admin_delete_user(rest[1]))

        # POST /api/admin/binding
        if method == "POST" and rest == ["binding"]:
            body = self._read_json_body()
            result = self.service.admin_create_binding(
                body.get("userId"),
                body.get("deviceTemplate"),
                body.get("expiresInSeconds"),
            )
            return self._send_json(200, result)

        # GET /api/admin/binding?userId=
        if method == "GET" and rest == ["binding"]:
            user_id = _first_query(query, "userId")
            return self._send_json(200, self.service.admin_list_bindings(user_id))

        # DELETE /api/admin/binding/<id>
        if method == "DELETE" and len(rest) == 2 and rest[0] == "binding":
            return self._send_json(200, self.service.admin_delete_binding(rest[1]))

        # POST /api/admin/binding/<id>/extend
        if (
            method == "POST"
            and len(rest) == 3
            and rest[0] == "binding"
            and rest[2] == "extend"
        ):
            body = self._read_json_body()
            result = self.service.admin_extend_binding(
                rest[1], body.get("extendsSeconds")
            )
            return self._send_json(200, result)

        # GET /api/admin/device?userId=
        if method == "GET" and rest == ["device"]:
            user_id = _first_query(query, "userId")
            return self._send_json(200, self.service.admin_list_devices(user_id))

        # DELETE /api/admin/device/<id>
        if method == "DELETE" and len(rest) == 2 and rest[0] == "device":
            return self._send_json(200, self.service.admin_delete_device(rest[1]))

        # GET /api/admin/config
        if method == "GET" and rest == ["config"]:
            return self._send_json(200, self.service.admin_get_config())

        # POST /api/admin/config/version
        if method == "POST" and rest == ["config", "version"]:
            body = self._read_json_body()
            result = self.service.admin_bump_config_version(body.get("note"))
            return self._send_json(200, result)

        # GET /api/admin/latest
        if method == "GET" and rest == ["latest"]:
            return self._send_json(200, self.service.admin_get_latest())

        # POST /api/admin/latest
        if method == "POST" and rest == ["latest"]:
            body = self._read_json_body()
            result = self.service.admin_set_latest(
                body.get("version"),
                body.get("downloadUrl"),
                body.get("sha256"),
                body.get("mandatory"),
            )
            return self._send_json(200, result)

        # GET/POST /api/admin/latest/android
        if method == "GET" and rest == ["latest", "android"]:
            return self._send_json(200, self.service.admin_get_latest("android"))

        if method == "POST" and rest == ["latest", "android"]:
            body = self._read_json_body()
            result = self.service.admin_set_latest(
                body.get("version"),
                body.get("downloadUrl"),
                body.get("sha256"),
                body.get("mandatory"),
                platform="android",
            )
            return self._send_json(200, result)

        # GET/POST /api/admin/latest/linux
        if method == "GET" and rest == ["latest", "linux"]:
            return self._send_json(200, self.service.admin_get_latest("linux"))

        if method == "POST" and rest == ["latest", "linux"]:
            body = self._read_json_body()
            result = self.service.admin_set_latest(
                body.get("version"),
                body.get("downloadUrl"),
                body.get("sha256"),
                body.get("mandatory"),
                platform="linux",
            )
            return self._send_json(200, result)

        # GET /api/admin/release?platform=&status=
        if method == "GET" and rest == ["release"]:
            return self._send_json(
                200,
                self.service.admin_list_releases(
                    _first_query(query, "platform"), _first_query(query, "status")
                ),
            )

        # POST /api/admin/release
        if method == "POST" and rest == ["release"]:
            body = self._read_json_body()
            result = self.service.admin_register_release(
                body.get("manifest"),
                body.get("signature"),
                body.get("signingKeyId"),
                body.get("note"),
            )
            return self._send_json(200, result)

        # POST /api/admin/release/<id>/publish | /revoke
        if (
            method == "POST"
            and len(rest) == 3
            and rest[0] == "release"
            and rest[2] in ("publish", "revoke")
        ):
            status = "published" if rest[2] == "publish" else "revoked"
            result = self.service.admin_set_release_status(rest[1], status)
            return self._send_json(200, result)

        # GET /api/admin/assignment?scope=&targetId=&platform=
        if method == "GET" and rest == ["assignment"]:
            return self._send_json(
                200,
                self.service.admin_list_assignments(
                    _first_query(query, "scope"),
                    _first_query(query, "targetId"),
                    _first_query(query, "platform"),
                ),
            )

        # POST /api/admin/assignment
        if method == "POST" and rest == ["assignment"]:
            body = self._read_json_body()
            result = self.service.admin_set_assignment(
                body.get("scope"),
                body.get("targetId"),
                body.get("platform"),
                body.get("releaseId"),
                body.get("featureFlags"),
                body.get("note"),
            )
            return self._send_json(200, result)

        # DELETE /api/admin/assignment/<id>
        if method == "DELETE" and len(rest) == 2 and rest[0] == "assignment":
            return self._send_json(
                200, self.service.admin_delete_assignment(rest[1])
            )

        # GET /api/admin/device/<id>/release
        if (
            method == "GET"
            and len(rest) == 3
            and rest[0] == "device"
            and rest[2] == "release"
        ):
            return self._send_json(
                200, self.service.admin_device_effective_release(rest[1])
            )

        # GET /api/admin/usage?deviceId=&userId=&start=&end=&granularity=
        if method == "GET" and rest == ["usage"]:
            return self._send_json(
                200,
                self.service.admin_usage(
                    _first_query(query, "deviceId"),
                    _first_query(query, "userId"),
                    _first_query(query, "start"),
                    _first_query(query, "end"),
                    _first_query(query, "granularity"),
                ),
            )

        # POST /api/admin/usage/refresh
        if method == "POST" and rest == ["usage", "refresh"]:
            return self._send_json(200, self.service.admin_refresh_usage())

        # GET /api/admin/activity?userId=
        if method == "GET" and rest == ["activity"]:
            return self._send_json(
                200, self.service.admin_list_activity(_first_query(query, "userId"))
            )

        # GET /api/admin/device/<id>/activity
        if (
            method == "GET"
            and len(rest) == 3
            and rest[0] == "device"
            and rest[2] == "activity"
        ):
            return self._send_json(
                200, self.service.admin_device_activity(rest[1])
            )

        # POST /api/admin/device/<id>/geo
        if (
            method == "POST"
            and len(rest) == 3
            and rest[0] == "device"
            and rest[2] == "geo"
        ):
            body = self._read_json_body()
            return self._send_json(
                200, self.service.admin_set_device_geo(rest[1], body)
            )

        # GET /api/admin/release-audit?targetId=&limit=
        if method == "GET" and rest == ["release-audit"]:
            raw_limit = _first_query(query, "limit")
            try:
                limit = int(raw_limit) if raw_limit else 100
            except ValueError:
                limit = 100
            return self._send_json(
                200,
                self.service.admin_release_audit(_first_query(query, "targetId"), limit),
            )

        return self._send_error(404, "NotFound")

    # ------------------------------------------------------------------
    # auth / body / response helpers
    # ------------------------------------------------------------------
    def _request_id_from_header(self) -> str:
        candidate = (self.headers.get("X-Request-ID") or "").strip()
        if _REQUEST_ID_RE.fullmatch(candidate):
            return candidate
        return "req_" + secrets.token_hex(12)

    def _claim_source_ip(self) -> str:
        """Honor X-Forwarded-For only from an explicitly trusted proxy."""
        try:
            peer = ipaddress.ip_address(self.client_address[0]).compressed
        except ValueError:
            return self.client_address[0]
        if peer not in self.settings.trusted_proxy_ips:
            return peer

        forwarded = (self.headers.get("X-Forwarded-For") or "").split(",", 1)[0]
        try:
            return ipaddress.ip_address(forwarded.strip()).compressed
        except ValueError:
            # A malformed proxy header is one shared bucket, never an avenue
            # to manufacture unbounded limiter keys.
            return peer

    def _admin_authorized(self) -> bool:
        if not self.settings.admin_token:
            return False
        token = auth.extract_bearer(self.headers.get("Authorization")) or ""
        return auth.timing_safe_equal(self.settings.admin_token, token) or self.service.ui_session_authorized(token)

    def _declared_body_length(self) -> int | None:
        """Return the drainable body length, or ``None`` when unbounded.

        ``None`` means the framing cannot be honoured -- chunked encoding,
        duplicate or malformed ``Content-Length``, or a body above
        ``MAX_JSON_BODY_BYTES`` -- so this connection cannot safely carry a
        further request.  Header inspection only; nothing is consumed.
        """
        transfer_encoding = self.headers.get("Transfer-Encoding", "") or ""
        if transfer_encoding and transfer_encoding.lower() != "identity":
            return None

        length_values = self.headers.get_all("Content-Length") or []
        if len(length_values) > 1:
            return None
        try:
            length = int(length_values[0] if length_values else "0")
        except (TypeError, ValueError):
            return None
        if length < 0 or length > MAX_JSON_BODY_BYTES:
            return None
        return length

    def _read_body_bytes(self) -> bytes:
        length = self._declared_body_length()
        if length is None:
            self.close_connection = True
            raise ServiceError("BadRequest", 400)
        if length <= 0:
            self._body_consumed = True
            return b""
        raw = self.rfile.read(length)
        self._body_consumed = True
        if len(raw) != length:
            self.close_connection = True
            raise ServiceError("BadRequest", 400)
        return raw

    def _discard_unparsed_body(self) -> None:
        """Drain a still-unread request body, or close the connection.

        Called from ``_dispatch``'s ``finally`` so error paths cannot leave
        bytes in the socket.  When the body cannot be drained the connection
        is unusable for a further request and must be closed.
        """
        if self._body_consumed or self.close_connection:
            return
        try:
            self._read_body_bytes()
        except Exception:
            # The route response has already been emitted.  Close rather than
            # attempting to write a second response on the same request.
            self.close_connection = True

    def _read_json_body(self) -> dict:
        raw = self._read_body_bytes()
        if not raw:
            return {}
        content_type = self.headers.get("Content-Type", "") or ""
        if "application/json" not in content_type.lower():
            # The body has been consumed before treating it as an empty body,
            # so HTTP/1.1 connection reuse cannot be desynchronised.
            return {}
        try:
            data = json.loads(raw.decode("utf-8"))
        except Exception:
            raise ServiceError("BadRequest", 400)
        if not isinstance(data, dict):
            raise ServiceError("BadRequest", 400)
        return data

    def _send_close_header_if_closing(self) -> None:
        """Advertise a decided close, as HTTP/1.1 keep-alive requires.

        ``BaseHTTPRequestHandler`` closes the socket for ``close_connection``
        but never emits the header, so a pooling upstream such as nginx would
        otherwise keep handing the dead connection to the next request.
        """
        if self.close_connection:
            self.send_header("Connection", "close")

    def _send_json(self, status: int, obj) -> int:
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self._send_close_header_if_closing()
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Request-ID", getattr(self, "_request_id", "req_unknown"))
        self.end_headers()
        self.wfile.write(data)
        return status

    def _send_empty_204(self) -> int:
        """Send the exact bodyless response expected by tunnel probes."""
        self.send_response(204)
        self._send_close_header_if_closing()
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Request-ID", getattr(self, "_request_id", "req_unknown"))
        self.end_headers()
        return 204

    def _send_static(self, status: int, content_type: str, data: bytes) -> int:
        self.send_response(status)
        self._send_close_header_if_closing()
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Request-ID", getattr(self, "_request_id", "req_unknown"))
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header(
            "Permissions-Policy",
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
        )
        self.send_header(
            "Content-Security-Policy",
            "default-src 'none'; script-src 'self'; style-src 'self'; "
            "connect-src 'self'; img-src 'none'; base-uri 'none'; "
            "form-action 'none'; frame-ancestors 'none'",
        )
        self.end_headers()
        self.wfile.write(data)
        return status

    def _send_error(self, status: int, code: str) -> int:
        body = {
            "error": {
                "code": code,
                "message": ERROR_MESSAGES.get(code, "服务器内部错误"),
            }
        }
        return self._send_json(status, body)


def _first_query(query: dict, key: str) -> str | None:
    values = query.get(key) or []
    if not values:
        return None
    value = values[0]
    if value == "":
        return None
    return value


def _validate_tls_settings(settings) -> None:
    has_cert = bool(settings.tls_cert)
    has_key = bool(settings.tls_key)
    if has_cert != has_key:
        raise RuntimeError("TLS certificate and key must be configured together")


def _validate_runtime_security(settings) -> None:
    """Reject production-shaped startup without authentication or TLS."""
    _validate_tls_settings(settings)
    if not settings.admin_token:
        raise RuntimeError("MYPROXY_ADMIN_TOKEN is required")
    if settings.tls_cert and (
        len(settings.admin_token) != 64
        or any(char not in "0123456789abcdefABCDEF" for char in settings.admin_token)
    ):
        raise RuntimeError(
            "MYPROXY_ADMIN_TOKEN must be 64 hexadecimal characters in production"
        )
    device_secret = settings.device_token_secret
    if len(device_secret) != 64 or any(
        char not in "0123456789abcdefABCDEF" for char in device_secret
    ):
        raise RuntimeError("MYPROXY_DEVICE_TOKEN_SECRET must be 64 hexadecimal characters")
    if not settings.tls_cert:
        host = str(settings.listen_host).strip().lower()
        if host not in {"127.0.0.1", "::1", "localhost"}:
            raise RuntimeError("TLS is required for non-loopback listeners")
    # Parsed at start-up, not on first use: a typo in the signing key list
    # would otherwise surface only when someone tries to publish a release,
    # which is the worst possible moment to discover it.  An empty list is
    # allowed -- it simply means this deployment cannot register releases.
    try:
        release.parse_public_keys(settings.release_signing_keys)
    except ValueError as exc:
        raise RuntimeError(f"MYPROXY_RELEASE_SIGNING_KEYS is invalid: {exc}") from exc


def _is_canonical_path(path: str) -> bool:
    """Whether ``path`` is already in the form nginx matched it in.

    nginx normalises a path (resolving ``.``/``..``, merging slashes, decoding)
    before matching its ``location =`` whitelist, but ``proxy_pass`` without a
    URI forwards the client's *original* request line.  So
    ``/api/admin/../device/claim`` passes the public gateway as
    ``/api/device/claim`` and would arrive here as an Admin route.  Anything
    that is not already canonical is refused, leaving the whitelist as the one
    place that decides what the public port can reach.  One trailing slash is
    still accepted, as it always was.
    """
    segments = path.split("/")[1:]
    if segments and segments[-1] == "":
        segments = segments[:-1]
    for raw in segments:
        if raw == "" or "\\" in raw:
            return False
        lowered = raw.lower()
        if "%2f" in lowered or "%5c" in lowered:
            return False
        if urllib.parse.unquote(raw) in (".", ".."):
            return False
    return True


def make_server(
    service, settings, *, max_concurrent_requests: int = MAX_CONCURRENT_REQUESTS
) -> ThreadingHTTPServer:
    """Create a threaded HTTP(S) server bound to ``settings.listen_*``."""

    _validate_tls_settings(settings)
    if (
        isinstance(max_concurrent_requests, bool)
        or not isinstance(max_concurrent_requests, int)
        or max_concurrent_requests < 1
    ):
        raise ValueError("HTTP concurrency limit must be a positive integer")

    class BoundedThreadingHTTPServer(ThreadingHTTPServer):
        request_queue_size = 128

        def __init__(self, *args, **kwargs):
            self.max_concurrent_requests = max_concurrent_requests
            self._request_slots = threading.BoundedSemaphore(max_concurrent_requests)
            super().__init__(*args, **kwargs)

        def process_request(self, request, client_address) -> None:
            # Apply the bound before ThreadingMixIn creates a worker thread.
            if not self._request_slots.acquire(blocking=False):
                self.shutdown_request(request)
                return
            try:
                super().process_request(request, client_address)
            except BaseException:
                self._request_slots.release()
                raise

        def process_request_thread(self, request, client_address) -> None:
            try:
                if not _complete_tls_handshake(request):
                    self.shutdown_request(request)
                    return
                super().process_request_thread(request, client_address)
            finally:
                self._request_slots.release()

        def get_request(self):
            request, client_address = super().get_request()
            request.settimeout(REQUEST_TIMEOUT_SECONDS)
            return request, client_address

    class Handler(_Handler):
        pass

    Handler.service = service
    Handler.settings = settings
    Handler.claim_tracker = _ClaimIpTracker(
        settings.claim_window_seconds, settings.claim_max_per_ip
    )

    httpd = BoundedThreadingHTTPServer(
        (settings.listen_host, settings.listen_port), Handler
    )
    httpd.daemon_threads = True

    if settings.tls_cert and settings.tls_key:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        if hasattr(ssl, "OP_NO_COMPRESSION"):
            context.options |= ssl.OP_NO_COMPRESSION
        context.load_cert_chain(settings.tls_cert, settings.tls_key)
        # The handshake is deferred to process_request_thread; see
        # _complete_tls_handshake for why it must not run inside accept().
        httpd.socket = context.wrap_socket(
            httpd.socket, server_side=True, do_handshake_on_connect=False
        )

    return httpd


def _build_xui_adapter(settings):
    """Build only the least-privilege x-ui boundary used by production API."""
    from .xui import UnixSocketXuiAdapter

    if not settings.xui_helper_socket:
        raise RuntimeError(
            "MYPROXY_XUI_HELPER_SOCKET is required; direct x-ui access is disabled"
        )
    return UnixSocketXuiAdapter(settings.xui_helper_socket)


def run(settings, service=None) -> None:
    """Create a default service (if needed), initialise DB, and serve forever."""
    _validate_runtime_security(settings)
    if service is None:
        from .app import MyProxyService
        xui = _build_xui_adapter(settings)
        service = MyProxyService(settings, xui)

    service.initialize()
    httpd = make_server(service, settings)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
