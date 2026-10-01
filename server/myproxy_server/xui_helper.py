"""Root-owned Unix-socket boundary for the 3x-ui SQLite adapter.

This process is intentionally separate from the HTTP API.  It is the only
runtime component that opens ``/etc/x-ui/x-ui.db`` or restarts x-ui.  The
protocol is one bounded JSON request and one generic JSON response per
connection; no SQL, paths, credentials or subprocess output are returned.
A failure may additionally carry one allow-listed ``code`` from
``xui.XUI_ERROR_CODES`` so the API can branch on an outcome without parsing
error text, which is deliberately uninformative across this boundary.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import socketserver
import stat
import struct
import threading
from datetime import datetime, timezone
from typing import Any

try:  # Unix-only modules stay optional so local Windows imports remain safe.
    import grp
    import pwd
except ImportError:  # pragma: no cover - exercised by Windows interpreters
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]

from .config import Settings
from .xui import XUI_ERROR_CODES, XuiError, XuiSqliteAdapter, is_myproxy_email

MAX_REQUEST_BYTES = 8192
MAX_EMAIL_BYTES = 256
# A client may connect and send only a fragment of a JSON line.  Keep the
# accepted socket bounded while waiting for the newline so one slow peer
# cannot pin a helper thread and file descriptor indefinitely.
REQUEST_READ_TIMEOUT_SECONDS = 2.0
# ThreadingUnixStreamServer otherwise creates one thread for every accepted
# connection.  The semaphore below applies backpressure before a thread is
# created and closes excess connections fail-closed.
MAX_CONCURRENT_CONNECTIONS = 16
ALLOWED_OPS = frozenset(
    {"public_profile", "add_client", "update_client", "remove_client", "list_clients"}
)


def _emit(event: str, **fields: Any) -> None:
    payload = {
        "ts": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
        "event": event,
        **fields,
    }
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def _validate_email(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > MAX_EMAIL_BYTES:
        raise ValueError("invalid email")
    if not is_myproxy_email(value):
        raise ValueError("email is outside the MyProxy namespace")
    return value


def _safe_operation(value: Any) -> str:
    return value if isinstance(value, str) and value in ALLOWED_OPS else "unknown"


def _safe_error_code(exc: BaseException) -> str | None:
    """Return the allow-listed outcome code for *exc*, or ``None``."""
    code = getattr(exc, "code", None) if isinstance(exc, XuiError) else None
    return code if code in XUI_ERROR_CODES else None


def _validate_optional_int(value: Any, name: str) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > 2**63 - 1:
        raise ValueError(f"invalid {name}")
    return value


def _dispatch(adapter: XuiSqliteAdapter, request: dict[str, Any]) -> Any:
    op = request.get("op")
    if op not in ALLOWED_OPS:
        raise ValueError("unsupported operation")
    if op == "public_profile":
        return adapter.public_profile()
    if op == "list_clients":
        # The helper is the privileged boundary.  Filter again even though
        # the concrete SQLite adapter also applies this policy, so an
        # accidental alternate adapter cannot disclose panel-owned clients.
        clients = adapter.list_clients()
        if not isinstance(clients, list):
            raise ValueError("invalid clients")
        return [
            client
            for client in clients
            if isinstance(client, dict) and is_myproxy_email(client.get("email"))
        ]
    email = _validate_email(request.get("email"))
    if op == "add_client":
        return adapter.add_client(email)
    if op == "remove_client":
        return adapter.remove_client(email)
    enable = request.get("enable")
    if enable is not None and not isinstance(enable, bool):
        raise ValueError("invalid enable")
    return adapter.update_client(
        email,
        enable=enable,
        limit_ip=_validate_optional_int(request.get("limit_ip"), "limit_ip"),
        total_gb=_validate_optional_int(request.get("total_gb"), "total_gb"),
        expiry_time=_validate_optional_int(request.get("expiry_time"), "expiry_time"),
    )


class _Handler(socketserver.StreamRequestHandler):
    adapter: XuiSqliteAdapter
    operation_lock: threading.Lock

    def setup(self) -> None:
        super().setup()
        # Set the timeout after StreamRequestHandler creates rfile/wfile so
        # readline() on a half-sent request is bounded on all supported Unix
        # Python versions.
        self.request.settimeout(REQUEST_READ_TIMEOUT_SECONDS)

    def _authorized_peer(self) -> bool:
        """Require the API's uid in addition to the socket group/mode."""
        try:
            if pwd is None:
                return False
            expected_uid = pwd.getpwnam("myproxy").pw_uid
            credentials = self.request.getsockopt(
                socket.SOL_SOCKET,
                socket.SO_PEERCRED,
                struct.calcsize("3i"),
            )
        except (AttributeError, OSError, KeyError, struct.error):
            return False
        _pid, uid, _gid = struct.unpack("3i", credentials)
        return uid == expected_uid

    def handle(self) -> None:
        if not self._authorized_peer():
            _emit("xui_helper_request", ok=False, reason="peer_denied")
            self._reply(False)
            return
        try:
            raw = self.rfile.readline(MAX_REQUEST_BYTES + 1)
        except (OSError, TimeoutError):
            # A timeout or transport failure is deliberately indistinguishable
            # from malformed input and never reaches the adapter.
            _emit("xui_helper_request", ok=False, reason="read_timeout")
            return
        if not raw or len(raw) > MAX_REQUEST_BYTES or not raw.endswith(b"\n"):
            _emit("xui_helper_request", ok=False, reason="malformed")
            self._reply(False)
            return
        operation: Any = "unknown"
        try:
            request = json.loads(raw.decode("utf-8"))
            if not isinstance(request, dict):
                raise ValueError("invalid request")
            operation = request.get("op")
            with self.operation_lock:
                result = _dispatch(self.adapter, request)
            self._reply(True, result)
            _emit("xui_helper_request", ok=True, op=_safe_operation(operation))
        except Exception as exc:
            # Never expose x-ui/SQLite/subprocess details to the API worker.
            # Only the closed XUI_ERROR_CODES vocabulary crosses the boundary.
            code = _safe_error_code(exc)
            _emit(
                "xui_helper_request",
                ok=False,
                op=_safe_operation(operation),
                code=code,
            )
            self._reply(False, code=code)

    def _reply(self, ok: bool, result: Any = None, *, code: str | None = None) -> None:
        payload = {"ok": ok}
        if ok:
            payload["result"] = result
        elif code is not None:
            payload["code"] = code
        self.wfile.write((json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        self.wfile.flush()


if hasattr(socketserver, "ThreadingUnixStreamServer"):
    class _Server(socketserver.ThreadingUnixStreamServer):  # type: ignore[attr-defined]
        daemon_threads = True
        allow_reuse_address = True

        def __init__(
            self,
            server_address: str,
            RequestHandlerClass: type[socketserver.BaseRequestHandler],
            bind_and_activate: bool = True,
            *,
            max_concurrent: int = MAX_CONCURRENT_CONNECTIONS,
        ) -> None:
            if (
                isinstance(max_concurrent, bool)
                or not isinstance(max_concurrent, int)
                or max_concurrent < 1
            ):
                raise ValueError("helper concurrency limit must be positive")
            self.max_concurrent = int(max_concurrent)
            self._connection_slots = threading.BoundedSemaphore(self.max_concurrent)
            super().__init__(server_address, RequestHandlerClass, bind_and_activate)

        def process_request(self, request: socket.socket, client_address: Any) -> None:
            # Reject before ThreadingMixIn creates a thread.  Closing is
            # fail-closed and avoids a blocking write in the accept loop.
            if not self._connection_slots.acquire(blocking=False):
                _emit("xui_helper_request", ok=False, reason="concurrency_limit")
                request.close()
                return
            try:
                super().process_request(request, client_address)
            except BaseException:
                self._connection_slots.release()
                raise

        def process_request_thread(self, request: socket.socket, client_address: Any) -> None:
            try:
                super().process_request_thread(request, client_address)
            finally:
                self._connection_slots.release()
else:  # pragma: no cover - used only to keep non-Unix audit imports safe
    class _Server:
        def __init__(self, *_args: Any, **_kwargs: Any) -> None:
            raise RuntimeError("x-ui helper requires Unix-domain socket support")


def serve(socket_path: str, adapter: XuiSqliteAdapter, socket_group: str = "myproxy") -> None:
    if grp is None:
        raise RuntimeError("x-ui helper requires a Unix account/group database")
    if not os.path.isabs(socket_path):
        raise RuntimeError("helper socket must be an absolute path")
    parent = os.path.dirname(socket_path)
    os.makedirs(parent, mode=0o750, exist_ok=True)
    os.chmod(parent, 0o750)
    try:
        os.chown(parent, 0, grp.getgrnam(socket_group).gr_gid)
    except (AttributeError, KeyError, PermissionError):
        raise RuntimeError("helper socket group is unavailable") from None
    try:
        os.unlink(socket_path)
    except FileNotFoundError:
        pass
    server = _Server(socket_path, _Handler)
    os.chown(socket_path, 0, grp.getgrnam(socket_group).gr_gid)
    os.chmod(socket_path, stat.S_IRUSR | stat.S_IWUSR | stat.S_IRGRP | stat.S_IWGRP)
    _Handler.adapter = adapter
    _Handler.operation_lock = threading.Lock()
    try:
        server.serve_forever()
    finally:
        server.server_close()
        try:
            os.unlink(socket_path)
        except FileNotFoundError:
            pass


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--socket", default=os.environ.get("MYPROXY_XUI_HELPER_SOCKET", "/run/myproxy/xui-helper.sock"))
    args = parser.parse_args()
    settings = Settings.from_env()
    settings.require_configured_deployment()
    adapter = XuiSqliteAdapter(
        settings.xui_db_path,
        settings.xui_inbound_id,
        server_host=settings.server_host,
        server_port=settings.server_port,
        public_key=settings.public_key,
        short_id=settings.short_id,
        sni=settings.sni,
        fingerprint=settings.fingerprint,
        flow=settings.flow,
        spider_x=settings.spider_x,
        restart_command=("/bin/systemctl", "restart", "x-ui"),
        # Keep the total restart budget below the API socket's 15s timeout:
        # 3 x 2s subprocess attempts plus bounded reset/backoff.
        restart_timeout=2.0,
        restart_attempts=3,
    )
    serve(args.socket, adapter)


if __name__ == "__main__":
    main()
