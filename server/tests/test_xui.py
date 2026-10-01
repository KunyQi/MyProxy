"""Unit tests for myproxy_server.xui against a minimal 3x-ui SQLite schema."""

from __future__ import annotations

import json
import os
import sqlite3
import sys
import tempfile
import unittest
from unittest import mock
import uuid as uuid_mod

from myproxy_server.xui import XuiError, XuiSqliteAdapter

PRIVATE_KEY = "PRIVATE_KEY_TEST_DO_NOT_LOG"

BASE_STREAM = {
    "network": "tcp",
    "security": "reality",
    "realitySettings": {
        "show": False,
        "xver": 0,
        "dest": "example.com:443",
        "serverNames": ["example.com"],
        "privateKey": PRIVATE_KEY,
        "shortIds": ["aabbccdd00112233"],
        "settings": {
            "publicKey": "PUBLIC_KEY_TEST",
            "fingerprint": "chrome",
            "serverName": "",
            "spiderX": "/",
        },
    },
    "tcpSettings": {"acceptProxyProtocol": False, "header": {"type": "none"}},
}

EXPECTED_PUBLIC_PROFILE = {
    "server": "127.0.0.1",
    "port": 443,
    "security": "reality",
    "publicKey": "PUBLIC_KEY_TEST",
    "shortId": "aabbccdd00112233",
    "sni": "example.com",
    "fingerprint": "chrome",
    "flow": "xtls-rprx-vision",
    "spiderX": "/",
}


def _make_client(email: str, client_id: str, enable=True) -> dict:
    return {
        "id": client_id,
        "flow": "xtls-rprx-vision",
        "email": email,
        "limitIp": 0,
        "totalGB": 0,
        "expiryTime": 0,
        "enable": enable,
        "tgId": 0,
        "subId": "deadbeefdeadbeef",
        "reset": 0,
    }


class XuiSqliteAdapterTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.db_path = os.path.join(self.tmp.name, "x-ui.db")
        self.restart_command = (sys.executable, "-c", "pass")

    def _create_schema(self) -> None:
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "CREATE TABLE inbounds ("
            "id INTEGER PRIMARY KEY, "
            "port INTEGER, "
            "settings TEXT, "
            "stream_settings TEXT"
            ")"
        )
        conn.execute(
            "CREATE TABLE client_traffics ("
            "inbound_id INTEGER, "
            "email TEXT, "
            "up INTEGER, "
            "down INTEGER, "
            "total INTEGER, "
            "enable INTEGER"
            ")"
        )
        conn.commit()
        conn.close()

    def _insert_inbound(
        self,
        *,
        port: int = 443,
        settings: dict | None = None,
        stream_settings: dict | None = None,
        inbound_id: int = 1,
    ) -> None:
        self._create_schema()
        settings = settings if settings is not None else {"clients": [], "decryption": "none", "fallbacks": []}
        stream = stream_settings if stream_settings is not None else BASE_STREAM
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "INSERT INTO inbounds (id, port, settings, stream_settings) VALUES (?, ?, ?, ?)",
            (inbound_id, port, json.dumps(settings), json.dumps(stream)),
        )
        conn.commit()
        conn.close()

    def _insert_traffic(self, email: str, up: int, down: int, total: int, enable: int = 1) -> None:
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "INSERT INTO client_traffics (inbound_id, email, up, down, total, enable) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (1, email, up, down, total, enable),
        )
        conn.commit()
        conn.close()

    def _make_adapter(self, **kwargs) -> XuiSqliteAdapter:
        params = dict(
            db_path=self.db_path,
            inbound_id=1,
            server_host="127.0.0.1",
            server_port=443,
            restart_command=self.restart_command,
        )
        params.update(kwargs)
        return XuiSqliteAdapter(**params)

    def _read_db_settings(self) -> dict:
        conn = sqlite3.connect(self.db_path)
        row = conn.execute("SELECT settings FROM inbounds WHERE id = 1").fetchone()
        conn.close()
        return json.loads(row[0])

    # ------------------------------------------------------------------
    def test_public_profile_fields_and_no_private_key(self) -> None:
        self._insert_inbound()
        profile = self._make_adapter().public_profile()

        self.assertEqual(profile, EXPECTED_PUBLIC_PROFILE)
        self.assertNotIn("privateKey", profile)
        self.assertNotIn("uuid", profile)
        serialized = json.dumps(profile)
        self.assertNotIn(PRIVATE_KEY, serialized)
        self.assertNotIn("PRIVATE_KEY_TEST", serialized)

    def test_restart_budget_is_bounded_and_attempt_count_is_explicit(self) -> None:
        adapter = self._make_adapter(
            restart_command=("/bin/systemctl", "restart", "x-ui"),
            restart_timeout=1.5,
            restart_attempts=3,
        )
        calls: list[dict] = []

        def failed_run(command, **kwargs):
            calls.append({"command": command, **kwargs})
            return type("Result", (), {"returncode": 1})()

        with mock.patch("myproxy_server.xui.subprocess.run", side_effect=failed_run), \
             mock.patch("myproxy_server.xui.time.sleep"):
            with self.assertRaises(XuiError):
                adapter._restart_xui()
        self.assertEqual(len(calls), 6)  # 3 restart attempts + 3 bounded reset-failed calls
        restart_calls = [item for item in calls if item["command"] == list(adapter.restart_command)]
        self.assertEqual(len(restart_calls), 3)
        self.assertTrue(all(item["timeout"] == 1.5 for item in restart_calls))

    def test_public_profile_constructor_overrides(self) -> None:
        self._insert_inbound(port=8443)
        adapter = self._make_adapter(
            server_host="vps.example.com",
            public_key="OVERRIDE_PUBLIC_KEY",
            sni="vps.example.com",
            fingerprint="safari",
            flow="xtls-rprx-vision-udp443",
            spider_x="/x",
        )
        profile = adapter.public_profile()

        self.assertEqual(profile["server"], "vps.example.com")
        self.assertEqual(profile["port"], 443)  # constructor server_port wins
        self.assertEqual(profile["publicKey"], "OVERRIDE_PUBLIC_KEY")
        self.assertEqual(profile["sni"], "vps.example.com")
        self.assertEqual(profile["fingerprint"], "safari")
        self.assertEqual(profile["flow"], "xtls-rprx-vision-udp443")
        self.assertEqual(profile["spiderX"], "/x")
        self.assertNotIn("privateKey", profile)

    def test_public_profile_uses_inbound_port_when_server_port_missing(self) -> None:
        self._insert_inbound(port=8443)
        profile = self._make_adapter(server_port=None).public_profile()
        self.assertEqual(profile["port"], 8443)

    def test_public_profile_short_id_override_and_validation(self) -> None:
        self._insert_inbound()
        profile = self._make_adapter(short_id="aabbccdd00112233").public_profile()
        self.assertEqual(profile["shortId"], "aabbccdd00112233")

        bad = self._make_adapter(short_id="0000000000000000")
        with self.assertRaises(XuiError):
            bad.public_profile()

    def test_public_profile_rejects_non_reality_security(self) -> None:
        stream = dict(BASE_STREAM)
        stream["security"] = "tls"
        self._insert_inbound(stream_settings=stream)
        with self.assertRaisesRegex(XuiError, "security must be reality"):
            self._make_adapter().public_profile()

    def test_add_client_success_and_valid_uuid(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        client_id = adapter.add_client("myproxy-new@example.com")

        uuid_mod.UUID(client_id)  # raises ValueError if invalid
        clients = adapter.list_clients()
        self.assertEqual(len(clients), 1)
        self.assertEqual(clients[0]["email"], "myproxy-new@example.com")
        self.assertEqual(clients[0]["id"], client_id)
        self.assertIs(clients[0]["enable"], True)
        self.assertEqual(clients[0]["up"], 0)
        self.assertEqual(clients[0]["down"], 0)
        self.assertEqual(clients[0]["total"], 0)

        settings = self._read_db_settings()
        self.assertEqual(settings["clients"][0]["email"], "myproxy-new@example.com")
        self.assertEqual(settings["clients"][0]["enable"], True)
        self.assertEqual(settings["clients"][0]["flow"], "xtls-rprx-vision")
        self.assertEqual(settings["clients"][0]["limitIp"], 0)
        self.assertEqual(settings["clients"][0]["totalGB"], 0)
        self.assertEqual(settings["clients"][0]["expiryTime"], 0)
        self.assertEqual(settings["clients"][0]["tgId"], 0)
        self.assertEqual(settings["clients"][0]["reset"], 0)
        self.assertEqual(len(settings["clients"][0]["subId"]), 16)

    def test_add_duplicate_email_raises_xui_error(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-dup@example.com")

        with self.assertRaisesRegex(XuiError, "already exists"):
            adapter.add_client("myproxy-dup@example.com")

    def test_legacy_settings_compare_and_swap_rejects_stale_writer(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        conn = sqlite3.connect(self.db_path)
        original = conn.execute(
            "SELECT settings FROM inbounds WHERE id = 1"
        ).fetchone()[0]
        replacement = {"clients": [_make_client("other@example.com", "other-id")]}
        conn.execute(
            "UPDATE inbounds SET settings = ? WHERE id = 1",
            (json.dumps(replacement),),
        )
        conn.commit()
        conn.close()

        with self.assertRaisesRegex(XuiError, "changed concurrently"):
            adapter._save_settings({"clients": []}, original)
        self.assertEqual(self._read_db_settings(), replacement)

    def test_update_enable_false_reflected_in_list(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-disable@example.com")

        adapter.update_client("myproxy-disable@example.com", enable=False)

        clients = adapter.list_clients()
        self.assertIs(clients[0]["enable"], False)
        settings = self._read_db_settings()
        # A JSON boolean, never 0/1: 3x-ui skips a disabled client only when
        # the value is a bool, so an integer left it enabled in Xray.
        self.assertIs(settings["clients"][0]["enable"], False)

        adapter.update_client("myproxy-disable@example.com", enable=True)
        self.assertIs(self._read_db_settings()["clients"][0]["enable"], True)

    def test_update_all_supported_fields(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-fields@example.com")

        adapter.update_client(
            "myproxy-fields@example.com",
            enable=False,
            limit_ip=2,
            total_gb=10,
            expiry_time=1700000000,
        )

        settings = self._read_db_settings()
        client = settings["clients"][0]
        self.assertEqual(client["enable"], 0)
        self.assertEqual(client["limitIp"], 2)
        self.assertEqual(client["totalGB"], 10)
        self.assertEqual(client["expiryTime"], 1700000000)

    def test_update_no_fields_does_not_break(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-same@example.com")
        before = adapter.list_clients()

        adapter.update_client("myproxy-same@example.com")

        after = adapter.list_clients()
        self.assertEqual(after, before)

    def test_update_missing_email_raises_xui_error(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()

        with self.assertRaisesRegex(XuiError, "not found"):
            adapter.update_client("myproxy-missing@example.com", enable=False)

    def test_remove_client(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-gone@example.com")

        adapter.remove_client("myproxy-gone@example.com")

        self.assertEqual(adapter.list_clients(), [])
        settings = self._read_db_settings()
        self.assertEqual(settings["clients"], [])

    def test_remove_missing_email_raises_xui_error(self) -> None:
        self._insert_inbound()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-keep@example.com")

        with self.assertRaisesRegex(XuiError, "not found"):
            adapter.remove_client("myproxy-missing@example.com")

    def test_no_inbound_raises_xui_error(self) -> None:
        self._create_schema()  # table exists but no row
        adapter = self._make_adapter()

        with self.assertRaisesRegex(XuiError, "inbound not found"):
            adapter.public_profile()
        with self.assertRaisesRegex(XuiError, "inbound not found"):
            adapter.add_client("myproxy-a@example.com")
        with self.assertRaisesRegex(XuiError, "inbound not found"):
            adapter.list_clients()

    def test_namespace_scopes_listing_and_all_mutations(self) -> None:
        settings = {
            "clients": [
                _make_client("user1", "panel-user1"),
                _make_client("myproxy-device-abc123", "myproxy-id"),
            ],
            "decryption": "none",
            "fallbacks": [],
        }
        self._insert_inbound(settings=settings)
        adapter = self._make_adapter()

        clients = adapter.list_clients()
        self.assertEqual([client["email"] for client in clients], ["myproxy-device-abc123"])

        for operation in (
            lambda: adapter.add_client("user1"),
            lambda: adapter.update_client("user1", enable=False),
            lambda: adapter.remove_client("user1"),
        ):
            with self.subTest(operation=operation), self.assertRaisesRegex(
                XuiError, "outside the MyProxy namespace"
            ):
                operation()

        self.assertEqual(self._read_db_settings(), settings)

    def test_traffic_mapping(self) -> None:
        settings = {
            "clients": [
                _make_client("myproxy-traffic@example.com", "11111111-1111-1111-1111-111111111111"),
                _make_client("myproxy-zero@example.com", "22222222-2222-2222-2222-222222222222"),
            ],
            "decryption": "none",
            "fallbacks": [],
        }
        self._insert_inbound(settings=settings)
        self._insert_traffic("myproxy-traffic@example.com", up=100, down=200, total=300, enable=1)

        clients = {c["email"]: c for c in self._make_adapter().list_clients()}

        self.assertEqual(clients["myproxy-traffic@example.com"]["up"], 100)
        self.assertEqual(clients["myproxy-traffic@example.com"]["down"], 200)
        self.assertEqual(clients["myproxy-traffic@example.com"]["total"], 300)
        self.assertIs(clients["myproxy-traffic@example.com"]["enable"], True)
        self.assertEqual(clients["myproxy-zero@example.com"]["up"], 0)
        self.assertEqual(clients["myproxy-zero@example.com"]["down"], 0)
        self.assertEqual(clients["myproxy-zero@example.com"]["total"], 0)

    def test_list_clients_enable_reads_from_settings(self) -> None:
        settings = {
            "clients": [
                _make_client("myproxy-enabled@example.com", "11111111-1111-1111-1111-111111111111", enable=True),
            ],
            "decryption": "none",
            "fallbacks": [],
        }
        self._insert_inbound(settings=settings)
        self._insert_traffic("myproxy-enabled@example.com", up=0, down=0, total=0, enable=0)

        clients = self._make_adapter().list_clients()
        self.assertIs(clients[0]["enable"], True)


if __name__ == "__main__":
    unittest.main()
