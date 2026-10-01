"""Shared origin configuration and public/private deployment boundaries."""
from __future__ import annotations

import json
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

from myproxy_server.config import Settings, load_deployment_config, parse_api_origin


class DeploymentConfigTests(unittest.TestCase):
    def settings(self, origin="https://api.example.com", **overrides):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "deployment.json"
        path.write_text(json.dumps({"api_base_url": origin}), encoding="utf-8")
        return Settings.from_env({"MYPROXY_DEPLOYMENT_CONFIG": str(path), **overrides})

    def test_default_port_and_explicit_port_preserve_origin(self):
        for origin, port in (("https://api.example.com", 443), ("https://api.example.com:8443", 8443), ("https://api.example.com:8443/", 8443)):
            with self.subTest(origin=origin):
                settings = self.settings(origin)
                self.assertEqual(settings.api_base_url, origin.removesuffix("/"))
                self.assertEqual(settings.api_host, "api.example.com")
                self.assertEqual(settings.api_port, port)
                self.assertEqual(settings.server_host, "api.example.com")
                self.assertIsNone(settings.server_port)
                self.assertEqual(
                    settings.connectivity_check_urls,
                    (f"{origin.removesuffix('/')}/connectivity-check",),
                )
                settings.require_configured_deployment()

    def test_connectivity_check_urls_are_optional_bounded_full_https_urls(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "deployment.json"
            urls = [
                "https://probe.example.net/check/v1",
                "https://[2001:db8::2]:8443/network/204",
            ]
            path.write_text(
                json.dumps({
                    "api_base_url": "https://api.example.com",
                    "connectivity_check_urls": [*urls, urls[0]],
                }),
                encoding="utf-8",
            )
            settings = Settings.from_env({"MYPROXY_DEPLOYMENT_CONFIG": str(path)})
            self.assertEqual(settings.connectivity_check_urls, tuple(urls))

    def test_connectivity_check_urls_reject_invalid_schema_and_urls(self):
        invalid_values = (
            None,
            "https://probe.example.net/check",
            [],
            ["https://probe.example.net/check"] * 5,
            ["http://probe.example.net/check"],
            ["https://user:pass@probe.example.net/check"],
            ["https://probe.example.net/check?"],
            ["https://probe.example.net/check?token=x"],
            ["https://probe.example.net/check#"],
            ["https://probe.example.net/check#frag"],
            [" https://probe.example.net/check"],
            ["https://probe.example.net/check\n"],
            ["https://probe.example.net/\x01check"],
            ["https://probe.example.net/check\\path"],
            ["https://bad_host.example.net/check"],
            ["https://probe.example.net:0/check"],
            ["https://probe.example.net:/check"],
            ["https://[broken]/check"],
            ["https://127.1/check"],
        )
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "deployment.json"
            for urls in invalid_values:
                path.write_text(
                    json.dumps({
                        "api_base_url": "https://api.example.com",
                        "connectivity_check_urls": urls,
                    }),
                    encoding="utf-8",
                )
                with self.subTest(urls=urls), self.assertRaises(ValueError):
                    load_deployment_config(path)

            path.write_text(
                json.dumps({"api_base_url": "https://api.example.com", "unknown": True}),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "unknown key"):
                load_deployment_config(path)

    def test_data_plane_overrides_are_independent_of_api_port(self):
        settings = self.settings(
            "https://api.example.com:9443",
            MYPROXY_SERVER_HOST="203.0.113.20", MYPROXY_SERVER_PORT="8443",
        )
        self.assertEqual(settings.api_host, "api.example.com")
        self.assertEqual(settings.api_port, 9443)
        self.assertEqual(settings.server_host, "203.0.113.20")
        self.assertEqual(settings.server_port, 8443)
        self.assertEqual(self.settings(MYPROXY_SERVER_HOST="").server_host, "api.example.com")

    def test_example_can_be_loaded_locally_but_cannot_start_production(self):
        settings = self.settings("https://api.example.invalid")
        with self.assertRaisesRegex(ValueError, "not configured"):
            settings.require_configured_deployment()

    def test_invalid_origins_fail_closed(self):
        for value in (
            None, 42, "", " https://example.com", "https://example.com\n", "\x01https://example.com", "https://example.com\x7f",
            "http://example.com", "https://u@example.com", "https://u:p@example.com",
            "https://example.com//", "https://example.com/path", "https://example.com?",
            "https://example.com?q=1", "https://example.com#", "https://example.com#x",
            "https://example.com:0", "https://example.com:65536", "https://example.com:abc",
            "https://example.com:", "https://", "https://bad_host", "https://-bad.test",
            "https://bad..test", "https://example.com\\path", "https://[broken]",
            "https://api.example.org.", "https://127.1", "https://0177.0.0.1",
            "https://123", "https://256.0.0.1", "https://0x7f000001", "https://0x7f.0.0.1",
            "https://[fe80::1%25eth0]", "https://[v1.example.org]", "https://[vF.test]",
        ):
            with self.subTest(value=value), self.assertRaises(ValueError):
                parse_api_origin(value)

    def test_missing_and_malformed_config_are_clear_errors(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "deployment.json"
            with self.assertRaisesRegex(ValueError, "missing or invalid"):
                load_deployment_config(path)
            for data in ("broken", "{}", "[]", '{"api_base_url":false}'):
                path.write_text(data, encoding="utf-8")
                with self.subTest(data=data), self.assertRaises(ValueError):
                    load_deployment_config(path)

    def test_installed_api_and_helper_read_same_config(self):
        deploy = Path(__file__).resolve().parents[1] / "deploy"
        expected = "Environment=MYPROXY_DEPLOYMENT_CONFIG=/opt/myproxy-api/deployment.json"
        for filename in ("myproxy-api.service", "myproxy-xui-helper.service"):
            self.assertIn(expected, (deploy / filename).read_text(encoding="utf-8"))
        helper_env = (deploy / "myproxy-xui-helper.env").read_text(encoding="utf-8")
        self.assertFalse(any(line.startswith("MYPROXY_SERVER_HOST=") for line in helper_env.splitlines()))

    def test_private_predeployment_audits_the_configured_api_port(self):
        script = Path(__file__).resolve().parents[1] / "deploy" / "predeploy_private.py"
        spec = importlib.util.spec_from_file_location("private_deployment_config_test", script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        settings = self.settings("https://api.example.com:9443")
        with mock.patch.dict(os.environ, {"MYPROXY_DEPLOYMENT_CONFIG": settings.deployment_config}):
            self.assertEqual(module._configured_public_port(), 9443)
            rendered = module._render_remote_script(module.REMOTE_AUDIT_SCRIPT)
        self.assertIn("sport = :9443", rendered)
        self.assertNotIn("__PUBLIC_PORT__", rendered)
        self.assertIn("sport = :1820", rendered)

    def test_private_predeployment_accepts_and_validates_optional_probe_urls(self):
        script = Path(__file__).resolve().parents[1] / "deploy" / "predeploy_private.py"
        spec = importlib.util.spec_from_file_location("private_probe_config_test", script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "deployment.json"
            path.write_text(
                json.dumps({
                    "api_base_url": "https://api.example.org",
                    "connectivity_check_urls": ["https://probe.example.net/empty-204"],
                }),
                encoding="utf-8",
            )
            with mock.patch.dict(os.environ, {"MYPROXY_DEPLOYMENT_CONFIG": str(path)}):
                self.assertEqual(module._deployment_payload(), path.resolve())

            path.write_text(
                json.dumps({
                    "api_base_url": "https://api.example.org",
                    "connectivity_check_urls": ["https://probe.example.net/check?query=1"],
                }),
                encoding="utf-8",
            )
            with mock.patch.dict(os.environ, {"MYPROXY_DEPLOYMENT_CONFIG": str(path)}):
                with self.assertRaises(module.PredeployError):
                    module._deployment_payload()

    def test_gateway_uses_ca_for_public_requests_and_keeps_backend_private(self):
        deploy = Path(__file__).resolve().parents[1] / "deploy"
        for filename in ("deploy.sh", "install-device-api-proxy.sh"):
            text = (deploy / filename).read_text(encoding="utf-8")
            self.assertNotIn("CERT_SHA256", text)
            self.assertIn("public-ca", text)
            logical_lines = []
            current = ""
            for line in text.splitlines():
                current += line.rstrip().removesuffix("\\").strip() + " "
                if not line.rstrip().endswith("\\"):
                    logical_lines.append(current)
                    current = ""
            for line in logical_lines:
                if "curl -k" in line or "curl -sk" in line:
                    self.assertIn("127.0.0.1", line)
        installer = (deploy / "install-device-api-proxy.sh").read_text(encoding="utf-8")
        self.assertIn("-checkend 0", installer)
        self.assertIn('-checkhost "$SERVER_NAME"', installer)
        self.assertIn("curl -sSf --resolve", installer)
        self.assertLess(installer.index("curl -sSf --resolve"), installer.rindex("RESTORE_NEEDED=0"))

    def test_connectivity_check_is_an_exact_get_route_to_the_api_backend(self):
        template = (Path(__file__).resolve().parents[1] / "deploy" / "nginx-device-api.conf.template").read_text(encoding="utf-8")
        marker = "    location = /connectivity-check {"
        self.assertEqual(template.count(marker), 1)
        start = template.index(marker)
        end = template.index("\n    }", start)
        block = template[start:end]
        self.assertIn("limit_except GET { deny all; }", block)
        self.assertIn("proxy_pass https://myproxy_device_api_backend;", block)
        self.assertNotIn("return ", block)

    def test_gateway_installers_accept_only_an_empty_204_probe_over_validated_tls(self):
        deploy = Path(__file__).resolve().parents[1] / "deploy"
        installer = (deploy / "install-device-api-proxy.sh").read_text(encoding="utf-8")
        self.assertIn("UPSTREAM_PROBE_STATUS=\"$(curl -ksS --max-time 5", installer)
        self.assertIn('"https://127.0.0.1:${UPSTREAM_PORT}/connectivity-check"', installer)
        self.assertIn('"$UPSTREAM_PROBE_STATUS" != "204" ] || [ -s "$PROBE_BODY"', installer)
        public_probe = installer.split("PUBLIC_PROBE_STATUS=", 1)[1].split("\nif [", 1)[0]
        self.assertIn("curl -sS --max-time 5", public_probe)
        self.assertIn("--resolve", public_probe)
        self.assertIn("/connectivity-check", public_probe)
        self.assertNotIn("-k", public_probe)
        self.assertNotIn("-L", public_probe)
        self.assertIn('"$PUBLIC_PROBE_STATUS" != "204" ] || [ -s "$PROBE_BODY"', installer)

        public_deploy = (deploy / "deploy.sh").read_text(encoding="utf-8")
        self.assertIn('"https://127.0.0.1:1820/connectivity-check"', public_deploy)
        self.assertIn('"$CONNECTIVITY_UPSTREAM_STATUS" != "204" ] || [ -s "$CONNECTIVITY_PROBE_BODY"', public_deploy)
        public_probe = public_deploy.split("CONNECTIVITY_PUBLIC_STATUS=", 1)[1].split("\nif [ -s", 1)[0]
        self.assertIn("curl -sS --max-time 5", public_probe)
        self.assertIn("--resolve", public_probe)
        self.assertIn("/connectivity-check", public_probe)
        self.assertNotIn("-k", public_probe)
        self.assertNotIn("-L", public_probe)
        self.assertIn('"$CONNECTIVITY_BODY_STATE" != "empty"', public_deploy)


if __name__ == "__main__":
    unittest.main()
