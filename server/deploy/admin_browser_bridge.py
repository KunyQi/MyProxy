#!/usr/bin/env python3
"""Serve the private admin UI on HTTP loopback through an existing pinned SSH tunnel.

The SSH launcher owns the tunnel. This tool executes only the restricted `token`
SSH command once, exchanges its output for a short-lived UI ticket, and never
retains or injects that administrator bearer into browser requests.
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import io
import json
from pathlib import Path
import re
import select
import shutil
import socket
import ssl
import subprocess
import sys
import time
import urllib.parse
import webbrowser

MAX_BODY = 512 * 1024 * 1024
CHUNK_SIZE = 64 * 1024
UPLOAD_RESPONSE_TIMEOUT = 3600
MAX_UPLOAD_RESPONSE = 2 * 1024 * 1024
PUBLIC_PATHS = {"/admin", "/admin/", "/admin/app.js", "/admin/app.css",
                "/admin/release-signing.js", "/admin/session", "/healthz", "/readyz"}
REQUEST_HEADERS = {"Accept", "Content-Type", "Authorization", "User-Agent"}
RESPONSE_HEADERS = {"content-type", "content-length", "cache-control", "content-security-policy",
                    "x-content-type-options", "x-frame-options", "referrer-policy",
                    "cross-origin-resource-policy", "cross-origin-opener-policy",
                    "permissions-policy", "strict-transport-security", "retry-after", "x-request-id"}


class PinnedConnection(http.client.HTTPSConnection):
    def __init__(self, port: int, certificate_sha256: str, *, timeout: int = 120):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        super().__init__("127.0.0.1", port, timeout=timeout, context=context)
        self.certificate_sha256 = certificate_sha256.lower()

    def connect(self):
        super().connect()
        certificate = self.sock.getpeercert(binary_form=True)
        actual = hashlib.sha256(certificate or b"").hexdigest()
        if not certificate or not hmac.compare_digest(actual, self.certificate_sha256):
            self.close()
            raise ssl.SSLCertVerificationError("Private tunnel certificate pin mismatch")


def safe_path(target: str) -> bool:
    if not target.startswith("/") or target.startswith("//") or len(target) > 8192:
        return False
    if "\\" in target or any(ord(char) < 32 or ord(char) == 127 for char in target):
        return False
    if re.search(r"%(?![0-9A-Fa-f]{2})", target):
        return False
    parsed = urllib.parse.urlsplit(target)
    if parsed.scheme or parsed.netloc or parsed.fragment or "%" in parsed.path:
        return False
    if "//" in parsed.path or any(part in {".", ".."} for part in parsed.path.split("/")):
        return False
    decoded_query = urllib.parse.unquote(parsed.query)
    if any(ord(char) < 32 or ord(char) == 127 for char in decoded_query):
        return False
    return parsed.path in PUBLIC_PATHS or bool(re.fullmatch(r"/api/admin/[A-Za-z0-9_./-]+", parsed.path))


def request_length(headers, method: str, expected_host: str) -> int:
    if headers.get_all("Host", []) != [expected_host]:
        raise ValueError("Unexpected Host")
    origins = headers.get_all("Origin", [])
    if origins and origins != ["http://" + expected_host]:
        raise ValueError("Unexpected Origin")
    if headers.get_all("Transfer-Encoding") or headers.get_all("Expect"):
        raise ValueError("Unsupported body framing")
    lengths = headers.get_all("Content-Length", [])
    if len(lengths) > 1 or (lengths and not re.fullmatch(r"0|[1-9][0-9]*", lengths[0])):
        raise ValueError("Invalid Content-Length")
    length = int(lengths[0]) if lengths else 0
    if length > MAX_BODY or (method == "GET" and length):
        raise ValueError("Invalid body size")
    for name in REQUEST_HEADERS:
        values = headers.get_all(name, [])
        if len(values) > 1 or any(any(ord(char) < 32 or ord(char) > 126 for char in value) for value in values):
            raise ValueError("Invalid relay header")
    return length


class BufferedResponseSocket:
    def __init__(self, data):
        self.data = data

    def makefile(self, mode):
        return io.BytesIO(self.data)


def upload_response(upstream_socket, source, client_socket, length: int, method: str):
    """Own TLS I/O in one thread and stop sending as soon as a response starts."""
    upstream_socket.setblocking(False)
    remaining, outgoing = length, b""
    raw_response = bytearray()
    send_wait, receive_wait = None, None
    started = last_progress = time.monotonic()
    while True:
        now = time.monotonic()
        if now - started > UPLOAD_RESPONSE_TIMEOUT or now - last_progress > 120:
            raise TimeoutError("Private upload connection timed out")
        read_sockets = [upstream_socket]
        if remaining and not outgoing and not raw_response:
            read_sockets.append(client_socket)
        write_sockets = [upstream_socket] if ((outgoing and send_wait != "read") or receive_wait == "write") else []
        pending = isinstance(upstream_socket, ssl.SSLSocket) and upstream_socket.pending() > 0
        readable, writable, _ = select.select(read_sockets, write_sockets, [], 0 if pending else 0.1)
        if pending or upstream_socket in readable or (receive_wait == "write" and upstream_socket in writable):
            try:
                received = upstream_socket.recv(CHUNK_SIZE)
                receive_wait = None
                send_wait = None
                if not received:
                    break
                raw_response.extend(received)
                outgoing, remaining = b"", 0
                last_progress = time.monotonic()
                if len(raw_response) > MAX_UPLOAD_RESPONSE:
                    raise http.client.HTTPException("Private upload response is too large")
            except ssl.SSLWantReadError:
                receive_wait = "read"
                send_wait = None
            except ssl.SSLWantWriteError:
                receive_wait = "write"
            except BlockingIOError:
                pass
        # TLS session tickets contain no HTTP bytes. Only plaintext response
        # bytes stop the upload; ready upstream responses always precede writes.
        if raw_response:
            continue
        if client_socket in readable and remaining and not outgoing:
            outgoing = source.read(min(CHUNK_SIZE, remaining))
            if not outgoing:
                raise EOFError("Incomplete request body")
            remaining -= len(outgoing)
            last_progress = time.monotonic()
        if outgoing and (upstream_socket in writable or (send_wait == "read" and upstream_socket in readable)):
            try:
                sent = upstream_socket.send(outgoing)
                if not sent:
                    raise ConnectionError("Private upload socket closed")
                outgoing = outgoing[sent:]
                send_wait = None
                last_progress = time.monotonic()
            except ssl.SSLWantReadError:
                send_wait = "read"
            except (ssl.SSLWantWriteError, BlockingIOError):
                send_wait = "write"
    response = http.client.HTTPResponse(BufferedResponseSocket(bytes(raw_response)), method=method)
    response.begin()
    # Parse the complete bounded response before emitting downstream headers.
    # A truncated upstream Content-Length cannot become a broken browser reply.
    body = response.read()
    return response, body


class BridgeServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, port: int, upstream_port: int, certificate_sha256: str):
        self.upstream_port = upstream_port
        self.certificate_sha256 = certificate_sha256
        super().__init__(("127.0.0.1", port), BridgeHandler)
        self.expected_host = f"127.0.0.1:{self.server_port}"

    def upstream(self):
        return PinnedConnection(self.upstream_port, self.certificate_sha256)

    def get_request(self):
        request, address = super().get_request()
        request.settimeout(120)
        return request, address


class BridgeHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    # Avoid buffered body bytes hiding from upload_response's readiness check.
    rbufsize = 0

    def log_message(self, *args):
        pass

    def finish_local_response(self):
        if getattr(self, "_local_response_finished", False):
            return
        self._local_response_finished = True
        try:
            self.wfile.flush()
            self.connection.shutdown(socket.SHUT_WR)
            # A bounded lingering close lets clients read an early error instead
            # of Windows resetting a socket which still has unread upload bytes.
            deadline = time.monotonic() + 0.2
            while time.monotonic() < deadline:
                if not select.select([self.connection], [], [], 0.02)[0]:
                    continue
                if not self.connection.recv(CHUNK_SIZE):
                    break
        except OSError:
            pass

    def reject(self, status: int, message: str):
        self.close_connection = True
        body = json.dumps({"error": {"code": "BridgeRejected", "message": message}}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def handle_expect_100(self):
        self.reject(417, "Expect is unsupported")
        self.finish_local_response()
        return False

    def relay(self):
        self.close_connection = True
        try:
            length = request_length(self.headers, self.command, self.server.expected_host)
            if not safe_path(self.path):
                raise ValueError("Path is not allowed")
            path = urllib.parse.urlsplit(self.path).path
            if path in PUBLIC_PATHS and self.command != ("POST" if path == "/admin/session" else "GET"):
                raise ValueError("Method is not allowed")
        except ValueError:
            self.reject(400, "Request is not allowed by the local bridge")
            self.finish_local_response()
            return
        connection = self.server.upstream()
        response_started = False
        try:
            self.connection.settimeout(120)
            # Pin verification completes before any HTTP header or body is sent.
            connection.connect()
            connection.putrequest(self.command, self.path, skip_accept_encoding=True)
            for name in REQUEST_HEADERS:
                value = self.headers.get(name)
                if value is not None:
                    connection.putheader(name, value)
            connection.putheader("Content-Length", str(length))
            connection.putheader("Connection", "close")
            connection.endheaders()
            if length:
                response, buffered_body = upload_response(connection.sock, self.rfile, self.connection, length, self.command)
            else:
                response, buffered_body = connection.getresponse(), None
            self.send_response(response.status)
            for name, value in response.getheaders():
                if name.lower() in RESPONSE_HEADERS:
                    self.send_header(name, value)
            self.send_header("Connection", "close")
            self.end_headers()
            response_started = True
            if buffered_body is not None:
                self.wfile.write(buffered_body)
            else:
                while data := response.read(CHUNK_SIZE):
                    self.wfile.write(data)
        except (OSError, EOFError, http.client.HTTPException):
            if not response_started:
                self.reject(502, "Private tunnel unavailable or certificate pin rejected")
        finally:
            connection.close()
            self.finish_local_response()

    do_GET = relay
    do_POST = relay
    do_DELETE = relay


def ssh_token(args) -> str:
    executable = shutil.which("ssh")
    if not executable:
        raise RuntimeError("OpenSSH ssh is unavailable")
    command = [executable, "-F", "none", "-T", "-i", str(args.ssh_key),
               "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
               "-o", "StrictHostKeyChecking=yes", "-o", f"UserKnownHostsFile={args.known_hosts}",
               "-o", "GlobalKnownHostsFile=none", "-o", "PreferredAuthentications=publickey",
               "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
               "-o", "ConnectTimeout=15", f"{args.ssh_user}@{args.host}", "token"]
    try:
        result = subprocess.run(command, capture_output=True, timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeError("Restricted SSH token command failed") from error
    if result.returncode != 0:
        raise RuntimeError("Restricted SSH token command failed")
    token = result.stdout.decode("ascii", errors="strict").strip()
    if not re.fullmatch(r"[A-Za-z0-9._~-]{16,4096}", token):
        raise RuntimeError("Restricted SSH command returned an invalid token")
    return token


def ui_ticket(upstream_port: int, certificate_sha256: str, bearer: str) -> str:
    connection = PinnedConnection(upstream_port, certificate_sha256, timeout=20)
    try:
        connection.connect()
        connection.request("POST", "/api/admin/ui-ticket", body=b"{}", headers={
            "Authorization": "Bearer " + bearer, "Content-Type": "application/json"})
        response = connection.getresponse()
        raw = response.read(65537)
        if response.status != 200 or len(raw) > 65536:
            raise RuntimeError("The private backend did not issue a UI ticket")
        document = json.loads(raw)
        ticket = document.get("ticket") if isinstance(document, dict) else None
        if not isinstance(ticket, str) or not re.fullmatch(r"[A-Za-z0-9_-]{43}", ticket):
            raise RuntimeError("The private backend returned an invalid UI ticket")
        return ticket
    finally:
        connection.close()


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-user", required=True)
    parser.add_argument("--ssh-key", required=True, type=Path)
    parser.add_argument("--known-hosts", required=True, type=Path)
    parser.add_argument("--upstream-port", required=True, type=int)
    parser.add_argument("--tls-sha256", required=True)
    parser.add_argument("--listen-port", type=int, default=0)
    parser.add_argument("--no-browser", action="store_true")
    args = parser.parse_args(argv)
    try:
        ipaddress.ip_address(args.host)
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", args.host):
            parser.error("--host must be an IP address or hostname")
    if not re.fullmatch(r"[a-z_][a-z0-9_-]{0,63}", args.ssh_user):
        parser.error("--ssh-user is invalid")
    if not 1 <= args.upstream_port <= 65535 or not 0 <= args.listen_port <= 65535:
        parser.error("ports are out of range")
    if not re.fullmatch(r"[A-Fa-f0-9]{64}", args.tls_sha256):
        parser.error("--tls-sha256 must be the trusted leaf DER certificate SHA-256")
    for name in ("ssh_key", "known_hosts"):
        path = getattr(args, name).resolve()
        if not path.is_file() or path.stat().st_size == 0:
            parser.error(f"--{name.replace('_', '-')} must name an existing nonempty file")
        setattr(args, name, path)
    return args


def main(argv=None) -> int:
    args = arguments(argv)
    server = None
    try:
        server = BridgeServer(args.listen_port, args.upstream_port, args.tls_sha256)
        bearer = ssh_token(args)
        try:
            ticket = ui_ticket(args.upstream_port, args.tls_sha256, bearer)
        finally:
            bearer = ""
        url = f"http://{server.expected_host}/admin/#ticket={ticket}"
        print(url, flush=True)
        if not args.no_browser:
            webbrowser.open(url)
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        return 0
    except (RuntimeError, OSError, ValueError, http.client.HTTPException):
        print("Local admin bridge failed. Check the restricted SSH identity, existing tunnel and trusted certificate pin.", file=sys.stderr)
        return 1
    finally:
        if server is not None:
            server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
