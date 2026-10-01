"""Observability Plane tests: bucketing, counter deltas, privacy limits, API.

The privacy limits get as much coverage as the arithmetic on purpose.  A
regression in "how many bytes" is a wrong number; a regression in "what this
plane is allowed to know" is a different product.
"""

import json
import os
import sys
import tempfile
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from myproxy_server import db, observability  # noqa: E402
from myproxy_server.app import MyProxyService, ServiceError  # noqa: E402
from myproxy_server.server import make_server  # noqa: E402

from test_api import FakeXuiAdapter, make_settings, stop_server  # noqa: E402


class CountingXui(FakeXuiAdapter):
    """FakeXuiAdapter whose per-client counters can be driven by a test."""

    def __init__(self):
        super().__init__()
        self.counters = {}
        self.list_calls = 0
        self.fail = False
        # (entered, release) events: when set, list_clients signals that it
        # was reached and then waits, so a test can overlap two sweeps.
        self.gate = None

    def set_counters(self, email, up, down):
        self.counters[email] = (up, down)

    def list_clients(self):
        self.list_calls += 1
        if self.gate is not None:
            entered, release = self.gate
            entered.set()
            release.wait(5)
        if self.fail:
            from myproxy_server.xui import XuiError

            raise XuiError("XuiUnavailable")
        clients = super().list_clients()
        for client in clients:
            up, down = self.counters.get(client["email"], (0, 0))
            client["up"] = up
            client["down"] = down
            client["total"] = up + down
        return clients


class BucketTest(unittest.TestCase):
    def test_hour_and_day_buckets(self):
        self.assertEqual(
            observability.hour_bucket("2026-09-20T13:47:11Z"), "2026-09-20T13:00:00Z"
        )
        self.assertEqual(
            observability.day_bucket("2026-09-20T13:47:11Z"), "2026-09-20T00:00:00Z"
        )

    def test_malformed_timestamps_are_rejected(self):
        for bad in ("2026-09-20 13:00:00", "2026-09-20T13:00:00", "", None, 5):
            with self.subTest(bad):
                with self.assertRaises(observability.ObservabilityError):
                    observability.hour_bucket(bad)

    def test_range_keeps_empty_buckets(self):
        labels = observability.bucket_range(
            "2026-09-20T10:15:00Z", "2026-09-20T13:00:00Z", "hour"
        )
        self.assertEqual(
            labels,
            [
                "2026-09-20T10:00:00Z",
                "2026-09-20T11:00:00Z",
                "2026-09-20T12:00:00Z",
                "2026-09-20T13:00:00Z",
            ],
        )

    def test_reversed_and_oversized_ranges_are_rejected(self):
        with self.assertRaises(observability.ObservabilityError):
            observability.bucket_range(
                "2026-09-20T13:00:00Z", "2026-09-20T10:00:00Z", "hour"
            )
        with self.assertRaises(observability.ObservabilityError):
            observability.bucket_range(
                "2020-01-01T00:00:00Z", "2026-09-20T00:00:00Z", "hour"
            )
        with self.assertRaises(observability.ObservabilityError):
            observability.bucket_range(
                "2026-09-20T10:00:00Z", "2026-09-20T13:00:00Z", "minute"
            )

    def test_series_folds_hours_into_days_without_losing_holes(self):
        rows = [
            {"bucketStart": "2026-09-20T01:00:00Z", "uplinkBytes": 10, "downlinkBytes": 1},
            {"bucketStart": "2026-09-20T05:00:00Z", "uplinkBytes": 5, "downlinkBytes": 2},
        ]
        labels = observability.bucket_range(
            "2026-09-19T00:00:00Z", "2026-09-20T00:00:00Z", "day"
        )
        series = observability.build_series(rows, labels, "day")
        self.assertEqual(len(series), 2)
        self.assertEqual(series[0]["totalBytes"], 0)  # the hole is still reported
        self.assertEqual(series[1]["uplinkBytes"], 15)
        self.assertEqual(series[1]["totalBytes"], 18)

    def test_rows_outside_the_window_are_dropped_not_folded_into_an_edge(self):
        rows = [
            {"bucketStart": "2026-01-01T00:00:00Z", "uplinkBytes": 999, "downlinkBytes": 0}
        ]
        labels = observability.bucket_range(
            "2026-09-20T10:00:00Z", "2026-09-20T11:00:00Z", "hour"
        )
        series = observability.build_series(rows, labels, "hour")
        self.assertEqual([item["totalBytes"] for item in series], [0, 0])


