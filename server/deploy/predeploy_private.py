#!/usr/bin/env python3
"""Fail-closed private predeployment for MyProxy Server.

This entry point deliberately does not install, reload, or otherwise modify
nginx or a firewall.  It is intended for a private predeployment in which the
API is reachable only over the target host's loopback interface.
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
from typing import Mapping, Sequence


REMOTE_USER = "root"
PRIVATE_PORT = 1820
RUNTIME_FILES = (
    "__init__.py",
    "__main__.py",
    "admin_ui.py",
    "app.py",
    "auth.py",
    "config.py",
    "db.py",
    "observability.py",
    "release.py",
    "server.py",
    "xui.py",
    "xui_helper.py",
)
XUI_HELPER_UNIT_NAME = "myproxy-xui-helper.service"
XUI_HELPER_ENV_NAME = "myproxy-xui-helper.env"


def _deployment_payload() -> Path:
    """Resolve and validate the shared deployment file before remote mutation."""
    server_dir = Path(__file__).resolve().parent.parent
    if str(server_dir) not in sys.path:
        sys.path.insert(0, str(server_dir))
    from myproxy_server.config import Settings

    try:
        settings = Settings.from_env()
        path = Path(settings.deployment_config)
        if not path.is_file() or path.is_symlink():
            raise ValueError("deployment.json must be a regular file")
        settings.require_configured_deployment()
    except ValueError as exc:
        raise PredeployError(str(exc)) from exc
    return path


def _configured_public_port() -> int:
    server_dir = Path(__file__).resolve().parent.parent
    if str(server_dir) not in sys.path:
        sys.path.insert(0, str(server_dir))
    from myproxy_server.config import Settings
    try:
        return Settings.from_env().api_port
    except ValueError as exc:
        raise PredeployError(str(exc)) from exc


def _render_remote_script(script: str) -> str:
    return script.replace("__PUBLIC_PORT__", str(_configured_public_port()))
AUDIT_KEYS = frozenset(
    {
        "audit_version",
        "remote_user",
        "python3",
        "curl",
        "ss",
        "systemctl",
        "tls_cert",
        "tls_key",
        "xui_db",
        "admin_dir",
        "admin_env",
        "xui_helper_env",
        "myproxy_active",
        "myproxy_enabled",
        "xui_helper_active",
        "xui_helper_enabled",
        "listener_1820",
        "public_listener",
        "nginx_myproxy_config",
        "nginx_upstream_1820",
        "nginx_public_listen",
        "ufw_state",
    }
)


class PredeployError(RuntimeError):
    """A fail-closed predeployment error safe to show to the operator."""


REMOTE_AUDIT_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail

command_state() {
  if command -v "$1" >/dev/null 2>&1; then
    printf '%s=ok\n' "$1"
  else
    printf '%s=missing\n' "$1"
  fi
}

listener_1820_state() {
  local ipv4 ipv6 status4 status6 count4
  set +e
  ipv4="$(ss -H -ltn4 'sport = :1820' 2>/dev/null)"
  status4="$?"
  ipv6="$(ss -H -ltn6 'sport = :1820' 2>/dev/null)"
  status6="$?"
  set -e
  if [ "$status4" -ne 0 ] || [ "$status6" -ne 0 ]; then
    printf 'error'
    return
  fi
  count4="$(printf '%s\n' "$ipv4" | awk 'NF { count += 1 } END { print count + 0 }')"
  if [ "$count4" -eq 0 ] && [ -z "$ipv6" ]; then
    printf 'none'
    return
  fi
  if [ "$count4" -eq 1 ] && [ -z "$ipv6" ] && \
     printf '%s\n' "$ipv4" | awk 'NF && $4 != "127.0.0.1:1820" { bad=1 } END { exit bad }'; then
    printf 'loopback'
  else
    printf 'unsafe'
  fi
}

public_listener_state() {
  local ipv4 ipv6 status4 status6
  set +e
  ipv4="$(ss -H -ltn4 'sport = :__PUBLIC_PORT__' 2>/dev/null)"
  status4="$?"
  ipv6="$(ss -H -ltn6 'sport = :__PUBLIC_PORT__' 2>/dev/null)"
  status6="$?"
  set -e
  if [ "$status4" -ne 0 ] || [ "$status6" -ne 0 ]; then
    printf 'error'
    return
  fi
  if printf '%s\n' "$ipv4" | awk 'NF && $4 !~ /^127\./ { found=1 } END { exit found ? 0 : 1 }'; then
    printf 'yes'
    return
  fi
  if printf '%s\n' "$ipv6" | awk 'NF && $4 !~ /^\[?::1\]?:/ { found=1 } END { exit found ? 0 : 1 }'; then
    printf 'yes'
    return
  fi
  printf 'no'
}

printf 'audit_version=1\n'
if [ "$(id -u)" -eq 0 ]; then
  printf 'remote_user=root\n'
else
  printf 'remote_user=non-root\n'
fi
command_state python3
command_state curl
command_state ss
command_state systemctl

if [ -f /etc/myproxy/tls/server.crt ] && [ ! -L /etc/myproxy/tls/server.crt ]; then
  printf 'tls_cert=present\n'
else
  printf 'tls_cert=missing-or-unsafe\n'
fi
if [ -f /etc/myproxy/tls/server.key ] && [ ! -L /etc/myproxy/tls/server.key ]; then
  printf 'tls_key=present\n'
else
  printf 'tls_key=missing-or-unsafe\n'
fi
if [ -f /etc/x-ui/x-ui.db ] && [ ! -L /etc/x-ui/x-ui.db ]; then
  printf 'xui_db=present\n'
else
  printf 'xui_db=missing-or-unsafe\n'
fi

if [ -L /etc/myproxy-api ]; then
  printf 'admin_dir=symlink\n'
elif [ ! -e /etc/myproxy-api ]; then
  printf 'admin_dir=missing\n'
elif [ ! -d /etc/myproxy-api ]; then
  printf 'admin_dir=abnormal\n'
else
  admin_dir_mode="$(stat -c '%a:%u:%g' /etc/myproxy-api 2>/dev/null || printf 'error')"
    case "$admin_dir_mode" in
    750:0:*|700:0:0|750:0:0) printf 'admin_dir=valid-secure\n' ;;
    *) printf 'admin_dir=insecure\n' ;;
  esac
fi

if [ -L /etc/myproxy-api/admin.env ]; then
  printf 'admin_env=symlink\n'
elif [ ! -e /etc/myproxy-api/admin.env ]; then
  printf 'admin_env=missing\n'
elif [ ! -f /etc/myproxy-api/admin.env ]; then
  printf 'admin_env=abnormal\n'
elif ! command -v python3 >/dev/null 2>&1; then
  printf 'admin_env=uncheckable\n'
else
  admin_shape="$(python3 - <<'PY'
import re
from pathlib import Path

path = Path("/etc/myproxy-api/admin.env")
values = {}
for line in path.read_text(encoding="utf-8").splitlines():
    stripped = line.strip()
    if not stripped or stripped.startswith("#"):
        continue
    if "=" not in line:
        values.setdefault("forbidden_assignment", []).append(line)
        continue
    name, value = line.split("=", 1)
    if name not in {"MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET"}:
        values.setdefault("forbidden_assignment", []).append(name)
        continue
    values.setdefault(name, []).append(value.strip())
valid = all(
    len(values.get(name, [])) == 1
    and re.fullmatch(r"[0-9a-fA-F]{64}", values[name][0])
    for name in ("MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET")
) and "forbidden_assignment" not in values
missing_device_secret = (
    len(values.get("MYPROXY_ADMIN_TOKEN", [])) == 1
    and re.fullmatch(r"[0-9a-fA-F]{64}", values["MYPROXY_ADMIN_TOKEN"][0])
    and "MYPROXY_DEVICE_TOKEN_SECRET" not in values
    and "forbidden_assignment" not in values
)
if valid:
    print("valid")
elif missing_device_secret:
    print("missing-device-secret")
else:
    print("invalid")
PY
)"
  admin_mode="$(stat -c '%a:%u:%g' /etc/myproxy-api/admin.env 2>/dev/null || printf 'error')"
  if [ "$admin_shape" = "missing-device-secret" ] && { [ "$admin_mode" = "600:0:0" ] || [[ "$admin_mode" = 640:0:* ]]; }; then
    printf 'admin_env=missing-device-secret\n'
  elif [ "$admin_shape" != "valid" ]; then
    printf 'admin_env=invalid\n'
  elif [[ "$admin_mode" = 640:0:* ]]; then
    printf 'admin_env=valid-secure\n'
  elif [ "$admin_mode" = "600:0:0" ]; then
    printf 'admin_env=valid-legacy\n'
  else
    printf 'admin_env=valid-insecure\n'
  fi
fi

if [ -L /etc/myproxy-api/xui-helper.env ]; then
  printf 'xui_helper_env=symlink\n'
elif [ ! -e /etc/myproxy-api/xui-helper.env ]; then
  printf 'xui_helper_env=missing\n'
elif [ ! -f /etc/myproxy-api/xui-helper.env ]; then
  printf 'xui_helper_env=abnormal\n'
elif [ "$(stat -c '%a:%u:%g' /etc/myproxy-api/xui-helper.env 2>/dev/null || printf error)" = "600:0:0" ]; then
  printf 'xui_helper_env=valid-secure\n'
else
  printf 'xui_helper_env=insecure\n'
fi

if systemctl is-active --quiet myproxy-api 2>/dev/null; then
  printf 'myproxy_active=yes\n'
else
  printf 'myproxy_active=no\n'
fi
if systemctl is-enabled --quiet myproxy-api 2>/dev/null; then
  printf 'myproxy_enabled=yes\n'
else
  printf 'myproxy_enabled=no\n'
fi
if systemctl is-active --quiet myproxy-xui-helper 2>/dev/null; then
  printf 'xui_helper_active=yes\n'
else
  printf 'xui_helper_active=no\n'
fi
if systemctl is-enabled --quiet myproxy-xui-helper 2>/dev/null; then
  printf 'xui_helper_enabled=yes\n'
else
  printf 'xui_helper_enabled=no\n'
fi

if command -v ss >/dev/null 2>&1; then
  printf 'listener_1820=%s\n' "$(listener_1820_state)"
  printf 'public_listener=%s\n' "$(public_listener_state)"
else
  printf 'listener_1820=uncheckable\n'
  printf 'public_listener=uncheckable\n'
fi

if [ -e /etc/nginx/conf.d/myproxy-device-api.conf ] || \
   [ -L /etc/nginx/conf.d/myproxy-device-api.conf ]; then
  printf 'nginx_myproxy_config=present\n'
else
  printf 'nginx_myproxy_config=absent\n'
fi
if ! command -v nginx >/dev/null 2>&1; then
  printf 'nginx_upstream_1820=absent\n'
  printf 'nginx_public_listen=absent\n'
else
  nginx_dump="$(mktemp /tmp/myproxy-nginx-audit.XXXXXX)"
  nginx_active="$(mktemp /tmp/myproxy-nginx-active.XXXXXX)"
  chmod 0600 "$nginx_dump" "$nginx_active"
  set +e
  nginx -T >"$nginx_dump" 2>&1
  nginx_status="$?"
  set -e
  if [ "$nginx_status" -ne 0 ]; then
    printf 'nginx_upstream_1820=error\n'
    printf 'nginx_public_listen=error\n'
  else
    sed 's/[[:space:]]*#.*$//' "$nginx_dump" >"$nginx_active"
    if grep -E -q -- '(^|[^0-9])0*1820([^0-9]|$)' "$nginx_active"; then
      printf 'nginx_upstream_1820=yes\n'
    else
      printf 'nginx_upstream_1820=no\n'
    fi
    if grep -E -q -- 'listen[[:space:]]+([^;[:space:]]*:)?0*__PUBLIC_PORT__([[:space:];]|$)' "$nginx_active"; then
      printf 'nginx_public_listen=yes\n'
    else
      printf 'nginx_public_listen=no\n'
    fi
  fi
  rm -f -- "$nginx_dump" "$nginx_active"
fi

if ! command -v ufw >/dev/null 2>&1; then
  printf 'ufw_state=absent\n'
else
  set +e
  ufw_output="$(LC_ALL=C ufw status 2>/dev/null)"
  ufw_status="$?"
  set -e
  if [ "$ufw_status" -ne 0 ]; then
    printf 'ufw_state=error\n'
  elif printf '%s\n' "$ufw_output" | grep -q '^Status: active'; then
    if printf '%s\n' "$ufw_output" | grep -Eq '^[[:space:]]*0*__PUBLIC_PORT__(/tcp)?([[:space:]]|\(v6\))+ALLOW'; then
      printf 'ufw_state=active-public-allowed\n'
    else
      printf 'ufw_state=active-no-public\n'
    fi
  else
    printf 'ufw_state=inactive\n'
  fi
fi
'''


REMOTE_CREATE_TMP_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
umask 077
directory="$(mktemp -d /tmp/myproxy-private-predeploy.XXXXXX)"
chmod 0700 "$directory"
printf '%s\n' "$directory"
'''


REMOTE_PREPARE_UPLOAD_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
remote_tmp="$1"
if [[ ! "$remote_tmp" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]]; then
  echo 'unsafe temporary path' >&2
  exit 1
fi
install -d -m 0700 "$remote_tmp/myproxy_server"
'''


REMOTE_FINALIZE_SECRET_UPLOAD_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
remote_tmp="$1"
secret_path="$remote_tmp/device-secret.env"
if [[ ! "$remote_tmp" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]]; then
  echo 'unsafe temporary path' >&2
  exit 1
fi
if [ ! -f "$secret_path" ] || [ -L "$secret_path" ]; then
  echo 'device secret upload is missing or unsafe' >&2
  exit 1
fi
chown root:root "$secret_path"
chmod 0600 "$secret_path"
'''


REMOTE_CLEANUP_TMP_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
remote_tmp="$1"
if [[ ! "$remote_tmp" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]]; then
  echo 'unsafe temporary path' >&2
  exit 1
fi
rm -rf -- "$remote_tmp"
'''


REMOTE_TRANSACTION_STATE_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
remote_tmp="$1"
if [[ ! "$remote_tmp" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]]; then
  echo 'unsafe'
  exit 1
fi
if [ ! -d "$remote_tmp" ]; then
  echo 'gone'
elif [ -f "$remote_tmp/.ready" ]; then
  echo 'ready'
else
  echo 'pending'
fi
'''


REMOTE_TRANSACTION_SIGNAL_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
remote_tmp="$1"
decision="$2"
if [[ ! "$remote_tmp" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]] || \
   { [ "$decision" != commit ] && [ "$decision" != rollback ]; }; then
  echo 'unsafe transaction signal' >&2
  exit 1
fi
if [ ! -d "$remote_tmp" ] || [ ! -f "$remote_tmp/.ready" ]; then
  echo 'transaction is not ready' >&2
  exit 1
fi
install -m 0600 /dev/null "$remote_tmp/.$decision"
'''


REMOTE_EMERGENCY_STOP_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
systemctl stop myproxy-api
systemctl stop myproxy-xui-helper
if systemctl is-active --quiet myproxy-api; then
  echo 'myproxy-api remained active after emergency stop' >&2
  exit 1
fi
if systemctl is-active --quiet myproxy-xui-helper; then
  echo 'myproxy-xui-helper remained active after emergency stop' >&2
  exit 1
fi
'''


REMOTE_ADMIN_UI_AUDIT_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail
headers="$(mktemp /tmp/myproxy-admin-ui-headers.XXXXXX)"
body="$(mktemp /tmp/myproxy-admin-ui-body.XXXXXX)"
chmod 0600 "$headers" "$body"
cleanup() {
  rm -f -- "$headers" "$body"
}
trap cleanup EXIT

html_status="$(curl --noproxy '*' -ksS -D "$headers" -o "$body" -w '%{http_code}' \
  https://127.0.0.1:1820/admin/ || true)"
css_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/admin/app.css || true)"
js_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/admin/app.js || true)"
unknown_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/admin/unknown || true)"
root_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/ || true)"

if [ "$html_status" != 200 ] || [ "$css_status" != 200 ] || \
   [ "$js_status" != 200 ] || [ "$unknown_status" != 404 ] || \
   [ "$root_status" != 404 ] || \
   ! grep -Fq '<title>MyProxy Server Management</title>' "$body" || \
   ! grep -qi '^Content-Security-Policy:.*default-src' "$headers" || \
   ! grep -qi '^Cache-Control:[[:space:]]*no-store' "$headers"; then
  echo 'admin UI audit failed' >&2
  exit 1
fi
printf 'admin_ui=valid\n'
'''


REMOTE_DEPLOY_SCRIPT = r'''#!/usr/bin/env bash
set -euo pipefail

REMOTE_TMP="$1"
REMOTE_DIR="/opt/myproxy-api"
STATE_DIR="/var/lib/myproxy-api"
XUI_HELPER_UNIT_PATH="/etc/systemd/system/myproxy-xui-helper.service"
ADMIN_DIR="/etc/myproxy-api"
ADMIN_ENV_PATH="$ADMIN_DIR/admin.env"
XUI_HELPER_ENV_PATH="$ADMIN_DIR/xui-helper.env"
INIT_SECRET_PATH="$REMOTE_TMP/device-secret.env"
UNIT_PATH="/etc/systemd/system/myproxy-api.service"
ROLLBACK_DIR="$REMOTE_TMP/.rollback"
DEPLOY_COMMITTED=0
ROLLBACK_ARMED=0
SNAPSHOT_READY=0
REMOTE_DIR_EXISTED=0
ADMIN_DIR_EXISTED=0
ADMIN_ENV_EXISTED=0
INIT_SECRET_PRESENT=0
UNIT_EXISTED=0
XUI_HELPER_UNIT_EXISTED=0
XUI_HELPER_ENV_EXISTED=0
STATE_DIR_EXISTED=0
MYPROXY_GROUP_CREATED=0
MYPROXY_USER_CREATED=0
MYPROXY_WAS_ACTIVE=0
MYPROXY_WAS_ENABLED=0
XUI_HELPER_WAS_ACTIVE=0
XUI_HELPER_WAS_ENABLED=0
ADMIN_DIR_MODE=""
ADMIN_DIR_OWNER=""
TLS_ROOT_MODE=""
TLS_ROOT_OWNER=""
TLS_DIR_MODE=""
TLS_DIR_OWNER=""
TLS_CERT_MODE=""
TLS_CERT_OWNER=""
TLS_KEY_MODE=""
TLS_KEY_OWNER=""

if [[ ! "$REMOTE_TMP" =~ ^/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+$ ]]; then
  echo '错误：拒绝使用未知远程临时路径' >&2
  exit 1
fi

restore_snapshot_file() {
  local existed="$1"
  local snapshot="$2"
  local target="$3"
  rm -f -- "$target"
  if [ "$existed" -eq 1 ]; then
    cp -a -- "$snapshot" "$target"
  fi
}

restore_tls_metadata() {
  local restore_status=0 tls_target
  if [ -z "$TLS_ROOT_MODE" ] || [ -z "$TLS_ROOT_OWNER" ] || \
     [ -z "$TLS_DIR_MODE" ] || [ -z "$TLS_DIR_OWNER" ] || \
     [ -z "$TLS_CERT_MODE" ] || [ -z "$TLS_CERT_OWNER" ] || \
     [ -z "$TLS_KEY_MODE" ] || [ -z "$TLS_KEY_OWNER" ]; then
    echo '错误：TLS 元数据快照不完整，拒绝静默恢复' >&2
    return 1
  fi
  for tls_target in /etc/myproxy /etc/myproxy/tls \
    /etc/myproxy/tls/server.crt /etc/myproxy/tls/server.key; do
    if [ -L "$tls_target" ]; then
      echo "错误：TLS 恢复目标是符号链接：$tls_target" >&2
      restore_status=1
    fi
  done
  # Never let chown/chmod follow a link planted in a rollback target.  The
  # metadata restore is deliberately all-or-nothing after this shape check.
  if [ "$restore_status" -ne 0 ]; then
    return "$restore_status"
  fi
  if ! chown "$TLS_ROOT_OWNER" /etc/myproxy; then
    echo '错误：无法恢复 /etc/myproxy 原 owner' >&2
    restore_status=1
  fi
  if ! chmod "$TLS_ROOT_MODE" /etc/myproxy; then
    echo '错误：无法恢复 /etc/myproxy 原 mode' >&2
    restore_status=1
  fi
  if ! chown "$TLS_DIR_OWNER" /etc/myproxy/tls; then
    echo '错误：无法恢复 /etc/myproxy/tls 原 owner' >&2
    restore_status=1
  fi
  if ! chmod "$TLS_DIR_MODE" /etc/myproxy/tls; then
    echo '错误：无法恢复 /etc/myproxy/tls 原 mode' >&2
    restore_status=1
  fi
  if ! chown "$TLS_CERT_OWNER" /etc/myproxy/tls/server.crt; then
    echo '错误：无法恢复 TLS 证书原 owner' >&2
    restore_status=1
  fi
  if ! chmod "$TLS_CERT_MODE" /etc/myproxy/tls/server.crt; then
    echo '错误：无法恢复 TLS 证书原 mode' >&2
    restore_status=1
  fi
  if ! chown "$TLS_KEY_OWNER" /etc/myproxy/tls/server.key; then
    echo '错误：无法恢复 TLS 私钥原 owner' >&2
    restore_status=1
  fi
  if ! chmod "$TLS_KEY_MODE" /etc/myproxy/tls/server.key; then
    echo '错误：无法恢复 TLS 私钥原 mode' >&2
    restore_status=1
  fi
  return "$restore_status"
}

rollback_remote() {
  local rollback_status=0 health_status
  set +e
  echo '错误：私有预部署未通过最终验收，开始恢复部署前状态...' >&2
  systemctl stop myproxy-api >/dev/null 2>&1 || true
  systemctl stop myproxy-xui-helper >/dev/null 2>&1 || true
  if [ "$SNAPSHOT_READY" -eq 1 ]; then
    if [ "$REMOTE_DIR_EXISTED" -eq 1 ]; then
      rm -rf -- "$REMOTE_DIR"
      tar xzf "$ROLLBACK_DIR/remote-dir.tar.gz" -C /opt
    else
      rm -rf -- "$REMOTE_DIR"
    fi
    restore_snapshot_file "$UNIT_EXISTED" "$ROLLBACK_DIR/myproxy-api.service" "$UNIT_PATH"
    restore_snapshot_file "$XUI_HELPER_UNIT_EXISTED" "$ROLLBACK_DIR/myproxy-xui-helper.service" "$XUI_HELPER_UNIT_PATH"
    restore_snapshot_file "$XUI_HELPER_ENV_EXISTED" "$ROLLBACK_DIR/xui-helper.env" "$XUI_HELPER_ENV_PATH"
    if [ "$STATE_DIR_EXISTED" -eq 1 ]; then
      rm -rf -- "$STATE_DIR"
      tar xzf "$ROLLBACK_DIR/state-dir.tar.gz" -C /var/lib
    else
      rm -rf -- "$STATE_DIR"
    fi
    restore_snapshot_file "$ADMIN_ENV_EXISTED" "$ROLLBACK_DIR/admin.env" "$ADMIN_ENV_PATH"
    if [ "$ADMIN_DIR_EXISTED" -eq 1 ]; then
      chmod "$ADMIN_DIR_MODE" "$ADMIN_DIR" >/dev/null 2>&1 || true
      chown "$ADMIN_DIR_OWNER" "$ADMIN_DIR" >/dev/null 2>&1 || true
    else
      rmdir -- "$ADMIN_DIR" >/dev/null 2>&1 || true
    fi
    if ! restore_tls_metadata; then
      rollback_status=1
    fi
    systemctl daemon-reload >/dev/null 2>&1 || true
  elif ! restore_tls_metadata; then
    rollback_status=1
  fi
  if [ "$MYPROXY_WAS_ENABLED" -eq 1 ]; then
    systemctl enable myproxy-api >/dev/null 2>&1 || true
  else
    systemctl disable myproxy-api >/dev/null 2>&1 || true
  fi
  if [ "$XUI_HELPER_WAS_ENABLED" -eq 1 ]; then
    systemctl enable myproxy-xui-helper >/dev/null 2>&1 || true
  else
    systemctl disable myproxy-xui-helper >/dev/null 2>&1 || true
  fi
  if [ "$XUI_HELPER_WAS_ACTIVE" -eq 1 ]; then
    if ! systemctl start myproxy-xui-helper >/dev/null 2>&1 || \
       ! systemctl is-active --quiet myproxy-xui-helper; then
      echo '错误：旧 myproxy-xui-helper 未能恢复为 active，请立即人工检查' >&2
      rollback_status=1
    fi
  else
    if ! systemctl stop myproxy-xui-helper >/dev/null 2>&1 || \
       systemctl is-active --quiet myproxy-xui-helper; then
      echo '错误：myproxy-xui-helper 未能恢复为 inactive，请立即人工检查' >&2
      rollback_status=1
    fi
  fi
  if [ "$MYPROXY_WAS_ACTIVE" -eq 1 ]; then
    if ! systemctl start myproxy-api >/dev/null 2>&1 || \
       ! systemctl is-active --quiet myproxy-api; then
      echo '错误：旧 myproxy-api 未能恢复为 active，请立即人工检查' >&2
      rollback_status=1
    else
      # /readyz was introduced by this release, so an older healthy service
      # may legitimately return 404 there.  Use the stable health endpoint to
      # validate the restored version without weakening active-state checks.
      health_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
        https://127.0.0.1:1820/healthz || true)"
      if [ "$health_status" != 200 ]; then
        echo "错误：旧 myproxy-api 恢复后 health 验收失败（HTTP $health_status），请立即人工检查" >&2
        rollback_status=1
      fi
    fi
  else
    if ! systemctl stop myproxy-api >/dev/null 2>&1 || \
       systemctl is-active --quiet myproxy-api; then
      echo '错误：myproxy-api 未能恢复为 inactive，请立即人工检查' >&2
      rollback_status=1
    fi
  fi
  if [ "$MYPROXY_USER_CREATED" -eq 1 ] && id -u myproxy >/dev/null 2>&1; then
    userdel myproxy >/dev/null 2>&1 || true
  fi
  if [ "$MYPROXY_GROUP_CREATED" -eq 1 ] && getent group myproxy >/dev/null 2>&1; then
    groupdel myproxy >/dev/null 2>&1 || true
  fi
  echo '==> [远端] 自动回滚流程结束' >&2
  return "$rollback_status"
}

finish_remote() {
  local status="$?"
  trap - EXIT
  if [ "$ROLLBACK_ARMED" -eq 1 ] && [ "$DEPLOY_COMMITTED" -ne 1 ]; then
    if ! rollback_remote; then
      echo '错误：自动回滚未完全恢复，请立即人工检查' >&2
      status=1
    fi
    if [ "$status" -eq 0 ]; then
      status=1
    fi
  fi
  rm -rf -- "$REMOTE_TMP" >/dev/null 2>&1 || true
  exit "$status"
}
trap finish_remote EXIT

capture_public_listeners() {
  {
    ss -H -ltn4 | awk 'NF && $4 !~ /^127\./ { print "ipv4:" $4 }'
    ss -H -ltn6 | awk 'NF && $4 !~ /^\[?::1\]?:/ { print "ipv6:" $4 }'
  } | LC_ALL=C sort -u
}

assert_no_public_exposure() {
  local ipv4 ipv6 status4 status6 nginx_dump nginx_active nginx_status ufw_output ufw_status
  set +e
  ipv4="$(ss -H -ltn4 'sport = :1820' 2>/dev/null)"
  status4="$?"
  ipv6="$(ss -H -ltn6 'sport = :1820' 2>/dev/null)"
  status6="$?"
  set -e
  if [ "$status4" -ne 0 ] || [ "$status6" -ne 0 ]; then
    echo '错误：无法审计 1820 监听，拒绝继续' >&2
    return 1
  fi
  if [ -n "$ipv6" ] || ! printf '%s\n' "$ipv4" | \
      awk 'NF && $4 != "127.0.0.1:1820" { bad=1 } END { exit bad }'; then
    echo '错误：检测到非 127.0.0.1 的 1820 监听，拒绝继续' >&2
    return 1
  fi

  set +e
  ipv4="$(ss -H -ltn4 'sport = :__PUBLIC_PORT__' 2>/dev/null)"
  status4="$?"
  ipv6="$(ss -H -ltn6 'sport = :__PUBLIC_PORT__' 2>/dev/null)"
  status6="$?"
  set -e
  if [ "$status4" -ne 0 ] || [ "$status6" -ne 0 ]; then
    echo '错误：无法审计 __PUBLIC_PORT__ 监听，拒绝继续' >&2
    return 1
  fi
  if printf '%s\n' "$ipv4" | awk 'NF && $4 !~ /^127\./ { found=1 } END { exit found ? 0 : 1 }' || \
     printf '%s\n' "$ipv6" | awk 'NF && $4 !~ /^\[?::1\]?:/ { found=1 } END { exit found ? 0 : 1 }'; then
    echo '错误：检测到公网 __PUBLIC_PORT__ 监听，拒绝继续' >&2
    return 1
  fi

  if [ -e /etc/nginx/conf.d/myproxy-device-api.conf ] || \
     [ -L /etc/nginx/conf.d/myproxy-device-api.conf ]; then
    echo '错误：检测到既有 MyProxy nginx 配置，拒绝私有预部署' >&2
    return 1
  fi
  if command -v nginx >/dev/null 2>&1; then
    nginx_dump="$(mktemp /tmp/myproxy-nginx-guard.XXXXXX)"
    nginx_active="$(mktemp /tmp/myproxy-nginx-active.XXXXXX)"
    chmod 0600 "$nginx_dump" "$nginx_active"
    set +e
    nginx -T >"$nginx_dump" 2>&1
    nginx_status="$?"
    set -e
    if [ "$nginx_status" -ne 0 ]; then
      rm -f -- "$nginx_dump" "$nginx_active"
      echo '错误：nginx 实际配置无法审计，拒绝继续' >&2
      return 1
    fi
    sed 's/[[:space:]]*#.*$//' "$nginx_dump" >"$nginx_active"
    if grep -E -q -- '(^|[^0-9])0*1820([^0-9]|$)' "$nginx_active" || \
       grep -E -q -- 'listen[[:space:]]+([^;[:space:]]*:)?0*__PUBLIC_PORT__([[:space:];]|$)' "$nginx_active"; then
      rm -f -- "$nginx_dump" "$nginx_active"
      echo '错误：nginx 实际配置含 MyProxy 上游或 __PUBLIC_PORT__ 监听，拒绝继续' >&2
      return 1
    fi
    rm -f -- "$nginx_dump" "$nginx_active"
  fi

  if command -v ufw >/dev/null 2>&1; then
    set +e
    ufw_output="$(LC_ALL=C ufw status 2>/dev/null)"
    ufw_status="$?"
    set -e
    if [ "$ufw_status" -ne 0 ]; then
      echo '错误：UFW 状态不可读，拒绝继续' >&2
      return 1
    fi
    if printf '%s\n' "$ufw_output" | grep -q '^Status: active' && \
       printf '%s\n' "$ufw_output" | grep -Eq '^[[:space:]]*0*__PUBLIC_PORT__(/tcp)?([[:space:]]|\(v6\))+ALLOW'; then
      echo '错误：UFW 已放行 __PUBLIC_PORT__，拒绝私有预部署' >&2
      return 1
    fi
  fi
}

admin_env_shape() {
  python3 - <<'PY'
import re
from pathlib import Path

path = Path("/etc/myproxy-api/admin.env")
values = {}
for line in path.read_text(encoding="utf-8").splitlines():
    stripped = line.strip()
    if not stripped or stripped.startswith("#"):
        continue
    if "=" not in line:
        raise SystemExit(1)
    name, value = line.split("=", 1)
    if name not in {"MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET"}:
        print("invalid")
        raise SystemExit
    values.setdefault(name, []).append(value.strip())
valid = all(
    len(values.get(name, [])) == 1
    and re.fullmatch(r"[0-9a-fA-F]{64}", values[name][0])
    for name in ("MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET")
)
missing_device_secret = (
    len(values.get("MYPROXY_ADMIN_TOKEN", [])) == 1
    and re.fullmatch(r"[0-9a-fA-F]{64}", values["MYPROXY_ADMIN_TOKEN"][0])
    and "MYPROXY_DEVICE_TOKEN_SECRET" not in values
)
if valid:
    print("valid")
elif missing_device_secret:
    print("missing-device-secret")
else:
    print("invalid")
PY
}

validate_admin_env() {
  [ "$(admin_env_shape)" = valid ]
}

validate_initialization_secret() {
  INIT_SECRET_PATH="$INIT_SECRET_PATH" python3 - <<'PY'
import os
import re
from pathlib import Path

path = Path(os.environ["INIT_SECRET_PATH"])
try:
    lines = path.read_text(encoding="ascii").splitlines()
except (OSError, UnicodeError):
    raise SystemExit(1)
assignments = [line for line in lines if line]
if len(assignments) != 1 or "=" not in assignments[0]:
    raise SystemExit(1)
name, value = assignments[0].split("=", 1)
valid = name == "MYPROXY_DEVICE_TOKEN_SECRET" and re.fullmatch(
    r"[0-9a-fA-F]{64}", value
)
raise SystemExit(0 if valid else 1)
PY
}

assert_admin_ui() {
  local headers body html_status css_status js_status unknown_status root_status valid
  headers="$(mktemp /tmp/myproxy-admin-ui-headers.XXXXXX)"
  body="$(mktemp /tmp/myproxy-admin-ui-body.XXXXXX)"
  chmod 0600 "$headers" "$body"
  html_status="$(curl --noproxy '*' -ksS -D "$headers" -o "$body" -w '%{http_code}' \
    https://127.0.0.1:1820/admin/ || true)"
  css_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
    https://127.0.0.1:1820/admin/app.css || true)"
  js_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
    https://127.0.0.1:1820/admin/app.js || true)"
  unknown_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
    https://127.0.0.1:1820/admin/unknown || true)"
  root_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
    https://127.0.0.1:1820/ || true)"
  valid=1
  if [ "$html_status" != 200 ] || [ "$css_status" != 200 ] || \
     [ "$js_status" != 200 ] || [ "$unknown_status" != 404 ] || \
     [ "$root_status" != 404 ] || \
     ! grep -Fq '<title>MyProxy Server Management</title>' "$body" || \
     ! grep -qi '^Content-Security-Policy:.*default-src' "$headers" || \
     ! grep -qi '^Cache-Control:[[:space:]]*no-store' "$headers"; then
    valid=0
  fi
  rm -f -- "$headers" "$body"
  if [ "$valid" -ne 1 ]; then
    echo '错误：私有 Admin UI 验收失败' >&2
    return 1
  fi
}

for required in python3 curl ss systemctl tar; do
  if ! command -v "$required" >/dev/null 2>&1; then
    echo "错误：远端缺少 $required，拒绝继续" >&2
    exit 1
  fi
done
if [ "$(id -u)" -ne 0 ]; then
  echo '错误：私有预部署必须以 root 执行' >&2
  exit 1
fi
if [ ! -f /etc/myproxy/tls/server.crt ] || [ -L /etc/myproxy/tls/server.crt ] || \
   [ ! -f /etc/myproxy/tls/server.key ] || [ -L /etc/myproxy/tls/server.key ] || \
   [ ! -f /etc/x-ui/x-ui.db ] || [ -L /etc/x-ui/x-ui.db ]; then
  echo '错误：TLS 或 x-ui 必需文件缺失/不安全，拒绝继续' >&2
  exit 1
fi
if [ -L /etc/myproxy ] || [ -L /etc/myproxy/tls ] || \
   [ ! -d /etc/myproxy ] || [ ! -d /etc/myproxy/tls ]; then
  echo '错误：/etc/myproxy 或 /etc/myproxy/tls 目录类型不安全，拒绝继续' >&2
  exit 1
fi

if [ ! -d "$REMOTE_TMP/myproxy_server" ] || [ -L "$REMOTE_TMP/myproxy_server" ] || \
   [ ! -f "$REMOTE_TMP/deployment.json" ] || [ -L "$REMOTE_TMP/deployment.json" ] || \
   [ ! -f "$REMOTE_TMP/myproxy-api.service" ] || [ -L "$REMOTE_TMP/myproxy-api.service" ] || \
   [ ! -f "$REMOTE_TMP/myproxy-xui-helper.service" ] || [ -L "$REMOTE_TMP/myproxy-xui-helper.service" ] || \
   [ ! -f "$REMOTE_TMP/myproxy-xui-helper.env" ] || [ -L "$REMOTE_TMP/myproxy-xui-helper.env" ]; then
  echo '错误：上传载荷缺失或类型异常' >&2
  exit 1
fi
if [ -e "$INIT_SECRET_PATH" ] || [ -L "$INIT_SECRET_PATH" ]; then
  if [ ! -f "$INIT_SECRET_PATH" ] || [ -L "$INIT_SECRET_PATH" ] || \
     [ "$(stat -c '%a:%u:%g' "$INIT_SECRET_PATH")" != '600:0:0' ] || \
     ! validate_initialization_secret; then
    echo '错误：设备密钥初始化载荷不安全或格式无效' >&2
    exit 1
  fi
  INIT_SECRET_PRESENT=1
fi
actual_top_level="$(find "$REMOTE_TMP" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
if [ "$INIT_SECRET_PRESENT" -eq 1 ]; then
  expected_top_level=$'deployment.json\ndevice-secret.env\nmyproxy-api.service\nmyproxy-xui-helper.env\nmyproxy-xui-helper.service\nmyproxy_server'
else
  expected_top_level=$'deployment.json\nmyproxy-api.service\nmyproxy-xui-helper.env\nmyproxy-xui-helper.service\nmyproxy_server'
fi
if [ "$actual_top_level" != "$expected_top_level" ]; then
  echo '错误：上传载荷顶层白名单不精确' >&2
  exit 1
fi
expected_files=$'__init__.py\n__main__.py\nadmin_ui.py\napp.py\nauth.py\nconfig.py\ndb.py\nobservability.py\nrelease.py\nserver.py\nxui.py\nxui_helper.py'
actual_files="$(find "$REMOTE_TMP/myproxy_server" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)"
if [ "$actual_files" != "$expected_files" ] || \
   [ -n "$(find "$REMOTE_TMP/myproxy_server" -mindepth 1 -maxdepth 1 ! -type f -print -quit)" ]; then
  echo '错误：上传的运行时白名单不精确' >&2
  exit 1
fi
if [ "$(grep -c '^Environment=MYPROXY_LISTEN_HOST=' "$REMOTE_TMP/myproxy-api.service")" -ne 1 ] || \
   ! grep -Fxq 'Environment=MYPROXY_LISTEN_HOST=127.0.0.1' "$REMOTE_TMP/myproxy-api.service" || \
   [ "$(grep -c '^Environment=MYPROXY_LISTEN_PORT=' "$REMOTE_TMP/myproxy-api.service")" -ne 1 ] || \
   ! grep -Fxq 'Environment=MYPROXY_LISTEN_PORT=1820' "$REMOTE_TMP/myproxy-api.service"; then
  echo '错误：上传的 systemd unit 未精确固定 127.0.0.1:1820' >&2
  exit 1
fi

PYTHONPATH="$REMOTE_TMP" MYPROXY_DEPLOYMENT_CONFIG="$REMOTE_TMP/deployment.json" \
  python3 -c 'from myproxy_server.config import Settings; s=Settings.from_env(); s.require_configured_deployment()'

echo '==> [远端] 最终只读公网暴露门禁'
assert_no_public_exposure

for target in "$REMOTE_DIR" "$STATE_DIR" "$ADMIN_DIR" "$ADMIN_ENV_PATH" "$XUI_HELPER_ENV_PATH" "$UNIT_PATH" "$XUI_HELPER_UNIT_PATH" "$REMOTE_DIR/deployment.json"; do
  if [ -L "$target" ]; then
    echo "错误：部署目标是符号链接：$target" >&2
    exit 1
  fi
done
if { [ -e "$REMOTE_DIR" ] && [ ! -d "$REMOTE_DIR" ]; } || \
   { [ -e "$STATE_DIR" ] && [ ! -d "$STATE_DIR" ]; } || \
   { [ -e "$ADMIN_DIR" ] && [ ! -d "$ADMIN_DIR" ]; } || \
   { [ -e "$ADMIN_ENV_PATH" ] && [ ! -f "$ADMIN_ENV_PATH" ]; } || \
   { [ -e "$UNIT_PATH" ] && [ ! -f "$UNIT_PATH" ]; } || \
   { [ -e "$XUI_HELPER_ENV_PATH" ] && [ ! -f "$XUI_HELPER_ENV_PATH" ]; } || \
   { [ -e "$XUI_HELPER_UNIT_PATH" ] && [ ! -f "$XUI_HELPER_UNIT_PATH" ]; } || \
   { [ -e "$REMOTE_DIR/deployment.json" ] && [ ! -f "$REMOTE_DIR/deployment.json" ]; }; then
  echo '错误：部署目标类型异常，拒绝覆盖' >&2
  exit 1
fi

[ -e "$REMOTE_DIR" ] && REMOTE_DIR_EXISTED=1
[ -e "$STATE_DIR" ] && STATE_DIR_EXISTED=1
[ -e "$ADMIN_DIR" ] && ADMIN_DIR_EXISTED=1
[ -e "$ADMIN_ENV_PATH" ] && ADMIN_ENV_EXISTED=1
[ -e "$UNIT_PATH" ] && UNIT_EXISTED=1
[ -e "$XUI_HELPER_ENV_PATH" ] && XUI_HELPER_ENV_EXISTED=1
[ -e "$XUI_HELPER_UNIT_PATH" ] && XUI_HELPER_UNIT_EXISTED=1
systemctl is-active --quiet myproxy-api 2>/dev/null && MYPROXY_WAS_ACTIVE=1 || true
systemctl is-enabled --quiet myproxy-api 2>/dev/null && MYPROXY_WAS_ENABLED=1 || true
systemctl is-active --quiet myproxy-xui-helper 2>/dev/null && XUI_HELPER_WAS_ACTIVE=1 || true
systemctl is-enabled --quiet myproxy-xui-helper 2>/dev/null && XUI_HELPER_WAS_ENABLED=1 || true
TLS_ROOT_MODE="$(stat -c '%a' /etc/myproxy)"
TLS_ROOT_OWNER="$(stat -c '%u:%g' /etc/myproxy)"
TLS_DIR_MODE="$(stat -c '%a' /etc/myproxy/tls)"
TLS_DIR_OWNER="$(stat -c '%u:%g' /etc/myproxy/tls)"
TLS_CERT_MODE="$(stat -c '%a' /etc/myproxy/tls/server.crt)"
TLS_CERT_OWNER="$(stat -c '%u:%g' /etc/myproxy/tls/server.crt)"
TLS_KEY_MODE="$(stat -c '%a' /etc/myproxy/tls/server.key)"
TLS_KEY_OWNER="$(stat -c '%u:%g' /etc/myproxy/tls/server.key)"
if [ "$ADMIN_DIR_EXISTED" -eq 1 ]; then
  ADMIN_DIR_MODE="$(stat -c '%a' "$ADMIN_DIR")"
  ADMIN_DIR_OWNER="$(stat -c '%u:%g' "$ADMIN_DIR")"
  if { [ "$ADMIN_DIR_MODE" != 700 ] && [ "$ADMIN_DIR_MODE" != 750 ]; } || \
     { [ "$ADMIN_DIR_OWNER" != '0:0' ] && [[ "$ADMIN_DIR_OWNER" != 0:* ]]; }; then
    echo '错误：既有 /etc/myproxy-api 目录权限或属主不安全' >&2
    exit 1
  fi
fi
if [ "$ADMIN_ENV_EXISTED" -eq 1 ]; then
  ADMIN_ENV_MODE="$(stat -c '%a:%u:%g' "$ADMIN_ENV_PATH")"
  if [ "$ADMIN_ENV_MODE" != '600:0:0' ] && [[ "$ADMIN_ENV_MODE" != 640:0:* ]]; then
    echo '错误：既有 admin.env 形状或权限不安全；不会自动轮换' >&2
    exit 1
  fi
  existing_admin_shape="$(admin_env_shape)"
  if [ "$existing_admin_shape" = valid ] && [ "$INIT_SECRET_PRESENT" -eq 1 ]; then
    echo '错误：远端已存在设备密钥，拒绝初始化载荷以避免轮换' >&2
    exit 1
  fi
  if [ "$existing_admin_shape" = missing-device-secret ] && \
     [ "$INIT_SECRET_PRESENT" -ne 1 ]; then
    echo '错误：既有 admin.env 缺设备密钥，必须显式提供初始化载荷' >&2
    exit 1
  fi
  if [ "$existing_admin_shape" != valid ] && \
     [ "$existing_admin_shape" != missing-device-secret ]; then
    echo '错误：既有 admin.env 形状不安全；不会自动修复或轮换' >&2
    exit 1
  fi
fi

install -d -m 0700 "$ROLLBACK_DIR"
capture_public_listeners >"$ROLLBACK_DIR/listeners.before"
ROLLBACK_ARMED=1
if [ "$MYPROXY_WAS_ACTIVE" -eq 1 ]; then
  systemctl stop myproxy-api
  if systemctl is-active --quiet myproxy-api; then
    echo '错误：无法停止旧 myproxy-api' >&2
    exit 1
  fi
fi
if [ "$XUI_HELPER_WAS_ACTIVE" -eq 1 ]; then
  systemctl stop myproxy-xui-helper
  if systemctl is-active --quiet myproxy-xui-helper; then
    echo '错误：无法停止旧 myproxy-xui-helper' >&2
    exit 1
  fi
fi
if [ "$REMOTE_DIR_EXISTED" -eq 1 ]; then
  tar czf "$ROLLBACK_DIR/remote-dir.tar.gz" -C /opt myproxy-api
fi
if [ "$STATE_DIR_EXISTED" -eq 1 ]; then
  tar czf "$ROLLBACK_DIR/state-dir.tar.gz" -C /var/lib myproxy-api
fi
if [ "$UNIT_EXISTED" -eq 1 ]; then
  cp -a -- "$UNIT_PATH" "$ROLLBACK_DIR/myproxy-api.service"
fi
if [ "$XUI_HELPER_UNIT_EXISTED" -eq 1 ]; then
  cp -a -- "$XUI_HELPER_UNIT_PATH" "$ROLLBACK_DIR/myproxy-xui-helper.service"
fi
if [ "$XUI_HELPER_ENV_EXISTED" -eq 1 ]; then
  cp -a -- "$XUI_HELPER_ENV_PATH" "$ROLLBACK_DIR/xui-helper.env"
fi
if [ "$ADMIN_ENV_EXISTED" -eq 1 ]; then
  cp -a -- "$ADMIN_ENV_PATH" "$ROLLBACK_DIR/admin.env"
fi
SNAPSHOT_READY=1

echo '==> [远端] 安装白名单 Server 代码与回环 systemd unit'
if ! getent group myproxy >/dev/null 2>&1; then
  groupadd --system myproxy
  MYPROXY_GROUP_CREATED=1
fi
if ! id -u myproxy >/dev/null 2>&1; then
  useradd --system --gid myproxy --home-dir /var/lib/myproxy \
    --shell /usr/sbin/nologin myproxy
  MYPROXY_USER_CREATED=1
fi
if [ "$(id -gn myproxy)" != myproxy ]; then
  echo '错误：已有 myproxy 用户的主组不是 myproxy，拒绝降权部署' >&2
  exit 1
fi
if ! myproxy_groups="$(id -Gn myproxy 2>/dev/null)"; then
  echo '错误：无法读取 myproxy 用户 supplementary groups，拒绝降权部署' >&2
  exit 1
fi
myproxy_extra_groups=""
for myproxy_group_name in $myproxy_groups; do
  if [ "$myproxy_group_name" != myproxy ]; then
    myproxy_extra_groups="${myproxy_extra_groups:+$myproxy_extra_groups }$myproxy_group_name"
  fi
done
if [ -n "$myproxy_extra_groups" ]; then
  echo "错误：既有 myproxy 用户属于 myproxy 之外的 supplementary groups（$myproxy_extra_groups），拒绝部署" >&2
  exit 1
fi
install -d -m 0750 -o root -g myproxy "$REMOTE_DIR" "$ADMIN_DIR"
# Keep the database directory root-only for the complete migration.  The
# unprivileged API account must not be able to race path checks, replace the
# migration temporary, or plant a symlink before the durable rename.
install -d -m 0700 -o root -g root "$STATE_DIR"
rm -rf -- "$REMOTE_DIR/myproxy_server"
cp -a -- "$REMOTE_TMP/myproxy_server" "$REMOTE_DIR/myproxy_server"
find "$REMOTE_DIR/myproxy_server" -type d -exec chmod 0750 {} +
find "$REMOTE_DIR/myproxy_server" -type f -exec chmod 0640 {} +
# The remote-dir archive snapshots deployment.json and restores it on rollback.
install -m 0640 -o root -g myproxy "$REMOTE_TMP/deployment.json" "$REMOTE_DIR/deployment.json"
chown -R root:myproxy "$REMOTE_DIR/myproxy_server"
install -m 0644 -o root -g root "$REMOTE_TMP/myproxy-api.service" "$UNIT_PATH"
install -m 0644 -o root -g root "$REMOTE_TMP/myproxy-xui-helper.service" "$XUI_HELPER_UNIT_PATH"
if [ "$XUI_HELPER_ENV_EXISTED" -eq 0 ]; then
  install -m 0600 -o root -g root "$REMOTE_TMP/myproxy-xui-helper.env" "$XUI_HELPER_ENV_PATH"
else
  chown root:root "$XUI_HELPER_ENV_PATH"
  chmod 0600 "$XUI_HELPER_ENV_PATH"
fi

if [ "$ADMIN_ENV_EXISTED" -eq 0 ]; then
  echo '==> [远端] 首次生成 Admin 并初始化 Device Token 密钥（不打印明文）'
  ADMIN_ENV_PATH="$ADMIN_ENV_PATH" INIT_SECRET_PATH="$INIT_SECRET_PATH" \
    INIT_SECRET_PRESENT="$INIT_SECRET_PRESENT" python3 - <<'PY'
import os
from pathlib import Path
import re
import secrets

path = Path(os.environ["ADMIN_ENV_PATH"])
if os.environ["INIT_SECRET_PRESENT"] == "1":
    assignment = Path(os.environ["INIT_SECRET_PATH"]).read_text(encoding="ascii").strip()
    name, device_secret = assignment.split("=", 1)
    if name != "MYPROXY_DEVICE_TOKEN_SECRET" or not re.fullmatch(
        r"[0-9a-fA-F]{64}", device_secret
    ):
        raise SystemExit(1)
else:
    device_secret = secrets.token_hex(32)
flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
fd = os.open(path, flags, 0o600)
try:
    payload = (
        f"MYPROXY_ADMIN_TOKEN={secrets.token_hex(32)}\n"
        f"MYPROXY_DEVICE_TOKEN_SECRET={device_secret}\n"
    ).encode("ascii")
    os.write(fd, payload)
    os.fsync(fd)
finally:
    os.close(fd)
dir_fd = os.open(path.parent, os.O_RDONLY)
try:
    os.fsync(dir_fd)
finally:
    os.close(dir_fd)
PY
  chown root:myproxy "$ADMIN_ENV_PATH"
  chmod 0640 "$ADMIN_ENV_PATH"
elif [ "$existing_admin_shape" = missing-device-secret ]; then
  echo '==> [远端] 在快照后原子初始化缺失的 Device Token 密钥（不打印明文）'
  ADMIN_ENV_PATH="$ADMIN_ENV_PATH" INIT_SECRET_PATH="$INIT_SECRET_PATH" python3 - <<'PY'
import os
from pathlib import Path
import re

path = Path(os.environ["ADMIN_ENV_PATH"])
secret_path = Path(os.environ["INIT_SECRET_PATH"])
original = path.read_text(encoding="utf-8")
values = {}
for line in original.splitlines():
    stripped = line.strip()
    if not stripped or stripped.startswith("#"):
        continue
    if "=" not in line:
        raise SystemExit(1)
    name, value = line.split("=", 1)
    if name != "MYPROXY_ADMIN_TOKEN":
        raise SystemExit(1)
    values.setdefault(name, []).append(value.strip())
if len(values.get("MYPROXY_ADMIN_TOKEN", [])) != 1 or not re.fullmatch(
    r"[0-9a-fA-F]{64}", values["MYPROXY_ADMIN_TOKEN"][0]
):
    raise SystemExit(1)
assignment = secret_path.read_text(encoding="ascii").strip()
name, device_secret = assignment.split("=", 1)
if name != "MYPROXY_DEVICE_TOKEN_SECRET" or not re.fullmatch(
    r"[0-9a-fA-F]{64}", device_secret
):
    raise SystemExit(1)
separator = "" if not original or original.endswith("\n") else "\n"
payload = (original + separator + f"MYPROXY_DEVICE_TOKEN_SECRET={device_secret}\n").encode(
    "utf-8"
)
temporary = path.with_name(f".admin.env.device-init.{os.getpid()}")
flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
fd = os.open(temporary, flags, 0o600)
try:
    os.write(fd, payload)
    os.fsync(fd)
finally:
    os.close(fd)
try:
    os.replace(temporary, path)
    dir_fd = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(dir_fd)
    finally:
        os.close(dir_fd)
except BaseException:
    temporary.unlink(missing_ok=True)
    raise
PY
  chown root:myproxy "$ADMIN_ENV_PATH"
  chmod 0640 "$ADMIN_ENV_PATH"
fi
validate_admin_env
chown root:myproxy "$ADMIN_ENV_PATH"
chmod 0640 "$ADMIN_ENV_PATH"

if [ -L "$STATE_DIR" ] || { [ -e "$STATE_DIR" ] && [ ! -d "$STATE_DIR" ]; }; then
  echo '错误：数据库目录是符号链接或类型异常，拒绝继续' >&2
  exit 1
fi
if [ -L "$STATE_DIR/myproxy.db" ]; then
  echo '错误：数据库路径是符号链接，拒绝继续' >&2
  exit 1
fi
for legacy_db in "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-wal" "$REMOTE_DIR/myproxy.db-shm"; do
  if [ -L "$legacy_db" ]; then
    echo '错误：旧数据库路径是符号链接，拒绝迁移' >&2
    exit 1
  fi
done
if [ ! -f "$REMOTE_DIR/myproxy.db" ] && {
  [ -e "$REMOTE_DIR/myproxy.db-wal" ] || [ -e "$REMOTE_DIR/myproxy.db-shm" ];
}; then
  echo '错误：发现无主旧 SQLite WAL/SHM，拒绝清理并要求人工恢复' >&2
  exit 1
fi
if [ -e "$REMOTE_DIR/myproxy.db" ] && [ -e "$STATE_DIR/myproxy.db" ]; then
  echo '错误：旧数据库与新状态数据库同时存在，拒绝猜测迁移是否完成' >&2
  exit 1
fi
DB_MIGRATED=0
if [ -f "$REMOTE_DIR/myproxy.db" ] && [ ! -e "$STATE_DIR/myproxy.db" ]; then
  OLD_DB="$REMOTE_DIR/myproxy.db" NEW_DB="$STATE_DIR/myproxy.db" python3 - <<'PY'
import os
import secrets
import sqlite3
import stat
from pathlib import Path

old = Path(os.environ["OLD_DB"])
new = Path(os.environ["NEW_DB"])
directory_fd = os.open(
    str(new.parent),
    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | os.O_NOFOLLOW,
)
try:
    directory_stat = os.fstat(directory_fd)
    if not stat.S_ISDIR(directory_stat.st_mode) or directory_stat.st_uid != 0 or \
       stat.S_IMODE(directory_stat.st_mode) & 0o077:
        raise SystemExit("database directory is not root-only")
    old_stat = os.lstat(old)
    if not stat.S_ISREG(old_stat.st_mode):
        raise SystemExit("legacy database is not a regular file")
    try:
        os.lstat(new)
    except FileNotFoundError:
        pass
    else:
        raise SystemExit("canonical database already exists")
    temporary_name = f".{new.name}.migrate.{os.getpid()}.{secrets.token_hex(8)}"
    fd = os.open(
        temporary_name,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
        0o600,
        dir_fd=directory_fd,
    )
    os.close(fd)
    temporary = new.with_name(temporary_name)
    try:
        source = sqlite3.connect(str(old), timeout=30)
        destination = None
        try:
            destination = sqlite3.connect(str(temporary), timeout=30)
            source.backup(destination)
            check = destination.execute("PRAGMA integrity_check").fetchone()
            if check != ("ok",):
                raise RuntimeError("SQLite integrity check failed")
            destination.commit()
        finally:
            if destination is not None:
                destination.close()
            source.close()
        fd = os.open(
            temporary_name,
            os.O_RDONLY | os.O_NOFOLLOW,
            dir_fd=directory_fd,
        )
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
        os.replace(
            temporary_name,
            new.name,
            src_dir_fd=directory_fd,
            dst_dir_fd=directory_fd,
        )
        final_stat = os.lstat(new)
        if not stat.S_ISREG(final_stat.st_mode) or final_stat.st_uid != 0:
            raise RuntimeError("canonical database was not installed safely")
        os.fsync(directory_fd)
    except BaseException:
        try:
            os.unlink(temporary_name, dir_fd=directory_fd)
        except FileNotFoundError:
            pass
        raise
finally:
    os.close(directory_fd)
PY
  DB_MIGRATED=1
fi
# Legacy cleanup is a post-commit step: rollback still has the complete
# pre-deploy state snapshot if any subsequent step fails.
if [ "$DB_MIGRATED" -eq 1 ]; then
  rm -f -- "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-shm" "$REMOTE_DIR/myproxy.db-wal"
fi
if [ -L "$STATE_DIR/myproxy.db" ] || \
   { [ -e "$STATE_DIR/myproxy.db" ] && [ ! -f "$STATE_DIR/myproxy.db" ]; }; then
  echo '错误：canonical 数据库迁移后不是安全普通文件，拒绝继续' >&2
  exit 1
fi
if [ ! -e "$STATE_DIR/myproxy.db" ]; then
  STATE_DIR="$STATE_DIR" python3 - <<'PY'
import os
import stat
from pathlib import Path

directory = Path(os.environ["STATE_DIR"])
directory_fd = os.open(
    str(directory),
    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | os.O_NOFOLLOW,
)
try:
    directory_stat = os.fstat(directory_fd)
    if not stat.S_ISDIR(directory_stat.st_mode) or directory_stat.st_uid != 0 or \
       stat.S_IMODE(directory_stat.st_mode) & 0o077:
        raise SystemExit("database directory is not root-only")
    fd = os.open(
        "myproxy.db",
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
        0o600,
        dir_fd=directory_fd,
    )
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
    os.fsync(directory_fd)
finally:
    os.close(directory_fd)
PY
fi
if [ -L "$STATE_DIR/myproxy.db" ] || [ ! -f "$STATE_DIR/myproxy.db" ]; then
  echo '错误：canonical 数据库创建后不是安全普通文件，拒绝继续' >&2
  exit 1
fi
chown myproxy:myproxy "$STATE_DIR/myproxy.db"
chmod 0600 "$STATE_DIR/myproxy.db"
# Only after migration, cleanup, and canonical-file validation may the API
# account receive ownership of the state directory.
chown myproxy:myproxy "$STATE_DIR"
chmod 0700 "$STATE_DIR"

if [ -L /etc/myproxy ] || [ -L /etc/myproxy/tls ] || \
   [ ! -d /etc/myproxy ] || [ ! -d /etc/myproxy/tls ]; then
  echo '错误：/etc/myproxy 或 /etc/myproxy/tls 不是安全目录' >&2
  exit 1
fi
chown root:myproxy /etc/myproxy /etc/myproxy/tls
chmod 0750 /etc/myproxy /etc/myproxy/tls
chown root:myproxy /etc/myproxy/tls/server.crt /etc/myproxy/tls/server.key
chmod 0640 /etc/myproxy/tls/server.crt /etc/myproxy/tls/server.key

systemctl daemon-reload
systemctl enable myproxy-xui-helper >/dev/null 2>&1 || true
systemctl restart myproxy-xui-helper
systemctl restart myproxy-api

health_status='000'
for _ in $(seq 1 20); do
  if systemctl is-active --quiet myproxy-api; then
    health_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
      https://127.0.0.1:1820/healthz || true)"
    if [ "$health_status" = 200 ]; then
      break
    fi
  fi
  sleep 1
done
if ! systemctl is-active --quiet myproxy-api || [ "$health_status" != 200 ]; then
  echo "错误：回环 health 验收失败（HTTP $health_status）" >&2
  exit 1
fi
ready_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/readyz || true)"
if [ "$ready_status" != 200 ]; then
  echo "错误：回环 readiness 验收失败（HTTP $ready_status）" >&2
  exit 1
fi
admin_status="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/api/admin/user || true)"
if [ "$admin_status" != 401 ]; then
  echo "错误：未授权 Admin 验收失败（HTTP $admin_status）" >&2
  exit 1
fi
assert_admin_ui

ipv4="$(ss -H -ltn4 'sport = :1820')"
ipv6="$(ss -H -ltn6 'sport = :1820')"
listener_count="$(printf '%s\n' "$ipv4" | awk 'NF { count += 1 } END { print count + 0 }')"
if [ "$listener_count" -ne 1 ] || [ -n "$ipv6" ] || \
   ! printf '%s\n' "$ipv4" | awk 'NF && $4 != "127.0.0.1:1820" { bad=1 } END { exit bad }'; then
  echo '错误：部署后监听不是唯一的 127.0.0.1:1820' >&2
  exit 1
fi

capture_public_listeners >"$ROLLBACK_DIR/listeners.after"
if comm -13 "$ROLLBACK_DIR/listeners.before" "$ROLLBACK_DIR/listeners.after" | grep -q .; then
  echo '错误：部署后出现新的非回环监听，拒绝提交' >&2
  exit 1
fi
assert_no_public_exposure

install -m 0600 /dev/null "$REMOTE_TMP/.ready"
echo '==> [远端] 内部验收通过，等待本机公网探测后提交'
decision=''
for _ in $(seq 1 60); do
  if [ -f "$REMOTE_TMP/.rollback" ]; then
    decision='rollback'
    break
  fi
  if [ -f "$REMOTE_TMP/.commit" ]; then
    # A commit signal can reach the host even when its SSH acknowledgement is
    # lost.  Give the controller a bounded chance to send rollback; rollback
    # always wins if both markers exist.
    for _grace in $(seq 1 5); do
      sleep 1
      if [ -f "$REMOTE_TMP/.rollback" ]; then
        decision='rollback'
        break
      fi
    done
    if [ "$decision" != rollback ]; then
      decision='commit'
    fi
    break
  fi
  sleep 1
done
if [ "$decision" != commit ]; then
  if [ "$decision" = rollback ]; then
    echo '错误：本机公网探测未通过，执行回滚' >&2
  else
    echo '错误：等待本机提交确认超时，执行回滚' >&2
  fi
  exit 1
fi

# The service may fail while the controller performs its external probe.
# Re-check the complete private boundary immediately before committing.
final_health="$(curl --noproxy '*' -ksS -o /dev/null -w '%{http_code}' \
  https://127.0.0.1:1820/healthz || true)"
ipv4="$(ss -H -ltn4 'sport = :1820')"
ipv6="$(ss -H -ltn6 'sport = :1820')"
listener_count="$(printf '%s\n' "$ipv4" | awk 'NF { count += 1 } END { print count + 0 }')"
if ! systemctl is-active --quiet myproxy-api || [ "$final_health" != 200 ] || \
   [ "$listener_count" -ne 1 ] || [ -n "$ipv6" ] || \
   ! printf '%s\n' "$ipv4" | awk 'NF && $4 != "127.0.0.1:1820" { bad=1 } END { exit bad }'; then
  echo '错误：commit 前最终回环状态已变化，执行回滚' >&2
  exit 1
fi
assert_admin_ui
assert_no_public_exposure
DEPLOY_COMMITTED=1
echo '==> [远端] 两阶段提交完成：health=200、Admin=401、仅 127.0.0.1:1820'
if systemctl is-enabled --quiet myproxy-api 2>/dev/null; then
  echo '==> [远端] myproxy-api enabled 状态：yes（沿用部署前状态）'
else
  echo '==> [远端] myproxy-api enabled 状态：no（私有预部署不自动 enable）'
fi
'''


def _validate_host(value: str) -> str:
    if not value or len(value) > 253 or not re.fullmatch(r"[A-Za-z0-9.-]+", value):
        raise PredeployError("--host 必须是显式 IP/域名，且不能包含空白或 SSH 选项")
    try:
        ipaddress.ip_address(value)
    except ValueError:
        labels = value.rstrip(".").split(".")
        if len(labels) < 2 or any(
            not label
            or len(label) > 63
            or label.startswith("-")
            or label.endswith("-")
            for label in labels
        ):
            raise PredeployError("--host 不是有效的 IP 或完整域名")
    return value


def _validate_key(value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_file() or path.is_symlink():
        raise PredeployError(f"SSH 私钥不存在、不是普通文件或是符号链接：{path}")
    return path.resolve()


def _validate_device_secret_file(value: Path) -> Path:
    path = value.expanduser()
    if not path.is_file() or path.is_symlink():
        raise PredeployError("设备密钥文件不存在、不是普通文件或是符号链接")
    try:
        lines = path.read_text(encoding="ascii").splitlines()
    except (OSError, UnicodeError) as exc:
        raise PredeployError("无法安全读取设备密钥文件") from exc
    if len(lines) != 1 or "=" not in lines[0]:
        raise PredeployError("设备密钥文件必须且只能包含一条赋值")
    name, secret = lines[0].split("=", 1)
    if name != "MYPROXY_DEVICE_TOKEN_SECRET" or not re.fullmatch(
        r"[0-9a-fA-F]{64}", secret
    ):
        raise PredeployError("设备密钥文件名称或格式无效")
    return path.resolve()


def _ssh_options(key: Path) -> list[str]:
    return [
        "-i",
        str(key),
        "-o",
        "BatchMode=yes",
        "-o",
        "IdentitiesOnly=yes",
        "-o",
        "StrictHostKeyChecking=yes",
        "-o",
        "ConnectTimeout=10",
        "-o",
        "ServerAliveInterval=15",
        "-o",
        "ServerAliveCountMax=3",
    ]


def _run(
    argv: Sequence[str],
    *,
    input_text: str | None = None,
    label: str,
) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            list(argv),
            input=input_text,
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            check=False,
        )
    except FileNotFoundError as exc:
        raise PredeployError(f"本机缺少命令：{argv[0]}") from exc
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "无错误详情"
        if len(detail) > 2000:
            detail = detail[-2000:]
        raise PredeployError(f"{label}失败（exit {result.returncode}）：{detail}")
    return result


def _run_bytes(
    argv: Sequence[str], *, input_bytes: bytes, label: str
) -> str:
    try:
        result = subprocess.run(
            list(argv),
            input=input_bytes,
            capture_output=True,
            check=False,
        )
    except FileNotFoundError as exc:
        raise PredeployError(f"本机缺少命令：{argv[0]}") from exc
    stdout = result.stdout.decode("utf-8", "replace")
    stderr = result.stderr.decode("utf-8", "replace")
    if result.returncode != 0:
        detail = stderr.strip() or stdout.strip() or "无错误详情"
        if len(detail) > 2000:
            detail = detail[-2000:]
        raise PredeployError(f"{label}失败（exit {result.returncode}）：{detail}")
    return stdout


def _ssh_argv(
    host: str,
    key: Path,
    *,
    args: Sequence[str] = (),
) -> list[str]:
    return [
        "ssh",
        "-T",
        *_ssh_options(key),
        f"{REMOTE_USER}@{host}",
        "/bin/bash",
        "-s",
        "--",
        *args,
    ]


def _ssh_script(
    host: str,
    key: Path,
    script: str,
    *,
    args: Sequence[str] = (),
    label: str = "SSH 远端操作",
) -> str:
    argv = _ssh_argv(host, key, args=args)
    # Send bytes so Windows does not translate LF to CRLF in the remote
    # Bash program (which would turn `pipefail` into `pipefail\r`).
    return _run_bytes(argv, input_bytes=_render_remote_script(script).encode("utf-8"), label=label)


def _scp(host: str, key: Path, sources: Sequence[Path], destination: str) -> None:
    if not sources:
        raise PredeployError("内部错误：SCP 源文件列表为空")
    argv = [
        "scp",
        *_ssh_options(key),
        *(str(source) for source in sources),
        f"{REMOTE_USER}@{host}:{destination}",
    ]
    _run(argv, label="SCP 白名单上传")


def _validate_local_payload() -> tuple[list[Path], Path, Path, Path]:
    server_dir = Path(__file__).resolve().parent.parent
    module_dir = server_dir / "myproxy_server"
    deploy_dir = server_dir / "deploy"
    if not module_dir.is_dir() or module_dir.is_symlink():
        raise PredeployError(f"本地运行时目录缺失或为符号链接：{module_dir}")
    if not deploy_dir.is_dir() or deploy_dir.is_symlink():
        raise PredeployError(f"本地部署目录缺失或为符号链接：{deploy_dir}")
    modules: list[Path] = []
    for name in RUNTIME_FILES:
        path = module_dir / name
        if not path.is_file() or path.is_symlink():
            raise PredeployError(f"本地运行时文件缺失或为符号链接：{path}")
        modules.append(path)
    unit = deploy_dir / "myproxy-api.service"
    if not unit.is_file() or unit.is_symlink():
        raise PredeployError(f"本地 systemd unit 缺失或为符号链接：{unit}")
    helper_unit = deploy_dir / XUI_HELPER_UNIT_NAME
    if not helper_unit.is_file() or helper_unit.is_symlink():
        raise PredeployError(f"本地 x-ui helper unit 缺失或为符号链接：{helper_unit}")
    helper_env = deploy_dir / XUI_HELPER_ENV_NAME
    if not helper_env.is_file() or helper_env.is_symlink():
        raise PredeployError(f"本地 x-ui helper env 缺失或为符号链接：{helper_env}")
    text = unit.read_text(encoding="utf-8")
    required_lines = {
        "ExecStart=/usr/bin/python3 -m myproxy_server",
        "EnvironmentFile=/etc/myproxy-api/admin.env",
        "Environment=MYPROXY_LISTEN_HOST=127.0.0.1",
        "Environment=MYPROXY_LISTEN_PORT=1820",
        "Environment=MYPROXY_DEPLOYMENT_CONFIG=/opt/myproxy-api/deployment.json",
    }
    lines = text.splitlines()
    for line in required_lines:
        if lines.count(line) != 1:
            raise PredeployError(f"本地 systemd unit 必须且只能包含一次：{line}")
    if "0.0.0.0" in text or "MYPROXY_DEVICE_API" in text:
        raise PredeployError("本地 systemd unit 含公网监听或 Device API 网关配置")
    return modules, unit, helper_unit, helper_env


def _create_remote_tmp(host: str, key: Path) -> str:
    output = _ssh_script(
        host,
        key,
        REMOTE_CREATE_TMP_SCRIPT,
        label="创建远端私有临时目录",
    ).strip()
    if not re.fullmatch(r"/tmp/myproxy-private-predeploy\.[A-Za-z0-9]+", output):
        raise PredeployError("远端临时目录响应不符合安全约束")
    return output


def _cleanup_remote_tmp(host: str, key: Path, remote_tmp: str) -> None:
    try:
        _ssh_script(
            host,
            key,
            REMOTE_CLEANUP_TMP_SCRIPT,
            args=(remote_tmp,),
            label="清理远端临时目录",
        )
    except PredeployError as exc:
        print(f"警告：{exc}", file=sys.stderr)


def _transaction_state(host: str, key: Path, remote_tmp: str) -> str:
    state = _ssh_script(
        host,
        key,
        REMOTE_TRANSACTION_STATE_SCRIPT,
        args=(remote_tmp,),
        label="读取私有预部署事务状态",
    ).strip()
    if state not in {"pending", "ready", "gone"}:
        raise PredeployError("远端事务返回未知状态，已 fail-closed")
    return state


def _signal_transaction(host: str, key: Path, remote_tmp: str, decision: str) -> None:
    if decision not in {"commit", "rollback"}:
        raise PredeployError("内部错误：未知事务决定")
    _ssh_script(
        host,
        key,
        REMOTE_TRANSACTION_SIGNAL_SCRIPT,
        args=(remote_tmp, decision),
        label=f"发送远端事务 {decision} 信号",
    )


def _collect_transaction_process(
    process: subprocess.Popen[bytes], *, timeout: float = 75.0
) -> tuple[int, str, str]:
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        process.kill()
        stdout, stderr = process.communicate()
        raise PredeployError(
            "远端事务未在超时内结束；SSH 通道已关闭，远端应由 EXIT trap 或确认超时回滚"
        ) from exc
    return (
        process.returncode,
        stdout.decode("utf-8", "replace"),
        stderr.decode("utf-8", "replace"),
    )


def _run_private_transaction(
    host: str, key: Path, remote_tmp: str
) -> dict[str, str]:
    argv = _ssh_argv(host, key, args=(remote_tmp,))
    try:
        process = subprocess.Popen(
            argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError as exc:
        raise PredeployError("本机缺少命令：ssh") from exc
    assert process.stdin is not None
    # Keep LF bytes intact on Windows; CRLF would break the remote Bash script.
    process.stdin.write(_render_remote_script(REMOTE_DEPLOY_SCRIPT).encode("utf-8"))
    process.stdin.close()
    process.stdin = None

    deadline = time.monotonic() + 45.0
    last_state_error: PredeployError | None = None
    while time.monotonic() < deadline:
        if process.poll() is not None:
            code, stdout, stderr = _collect_transaction_process(process, timeout=1)
            detail = stderr.strip() or stdout.strip() or "无错误详情"
            raise PredeployError(f"远端事务在本机验收前结束（exit {code}）：{detail[-3000:]}")
        try:
            state = _transaction_state(host, key, remote_tmp)
            last_state_error = None
        except PredeployError as exc:
            last_state_error = exc
            time.sleep(1)
            continue
        if state == "ready":
            break
        if state == "gone":
            raise PredeployError("远端事务目录在提交前消失，拒绝继续")
        time.sleep(1)
    else:
        detail = f"：{last_state_error}" if last_state_error is not None else ""
        try:
            _signal_transaction(host, key, remote_tmp, "rollback")
        except PredeployError:
            pass
        code, stdout, stderr = _collect_transaction_process(process)
        raise PredeployError(
            f"等待远端事务 ready 超时{detail}；已要求回滚（exit {code}）"
        )

    print("==> 远端内部验收通过；在回滚仍有效时执行最终只读审计与公网探测", flush=True)
    blockers: list[str] = []
    prepared_audit: dict[str, str] = {}
    try:
        prepared_audit = _parse_audit(
            _ssh_script(
                host,
                key,
                REMOTE_AUDIT_SCRIPT,
                label="提交前远端只读审计",
            )
        )
        blockers.extend(_audit_blockers(prepared_audit))
        if prepared_audit.get("myproxy_active") != "yes":
            blockers.append("提交前 myproxy-api 未 active")
        if prepared_audit.get("xui_helper_active") != "yes":
            blockers.append("提交前 myproxy-xui-helper 未 active")
        if prepared_audit.get("listener_1820") != "loopback":
            blockers.append("提交前未形成唯一回环 1820 监听")
        if prepared_audit.get("admin_dir") != "valid-secure":
            blockers.append("提交前 /etc/myproxy-api 目录未处于安全状态")
        if prepared_audit.get("admin_env") != "valid-secure":
            blockers.append("提交前 admin.env 未处于安全有效状态")
        if prepared_audit.get("xui_helper_env") != "valid-secure":
            blockers.append("提交前 xui-helper.env 未处于 root-only 安全状态")
        admin_ui_state = _ssh_script(
            host,
            key,
            REMOTE_ADMIN_UI_AUDIT_SCRIPT,
            label="提交前私有 Admin UI 验收",
        ).strip()
        if admin_ui_state != "admin_ui=valid":
            blockers.append("提交前私有 Admin UI 响应无效")
    except PredeployError as exc:
        blockers.append(f"提交前远端审计失败：{exc}")
    for port in (_configured_public_port(), PRIVATE_PORT):
        state = "OPEN" if _external_port_open(host, port) else "closed-or-filtered"
        print(f"  提交前公网探测 {host}:{port}: {state}", flush=True)
        if state == "OPEN":
            blockers.append(f"提交前从公网可连接 {host}:{port}")

    decision = "rollback" if blockers else "commit"
    signal_error: PredeployError | None = None
    try:
        _signal_transaction(host, key, remote_tmp, decision)
    except PredeployError as exc:
        signal_error = exc
        if decision == "commit":
            blockers.append(f"提交信号失败：{exc}")
            try:
                _signal_transaction(host, key, remote_tmp, "rollback")
            except PredeployError:
                pass
    code, stdout, stderr = _collect_transaction_process(process)
    if stdout.strip():
        print(stdout.rstrip())
    if blockers or signal_error is not None:
        detail = "；".join(blockers) or str(signal_error)
        if code == 0 and decision == "rollback":
            detail += "；远端异常接受了 rollback 后仍返回成功"
        raise PredeployError(f"两阶段提交未通过并已要求回滚：{detail}")
    if code != 0:
        detail = stderr.strip() or "无错误详情"
        raise PredeployError(f"远端 commit 后事务失败（exit {code}）：{detail[-3000:]}")

    print("==> commit 已确认；执行提交后只读状态复核", flush=True)
    final_blockers: list[str] = []
    final_audit: dict[str, str] = {}
    try:
        final_audit = _parse_audit(
            _ssh_script(
                host,
                key,
                REMOTE_AUDIT_SCRIPT,
                label="提交后远端只读审计",
            )
        )
        final_blockers.extend(_audit_blockers(final_audit))
        if final_audit.get("myproxy_active") != "yes":
            final_blockers.append("提交后 myproxy-api 未 active")
        if final_audit.get("xui_helper_active") != "yes":
            final_blockers.append("提交后 myproxy-xui-helper 未 active")
        if final_audit.get("listener_1820") != "loopback":
            final_blockers.append("提交后未形成唯一回环 1820 监听")
        if final_audit.get("admin_dir") != "valid-secure":
            final_blockers.append("提交后 /etc/myproxy-api 目录未处于安全状态")
        if final_audit.get("admin_env") != "valid-secure":
            final_blockers.append("提交后 admin.env 未处于安全有效状态")
        if final_audit.get("xui_helper_env") != "valid-secure":
            final_blockers.append("提交后 xui-helper.env 未处于 root-only 安全状态")
        admin_ui_state = _ssh_script(
            host,
            key,
            REMOTE_ADMIN_UI_AUDIT_SCRIPT,
            label="提交后私有 Admin UI 验收",
        ).strip()
        if admin_ui_state != "admin_ui=valid":
            final_blockers.append("提交后私有 Admin UI 响应无效")
    except PredeployError as exc:
        final_blockers.append(f"提交后远端审计失败：{exc}")
    for port in (_configured_public_port(), PRIVATE_PORT):
        if _external_port_open(host, port):
            final_blockers.append(f"提交后从公网可连接 {host}:{port}")
    if final_blockers:
        stop_detail = ""
        try:
            _ssh_script(
                host,
                key,
                REMOTE_EMERGENCY_STOP_SCRIPT,
                label="提交后异常的紧急停服",
            )
            stop_detail = "；已紧急停止 myproxy-api 以保持 fail-closed"
        except PredeployError as exc:
            stop_detail = f"；紧急停服也失败：{exc}"
        raise PredeployError("提交后复核失败：" + "；".join(final_blockers) + stop_detail)
    return final_audit


def _deploy(
    host: str, key: Path, device_secret_file: Path | None = None
) -> dict[str, str]:
    modules, unit, helper_unit, helper_env = _validate_local_payload()
    deployment = _deployment_payload()
    remote_tmp = _create_remote_tmp(host, key)
    transaction_started = False
    try:
        _ssh_script(
            host,
            key,
            REMOTE_PREPARE_UPLOAD_SCRIPT,
            args=(remote_tmp,),
            label="准备远端白名单上传目录",
        )
        _scp(host, key, modules, f"{remote_tmp}/myproxy_server/")
        _scp(host, key, (unit,), f"{remote_tmp}/myproxy-api.service")
        _scp(host, key, (helper_unit,), f"{remote_tmp}/{XUI_HELPER_UNIT_NAME}")
        _scp(host, key, (helper_env,), f"{remote_tmp}/{XUI_HELPER_ENV_NAME}")
        _scp(host, key, (deployment,), f"{remote_tmp}/deployment.json")
        if device_secret_file is not None:
            _scp(host, key, (device_secret_file,), f"{remote_tmp}/device-secret.env")
            _ssh_script(
                host,
                key,
                REMOTE_FINALIZE_SECRET_UPLOAD_SCRIPT,
                args=(remote_tmp,),
                label="固定远端设备密钥初始化载荷权限",
            )
        print("==> 白名单载荷已上传；开始带自动回滚的远端事务")
        transaction_started = True
        return _run_private_transaction(host, key, remote_tmp)
    except PredeployError as exc:
        if transaction_started:
            raise PredeployError(
                f"{exc}\n远端事务已启动；若 SSH 在事务中断开，请先检查保留路径 {remote_tmp} 与服务状态。"
            ) from exc
        raise
    finally:
        if not transaction_started:
            _cleanup_remote_tmp(host, key, remote_tmp)


def _parse_audit(output: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for raw_line in output.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if "=" not in line:
            raise PredeployError("远端审计返回了无法解析的输出，已 fail-closed")
        key, value = line.split("=", 1)
        if key not in AUDIT_KEYS or key in result or not value:
            raise PredeployError("远端审计字段缺失、重复或未知，已 fail-closed")
        result[key] = value
    missing = AUDIT_KEYS - result.keys()
    if missing:
        raise PredeployError(f"远端审计缺少字段：{', '.join(sorted(missing))}")
    if result["audit_version"] != "1":
        raise PredeployError("远端审计版本不匹配")
    return result


def _audit_blockers(
    audit: Mapping[str, str], *, allow_missing_device_secret: bool = False
) -> list[str]:
    blockers: list[str] = []
    expected = {
        "remote_user": "root",
        "python3": "ok",
        "curl": "ok",
        "ss": "ok",
        "systemctl": "ok",
        "tls_cert": "present",
        "tls_key": "present",
        "xui_db": "present",
    }
    for key, value in expected.items():
        if audit.get(key) != value:
            blockers.append(f"{key}={audit.get(key, 'missing')}（要求 {value}）")
    allowed_admin_env = {"missing", "valid-secure", "valid-legacy"}
    if allow_missing_device_secret:
        allowed_admin_env.add("missing-device-secret")
    if audit.get("admin_env") not in allowed_admin_env:
        blockers.append(
            f"admin_env={audit.get('admin_env')}（必须首次缺失、安全有效，或显式初始化缺失的设备密钥）"
        )
    if audit.get("admin_dir") not in {"missing", "valid-secure"}:
        blockers.append(f"admin_dir={audit.get('admin_dir')}（目录必须缺失或安全有效）")
    if audit.get("xui_helper_env") not in {"missing", "valid-secure"}:
        blockers.append(
            f"xui_helper_env={audit.get('xui_helper_env')}（必须缺失或 root-only 安全有效）"
        )
    if audit.get("listener_1820") not in {"none", "loopback"}:
        blockers.append(f"listener_1820={audit.get('listener_1820')}（必须无监听或仅回环）")
    if audit.get("public_listener") != "no":
        blockers.append(
            f"public_listener={audit.get('public_listener')}（不得有公网 API 监听）"
        )
    if audit.get("nginx_myproxy_config") != "absent":
        blockers.append("发现既有 MyProxy nginx 配置")
    for key in ("nginx_upstream_1820", "nginx_public_listen"):
        if audit.get(key) not in {"no", "absent"}:
            blockers.append(f"{key}={audit.get(key)}（检测到配置或无法安全扫描）")
    if audit.get("ufw_state") in {"error", "active-public-allowed"}:
        blockers.append(f"ufw_state={audit.get('ufw_state')}（状态不可确认或已有公网 API 放行）")
    return blockers


def _print_audit(audit: Mapping[str, str]) -> None:
    print("远端私有预部署审计（未打印凭据或配置内容）：")
    for key in sorted(AUDIT_KEYS - {"audit_version"}):
        print(f"  {key}: {audit[key]}")


def _external_port_open(host: str, port: int, timeout: float = 3.0) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except (OSError, TimeoutError):
        return False


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", help="目标 VPS IP 或完整域名")
    parser.add_argument("--ssh-key", help="显式 SSH 私钥路径")
    parser.add_argument(
        "--config",
        type=Path,
        help="只含 host 与 ssh_key 的本机 JSON；不能与对应命令行参数混用",
    )
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument(
        "--audit-only",
        action="store_true",
        help="只执行远端只读审计，不上传或修改服务",
    )
    mode.add_argument(
        "--deploy",
        action="store_true",
        help="审计通过后执行仅回环的事务式私有预部署",
    )
    parser.add_argument(
        "--confirm",
        help="部署时必须精确填写 PRIVATE-PREDEPLOY；审计模式不需要",
    )
    parser.add_argument(
        "--initialize-device-secret-file",
        type=Path,
        help="首次部署时显式初始化缺失的设备密钥；文件只允许一条 64 位十六进制赋值",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        config: dict[str, object] = {}
        if args.config is not None:
            if args.host is not None or args.ssh_key is not None:
                raise PredeployError("--config 不能与 --host/--ssh-key 混用")
            try:
                loaded = json.loads(args.config.read_text(encoding="utf-8"))
            except (OSError, UnicodeError, json.JSONDecodeError) as exc:
                raise PredeployError(f"无法读取本机预部署配置：{args.config}") from exc
            if not isinstance(loaded, dict) or set(loaded) != {"host", "ssh_key"}:
                raise PredeployError("预部署配置必须且只能包含 host 与 ssh_key")
            config = loaded
        host_value = config.get("host", args.host)
        key_value = config.get("ssh_key", args.ssh_key)
        if not isinstance(host_value, str) or not isinstance(key_value, str):
            raise PredeployError("必须通过命令行或 --config 显式提供 host 与 ssh_key")
        host = _validate_host(host_value)
        key = _validate_key(key_value)
        if args.audit_only and args.confirm is not None:
            raise PredeployError("--audit-only 不接受 --confirm")
        if args.audit_only and args.initialize_device_secret_file is not None:
            raise PredeployError("--audit-only 不接受 --initialize-device-secret-file")
        if args.deploy and args.confirm != "PRIVATE-PREDEPLOY":
            raise PredeployError("部署必须显式设置 --confirm PRIVATE-PREDEPLOY")
        if args.deploy:
            _deployment_payload()
        device_secret_file = None
        if args.initialize_device_secret_file is not None:
            device_secret_file = _validate_device_secret_file(
                args.initialize_device_secret_file
            )
        print(f"==> 私有预部署审计目标：{REMOTE_USER}@{host}")
        audit = _parse_audit(
            _ssh_script(host, key, REMOTE_AUDIT_SCRIPT, label="SSH 远端只读审计")
        )
        _print_audit(audit)
        blockers = _audit_blockers(
            audit, allow_missing_device_secret=device_secret_file is not None
        )
        if device_secret_file is not None and audit.get("admin_env") not in {
            "missing",
            "missing-device-secret",
        }:
            blockers.append("远端并非缺失设备密钥，拒绝初始化以避免轮换")
        print("==> 从本机验证配置的公网 API 端口及私有后端端口不可连接")
        for port in (_configured_public_port(), PRIVATE_PORT):
            state = "OPEN" if _external_port_open(host, port) else "closed-or-filtered"
            print(f"  {host}:{port}: {state}")
            if state == "OPEN":
                blockers.append(f"从公网可连接 {host}:{port}")
        if blockers:
            print("审计未通过，未修改远端：", file=sys.stderr)
            for blocker in blockers:
                print(f"  - {blocker}", file=sys.stderr)
            return 2
        if args.audit_only:
            print("==> 审计通过：当前没有检测到 MyProxy 公网入口；尚未修改远端")
            return 0
        print("==> 审计通过；开始私有预部署（不会调用 nginx 或 UFW 修改命令）")
        committed_audit = _deploy(host, key, device_secret_file)
        print("==> 提交前最终审计结果（提交时未改变 nginx/UFW）：")
        _print_audit(committed_audit)
        print("==> 私有预部署成功：服务 active，仅 127.0.0.1:1820，公网 API 及私有后端端口不可连接")
        return 0
    except PredeployError as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
