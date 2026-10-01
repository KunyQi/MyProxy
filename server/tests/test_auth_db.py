"""Unit tests for auth helpers and SQLite repository functions.

The tests use only the standard library, never touch the network, and operate
exclusively on a temporary SQLite database.
"""

from __future__ import annotations

import os
import sqlite3
import tempfile
import unittest

import myproxy_server.auth as auth
import myproxy_server.db as db


class AuthDbTest(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self._tmp.name, "myproxy-test.db")
        self._connections: list[sqlite3.Connection] = []
        db.init_db(self.db_path)

    def tearDown(self) -> None:
        for conn in self._connections:
            try:
                conn.close()
            except Exception:
                pass
        self._connections.clear()
        self._tmp.cleanup()

    def connect(self) -> sqlite3.Connection:
        conn = db.connect(self.db_path)
        self._connections.append(conn)
        return conn


# ---------------------------------------------------------------------------
# auth.py
# ---------------------------------------------------------------------------

class AuthTest(AuthDbTest):
    def test_device_token_format_and_hash(self):
        tok = auth.generate_device_token()
        self.assertTrue(tok.startswith("tok_"))
        self.assertGreater(len(tok), 20)
        self.assertNotIn(" ", tok)

        digest = auth.hash_token(tok)
        self.assertEqual(len(digest), 64)
        self.assertNotEqual(digest, tok)
        self.assertTrue(all(c in "0123456789abcdef" for c in digest))
        self.assertEqual(digest, auth.hash_token(tok))

        other = auth.generate_device_token()
        self.assertNotEqual(tok, other)
        self.assertNotEqual(digest, auth.hash_token(other))

    def test_derived_device_token_is_deterministic_and_context_bound(self):
        token = auth.derive_device_token(
            "deployment-secret", "lnk_1", "dev_1", "instance-12345678"
        )
        self.assertEqual(
            token,
            auth.derive_device_token(
                "deployment-secret", "lnk_1", "dev_1", "instance-12345678"
            ),
        )
        self.assertTrue(token.startswith("tok_"))
        self.assertNotEqual(
            token,
            auth.derive_device_token(
                "deployment-secret", "lnk_2", "dev_1", "instance-12345678"
            ),
        )
        with self.assertRaises(ValueError):
            auth.derive_device_token("", "lnk_1", "dev_1", "instance-12345678")

    def test_extract_bearer(self):
        self.assertEqual(auth.extract_bearer("Bearer abc.def-ghi"), "abc.def-ghi")
        self.assertEqual(auth.extract_bearer("bearer xyz"), "xyz")
        self.assertEqual(auth.extract_bearer("  Bearer   token123  "), "token123")

        self.assertIsNone(auth.extract_bearer(None))
        self.assertIsNone(auth.extract_bearer(""))
        self.assertIsNone(auth.extract_bearer("   "))
        self.assertIsNone(auth.extract_bearer("Basic abc"))
        self.assertIsNone(auth.extract_bearer("Bearer"))
        self.assertIsNone(auth.extract_bearer("Bearer token extra"))

    def test_timing_safe_equal(self):
        self.assertTrue(auth.timing_safe_equal("abc", "abc"))
        self.assertFalse(auth.timing_safe_equal("abc", "abd"))
        self.assertFalse(auth.timing_safe_equal("abc", ""))
        self.assertFalse(auth.timing_safe_equal(None, "abc"))  # type: ignore[arg-type]

    def test_normalize_pairing_code(self):
        self.assertEqual(auth.normalize_pairing_code("A7K9M2QF"), "A7K9-M2QF")
        self.assertEqual(auth.normalize_pairing_code("a7k9 m2qf"), "A7K9-M2QF")
        self.assertEqual(auth.normalize_pairing_code("A7K9-M2QF"), "A7K9-M2QF")
        self.assertEqual(auth.normalize_pairing_code("  a7k9-m2qf  "), "A7K9-M2QF")
        self.assertEqual(auth.normalize_pairing_code("A7K-9M2QF"), "A7K9-M2QF")

        self.assertIsNone(auth.normalize_pairing_code(None))
        self.assertIsNone(auth.normalize_pairing_code(""))
        self.assertIsNone(auth.normalize_pairing_code("   "))
        self.assertIsNone(auth.normalize_pairing_code("A7K9M2Q"))
        self.assertIsNone(auth.normalize_pairing_code("A7K9M2QFA"))
        self.assertIsNone(auth.normalize_pairing_code("A7K9-M2Q!"))
        self.assertIsNone(auth.normalize_pairing_code("A7K9-M2Q "))
        self.assertIsNone(auth.normalize_pairing_code("----"))
        self.assertIsNone(auth.normalize_pairing_code("A7K9 M2Q"))


# ---------------------------------------------------------------------------
# db.py -- initialization
# ---------------------------------------------------------------------------