class CounterDeltaTest(unittest.TestCase):
    def test_normal_progress(self):
        self.assertEqual(observability.counter_delta(100, 250), 150)

    def test_counter_reset_counts_the_new_value_not_a_negative(self):
        # x-ui's counters restart from zero whenever Xray restarts.  Treating
        # the reading as the delta neither invents traffic nor loses what has
        # accumulated since the restart.
        self.assertEqual(observability.counter_delta(1000, 40), 40)

    def test_first_ever_reading_counts_in_full(self):
        self.assertEqual(observability.counter_delta(0, 4096), 4096)

    def test_garbage_readings_do_not_produce_traffic(self):
        for previous, current in ((0, -5), (0, None), (0, True), (0, "12")):
            with self.subTest(current=current):
                self.assertEqual(observability.counter_delta(previous, current), 0)


class PrivacyLimitTest(unittest.TestCase):
    def test_ip_is_truncated_never_stored_whole(self):
        self.assertEqual(observability.ip_prefix("203.0.113.47"), "203.0.113.0/24")
        self.assertEqual(
            observability.ip_prefix("2001:db8:abcd:1234::1"), "2001:db8:abcd::/48"
        )

    def test_loopback_and_junk_yield_no_location(self):
        for value in ("127.0.0.1", "::1", "0.0.0.0", "not-an-ip", "", None):
            with self.subTest(value):
                self.assertEqual(observability.ip_prefix(value), "")

    def test_unknown_service_category_is_rejected_not_rebucketed(self):
        # Silently folding an unknown category into "other" would hide a
        # version skew behind plausible-looking numbers.
        with self.assertRaises(observability.ObservabilityError):
            observability.normalise_categories({"youtube.com": 10})
        with self.assertRaises(observability.ObservabilityError):
            observability.normalise_categories({"torrent": 10})

    def test_known_categories_pass_through(self):
        parsed = observability.normalise_categories({"video": 10, "web": 20})
        self.assertEqual(parsed, {"video": 10, "web": 20})

    def test_byte_counts_must_be_plausible_integers(self):
        for bad in (-1, True, "10", 1.5, observability.MAX_REPORTED_BYTES_PER_BUCKET + 1):
            with self.subTest(bad):
                with self.assertRaises(observability.ObservabilityError):
                    observability.clamp_reported_bytes(bad)

    def test_geo_validation_rejects_unknown_fields_and_long_values(self):
        self.assertEqual(
            observability.validate_geo({"country": "AU", "city": "Sydney"}),
            {"country": "AU", "city": "Sydney"},
        )
        with self.assertRaises(observability.ObservabilityError):
            observability.validate_geo({"latitude": "-33.86"})
        with self.assertRaises(observability.ObservabilityError):
            observability.validate_geo({"city": "x" * 100})


class ActivityStateTest(unittest.TestCase):
    NOW = "2026-09-20T12:00:00Z"

    def test_states_by_age(self):
        cases = {
            "2026-09-20T11:59:00Z": "connected",
            "2026-09-20T11:57:00Z": "connected",
            "2026-09-20T11:50:00Z": "idle",
            "2026-09-20T11:05:00Z": "idle",
            "2026-09-20T10:00:00Z": "offline",
            "": "offline",
            None: "offline",
        }
        for last_seen, expected in cases.items():
            with self.subTest(last_seen):
                self.assertEqual(
                    observability.activity_state(last_seen, self.NOW), expected
                )

    def test_a_future_timestamp_is_treated_as_just_seen(self):
        self.assertEqual(
            observability.activity_state("2026-09-20T12:05:00Z", self.NOW), "connected"
        )


class ObservabilityApiTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory()
        cls.db_path = os.path.join(cls._tmpdir.name, "obs.db")
        cls.admin_token = "test-admin-token"
        cls.xui = CountingXui()
        cls.settings = make_settings(
            cls.db_path,
            claim_max_per_ip=10000,
            usage_ingest_interval_seconds=60,
        )
        cls.service = MyProxyService(cls.settings, cls.xui)
        cls.service.initialize()
        cls.httpd = make_server(cls.service, cls.settings)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        stop_server(cls.httpd, cls.thread)
        cls._tmpdir.cleanup()

    @classmethod
    def _api(cls, method, path, body=None, token=None):
        from test_api import ApiTest

        ApiTest.port = cls.port
        return ApiTest._api(method, path, body, token)

    @classmethod
    def _admin(cls, method, path, body=None):
        return cls._api(method, path, body, cls.admin_token)

    def _device(self, method, path, body=None):
        return self._api(method, path, body, self.device_token)

    def setUp(self):
        self.user = self._admin(
            "POST", "/api/admin/user", {"username": f"u{os.urandom(6).hex()}"}
        )[1]
        binding = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": self.user["id"], "deviceTemplate": "windows"},
        )[1]
        claim = self._api(
            "POST",
            "/api/device/claim",
            {
                "pairingCode": binding["code"],
                "deviceName": f"OBS-{os.urandom(4).hex()}",
                "platform": "windows",
                "clientVersion": "1.0.0",
            },
        )[1]
        self.device_token = claim["deviceToken"]
        self.device_id = claim["deviceId"]
        _, devices = self._admin("GET", f"/api/admin/device?userId={self.user['id']}")
        self.email = devices["devices"][0]["xrayClientEmail"]

    # -- ingestion -----------------------------------------------------
    def test_xui_sweep_records_deltas_and_survives_a_counter_reset(self):
        now = "2026-09-20T12:00:00Z"
        self.xui.set_counters(self.email, 1000, 2000)
        self.service._sweep_xui_counters(now, force=True)
        self.xui.set_counters(self.email, 1500, 2500)
        self.service._sweep_xui_counters(now, force=True)
        # Xray restarted: counters fall back to a small value.
        self.xui.set_counters(self.email, 30, 40)
        self.service._sweep_xui_counters(now, force=True)

        with db.connect(self.db_path) as conn:
            rows = db.list_usage(conn, self.device_id, None, now, now)
        self.assertEqual(len(rows), 1)
        # 1000 + 500 + 30 up, 2000 + 500 + 40 down.
        self.assertEqual(rows[0]["uplinkBytes"], 1530)
        self.assertEqual(rows[0]["downlinkBytes"], 2540)

    def test_sweep_is_throttled_between_heartbeats(self):
        before = self.xui.list_calls
        for _ in range(5):
            self._device("POST", "/api/device/heartbeat", {})
        # One sweep at most, even though five heartbeats arrived.
        self.assertLessEqual(self.xui.list_calls - before, 1)

    def test_sweep_failure_never_breaks_a_heartbeat(self):
        self.xui.fail = True
        self.addCleanup(setattr, self.xui, "fail", False)
        status, payload = self._device("POST", "/api/device/heartbeat", {})
        self.assertEqual(status, 200, payload)
        self.assertTrue(payload["ok"])

    def test_a_deleted_device_does_not_come_back_through_its_disabled_client(self):
        now = "2026-09-20T13:00:00Z"
        self.xui.set_counters(self.email, 10_000, 20_000)
        self.service._sweep_xui_counters(now, force=True)
        self.service.admin_delete_device(self.device_id)
        # Deleting disables the x-ui client rather than removing it, so its
        # cumulative counters are still listed by the next sweep.  They used
        # to be read against the cleared cursor and re-added whole.
        self.service._sweep_xui_counters(now, force=True)

        with db.connect(self.db_path) as conn:
            rows = db.list_usage(conn, self.device_id, None, now, now)
        self.assertEqual(rows, [])

    def test_a_failed_sweep_still_throttles_the_next_heartbeats(self):
        # While the helper is unwell every heartbeat used to retry the sweep
        # and wait out its timeout.  The throttle now counts attempts.
        self.xui.fail = True
        self.addCleanup(setattr, self.xui, "fail", False)
        before = self.xui.list_calls
        self.service._sweep_xui_counters("2026-09-20T14:00:00Z")
        self.service._sweep_xui_counters("2026-09-20T14:00:10Z")
        self.assertEqual(self.xui.list_calls - before, 1)

    def test_overlapping_sweeps_count_a_delta_once(self):
        start = "2026-09-20T15:00:00Z"
        self.xui.set_counters(self.email, 0, 0)
        self.service._sweep_xui_counters(start, force=True)
        self.xui.set_counters(self.email, 1000, 0)

        entered, release = threading.Event(), threading.Event()
        self.xui.gate = (entered, release)
        self.addCleanup(setattr, self.xui, "gate", None)
        later = "2026-09-20T16:00:00Z"
        first = threading.Thread(
            target=self.service._sweep_xui_counters, args=(later,), daemon=True
        )
        first.start()
        self.assertTrue(entered.wait(5))
        # A second heartbeat arrives while the first sweep waits on x-ui.  Both
        # used to read the same cursor and each add the 1000 bytes.
        self.xui.gate = None
        self.assertEqual(self.service._sweep_xui_counters(later), 0)
        release.set()
        first.join(5)

        with db.connect(self.db_path) as conn:
            rows = db.list_usage(conn, self.device_id, None, later, later)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["uplinkBytes"], 1000)

    def test_user_usage_lists_only_that_users_categories(self):
        self._device("POST", "/api/device/usage", {"categories": {"video": 5000}})
        other_user = self._admin(
            "POST", "/api/admin/user", {"username": f"u{os.urandom(6).hex()}"}
        )[1]
        binding = self._admin(
            "POST",
            "/api/admin/binding",
            {"userId": other_user["id"], "deviceTemplate": "windows"},
        )[1]
        other_token = self._api(
            "POST",
            "/api/device/claim",
            {
                "pairingCode": binding["code"],
                "deviceName": f"OBS-{os.urandom(4).hex()}",
                "platform": "windows",
                "clientVersion": "1.0.0",
            },
        )[1]["deviceToken"]
        self._api(
            "POST", "/api/device/usage", {"categories": {"messaging": 1000}}, other_token
        )

        _, usage = self._admin("GET", f"/api/admin/usage?userId={self.user['id']}")
        categories = {item["category"] for item in usage["categories"]}
        self.assertIn("video", categories)
        self.assertNotIn("messaging", categories)

    # -- device reporting ----------------------------------------------
    def test_device_reports_categories(self):
        status, payload = self._device(
            "POST",
            "/api/device/usage",
            {"categories": {"video": 5000, "web": 1000}},
        )
        self.assertEqual(status, 200, payload)
        _, usage = self._admin("GET", f"/api/admin/usage?deviceId={self.device_id}")
        categories = {item["category"]: item["totalBytes"] for item in usage["categories"]}
        self.assertEqual(categories.get("video"), 5000)
        self.assertEqual(categories.get("web"), 1000)

    def test_device_cannot_report_an_unknown_category(self):
        status, payload = self._device(
            "POST", "/api/device/usage", {"categories": {"tiktok.com": 10}}
        )
        self.assertEqual(status, 400)
        self.assertEqual(payload["error"]["code"], "BadRequest")

    def test_device_cannot_move_the_total_only_the_attribution(self):
        # Totals come from the x-ui sweep, which measures the data plane.  A
        # compromised device must not be able to report its usage as zero, or
        # as anything else.
        status, _ = self._device(
            "POST",
            "/api/device/usage",
            {"uplinkBytes": 999999, "downlinkBytes": 999999},
        )
        self.assertEqual(status, 200)
        _, usage = self._admin("GET", f"/api/admin/usage?deviceId={self.device_id}")
        self.assertEqual(usage["totals"]["totalBytes"], 0)

    def test_device_cannot_backfill_history(self):
        # Otherwise one device could rewrite a month of somebody's trend.
        status, _ = self._device(
            "POST",
            "/api/device/usage",
            {"bucketStart": "2026-01-01T00:00:00Z", "categories": {"web": 10}},
        )
        self.assertEqual(status, 400)

    def test_usage_report_requires_a_device_token(self):
        for token in (None, "nope"):
            with self.subTest(token=token):
                status, payload = self._api(
                    "POST", "/api/device/usage", {"categories": {"web": 1}}, token
                )
                self.assertEqual(status, 401)
                self.assertEqual(payload["error"]["code"], "TokenInvalid")

    def test_report_updates_activity_but_loopback_is_not_an_address(self):
        self._device("POST", "/api/device/usage", {"categories": {"web": 1}})
        _, activity = self._admin(
            "GET", f"/api/admin/device/{self.device_id}/activity"
        )
        self.assertEqual(activity["state"], "connected")
        # The address *is* stored now (see ClientAddressTest), but this
        # request came over loopback with no trusted proxy in front of it, so
        # there is no client address to store.  Recording 127.0.0.1 would make
        # "no address yet" indistinguishable from a device that really does
        # connect from the local machine.
        self.assertEqual(activity["location"]["ipAddress"], "")
        self.assertEqual(activity["location"]["ipPrefix"], "")
        self.assertEqual(activity["recentAddresses"], [])
        with db.connect(self.db_path) as conn:
            row = db.get_activity(conn, self.device_id)
        self.assertNotIn("127.0.0.1", str(row))

    # -- queries -------------------------------------------------------
    def test_usage_series_reports_empty_buckets(self):
        _, usage = self._admin(
            "GET",
            f"/api/admin/usage?deviceId={self.device_id}"
            "&start=2026-09-20T10:00:00Z&end=2026-09-20T13:00:00Z&granularity=hour",
        )
        self.assertEqual(len(usage["series"]), 4)
        self.assertEqual(usage["granularity"], "hour")
        self.assertEqual(usage["retentionDays"], 90)

    def test_daily_granularity_is_summed_from_hourly_rows(self):
        now = "2026-09-20T09:00:00Z"
        with db.connect(self.db_path) as conn:
            conn.execute("BEGIN IMMEDIATE")
            db.add_usage(conn, self.device_id, self.user["id"], now, 100, 200, now)
            db.add_usage(
                conn,
                self.device_id,
                self.user["id"],
                "2026-09-20T15:00:00Z",
                1,
                2,
                now,
            )
        _, usage = self._admin(
            "GET",
            f"/api/admin/usage?deviceId={self.device_id}"
            "&start=2026-09-20T00:00:00Z&end=2026-09-20T00:00:00Z&granularity=day",
        )
        self.assertEqual(len(usage["series"]), 1)
        self.assertEqual(usage["series"][0]["uplinkBytes"], 101)
        self.assertEqual(usage["totals"]["totalBytes"], 303)

    def test_usage_rejects_a_bad_window(self):
        for query in (
            "start=nonsense&end=2026-09-20T13:00:00Z",
            "start=2026-09-20T13:00:00Z&end=2026-09-20T10:00:00Z",
            "granularity=minute",
        ):
            with self.subTest(query):
                status, _ = self._admin("GET", f"/api/admin/usage?{query}")
                self.assertEqual(status, 400)

    def test_usage_for_an_unknown_device_is_404(self):
        status, _ = self._admin("GET", "/api/admin/usage?deviceId=dev_missing")
        self.assertEqual(status, 404)

    def test_activity_listing_covers_every_device(self):
        _, listing = self._admin("GET", f"/api/admin/activity?userId={self.user['id']}")
        ids = {item["deviceId"] for item in listing["devices"]}
        self.assertIn(self.device_id, ids)
        for item in listing["devices"]:
            self.assertIn(item["state"], observability.ACTIVITY_STATES)

    # -- operator-supplied location ------------------------------------
    def test_admin_can_set_and_clear_coarse_location(self):
        status, payload = self._admin(
            "POST",
            f"/api/admin/device/{self.device_id}/geo",
            {"country": "AU", "city": "Sydney", "isp": "Example Telecom"},
        )
        self.assertEqual(status, 200, payload)
        self.assertEqual(payload["location"]["country"], "AU")
        self.assertEqual(payload["location"]["city"], "Sydney")

        _, cleared = self._admin(
            "POST", f"/api/admin/device/{self.device_id}/geo", {"city": ""}
        )
        self.assertEqual(cleared["location"]["city"], "")
        self.assertEqual(cleared["location"]["country"], "AU")

    def test_geo_rejects_fields_outside_the_coarse_set(self):
        status, _ = self._admin(
            "POST",
            f"/api/admin/device/{self.device_id}/geo",
            {"latitude": "-33.8688"},
        )
        self.assertEqual(status, 400)

    def test_geo_on_an_unknown_device_is_404(self):
        status, _ = self._admin(
            "POST", "/api/admin/device/dev_missing/geo", {"country": "AU"}
        )
        self.assertEqual(status, 404)

    # -- authorisation --------------------------------------------------
    def test_observability_admin_routes_reject_a_device_token(self):
        for method, path, body in (
            ("GET", "/api/admin/usage", None),
            ("GET", "/api/admin/activity", None),
            ("POST", "/api/admin/usage/refresh", {}),
            ("GET", f"/api/admin/device/{self.device_id}/activity", None),
            ("POST", f"/api/admin/device/{self.device_id}/geo", {"country": "AU"}),
        ):
            with self.subTest(path):
                status, payload = self._api(method, path, body, self.device_token)
                self.assertEqual(status, 401)
                self.assertEqual(payload["error"]["code"], "AdminUnauthorized")

    def test_a_device_cannot_read_another_devices_usage(self):
        # The Device API has no usage read route at all; this pins that.
        status, _ = self._device("GET", "/api/device/usage")
        self.assertEqual(status, 404)


