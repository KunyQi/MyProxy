from __future__ import annotations

import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock


DEPLOY_DIR = Path(__file__).resolve().parents[1] / "deploy"
SCRIPT_PATH = DEPLOY_DIR / "predeploy_private.py"
SPEC = importlib.util.spec_from_file_location("predeploy_private", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def _safe_audit_output() -> str:
    values = {key: "no" for key in MODULE.AUDIT_KEYS}
    values.update(
        {
            "audit_version": "1",
            "remote_user": "root",
            "python3": "ok",
            "curl": "ok",
            "ss": "ok",
            "systemctl": "ok",
            "tls_cert": "present",
            "tls_key": "present",
            "xui_db": "present",
            "admin_dir": "valid-secure",
            "admin_env": "valid-secure",
            "xui_helper_env": "valid-secure",
            "myproxy_active": "yes",
            "myproxy_enabled": "no",
            "xui_helper_active": "yes",
            "xui_helper_enabled": "yes",
            "listener_1820": "loopback",
            "public_listener": "no",
            "nginx_myproxy_config": "absent",
            "nginx_upstream_1820": "no",
            "nginx_public_listen": "no",
            "ufw_state": "active-no-public",
        }
    )
    return "\n".join(f"{key}={values[key]}" for key in sorted(values)) + "\n"


def _safe_ssh_response(_host, _key, script, **_kwargs) -> str:
    if script == MODULE.REMOTE_ADMIN_UI_AUDIT_SCRIPT:
        return "admin_ui=valid\n"
    return _safe_audit_output()


class _FakeStdin:
    def __init__(self) -> None:
        self.data = b""

    def write(self, value: bytes) -> int:
        self.data += value
        return len(value)

    def close(self) -> None:
        return None


class _FakeProcess:
    def __init__(self) -> None:
        self.stdin = _FakeStdin()
        self.returncode: int | None = None

    def poll(self) -> int | None:
        return self.returncode

    def communicate(self, timeout: float | None = None):
        del timeout
        return b"transaction output\n", b""

    def kill(self) -> None:
        self.returncode = -9


class PrivatePredeployTests(unittest.TestCase):
    def test_embedded_remote_scripts_have_valid_bash_syntax(self) -> None:
        bash = shutil.which("bash")
        if bash is None:
            windows_git_bash = Path(r"C:\Program Files\Git\bin\bash.exe")
            if windows_git_bash.is_file():
                bash = str(windows_git_bash)
        if bash is None:
            self.skipTest("bash is unavailable")
        for name in (
            "REMOTE_AUDIT_SCRIPT",
            "REMOTE_CREATE_TMP_SCRIPT",
            "REMOTE_PREPARE_UPLOAD_SCRIPT",
            "REMOTE_FINALIZE_SECRET_UPLOAD_SCRIPT",
            "REMOTE_CLEANUP_TMP_SCRIPT",
            "REMOTE_TRANSACTION_STATE_SCRIPT",
            "REMOTE_TRANSACTION_SIGNAL_SCRIPT",
            "REMOTE_EMERGENCY_STOP_SCRIPT",
            "REMOTE_ADMIN_UI_AUDIT_SCRIPT",
            "REMOTE_DEPLOY_SCRIPT",
        ):
            with self.subTest(name=name):
                result = subprocess.run(
                    [bash, "-n"],
                    input=getattr(MODULE, name),
                    text=True,
                    encoding="utf-8",
                    capture_output=True,
                    check=False,
                )
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_private_deploy_never_mutates_nginx_or_ufw(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        forbidden = (
            "systemctl reload nginx",
            "systemctl restart nginx",
            "systemctl enable nginx",
            "systemctl disable nginx",
            "ufw allow",
            "ufw delete",
            "ufw --force",
            "install-device-api-proxy",
            "nginx-device-api.conf.template",
        )
        for value in forbidden:
            with self.subTest(value=value):
                self.assertNotIn(value, script)
        self.assertIn("nginx -T", script)
        self.assertIn("LC_ALL=C ufw status", script)
        self.assertIn("0*1820", script)

    def test_private_deploy_requires_exact_loopback_listener(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        self.assertIn('Environment=MYPROXY_LISTEN_HOST=127.0.0.1', script)
        self.assertIn('Environment=MYPROXY_LISTEN_PORT=1820', script)
        self.assertIn('$4 != "127.0.0.1:1820"', script)
        self.assertIn("https://127.0.0.1:1820/healthz", script)
        self.assertIn("https://127.0.0.1:1820/readyz", script)
        self.assertIn("https://127.0.0.1:1820/admin/", script)
        self.assertIn("https://127.0.0.1:1820/admin/app.css", script)
        self.assertIn("https://127.0.0.1:1820/admin/app.js", script)
        self.assertIn("https://127.0.0.1:1820/admin/release-signing.js", script)
        self.assertIn("Content-Security-Policy", script)
        self.assertIn("[ \"$admin_status\" != 401 ]", script)
        self.assertIn("capture_public_listeners", script)

    def test_remote_transaction_waits_for_local_commit_or_rolls_back(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        ready = script.index('"$REMOTE_TMP/.ready"')
        wait_for_commit = script.index('"$REMOTE_TMP/.commit"')
        wait_for_rollback = script.index('"$REMOTE_TMP/.rollback"')
        committed = script.rindex("DEPLOY_COMMITTED=1")
        self.assertLess(ready, wait_for_commit)
        self.assertLess(wait_for_rollback, wait_for_commit)
        self.assertLess(wait_for_commit, committed)
        self.assertIn('"$REMOTE_TMP/.rollback"', script)
        self.assertIn("等待本机提交确认超时，执行回滚", script)
        self.assertIn("commit 前最终回环状态已变化，执行回滚", script)

    def test_python_orchestrator_commits_only_after_safe_external_probe(self) -> None:
        process = _FakeProcess()
        fake_stdin = process.stdin
        decisions: list[str] = []

        def signal(_host, _key, _remote_tmp, decision):
            decisions.append(decision)
            process.returncode = 0 if decision == "commit" else 1

        with (
            mock.patch.object(MODULE.subprocess, "Popen", return_value=process),
            mock.patch.object(MODULE, "_transaction_state", return_value="ready"),
            mock.patch.object(MODULE, "_ssh_script", side_effect=_safe_ssh_response),
            mock.patch.object(MODULE, "_external_port_open", return_value=False),
            mock.patch.object(MODULE, "_signal_transaction", side_effect=signal),
        ):
            audit = MODULE._run_private_transaction(
                "example.test", Path("key"), "/tmp/myproxy-private-predeploy.ABCD"
            )
        self.assertEqual(decisions, ["commit"])
        self.assertEqual(audit["listener_1820"], "loopback")
        self.assertIn(b"DEPLOY_COMMITTED=1", fake_stdin.data)
        self.assertNotIn(b"\r\n", fake_stdin.data)

    def test_python_orchestrator_requests_rollback_on_public_port(self) -> None:
        process = _FakeProcess()
        decisions: list[str] = []

        def signal(_host, _key, _remote_tmp, decision):
            decisions.append(decision)
            process.returncode = 0 if decision == "commit" else 1

        with (
            mock.patch.object(MODULE.subprocess, "Popen", return_value=process),
            mock.patch.object(MODULE, "_transaction_state", return_value="ready"),
            mock.patch.object(MODULE, "_ssh_script", side_effect=_safe_ssh_response),
            mock.patch.object(MODULE, "_external_port_open", return_value=True),
            mock.patch.object(MODULE, "_signal_transaction", side_effect=signal),
        ):
            with self.assertRaises(MODULE.PredeployError):
                MODULE._run_private_transaction(
                    "example.test", Path("key"), "/tmp/myproxy-private-predeploy.ABCD"
                )
        self.assertEqual(decisions, ["rollback"])

    def test_existing_admin_env_rejects_every_extra_assignment(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        self.assertIn(
            'if name not in {"MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET"}',
            script,
        )
        self.assertNotIn('rm -f -- "$REMOTE_DIR/server.py"', script)
        self.assertNotIn('"$REMOTE_DIR/myproxy-api.db"', script)
        self.assertIn("远端已存在设备密钥，拒绝初始化载荷以避免轮换", script)
        self.assertIn("source.backup(destination)", script)
        self.assertIn("PRAGMA integrity_check", script)
        self.assertIn("src_dir_fd=directory_fd", script)
        self.assertIn("dst_dir_fd=directory_fd", script)
        self.assertIn("chown root:myproxy /etc/myproxy /etc/myproxy/tls", script)

    def test_missing_device_secret_requires_explicit_initialization(self) -> None:
        audit = MODULE._parse_audit(_safe_audit_output())
        audit["admin_env"] = "missing-device-secret"
        self.assertTrue(MODULE._audit_blockers(audit))
        self.assertEqual(
            MODULE._audit_blockers(audit, allow_missing_device_secret=True), []
        )

    def test_device_secret_file_must_be_one_exact_assignment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "device.env"
            path.write_text(
                "MYPROXY_DEVICE_TOKEN_SECRET=" + "a" * 64 + "\n",
                encoding="ascii",
            )
            self.assertEqual(MODULE._validate_device_secret_file(path), path.resolve())
            path.write_text("MYPROXY_DEVICE_TOKEN_SECRET=short\n", encoding="ascii")
            with self.assertRaises(MODULE.PredeployError):
                MODULE._validate_device_secret_file(path)
            path.write_text(
                "MYPROXY_DEVICE_TOKEN_SECRET=" + "b" * 64 + "\nEXTRA=value\n",
                encoding="ascii",
            )
            with self.assertRaises(MODULE.PredeployError):
                MODULE._validate_device_secret_file(path)

    def test_device_secret_initialization_is_after_snapshot_and_rollback_restores(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        snapshot = script.index('cp -a -- "$ADMIN_ENV_PATH" "$ROLLBACK_DIR/admin.env"')
        initialize = script.index("原子初始化缺失的 Device Token 密钥")
        restore = script.index(
            'restore_snapshot_file "$ADMIN_ENV_EXISTED" "$ROLLBACK_DIR/admin.env"'
        )
        self.assertLess(restore, snapshot)
        self.assertLess(snapshot, initialize)
        self.assertIn("os.replace(temporary, path)", script)
        self.assertNotIn('cat "$INIT_SECRET_PATH"', script)

    def test_rollback_restores_tls_before_start_and_uses_legacy_health(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        rollback = script[script.index("rollback_remote()") : script.index("finish_remote()")]
        restore = rollback.index("restore_tls_metadata")
        helper_start = rollback.index("systemctl start myproxy-xui-helper")
        api_start = rollback.index("systemctl start myproxy-api")
        self.assertLess(restore, helper_start)
        self.assertLess(restore, api_start)
        self.assertIn("https://127.0.0.1:1820/healthz", rollback)
        self.assertNotIn("https://127.0.0.1:1820/readyz", rollback)
        tls_restore = script[
            script.index("restore_tls_metadata()") : script.index("rollback_remote()")
        ]
        link_guard = tls_restore.index('if [ "$restore_status" -ne 0 ]')
        self.assertLess(link_guard, tls_restore.index('chown "$TLS_ROOT_OWNER"'))

    def test_state_directory_stays_root_only_until_migration_finishes(self) -> None:
        script = MODULE.REMOTE_DEPLOY_SCRIPT
        root_only = script.index('install -d -m 0700 -o root -g root "$STATE_DIR"')
        migration = script.index('OLD_DB="$REMOTE_DIR/myproxy.db"')
        cleanup = script.index(
            'rm -f -- "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-shm" "$REMOTE_DIR/myproxy.db-wal"'
        )
        handoff = script.index('chown myproxy:myproxy "$STATE_DIR"', migration)
        self.assertLess(root_only, migration)
        self.assertLess(migration, cleanup)
        self.assertLess(cleanup, handoff)

    def test_ssh_script_sends_lf_only_binary_program(self) -> None:
        captured: dict[str, bytes] = {}

        def run_bytes(_argv, *, input_bytes, label):
            del label
            captured["value"] = input_bytes
            return "ok\n"

        with mock.patch.object(MODULE, "_run_bytes", side_effect=run_bytes):
            result = MODULE._ssh_script(
                "example.test", Path("key"), "set -euo pipefail\necho ok\n"
            )
        self.assertEqual(result, "ok\n")
        self.assertNotIn(b"\r", captured["value"])

    def test_audit_parser_rejects_unknown_duplicate_and_missing_fields(self) -> None:
        valid = "\n".join(f"{key}=value" for key in sorted(MODULE.AUDIT_KEYS))
        valid = valid.replace("audit_version=value", "audit_version=1")
        parsed = MODULE._parse_audit(valid)
        self.assertEqual(set(parsed), MODULE.AUDIT_KEYS)
        with self.assertRaises(MODULE.PredeployError):
            MODULE._parse_audit(valid + "\nunknown=value\n")
        first = sorted(MODULE.AUDIT_KEYS)[0]
        with self.assertRaises(MODULE.PredeployError):
            MODULE._parse_audit(valid + f"\n{first}=again\n")
        with self.assertRaises(MODULE.PredeployError):
            MODULE._parse_audit("audit_version=1\n")

    def test_audit_blockers_fail_closed_on_exposure_and_unreadable_state(self) -> None:
        safe = {
            key: "no" for key in MODULE.AUDIT_KEYS
        }
        safe.update(
            {
                "audit_version": "1",
                "remote_user": "root",
                "python3": "ok",
                "curl": "ok",
                "ss": "ok",
                "systemctl": "ok",
                "tls_cert": "present",
                "tls_key": "present",
                "xui_db": "present",
                "admin_dir": "missing",
                "admin_env": "missing",
                "xui_helper_env": "missing",
                "listener_1820": "none",
                "public_listener": "no",
                "nginx_myproxy_config": "absent",
                "nginx_upstream_1820": "no",
                "nginx_public_listen": "no",
                "ufw_state": "active-no-public",
            }
        )
        self.assertEqual(MODULE._audit_blockers(safe), [])
        cases = {
            "listener_1820": "unsafe",
            "public_listener": "yes",
            "nginx_myproxy_config": "present",
            "nginx_upstream_1820": "error",
            "nginx_public_listen": "yes",
            "ufw_state": "active-public-allowed",
            "admin_env": "valid-insecure",
            "admin_dir": "symlink",
        }
        for key, value in cases.items():
            with self.subTest(key=key, value=value):
                audit = dict(safe)
                audit[key] = value
                self.assertTrue(MODULE._audit_blockers(audit))

    def test_local_payload_is_an_explicit_allowlist(self) -> None:
        modules, unit, helper_unit, helper_env = MODULE._validate_local_payload()
        self.assertEqual([path.name for path in modules], list(MODULE.RUNTIME_FILES))
        self.assertEqual(unit.name, "myproxy-api.service")
        self.assertEqual(helper_unit.name, "myproxy-xui-helper.service")
        self.assertEqual(helper_env.name, "myproxy-xui-helper.env")
        self.assertTrue(all(path.is_file() for path in modules))
        self.assertTrue(helper_unit.is_file())
        self.assertTrue(helper_env.is_file())

    def test_host_validation_rejects_ssh_option_injection(self) -> None:
        self.assertEqual(MODULE._validate_host("203.0.113.10"), "203.0.113.10")
        for value in ("", "-oProxyCommand=bad", "host name", "localhost"):
            with self.subTest(value=value):
                with self.assertRaises(MODULE.PredeployError):
                    MODULE._validate_host(value)


if __name__ == "__main__":
    unittest.main()
