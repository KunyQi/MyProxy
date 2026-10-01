"""Races between an administrator's deletion and a device's own writes.

Each test forces the deletion to land between a request's status check and
its writes -- the window every one of these bugs lived in.  The injected
deletion waits at most a second for the request: before the fix it completed
inside that second (the request held no lock yet) and the request then wrote
on top of it; after the fix the request holds the write lock from its status
check on, so the deletion can only run once the request has committed.
"""

import http.client
import os
import sys
import tempfile
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from myproxy_server import app as app_module  # noqa: E402
from myproxy_server import db  # noqa: E402
from myproxy_server.app import MyProxyService, ServiceError  # noqa: E402
from myproxy_server.server import make_server  # noqa: E402

from test_api import FakeXuiAdapter, make_settings, stop_server  # noqa: E402

INSTANCE = "inst-0123456789abcdef"


class _DeletionRace:
    """Patch ``app.db.<reader>`` so its first call starts ``delete`` and gives it a second."""

    def __init__(self, reader, delete):
        self._reader = reader
        self._delete = delete
        self._original = getattr(app_module.db, reader)
        self.thread = None
        self.error = None

    def __enter__(self):
        def patched(*args, **kwargs):
            row = self._original(*args, **kwargs)
            if self.thread is None:
                self.thread = threading.Thread(target=self._run)
                self.thread.start()
                self.thread.join(timeout=1.0)
            return row

        setattr(app_module.db, self._reader, patched)
        return self

    def _run(self):
        try:
            self._delete()
        except Exception as exc:  # surfaced by the test, not swallowed
            self.error = exc

    def __exit__(self, *exc):
        setattr(app_module.db, self._reader, self._original)
        if self.thread is not None:
            self.thread.join(timeout=10)
        return False


class WriteRaceTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.settings = make_settings(
            os.path.join(self._tmp.name, "myproxy.db"), device_token_secret="b" * 64
        )
        self.xui = FakeXuiAdapter()
        self.service = MyProxyService(self.settings, self.xui)
        self.service.initialize()
        self.user = self.service.admin_create_user("alice")

    def tearDown(self):
        self._tmp.cleanup()

    def _claim(self):
        binding = self.service.admin_create_binding(self.user["id"], "windows")
        return self.service.claim(binding["code"], "PC", "windows", "0.1.0", INSTANCE)

    def _observability_rows(self, device_id):
        with db.connect(self.settings.db_path) as conn:
            return {
                table: conn.execute(
                    f"SELECT COUNT(*) FROM {table} WHERE device_id = ?", (device_id,)
                ).fetchone()[0]
                for table in ("device_activity", "device_addresses", "usage_categories")
            }

    def _assert_forgotten(self, race, device_id):
        self.assertIsNone(race.error)
        with db.connect(self.settings.db_path) as conn:
            self.assertEqual(db.get_device(conn, device_id)["status"], "disabled")
        self.assertEqual(
            self._observability_rows(device_id),
            {"device_activity": 0, "device_addresses": 0, "usage_categories": 0},
            "a deleted device must not get its address or usage written back",
        )

    def test_heartbeat_cannot_write_back_a_device_deleted_mid_request(self):
        claimed = self._claim()
        delete = lambda: self.service.admin_delete_device(claimed["deviceId"])  # noqa: E731
        with _DeletionRace("get_device_by_token_hash", delete) as race:
            try:
                self.service.heartbeat(claimed["deviceToken"], client_ip="203.0.113.7")
            except ServiceError:
                pass  # losing the race outright is also a correct outcome
        self._assert_forgotten(race, claimed["deviceId"])

    def test_usage_report_cannot_write_back_a_device_deleted_mid_request(self):
        claimed = self._claim()
        delete = lambda: self.service.admin_delete_device(claimed["deviceId"])  # noqa: E731
        with _DeletionRace("get_device_by_token_hash", delete) as race:
            try:
                self.service.device_report_usage(
                    claimed["deviceToken"],
                    {"categories": {"video": 1000}},
                    client_ip="198.51.100.9",
                )
            except ServiceError:
                pass
        self._assert_forgotten(race, claimed["deviceId"])

    def test_a_pairing_code_created_while_its_user_is_deleted_cannot_be_claimed(self):
        delete = lambda: self.service.admin_delete_user(self.user["id"])  # noqa: E731
        with _DeletionRace("get_user", delete) as race:
            binding = self.service.admin_create_binding(self.user["id"], "windows")
        self.assertIsNone(race.error)

        with self.assertRaises(ServiceError) as caught:
            self.service.claim(binding["code"], "PC", "windows", "0.1.0", INSTANCE)
        self.assertEqual(caught.exception.code, "PairingInvalid")
        with db.connect(self.settings.db_path) as conn:
            devices = db.list_devices(conn, self.user["id"])
        self.assertEqual([d for d in devices if d.get("status") == "active"], [])
        self.assertEqual(
            [c for c in self.xui.list_clients() if c.get("enable")],
            [],
            "the compensation must remove the client the failed claim added",
        )

    def test_a_rebind_starts_the_new_clients_usage_from_zero(self):
        claimed = self._claim()

        def set_counter(email, up):
            for client in self.xui._clients:
                if client["email"] == email:
                    client["up"] = up

        def email():
            with db.connect(self.settings.db_path) as conn:
                return db.get_device(conn, claimed["deviceId"])["xrayClientEmail"]

        set_counter(email(), 500_000_000)
        self.service._sweep_xui_counters("2026-09-24T10:00:00Z")
        self._claim()  # same installation re-pairs: new x-ui client, same device row
        set_counter(email(), 800_000_000)
        self.service._sweep_xui_counters("2026-09-24T11:00:00Z")

        with db.connect(self.settings.db_path) as conn:
            rows = db.list_usage(
                conn, claimed["deviceId"], None, "2026-09-24T11:00:00Z", "2026-09-24T11:00:00Z"
            )
        self.assertEqual(rows[0]["uplinkBytes"], 800_000_000)