class RetentionTest(unittest.TestCase):
    def test_pruning_drops_rows_past_the_cutoff(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "r.db")
            db.init_db(path)
            now = "2026-09-20T12:00:00Z"
            with db.connect(path) as conn:
                conn.execute("BEGIN IMMEDIATE")
                db.add_usage(conn, "dev_a", "usr_a", "2026-01-01T00:00:00Z", 1, 1, now)
                db.add_usage(conn, "dev_a", "usr_a", now, 2, 2, now)
                db.add_usage_category(
                    conn, "dev_a", "2026-01-01T00:00:00Z", "web", 5, now
                )
            cutoff = observability.retention_cutoff(now, 90)
            with db.connect(path) as conn:
                conn.execute("BEGIN IMMEDIATE")
                removed = db.prune_usage(conn, cutoff)
            self.assertEqual(removed, 1)
            with db.connect(path) as conn:
                rows = db.list_usage(
                    conn, "dev_a", None, "2020-01-01T00:00:00Z", now
                )
                categories = db.list_usage_categories(
                    conn, "dev_a", "2020-01-01T00:00:00Z", now
                )
            self.assertEqual(len(rows), 1)
            self.assertEqual(categories, [])

    def test_deleting_a_device_forgets_its_usage(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "f.db")
            db.init_db(path)
            now = "2026-09-20T12:00:00Z"
            with db.connect(path) as conn:
                conn.execute("BEGIN IMMEDIATE")
                db.add_usage(conn, "dev_a", "usr_a", now, 10, 10, now)
                db.add_usage_category(conn, "dev_a", now, "video", 10, now)
                db.set_usage_cursor(conn, "dev_a", 10, 10, now)
                db.upsert_activity(conn, "dev_a", "usr_a", now, "203.0.113.0/24")
            with db.connect(path) as conn:
                conn.execute("BEGIN IMMEDIATE")
                db.clear_observability_for_device(conn, "dev_a")
            with db.connect(path) as conn:
                self.assertEqual(
                    db.list_usage(conn, "dev_a", None, "2020-01-01T00:00:00Z", now), []
                )
                self.assertIsNone(db.get_usage_cursor(conn, "dev_a"))
                self.assertIsNone(db.get_activity(conn, "dev_a"))


