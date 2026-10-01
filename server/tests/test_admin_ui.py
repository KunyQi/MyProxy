"""Security and delivery tests for the private Server administration UI."""

from __future__ import annotations

from html.parser import HTMLParser
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from concurrent.futures import ThreadPoolExecutor
import urllib.error
import urllib.request

from test_api import FakeXuiAdapter, make_settings, start_server, stop_server


class _MarkupAudit(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.ids: list[str] = []
        self.external_urls: list[str] = []
        self.inline_handlers: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        del tag
        for name, value in attrs:
            if name == "id" and value:
                self.ids.append(value)
            if name in {"src", "href"} and value and not value.startswith("/"):
                self.external_urls.append(value)
            if name.lower().startswith("on"):
                self.inline_handlers.append(name)


class AdminUiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls._tmpdir = tempfile.TemporaryDirectory()
        db_path = str(Path(cls._tmpdir.name) / "myproxy.db")
        cls.admin_token = "ui-test-admin-token"
        settings = make_settings(db_path, admin_token=cls.admin_token)
        cls.httpd, cls.thread, cls.port = start_server(settings, FakeXuiAdapter())

    @classmethod
    def tearDownClass(cls) -> None:
        stop_server(cls.httpd, cls.thread)
        cls._tmpdir.cleanup()

    @classmethod
    def _request(
        cls, path: str, *, method: str = "GET", token: str | None = None, body=None
    ) -> tuple[int, object, bytes]:
        headers = {}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if body is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            f"http://127.0.0.1:{cls.port}{path}",
            method=method,
            headers=headers,
            data=json.dumps(body).encode() if body is not None else None,
        )
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read()

    def test_ssh_ticket_exchange_replay_and_logout(self) -> None:
        self.assertEqual(self._request('/api/admin/ui-ticket', method='POST')[0], 401)
        status, _, raw = self._request('/api/admin/ui-ticket', method='POST', token=self.admin_token)
        self.assertEqual(status, 200)
        ticket = json.loads(raw)['ticket']
        status, headers, raw = self._request('/admin/session', method='POST', body={'ticket': ticket})
        self.assertEqual(status, 200)
        self.assertEqual(headers['Cache-Control'], 'no-store')
        session = json.loads(raw)['token']
        self.assertNotEqual(session, self.admin_token)
        self.assertEqual(self._request('/admin/session', method='POST', body={'ticket': ticket})[0], 401)
        self.assertEqual(self._request('/api/admin/user', token=session)[0], 200)
        self.assertEqual(self._request('/api/admin/ui-ticket', method='POST', token=session)[0], 401)
        self.assertEqual(self._request('/api/admin/ui-session', method='DELETE', token=session)[0], 200)
        self.assertEqual(self._request('/api/admin/user', token=session)[0], 401)
        self.assertEqual(self._request('/api/admin/user', token=self.admin_token)[0], 200)

    def test_ticket_and_session_expiry(self) -> None:
        service = self.httpd.RequestHandlerClass.service
        with patch('myproxy_server.app.time.monotonic', return_value=100):
            ticket = service.issue_ui_ticket()['ticket']
        with patch('myproxy_server.app.time.monotonic', return_value=160):
            self.assertEqual(self._request('/admin/session', method='POST', body={'ticket': ticket})[0], 401)
            ticket = service.issue_ui_ticket()['ticket']
            token = service.redeem_ui_ticket(ticket)['token']
        with patch('myproxy_server.app.time.monotonic', return_value=3760):
            self.assertFalse(service.ui_session_authorized(token))

    def test_ticket_is_consumed_atomically(self) -> None:
        service = self.httpd.RequestHandlerClass.service
        ticket = service.issue_ui_ticket()['ticket']
        def exchange(_):
            return self._request('/admin/session', method='POST', body={'ticket': ticket})[0]
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sorted(pool.map(exchange, range(2))), [200, 401])

    def test_invalid_ticket_cannot_bypass_auth(self) -> None:
        for value in (None, [], '', 'x'*43, self.admin_token):
            self.assertEqual(self._request('/admin/session', method='POST', body={'ticket': value})[0], 401)

    def test_ui_access_is_bounded_and_restart_invalidates_it(self) -> None:
        from myproxy_server.app import MyProxyService, ServiceError
        current = self.httpd.RequestHandlerClass.service
        service = MyProxyService(current.settings, FakeXuiAdapter())
        with patch('myproxy_server.app.time.monotonic', return_value=100):
            tickets = [service.issue_ui_ticket()['ticket'] for _ in range(128)]
            with self.assertRaises(ServiceError) as error:
                service.issue_ui_ticket()
            self.assertEqual(error.exception.status, 429)
            tokens = [service.redeem_ui_ticket(ticket)['token'] for ticket in tickets]
            ticket = service.issue_ui_ticket()['ticket']
            with self.assertRaises(ServiceError) as error:
                service.redeem_ui_ticket(ticket)
            self.assertEqual(error.exception.status, 429)
            restarted = MyProxyService(current.settings, FakeXuiAdapter())
            self.assertFalse(restarted.ui_session_authorized(tokens[0]))
            with self.assertRaises(ServiceError):
                restarted.redeem_ui_ticket(ticket)

    def test_admin_paths_serve_the_same_private_shell_and_root_stays_closed(self) -> None:
        bodies = []
        for path in ("/admin", "/admin/"):
            with self.subTest(path=path):
                status, headers, body = self._request(path)
                self.assertEqual(status, 200)
                self.assertEqual(headers.get_content_type(), "text/html")
                self.assertIn(b"MyProxy Server Management", body)
                self.assertIn(b'/admin/app.js', body)
                self.assertNotIn(self.admin_token.encode("ascii"), body)
                bodies.append(body)
        self.assertEqual(bodies[0], bodies[1])
        status, headers, body = self._request("/")
        self.assertEqual(status, 404)
        self.assertEqual(headers.get_content_type(), "application/json")
        self.assertEqual(json.loads(body.decode("utf-8"))["error"]["code"], "NotFound")

    def test_static_assets_have_strict_security_headers(self) -> None:
        expected_types = {
            "/admin/": "text/html",
            "/admin/app.css": "text/css",
            "/admin/app.js": "text/javascript",
        }
        for path, content_type in expected_types.items():
            with self.subTest(path=path):
                status, headers, body = self._request(path)
                self.assertEqual(status, 200)
                self.assertTrue(body)
                self.assertEqual(headers.get_content_type(), content_type)
                self.assertEqual(headers["Cache-Control"], "no-store")
                self.assertEqual(headers["X-Content-Type-Options"], "nosniff")
                self.assertEqual(headers["X-Frame-Options"], "DENY")
                self.assertEqual(headers["Referrer-Policy"], "no-referrer")
                self.assertEqual(headers["Cross-Origin-Resource-Policy"], "same-origin")
                csp = headers["Content-Security-Policy"]
                self.assertIn("default-src 'none'", csp)
                self.assertIn("script-src 'self'", csp)
                self.assertIn("connect-src 'self'", csp)
                self.assertIn("frame-ancestors 'none'", csp)
                self.assertIn("form-action 'none'", csp)
                self.assertIn("img-src 'none'", csp)
                self.assertNotIn("'unsafe-inline'", csp)

    def test_markup_is_local_unique_and_has_no_inline_handlers(self) -> None:
        status, _, body = self._request("/admin/")
        self.assertEqual(status, 200)
        audit = _MarkupAudit()
        audit.feed(body.decode("utf-8"))
        self.assertEqual(len(audit.ids), len(set(audit.ids)))
        self.assertEqual(audit.external_urls, [])
        self.assertEqual(audit.inline_handlers, [])
        self.assertNotIn("token-input", audit.ids)
        self.assertNotIn(b'type="password"', body)
        for required_id in (
            "login-help",
            "users-section",
            "bindings-section",
            "devices-section",
            "operations-section",
            "releases-section",
            "usage-section",
        ):
            self.assertIn(required_id, audit.ids)

    def test_release_and_usage_panels_only_reach_admin_routes(self) -> None:
        """The private UI must never become a second way into the Device API.

        Its isolation comes from nginx's exact-location allowlist plus Bearer
        checks, not from the page being well behaved -- but a page that calls
        a device route would still be a bug worth catching here.
        """
        status, _, body = self._request("/admin/app.js")
        self.assertEqual(status, 200)
        script = body.decode("utf-8")

        for admin_route in (
            "/api/admin/release",
            "/api/admin/assignment",
            "/api/admin/release-audit",
            "/api/admin/activity",
            "/api/admin/usage",
        ):
            with self.subTest(admin_route=admin_route):
                self.assertIn(admin_route, script)

        self.assertNotIn("/api/device/", script)

    def test_script_never_persists_token_or_uses_html_injection_sinks(self) -> None:
        status, _, body = self._request("/admin/app.js")
        self.assertEqual(status, 200)
        script = body.decode("utf-8")
        for forbidden in (
            "localStorage",
            "sessionStorage",
            "document.cookie",
            ".innerHTML",
            "insertAdjacentHTML",
            "eval(",
            "new Function",
            "binding.code",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, script)
        self.assertIn('credentials: "omit"', script)
        self.assertIn('redirect: "error"', script)
        self.assertIn("textContent", script)

    def test_binding_code_is_rendered_before_any_post_create_refresh(self) -> None:
        status, _, body = self._request("/admin/app.js")
        self.assertEqual(status, 200)
        script = body.decode("utf-8")
        start = script.index('byId("binding-form").addEventListener')
        end = script.index('byId("users-body").addEventListener', start)
        handler = script[start:end]
        render_position = handler.index("state.newBindingCode = result.code")
        refresh_position = handler.index("await refreshAll()")
        self.assertLess(render_position, refresh_position)

    def test_ui_does_not_weaken_admin_api_authentication(self) -> None:
        status, _, body = self._request("/api/admin/user")
        self.assertEqual(status, 401)
        error = json.loads(body.decode("utf-8"))
        self.assertEqual(error["error"]["code"], "AdminUnauthorized")
        status, _, body = self._request(
            "/api/admin/user", token=self.admin_token
        )
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body.decode("utf-8")), {"users": []})

    def test_non_get_ui_route_is_not_an_operation_endpoint(self) -> None:
        status, headers, body = self._request("/admin/app.js", method="POST")
        self.assertEqual(status, 404)
        self.assertEqual(headers.get_content_type(), "application/json")
        self.assertEqual(json.loads(body.decode("utf-8"))["error"]["code"], "NotFound")

    def test_javascript_has_valid_syntax_when_node_is_available(self) -> None:
        node = shutil.which("node")
        if node is None:
            self.skipTest("node is unavailable")
        _, _, body = self._request("/admin/app.js")
        with tempfile.NamedTemporaryFile(suffix=".js", delete=False) as stream:
            path = Path(stream.name)
            stream.write(body)
        try:
            result = subprocess.run(
                [node, "--check", str(path)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
        finally:
            path.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