class CanonicalPathTests(unittest.TestCase):
    """nginx matches the normalised path but forwards the raw one."""

    @classmethod
    def setUpClass(cls):
        cls._tmp = tempfile.TemporaryDirectory()
        cls.settings = make_settings(os.path.join(cls._tmp.name, "myproxy.db"))
        cls.service = MyProxyService(cls.settings, FakeXuiAdapter())
        cls.service.initialize()
        cls.httpd = make_server(cls.service, cls.settings)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        stop_server(cls.httpd, cls.thread)
        cls._tmp.cleanup()

    def _status(self, method, path, token=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            # putrequest sends the path verbatim: no client-side normalisation.
            conn.putrequest(method, path, skip_accept_encoding=True)
            if token:
                conn.putheader("Authorization", "Bearer " + token)
            conn.putheader("Content-Length", "0")
            conn.endheaders()
            response = conn.getresponse()
            response.read()
            return response.status
        finally:
            conn.close()

    def test_dot_segments_never_reach_the_admin_branch(self):
        for path in (
            "/api/admin/../device/claim",
            "/api/admin/%2E%2E/device/claim",
            "/api/admin/user/%2E%2E%2F..%2Fdevice%2Fclaim",
            "/api/device/./claim",
            "/api//device/claim",
            "/api/device%5Cclaim",
        ):
            with self.subTest(path=path):
                self.assertEqual(self._status("POST", path), 404)
                self.assertEqual(
                    self._status("POST", path, token="test-admin-token"),
                    404,
                    "a valid admin token must not tell 404 from 401 apart either",
                )

    def test_canonical_paths_still_route(self):
        self.assertEqual(self._status("GET", "/healthz"), 200)
        self.assertEqual(self._status("GET", "/admin"), 200)
        self.assertEqual(self._status("GET", "/admin/"), 200)
        self.assertEqual(self._status("GET", "/api/admin/user"), 401)


if __name__ == "__main__":
    unittest.main()