class InitDbTest(AuthDbTest):
    def test_init_db_is_idempotent(self):
        db.init_db(self.db_path)
        db.init_db(self.db_path)

        with self.connect() as conn:
            self.assertEqual(db.get_meta(conn, "latest_version"), db.__version__)
            self.assertEqual(db.get_meta(conn, "latest_download_url"), "")
            self.assertEqual(db.get_meta(conn, "latest_sha256"), "")
            self.assertEqual(db.get_meta(conn, "latest_mandatory"), "false")
            self.assertEqual(db.get_meta(conn, "android_latest_version"), "0.1.0")
            self.assertEqual(db.get_meta(conn, "android_latest_download_url"), "")
            self.assertEqual(db.get_meta(conn, "android_latest_sha256"), "")
            self.assertEqual(db.get_meta(conn, "android_latest_mandatory"), "false")

            latest = db.latest_config(conn)
            self.assertIsNotNone(latest)
            self.assertEqual(latest["version"], 1)
            self.assertEqual(latest["config"], {})
            self.assertRegex(latest["createdAt"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

            count = conn.execute("SELECT COUNT(*) FROM config_versions").fetchone()[0]
            self.assertEqual(count, 1)

            tables = {
                row[0]
                for row in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type = 'table'"
                )
            }
            self.assertTrue(
                {"users", "binding_links", "devices", "config_versions", "server_meta"}
                <= tables
            )

            index_sql = conn.execute(
                "SELECT sql FROM sqlite_master"
                " WHERE type = 'index' AND name = 'idx_devices_user_instance'"
            ).fetchone()[0]
            self.assertIn("status = 'active'", index_sql)

    def test_init_db_migrates_legacy_devices_table(self):
        legacy_path = os.path.join(self._tmp.name, "legacy.db")
        conn = sqlite3.connect(legacy_path)
        conn.executescript(
            """
            CREATE TABLE users (
                id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL DEFAULT '', status TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE devices (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL, device_name TEXT NOT NULL,
                platform TEXT NOT NULL DEFAULT 'windows',
                client_version TEXT NOT NULL DEFAULT '', token_hash TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL DEFAULT 'active',
                xray_client_email TEXT NOT NULL UNIQUE, token_created_at TEXT NOT NULL,
                token_expires_at TEXT, last_seen_at TEXT, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """
        )
        conn.close()

        db.init_db(legacy_path)
        with db.connect(legacy_path) as migrated:
            columns = {
                row["name"] for row in migrated.execute("PRAGMA table_info(devices)")
            }
            self.assertIn("client_instance_id_hash", columns)

    def test_connect_commits_and_rolls_back(self):
        conn = self.connect()
        with conn:
            conn.execute(
                "INSERT INTO server_meta(key, value, updated_at)"
                " VALUES ('manual_key', 'v1', ?)",
                (db.utc_now(),),
            )
        conn.close()
        self._connections.remove(conn)

        with self.connect() as conn2:
            self.assertEqual(db.get_meta(conn2, "manual_key"), "v1")

        conn3 = self.connect()
        try:
            with conn3:
                conn3.execute(
                    "INSERT INTO server_meta(key, value, updated_at)"
                    " VALUES ('rollback_key', 'v2', ?)",
                    (db.utc_now(),),
                )
                raise RuntimeError("force rollback")
        except RuntimeError:
            pass
        finally:
            conn3.close()
            self._connections.remove(conn3)

        with self.connect() as conn4:
            self.assertIsNone(db.get_meta(conn4, "rollback_key"))


# ---------------------------------------------------------------------------
# db.py -- users
# ---------------------------------------------------------------------------

class UserDbTest(AuthDbTest):
    def test_user_crud_and_soft_delete(self):
        with self.connect() as conn:
            user = db.create_user(conn, " alice ", "Alice")
            self.assertTrue(user["id"].startswith("usr_"))
            self.assertEqual(user["username"], "alice")
            self.assertEqual(user["displayName"], "Alice")
            self.assertEqual(user["status"], "active")
            self.assertIn("createdAt", user)
            self.assertIn("updatedAt", user)

            self.assertEqual(db.get_user(conn, user["id"])["id"], user["id"])
            self.assertEqual(db.get_user(conn, "missing"), None)

            self.assertEqual(len(db.list_users(conn)), 1)
            self.assertEqual(len(db.list_users(conn, username="alice")), 1)
            self.assertEqual(len(db.list_users(conn, username="bob")), 0)

            bob = db.create_user(conn, "bob", "")
            self.assertEqual(bob["displayName"], "bob")
            self.assertEqual(len(db.list_users(conn)), 2)

            self.assertTrue(db.soft_delete_user(conn, user["id"]))
            deleted = db.get_user(conn, user["id"])
            self.assertEqual(deleted["status"], "deleted")
            self.assertEqual(len(db.list_users(conn)), 1)
            # Idempotent for an existing user.
            self.assertTrue(db.soft_delete_user(conn, user["id"]))
            self.assertFalse(db.soft_delete_user(conn, "missing"))

    def test_soft_delete_revokes_active_bindings_in_same_txn(self):
        with self.connect() as conn:
            user = db.create_user(conn, "carol", "Carol")
            b1 = db.create_binding(conn, user["id"], "windows", db.utc_now())
            b2 = db.create_binding(conn, user["id"], "windows", db.utc_now())
            db.mark_binding_claimed(conn, b2["id"], "some-device")

            self.assertTrue(db.soft_delete_user(conn, user["id"]))
            self.assertEqual(db.get_binding_by_id(conn, b1["id"])["status"], "revoked")
            self.assertEqual(db.get_binding_by_id(conn, b2["id"])["status"], "claimed")


# ---------------------------------------------------------------------------
# db.py -- binding links
# ---------------------------------------------------------------------------

class BindingDbTest(AuthDbTest):
    def test_binding_lifecycle(self):
        with self.connect() as conn:
            user = db.create_user(conn, "dave", "Dave")
            binding = db.create_binding(conn, user["id"], "windows", db.utc_now())
            self.assertTrue(binding["id"].startswith("lnk_"))
            self.assertRegex(binding["code"], r"^[A-Z0-9]{4}-[A-Z0-9]{4}$")
            self.assertEqual(binding["status"], "active")
            self.assertEqual(binding["failedAttempts"], 0)
            self.assertEqual(binding["maxAttempts"], 5)

            by_code = db.get_binding_by_code(conn, binding["code"])
            self.assertEqual(by_code["id"], binding["id"])
            self.assertIsNone(db.get_binding_by_code(conn, "ZZZZ-ZZZZ"))

            # Failed attempts increment while status stays active.
            now = db.utc_now()
            db.mark_binding_failed(conn, binding["id"], now)
            db.mark_binding_failed(conn, binding["id"], now)
            after_failed = db.get_binding_by_id(conn, binding["id"])
            self.assertEqual(after_failed["failedAttempts"], 2)
            self.assertEqual(after_failed["lastFailedAt"], now)
            self.assertEqual(after_failed["status"], "active")

            # Claimed.
            claimed = db.mark_binding_claimed(conn, binding["id"], "dev_123")
            self.assertEqual(claimed["status"], "claimed")
            self.assertEqual(claimed["claimedDeviceId"], "dev_123")
            self.assertIn("claimedAt", claimed)

            # 撤销是终态，对 claimed 同样生效：泄漏的配对码必须停止重放，
            # 后台列表也必须显示为 revoked。设备行独立，已绑定设备不受影响。
            self.assertTrue(db.revoke_binding(conn, binding["id"]))
            self.assertEqual(db.get_binding_by_id(conn, binding["id"])["status"], "revoked")
            self.assertTrue(db.revoke_binding(conn, binding["id"]))
            self.assertEqual(db.get_binding_by_id(conn, binding["id"])["status"], "revoked")

        with self.connect() as conn:
            user = db.create_user(conn, "erin", "Erin")
            expired = db.create_binding(conn, user["id"], "windows", db.utc_now())
            db.mark_binding_expired(conn, expired["id"])
            self.assertEqual(db.get_binding_by_id(conn, expired["id"])["status"], "expired")

            revoked = db.create_binding(conn, user["id"], "windows", db.utc_now())
            self.assertTrue(db.revoke_binding(conn, revoked["id"]))
            self.assertEqual(db.get_binding_by_id(conn, revoked["id"])["status"], "revoked")
            self.assertTrue(db.revoke_binding(conn, revoked["id"]))
            self.assertFalse(db.revoke_binding(conn, "missing"))

            # Only an active link may expire.  Expiring a revoked or claimed
            # one would erase a terminal status and its audit trail.
            db.mark_binding_expired(conn, revoked["id"])
            self.assertEqual(db.get_binding_by_id(conn, revoked["id"])["status"], "revoked")
            claimed_link = db.create_binding(conn, user["id"], "windows", db.utc_now())
            db.mark_binding_claimed(conn, claimed_link["id"], "dev_456")
            db.mark_binding_expired(conn, claimed_link["id"])
            self.assertEqual(
                db.get_binding_by_id(conn, claimed_link["id"])["status"], "claimed"
            )

            self.assertEqual(len(db.list_bindings(conn)), 4)
            self.assertEqual(len(db.list_bindings(conn, user_id=user["id"])), 3)
            self.assertEqual(len(db.list_bindings(conn, user_id="missing")), 0)


# ---------------------------------------------------------------------------
# db.py -- devices
# ---------------------------------------------------------------------------

class DeviceDbTest(AuthDbTest):
    def test_device_lifecycle_and_token_rotation(self):
        with self.connect() as conn:
            user = db.create_user(conn, "frank", "Frank")

            token = auth.generate_device_token()
            token_hash = auth.hash_token(token)
            dev = db.create_device(
                conn,
                user["id"],
                "DESKTOP-ABC",
                "windows",
                "0.1.0",
                token_hash,
                "myproxy-device-1@example.test",
            )
            self.assertTrue(dev["id"].startswith("dev_"))
            self.assertEqual(dev["deviceName"], "DESKTOP-ABC")
            self.assertEqual(dev["status"], "active")
            self.assertEqual(dev["xrayClientEmail"], "myproxy-device-1@example.test")
            self.assertNotIn("tokenHash", dev)
            self.assertNotIn("token_hash", dev)
            self.assertIsNone(dev["lastSeenAt"])

            self.assertEqual(
                db.get_device_by_token_hash(conn, token_hash)["id"], dev["id"]
            )
            self.assertIsNone(db.get_device_by_token_hash(conn, auth.hash_token("nope")))

            found = db.find_active_device_by_name(
                conn, user["id"], "DESKTOP-ABC", "windows"
            )
            self.assertEqual(found["id"], dev["id"])
            self.assertIsNone(
                db.find_active_device_by_name(conn, user["id"], "DESKTOP-ABC", "linux")
            )

            # Rotate token.
            new_token = auth.generate_device_token()
            new_hash = auth.hash_token(new_token)
            now = db.utc_now()
            rotated = db.rotate_device_token(
                conn, dev["id"], new_hash, "myproxy-device-2@example.test", now
            )
            self.assertEqual(rotated["xrayClientEmail"], "myproxy-device-2@example.test")
            self.assertIsNone(db.get_device_by_token_hash(conn, token_hash))
            self.assertEqual(
                db.get_device_by_token_hash(conn, new_hash)["id"], dev["id"]
            )

            row = conn.execute(
                "SELECT token_hash, xray_client_email, token_created_at, updated_at"
                " FROM devices WHERE id = ?",
                (dev["id"],),
            ).fetchone()
            self.assertEqual(row["token_hash"], new_hash)
            self.assertEqual(row["xray_client_email"], "myproxy-device-2@example.test")
            self.assertEqual(row["token_created_at"], now)
            self.assertEqual(row["updated_at"], now)

            # touch seen.
            seen_now = db.utc_now()
            db.touch_device_seen(conn, dev["id"], seen_now)
            self.assertEqual(db.get_device(conn, dev["id"])["lastSeenAt"], seen_now)

            # disable (idempotent).
            self.assertTrue(db.set_device_disabled(conn, dev["id"]))
            self.assertTrue(db.set_device_disabled(conn, dev["id"]))
            self.assertEqual(db.get_device(conn, dev["id"])["status"], "disabled")
            self.assertIsNone(
                db.find_active_device_by_name(conn, user["id"], "DESKTOP-ABC", "windows")
            )
            self.assertFalse(db.set_device_disabled(conn, "missing"))

            self.assertEqual(len(db.list_devices(conn)), 1)
            self.assertEqual(len(db.list_devices(conn, user_id=user["id"])), 1)
            self.assertEqual(len(db.list_devices(conn, user_id="missing")), 0)


# ---------------------------------------------------------------------------
# db.py -- config versions and server_meta
# ---------------------------------------------------------------------------

class ConfigDbTest(AuthDbTest):
    def test_config_version_insert_and_read(self):
        with self.connect() as conn:
            first = db.latest_config(conn)
            self.assertEqual(first["version"], 1)
            self.assertEqual(first["config"], {})

            profile = {"server": "127.0.0.1", "port": 443, "security": "reality"}
            inserted = db.insert_config(conn, profile, "rotate", db.utc_now())
            self.assertEqual(inserted["version"], 2)
            self.assertEqual(inserted["config"], profile)

            latest = db.latest_config(conn)
            self.assertEqual(latest["version"], 2)
            self.assertEqual(latest["config"], profile)
            self.assertIn("createdAt", latest)

            db.insert_config(conn, {"server": "127.0.0.2"}, "rotate again", db.utc_now())
            self.assertEqual(db.latest_config(conn)["version"], 3)

    def test_server_meta_read_write(self):
        with self.connect() as conn:
            now = db.utc_now()
            self.assertIsNone(db.get_meta(conn, "custom_key"))
            db.set_meta(conn, "custom_key", "custom_value", now)
            self.assertEqual(db.get_meta(conn, "custom_key"), "custom_value")

            # Existing key is updated.
            db.set_meta(conn, "custom_key", "new_value", now)
            self.assertEqual(db.get_meta(conn, "custom_key"), "new_value")

            self.assertEqual(db.get_meta(conn, "latest_version"), db.__version__)


if __name__ == "__main__":
    unittest.main()
