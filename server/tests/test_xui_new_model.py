"""Unit tests for the new 3x-ui data model (clients + client_inbounds).

These tests use a temporary SQLite DB that contains both the new tables and
the legacy ``inbounds.settings.clients`` JSON (seeded with ``user1``) so we can
prove add/remove/update are performed on the new tables while user1 is never
touched.
"""

from __future__ import annotations

import json
import os
import sqlite3
import sys
import tempfile
import unittest
import uuid as uuid_mod
from unittest import mock
from contextlib import closing

from myproxy_server.app import MyProxyService
from myproxy_server.config import Settings
from myproxy_server import db
from myproxy_server.xui import XuiError, XuiSqliteAdapter, new_myproxy_email

STREAM = {
    "network": "tcp",
    "security": "reality",
    "realitySettings": {
        "show": False,
        "xver": 0,
        "dest": "example.com:443",
        "serverNames": ["example.com"],
        "privateKey": "PRIVATE_KEY_TEST_DO_NOT_LOG",
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

USER1_UUID = "531f0c62-85cd-4c60-96ca-30dd1e994a22"


class NewModelXuiAdapterTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.db_path = os.path.join(self.tmp.name, "x-ui.db")
        self.restart_command = (sys.executable, "-c", "pass")

    def _create_schema(self, *, seed_legacy_myproxy: list[dict] | None = None) -> None:
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "CREATE TABLE inbounds ("
            "id INTEGER PRIMARY KEY, port INTEGER, settings TEXT, stream_settings TEXT)"
        )
        conn.execute(
            "CREATE TABLE client_traffics ("
            "id INTEGER PRIMARY KEY, inbound_id INTEGER, enable INTEGER, email TEXT,"
            "up INTEGER, down INTEGER, expiry_time INTEGER, total INTEGER, reset INTEGER,"
            "last_online INTEGER)"
        )
        conn.execute(
            "CREATE TABLE clients ("
            "id INTEGER PRIMARY KEY, email TEXT NOT NULL, sub_id TEXT, uuid TEXT,"
            "password TEXT, auth TEXT, flow TEXT, security TEXT, reverse TEXT,"
            "wg_private_key TEXT, wg_public_key TEXT, wg_allowed_ips TEXT,"
            "wg_pre_shared_key TEXT, wg_keep_alive INTEGER DEFAULT 0, secret TEXT,"
            "ad_tag TEXT DEFAULT '', limit_ip INTEGER, total_gb INTEGER,"
            "expiry_time INTEGER, enable INTEGER DEFAULT 1, tg_id INTEGER,"
            "group_name TEXT DEFAULT '', comment TEXT, reset INTEGER DEFAULT 0,"
            "created_at INTEGER, updated_at INTEGER)"
        )
        conn.execute(
            "CREATE TABLE client_inbounds ("
            "client_id INTEGER, inbound_id INTEGER, flow_override TEXT, created_at INTEGER,"
            "PRIMARY KEY (client_id, inbound_id))"
        )
        conn.commit()

        legacy_clients = [
            {
                "id": USER1_UUID,
                "security": "",
                "flow": "xtls-rprx-vision",
                "email": "user1",
                "limitIp": 0,
                "totalGB": 0,
                "expiryTime": 0,
                "enable": True,
                "tgId": 0,
                "subId": "d25c821f8caa40f7",
                "comment": "",
                "reset": 0,
            }
        ]
        legacy_clients.extend(seed_legacy_myproxy or [])
        settings = {"clients": legacy_clients, "decryption": "none", "fallbacks": []}
        conn.execute(
            "INSERT INTO inbounds (id, port, settings, stream_settings) VALUES (1, 443, ?, ?)",
            (json.dumps(settings), json.dumps(STREAM)),
        )
        conn.execute(
            "INSERT INTO clients ("
            "email, sub_id, uuid, password, auth, flow, security, reverse,"
            "wg_private_key, wg_public_key, wg_allowed_ips, wg_pre_shared_key,"
            "wg_keep_alive, secret, ad_tag, limit_ip, total_gb, expiry_time,"
            "enable, tg_id, group_name, comment, reset, created_at, updated_at)"
            "VALUES ('user1', 'd25c821f8caa40f7', ?, '', '', 'xtls-rprx-vision', '', '',"
            "'', '', '', '', 0, '', '', 0, 0, 0, 1, 0, '', '', 0, 1784819646717, 1784819646717)",
            (USER1_UUID,),
        )
        conn.execute(
            "INSERT INTO client_inbounds (client_id, inbound_id, flow_override, created_at)"
            "VALUES (1, 1, 'xtls-rprx-vision', 1784819646717)"
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

    def _read_legacy_settings(self) -> dict:
        conn = sqlite3.connect(self.db_path)
        row = conn.execute("SELECT settings FROM inbounds WHERE id = 1").fetchone()
        conn.close()
        return json.loads(row[0])

    def _read_new_clients(self) -> list[sqlite3.Row]:
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        rows = conn.execute("SELECT * FROM clients ORDER BY id").fetchall()
        conn.close()
        return rows

    def _read_new_client_inbounds(self, client_id: int) -> list[sqlite3.Row]:
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        rows = conn.execute(
            "SELECT * FROM client_inbounds WHERE client_id = ? ORDER BY inbound_id",
            (client_id,),
        ).fetchall()
        conn.close()
        return rows

    def _insert_inbound(self, inbound_id: int) -> None:
        """Add an empty inbound so one new-model client can be shared."""
        settings = {"clients": [], "decryption": "none", "fallbacks": []}
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "INSERT INTO inbounds (id, port, settings, stream_settings) VALUES (?, ?, ?, ?)",
            (inbound_id, 8443, json.dumps(settings), json.dumps(STREAM)),
        )
        conn.commit()
        conn.close()

    def _link_client_to_inbound(self, client_id: int, inbound_id: int) -> None:
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "INSERT INTO client_inbounds (client_id, inbound_id, flow_override, created_at)"
            " VALUES (?, ?, 'xtls-rprx-vision', 1784819646717)",
            (client_id, inbound_id),
        )
        conn.commit()
        conn.close()

    # ------------------------------------------------------------------
    def test_add_uses_new_tables_and_keeps_user1(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        client_uuid = adapter.add_client("myproxy-dev_testclient")

        uuid_mod.UUID(client_uuid)
        rows = self._read_new_clients()
        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[0]["email"], "user1")
        self.assertEqual(rows[1]["email"], "myproxy-dev_testclient")
        self.assertEqual(rows[1]["uuid"], client_uuid)
        self.assertEqual(rows[1]["flow"], "xtls-rprx-vision")
        self.assertEqual(rows[1]["enable"], 1)
        self.assertEqual(rows[1]["limit_ip"], 0)
        self.assertEqual(rows[1]["total_gb"], 0)
        self.assertEqual(rows[1]["expiry_time"], 0)
        self.assertEqual(rows[1]["tg_id"], 0)
        self.assertEqual(rows[1]["reset"], 0)
        self.assertEqual(len(rows[1]["sub_id"]), 16)

        links = self._read_new_client_inbounds(rows[1]["id"])
        self.assertEqual(len(links), 1)
        self.assertEqual(links[0]["inbound_id"], 1)
        self.assertEqual(links[0]["flow_override"], "xtls-rprx-vision")

        legacy = self._read_legacy_settings()
        self.assertEqual([c["email"] for c in legacy["clients"]], ["user1"])

    def test_add_removes_stale_legacy_same_email_only(self) -> None:
        stale = {
            "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "flow": "xtls-rprx-vision",
            "email": "myproxy-dev_stale",
            "limitIp": 0,
            "totalGB": 0,
            "expiryTime": 0,
            "enable": True,
            "tgId": 0,
            "subId": "deadbeefdeadbeef",
            "reset": 0,
        }
        self._create_schema(seed_legacy_myproxy=[stale])
        adapter = self._make_adapter()

        adapter.add_client("myproxy-dev_stale")

        legacy = self._read_legacy_settings()
        self.assertEqual([c["email"] for c in legacy["clients"]], ["user1"])
        rows = self._read_new_clients()
        self.assertEqual(len(rows), 2)

    def test_remove_deletes_both_new_tables_and_keeps_user1(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-dev_gone")

        adapter.remove_client("myproxy-dev_gone")

        rows = self._read_new_clients()
        self.assertEqual([r["email"] for r in rows], ["user1"])
        self.assertEqual(len(self._read_new_client_inbounds(1)), 1)
        legacy = self._read_legacy_settings()
        self.assertEqual([c["email"] for c in legacy["clients"]], ["user1"])

    def test_remove_missing_raises(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        with self.assertRaisesRegex(XuiError, "not found") as caught:
            adapter.remove_client("myproxy-dev_missing")
        self.assertEqual(caught.exception.code, "client_not_found")

    def test_remove_only_unlinks_target_inbound_for_shared_client(self) -> None:
        self._create_schema()
        adapter_one = self._make_adapter(inbound_id=1)
        client_uuid = adapter_one.add_client("myproxy-dev_shared")
        client_pk = next(
            row["id"] for row in self._read_new_clients() if row["uuid"] == client_uuid
        )
        self._insert_inbound(2)
        self._link_client_to_inbound(client_pk, 2)

        adapter_two = self._make_adapter(inbound_id=2)
        adapter_two.update_client("myproxy-dev_shared", enable=False)
        adapter_one.remove_client("myproxy-dev_shared")

        rows = self._read_new_clients()
        self.assertEqual([row["email"] for row in rows], ["user1", "myproxy-dev_shared"])
        self.assertEqual(rows[1]["enable"], 0)
        self.assertEqual(
            [row["inbound_id"] for row in self._read_new_client_inbounds(client_pk)],
            [2],
        )
        self.assertEqual(
            [client["email"] for client in adapter_two.list_clients()],
            ["myproxy-dev_shared"],
        )

    def test_update_requires_target_inbound_link(self) -> None:
        self._create_schema()
        adapter_one = self._make_adapter(inbound_id=1)
        adapter_one.add_client("myproxy-dev_scoped")
        self._insert_inbound(2)

        adapter_two = self._make_adapter(inbound_id=2)
        with self.assertRaisesRegex(XuiError, "not found"):
            adapter_two.update_client("myproxy-dev_scoped", enable=False)

        row = next(
            row for row in self._read_new_clients() if row["email"] == "myproxy-dev_scoped"
        )
        self.assertEqual(row["enable"], 1)

    def test_update_changes_new_clients_table(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-dev_fields")

        adapter.update_client(
            "myproxy-dev_fields",
            enable=False,
            limit_ip=2,
            total_gb=10,
            expiry_time=1700000000,
        )

        rows = self._read_new_clients()
        client = [r for r in rows if r["email"] == "myproxy-dev_fields"][0]
        self.assertEqual(client["enable"], 0)
        self.assertEqual(client["limit_ip"], 2)
        self.assertEqual(client["total_gb"], 10)
        self.assertEqual(client["expiry_time"], 1700000000)
        self.assertEqual(client["updated_at"] > 0, True)

        clients = {c["email"]: c for c in adapter.list_clients()}
        self.assertFalse(clients["myproxy-dev_fields"]["enable"])
        self.assertNotIn("user1", clients)

    def test_update_missing_raises(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        with self.assertRaisesRegex(XuiError, "not found") as caught:
            adapter.update_client("myproxy-dev_missing", enable=False)
        self.assertEqual(caught.exception.code, "client_not_found")

    def test_missing_inbound_rejects_client_mutations_without_writes(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        with mock.patch.object(adapter, "_restart_xui") as restart:
            adapter.add_client("myproxy-dev_existing")
            with closing(sqlite3.connect(self.db_path)) as conn, conn:
                conn.execute("DELETE FROM inbounds WHERE id = 1")
                conn.execute("DELETE FROM client_inbounds WHERE inbound_id = 1")
            before = [dict(row) for row in self._read_new_clients()]
            restart.reset_mock()
            operations = {
                "add": lambda: adapter.add_client("myproxy-dev_new"),
                "update": lambda: adapter.update_client("myproxy-dev_existing", enable=False),
                "remove": lambda: adapter.remove_client("myproxy-dev_existing"),
            }
            for name, operation in operations.items():
                with self.subTest(operation=name):
                    with self.assertRaises(XuiError) as caught:
                        operation()
                    self.assertIsNone(caught.exception.code)
                    self.assertEqual(before, [dict(row) for row in self._read_new_clients()])
                    with closing(sqlite3.connect(self.db_path)) as conn:
                        self.assertEqual(conn.execute("SELECT COUNT(*) FROM client_inbounds").fetchone()[0], 0)
                    restart.assert_not_called()

    def test_rebind_with_deleted_inbound_preserves_pairing_code_and_device(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        settings = Settings(db_path=os.path.join(self.tmp.name, "myproxy.db"))
        service = MyProxyService(settings, adapter)
        with mock.patch.object(adapter, "_restart_xui"):
            service.initialize()
            user = service.admin_create_user("missing-inbound")
            first = service.admin_create_binding(user["id"], "windows")
            claimed = service.claim(first["code"], "TEST-PC", "windows", "0.1.0")
            second = service.admin_create_binding(user["id"], "windows")
            with closing(db.connect(settings.db_path)) as conn:
                original_device = db.get_device(conn, claimed["deviceId"])
            with closing(sqlite3.connect(self.db_path)) as conn, conn:
                conn.execute("DELETE FROM inbounds WHERE id = 1")
                conn.execute("DELETE FROM client_inbounds WHERE inbound_id = 1")
            original_clients = [dict(row) for row in self._read_new_clients()]

            with self.assertRaises(XuiError) as caught:
                service.claim(second["code"], "TEST-PC", "windows", "0.1.0")
            self.assertIsNone(caught.exception.code)
            with closing(db.connect(settings.db_path)) as conn:
                self.assertEqual(db.get_binding_by_code(conn, second["code"])["status"], "active")
                self.assertEqual(original_device, db.get_device(conn, claimed["deviceId"]))
            self.assertEqual(original_clients, [dict(row) for row in self._read_new_clients()])
            with closing(sqlite3.connect(self.db_path)) as conn:
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM client_inbounds").fetchone()[0], 0)

    def test_list_clients_reads_new_tables_and_traffic(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-dev_traffic")

        conn = sqlite3.connect(self.db_path)
        conn.execute(
            "INSERT INTO client_traffics (inbound_id, enable, email, up, down, total)"
            " VALUES (1, 1, 'myproxy-dev_traffic', 100, 200, 300)"
        )
        conn.commit()
        conn.close()

        clients = {c["email"]: c for c in adapter.list_clients()}
        self.assertEqual(set(clients), {"myproxy-dev_traffic"})
        self.assertEqual(clients["myproxy-dev_traffic"]["up"], 100)
        self.assertEqual(clients["myproxy-dev_traffic"]["down"], 200)
        self.assertEqual(clients["myproxy-dev_traffic"]["total"], 300)

    def test_schema_probe_failure_raises_instead_of_using_legacy(self) -> None:
        """探测失败不得降级到 legacy 分支。

        紧跟 _restart_xui() 之后遇到 database is locked 时，降级会把 client
        写进 inbounds.settings.clients 而不是 xray 实际读取的表：claim 提交
        并返回 200，设备却在握手时被拒，且没有任何日志。
        """
        adapter = self._make_adapter(db_path=os.path.join(self.tmp.name, "missing.db"))
        with self.assertRaises(XuiError):
            adapter._has_new_client_tables()

        managed = new_myproxy_email("dev_" + "1" * 24)
        with self.assertRaises(XuiError):
            adapter.add_client(managed)

        # 失败必须是探测抛出的，legacy 表不能被写。
        self.assertFalse(os.path.exists(os.path.join(self.tmp.name, "missing.db")))

    def test_duplicate_raises(self) -> None:
        self._create_schema()
        adapter = self._make_adapter()
        adapter.add_client("myproxy-dev_dup")
        with self.assertRaisesRegex(XuiError, "already exists"):
            adapter.add_client("myproxy-dev_dup")


if __name__ == "__main__":
    unittest.main()