class ClientAddressTest(unittest.TestCase):
    """The operator can see a device's real egress address, and its history.

    These drive the service directly rather than over HTTP because a loopback
    request has no client address to record.  The step these skip -- turning a
    request into ``client_ip`` -- is ``server._claim_source_ip``, which only
    honours ``X-Forwarded-For`` from a trusted proxy and has its own coverage
    in ``test_security_regressions``.
    """

    def _service(self, **overrides):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        settings = make_settings(os.path.join(tmp.name, "addr.db"), **overrides)
        service = MyProxyService(settings, FakeXuiAdapter())
        service.initialize()
        return service

    def _device(self, service):
        user = service.admin_create_user(f"u{os.urandom(6).hex()}")
        binding = service.admin_create_binding(user["id"], "windows")
        claim = service.claim(binding["code"], "ADDR-PC", "windows", "1.0.0")
        return claim["deviceToken"], claim["deviceId"]

    # -- the current address -------------------------------------------
    def test_the_full_address_is_stored_next_to_its_prefix(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.47")

        location = service.admin_device_activity(device_id)["location"]
        self.assertEqual(location["ipAddress"], "203.0.113.47")
        # The prefix is derived from the address rather than replacing it, so
        # the coarse location view keeps working unchanged.
        self.assertEqual(location["ipPrefix"], "203.0.113.0/24")

    def test_a_request_without_an_address_does_not_blank_the_stored_one(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.47")
        # A heartbeat that arrives without a trusted forwarding header must
        # not erase what the server already knew; otherwise one misrouted
        # request wipes the evidence an operator is looking at.
        for blank in ("", "127.0.0.1", "::1", "not-an-ip", None):
            with self.subTest(blank):
                service.heartbeat(token, client_ip=blank or "")
                location = service.admin_device_activity(device_id)["location"]
                self.assertEqual(location["ipAddress"], "203.0.113.47")

    def test_a_new_address_replaces_the_current_one(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.47")
        service.heartbeat(token, client_ip="198.51.100.9")

        location = service.admin_device_activity(device_id)["location"]
        self.assertEqual(location["ipAddress"], "198.51.100.9")
        self.assertEqual(location["ipPrefix"], "198.51.100.0/24")

    # -- the history ---------------------------------------------------
    def test_history_keeps_the_most_recently_used_addresses(self):
        service = self._service(device_address_history=3)
        token, device_id = self._device(service)
        for index in range(5):
            service.heartbeat(
                token,
                client_ip=f"203.0.113.{index}",
                now=f"2026-09-20T0{index}:00:00Z",
            )

        detail = service.admin_device_activity(device_id)
        self.assertEqual(detail["addressHistoryLimit"], 3)
        # Newest first, and the two oldest are gone.
        self.assertEqual(
            [entry["address"] for entry in detail["recentAddresses"]],
            ["203.0.113.4", "203.0.113.3", "203.0.113.2"],
        )

    def test_eviction_is_by_last_use_not_by_first(self):
        # A device that has been on the same connection for a year must not
        # pin the whole history: coming back to an old address makes it recent
        # again.
        service = self._service(device_address_history=2)
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.1", now="2026-09-20T01:00:00Z")
        service.heartbeat(token, client_ip="203.0.113.2", now="2026-09-20T02:00:00Z")
        service.heartbeat(token, client_ip="203.0.113.1", now="2026-09-20T03:00:00Z")
        service.heartbeat(token, client_ip="203.0.113.3", now="2026-09-20T04:00:00Z")

        detail = service.admin_device_activity(device_id)
        self.assertEqual(
            [entry["address"] for entry in detail["recentAddresses"]],
            ["203.0.113.3", "203.0.113.1"],
        )

    def test_returning_to_an_address_moves_last_seen_and_keeps_first_seen(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.7", now="2026-09-20T01:00:00Z")
        service.heartbeat(token, client_ip="203.0.113.7", now="2026-09-20T05:00:00Z")

        entries = service.admin_device_activity(device_id)["recentAddresses"]
        # One row, not two: a month on one connection is one line in the
        # history, which is what makes a capped history readable.
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["firstSeenAt"], "2026-09-20T01:00:00Z")
        self.assertEqual(entries[0]["lastSeenAt"], "2026-09-20T05:00:00Z")

    def test_ipv6_is_compressed_before_storage(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="2001:0db8:abcd:1234:0000:0000:0000:0001")
        service.heartbeat(token, client_ip="2001:db8:abcd:1234::1")

        detail = service.admin_device_activity(device_id)
        # The same client written two ways is one address, not two.
        self.assertEqual(len(detail["recentAddresses"]), 1)
        self.assertEqual(detail["location"]["ipAddress"], "2001:db8:abcd:1234::1")
        self.assertEqual(detail["location"]["ipPrefix"], "2001:db8:abcd::/48")

    def test_a_zero_limit_keeps_the_current_address_and_no_history(self):
        service = self._service(device_address_history=0)
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.1")
        service.heartbeat(token, client_ip="203.0.113.2")

        detail = service.admin_device_activity(device_id)
        self.assertEqual(detail["location"]["ipAddress"], "203.0.113.2")
        self.assertEqual(detail["recentAddresses"], [])

    # -- boundaries ----------------------------------------------------
    def test_the_device_api_never_hands_back_an_address(self):
        # Addresses leave the server through the Admin API only.  A device
        # that could read them back would be able to locate its user's other
        # devices, which is the Admin/Device boundary, not a privacy nicety.
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.47")

        payloads = [
            service.heartbeat(token, client_ip="203.0.113.47"),
            service.get_device_config(token),
            service.device_report_usage(token, {"categories": {"web": 1}},
                                        client_ip="203.0.113.47"),
        ]
        for payload in payloads:
            rendered = json.dumps(payload)
            # Guard against the assertions below passing on an empty payload.
            self.assertGreater(len(rendered), 20, rendered)
            self.assertNotIn("203.0.113", rendered)
            self.assertNotIn("ipAddress", rendered)
            self.assertNotIn("recentAddresses", rendered)

    def test_deleting_a_device_forgets_its_addresses(self):
        service = self._service()
        token, device_id = self._device(service)
        service.heartbeat(token, client_ip="203.0.113.47")
        service.admin_delete_device(device_id)

        with db.connect(service.settings.db_path) as conn:
            self.assertEqual(db.list_device_addresses(conn, device_id), [])
            self.assertIsNone(db.get_activity(conn, device_id))

    # -- migration -----------------------------------------------------
    def test_an_existing_activity_table_gains_the_column_without_losing_rows(self):
        # init_db is the whole migration story, and CREATE TABLE IF NOT EXISTS
        # does not add a column to a table that already exists.
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "old.db")
            db.init_db(path)
            with db.connect(path) as conn:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute("DROP TABLE device_addresses")
                conn.execute("DROP TABLE device_activity")
                # The table exactly as it shipped before this change.
                conn.execute(
                    "CREATE TABLE device_activity ("
                    " device_id TEXT PRIMARY KEY, user_id TEXT NOT NULL,"
                    " last_seen_at TEXT, ip_prefix TEXT NOT NULL DEFAULT '',"
                    " country TEXT NOT NULL DEFAULT '',"
                    " region TEXT NOT NULL DEFAULT '',"
                    " city TEXT NOT NULL DEFAULT '',"
                    " asn TEXT NOT NULL DEFAULT '', isp TEXT NOT NULL DEFAULT '',"
                    " updated_at TEXT NOT NULL)"
                )
                conn.execute(
                    "INSERT INTO device_activity(device_id, user_id, last_seen_at,"
                    " ip_prefix, updated_at) VALUES (?, ?, ?, ?, ?)",
                    (
                        "dev_old",
                        "usr_old",
                        "2026-09-01T00:00:00Z",
                        "203.0.113.0/24",
                        "2026-09-01T00:00:00Z",
                    ),
                )

            db.init_db(path)
            db.init_db(path)  # idempotent

            with db.connect(path) as conn:
                row = db.get_activity(conn, "dev_old")
            self.assertIsNotNone(row)
            # The prefix survives; the address is empty because the server
            # genuinely never recorded one for this row.
            self.assertEqual(row["ipPrefix"], "203.0.113.0/24")
            self.assertEqual(row["ipAddress"], "")
            self.assertTrue(db.check_readiness(path))


class UnconfiguredServiceTest(unittest.TestCase):
    def test_service_rejects_a_bad_geo_payload_before_touching_the_database(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "g.db")
            settings = make_settings(path)
            service = MyProxyService(settings, FakeXuiAdapter())
            service.initialize()
            with self.assertRaises(ServiceError) as ctx:
                service.admin_set_device_geo("dev_missing", {"nope": "x"})
            self.assertEqual(ctx.exception.code, "BadRequest")


if __name__ == "__main__":
    unittest.main()
