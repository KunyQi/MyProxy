#!/usr/bin/env bash
# Install a TLS nginx entry point that exposes only MyProxy Device API routes.
set -euo pipefail

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "错误：请以 root 运行" >&2
  exit 1
fi
if [ "${MYPROXY_DEPLOY_CONFIRM:-}" != "YES" ]; then
  echo "错误：独立安装器会修改并 reload nginx；请显式设置 MYPROXY_DEPLOY_CONFIRM=YES" >&2
  exit 1
fi
for required in \
  MYPROXY_DEVICE_API_SERVER_NAME \
  MYPROXY_DEVICE_API_TLS_MODE \
  MYPROXY_DEVICE_API_TLS_CERT \
  MYPROXY_DEVICE_API_TLS_KEY \
  MYPROXY_DEVICE_API_PUBLIC_PORT \
  MYPROXY_DEVICE_API_UPSTREAM_PORT; do
  if [ -z "${!required:-}" ]; then
    echo "错误：独立安装器必须显式设置 ${required}；不会使用生产默认值" >&2
    exit 1
  fi
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="${MYPROXY_DEVICE_API_TEMPLATE:-$SCRIPT_DIR/nginx-device-api.conf.template}"
SERVER_NAME="$MYPROXY_DEVICE_API_SERVER_NAME"
TLS_MODE="$MYPROXY_DEVICE_API_TLS_MODE"
TLS_CERT="$MYPROXY_DEVICE_API_TLS_CERT"
TLS_KEY="$MYPROXY_DEVICE_API_TLS_KEY"
PUBLIC_PORT="$MYPROXY_DEVICE_API_PUBLIC_PORT"
UPSTREAM_PORT="$MYPROXY_DEVICE_API_UPSTREAM_PORT"
TARGET="/etc/nginx/conf.d/myproxy-device-api.conf"
DEFAULT_SITE="/etc/nginx/sites-enabled/default"
RENDERED="$(mktemp /tmp/myproxy-device-api.conf.XXXXXX)"
PROBE_BODY=""
BACKUP=""
DEFAULT_BACKUP=""
DEFAULT_SITE_EXISTED=0
RESTORE_NEEDED=0

cleanup() {
  local exit_status="$?"
  local restore_failed=0
  trap - EXIT
  # EXIT traps run for every errexit failure.  Keep cleanup itself best-effort
  # so one failed restore operation cannot prevent the other target from being
  # restored.
  set +e
  if [ "$RESTORE_NEEDED" -eq 1 ]; then
    if [ -n "$BACKUP" ]; then
      rm -f -- "$TARGET"
      if ! cp -a -- "$BACKUP" "$TARGET"; then
        echo "错误：EXIT 回滚无法恢复原 Device API nginx 配置，请立即人工处理" >&2
        restore_failed=1
      fi
    else
      if ! rm -f -- "$TARGET"; then
        echo "错误：EXIT 回滚无法移除新 Device API nginx 配置，请立即人工处理" >&2
        restore_failed=1
      fi
    fi
    if ! rm -f -- "$DEFAULT_SITE"; then
      echo "错误：EXIT 回滚无法清理 nginx 默认站点路径，请立即人工处理" >&2
      restore_failed=1
    fi
    if [ "$DEFAULT_SITE_EXISTED" -eq 1 ] && [ -n "$DEFAULT_BACKUP" ]; then
      if ! cp -a -- "$DEFAULT_BACKUP" "$DEFAULT_SITE"; then
        echo "错误：EXIT 回滚无法恢复原 nginx 默认站点，请立即人工处理" >&2
        restore_failed=1
      fi
    fi
    # The replacement may already have been loaded before a later acceptance
    # check failed.  Bring the running nginx state back in sync with the files.
    if [ "$restore_failed" -eq 0 ]; then
      if nginx -t >/dev/null 2>&1; then
        systemctl reload nginx >/dev/null 2>&1 || \
          echo "错误：原 nginx 配置已恢复，但自动 reload 失败，请立即人工处理" >&2
      else
        echo "错误：原 nginx 文件已恢复但校验失败，未 reload，请立即人工处理" >&2
      fi
    fi
  fi
  rm -f -- "$RENDERED"
  if [ -n "$PROBE_BODY" ]; then
    rm -f -- "$PROBE_BODY"
  fi
  if [ -n "$BACKUP" ]; then
    rm -f -- "$BACKUP"
  fi
  if [ -n "$DEFAULT_BACKUP" ]; then
    rm -f -- "$DEFAULT_BACKUP"
  fi
  exit "$exit_status"
}
trap cleanup EXIT
PROBE_BODY="$(mktemp /tmp/myproxy-device-api-probe.XXXXXX)"

restore_previous_nginx_config() {
  if [ -n "$BACKUP" ]; then
    rm -f -- "$TARGET"
    cp -a -- "$BACKUP" "$TARGET"
  else
    rm -f -- "$TARGET"
  fi
  rm -f -- "$DEFAULT_SITE"
  if [ "$DEFAULT_SITE_EXISTED" -eq 1 ]; then
    cp -a -- "$DEFAULT_BACKUP" "$DEFAULT_SITE"
  fi
  RESTORE_NEEDED=0
}

if ! command -v nginx >/dev/null 2>&1; then
  echo "错误：未安装 nginx；请先用系统包管理器安装" >&2
  exit 1
fi
if ! command -v openssl >/dev/null 2>&1; then
  echo "错误：未安装 openssl" >&2
  exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "错误：未安装 curl" >&2
  exit 1
fi
if [ ! -f "$TEMPLATE" ] || [ -L "$TEMPLATE" ]; then
  echo "错误：反代模板不存在或是符号链接：$TEMPLATE" >&2
  exit 1
fi
if [[ ! "$SERVER_NAME" =~ ^[A-Za-z0-9.-]+$ ]] || [[ "$SERVER_NAME" != *.* ]]; then
  echo "错误：MYPROXY_DEVICE_API_SERVER_NAME 必须是有效 IP 或域名" >&2
  exit 1
fi
if [[ ! "$PUBLIC_PORT" =~ ^[0-9]+$ ]] || [ "$PUBLIC_PORT" -lt 1 ] || [ "$PUBLIC_PORT" -gt 65535 ]; then
  echo "错误：MYPROXY_DEVICE_API_PUBLIC_PORT 必须是 1-65535" >&2
  exit 1
fi
if [[ ! "$UPSTREAM_PORT" =~ ^[0-9]+$ ]] || [ "$UPSTREAM_PORT" -lt 1 ] || [ "$UPSTREAM_PORT" -gt 65535 ]; then
  echo "错误：MYPROXY_DEVICE_API_UPSTREAM_PORT 必须是 1-65535" >&2
  exit 1
fi
if [[ ! "$TLS_CERT" =~ ^/[A-Za-z0-9._/-]+$ ]] || [ ! -f "$TLS_CERT" ]; then
  echo "错误：公网 TLS 证书路径无效：$TLS_CERT" >&2
  exit 1
fi
if [[ ! "$TLS_KEY" =~ ^/[A-Za-z0-9._/-]+$ ]] || [ ! -f "$TLS_KEY" ]; then
  echo "错误：公网 TLS 私钥路径无效：$TLS_KEY" >&2
  exit 1
fi
if [ "$TLS_MODE" != "public-ca" ]; then
  echo "错误：公网 TLS 使用 public-ca 和系统信任库" >&2
  exit 1
fi
# Validate expiry and host coverage first. The final request verifies the
# served full chain against the system CA trust store after reload.
openssl x509 -in "$TLS_CERT" -noout -checkend 0 >/dev/null
if [[ "$SERVER_NAME" =~ ^[0-9.]+$ ]]; then
  openssl x509 -in "$TLS_CERT" -noout -checkip "$SERVER_NAME" >/dev/null
else
  openssl x509 -in "$TLS_CERT" -noout -checkhost "$SERVER_NAME" >/dev/null
fi
if ! curl -ksSf "https://127.0.0.1:${UPSTREAM_PORT}/healthz" >/dev/null; then
  echo "错误：私有后端 https://127.0.0.1:${UPSTREAM_PORT}/healthz 不可用" >&2
  exit 1
fi
# Match the client probe contract before changing nginx: exact GET, exact 204,
# no response body. curl does not follow redirects unless -L is requested.
: > "$PROBE_BODY"
UPSTREAM_PROBE_STATUS="$(curl -ksS --max-time 5 --output "$PROBE_BODY" \
  --write-out '%{http_code}' "https://127.0.0.1:${UPSTREAM_PORT}/connectivity-check" || true)"
if [ "$UPSTREAM_PROBE_STATUS" != "204" ] || [ -s "$PROBE_BODY" ]; then
  echo "错误：私有后端 connectivity-check 必须返回空 HTTP 204（HTTP $UPSTREAM_PROBE_STATUS）" >&2
  exit 1
fi
if [ -L "$TARGET" ]; then
  echo "错误：目标 nginx 配置是符号链接，拒绝覆盖" >&2
  exit 1
fi
if [ -e "$TARGET" ] && [ ! -f "$TARGET" ]; then
  echo "错误：目标 nginx 配置不是普通文件，拒绝覆盖" >&2
  exit 1
fi
sed \
  -e "s|__SERVER_NAME__|$SERVER_NAME|g" \
  -e "s|__TLS_CERT__|$TLS_CERT|g" \
  -e "s|__TLS_KEY__|$TLS_KEY|g" \
  -e "s|__PUBLIC_PORT__|$PUBLIC_PORT|g" \
  -e "s|__UPSTREAM_PORT__|$UPSTREAM_PORT|g" \
  "$TEMPLATE" > "$RENDERED"

if [ -f "$TARGET" ]; then
  BACKUP="$(mktemp /tmp/myproxy-device-api.previous.XXXXXX)"
  rm -f -- "$BACKUP"
  cp -a -- "$TARGET" "$BACKUP"
fi
if [ -e "$DEFAULT_SITE" ] || [ -L "$DEFAULT_SITE" ]; then
  if [ -e "$DEFAULT_SITE" ] && [ ! -f "$DEFAULT_SITE" ] && [ ! -L "$DEFAULT_SITE" ]; then
    echo "错误：nginx 默认站点目标类型异常，拒绝覆盖" >&2
    exit 1
  fi
  DEFAULT_SITE_EXISTED=1
  DEFAULT_BACKUP="$(mktemp /tmp/myproxy-nginx-default.previous.XXXXXX)"
  rm -f -- "$DEFAULT_BACKUP"
  cp -a -- "$DEFAULT_SITE" "$DEFAULT_BACKUP"
fi
# Arm the EXIT rollback before the first mutation.  In particular, TARGET may
# be the only existing file when sites-enabled/default is absent; an install
# failure must still remove a partially-created target or restore its backup.
RESTORE_NEEDED=1
rm -f -- "$DEFAULT_SITE"
install -m 0644 "$RENDERED" "$TARGET"
if ! nginx -t; then
  restore_previous_nginx_config
  nginx -t >/dev/null
  echo "错误：nginx 配置校验失败，已恢复原配置" >&2
  exit 1
fi

if ! systemctl reload nginx; then
  restore_previous_nginx_config
  nginx -t >/dev/null
  systemctl reload nginx || true
  echo "错误：nginx reload 失败，已恢复原配置并再次尝试 reload" >&2
  exit 1
fi
if [ -e "$DEFAULT_SITE" ] || [ -L "$DEFAULT_SITE" ]; then
  restore_previous_nginx_config
  nginx -t >/dev/null
  systemctl reload nginx || true
  echo "错误：nginx 默认站点仍处于启用状态，已恢复原配置" >&2
  exit 1
fi
if ! NGINX_CONFIG_DUMP="$(nginx -T 2>/dev/null)" || \
   printf '%s\n' "$NGINX_CONFIG_DUMP" | grep -Eq '^[[:space:]]*listen[[:space:]]+([^[:space:];]+:)?80([[:space:];]|$)'; then
  restore_previous_nginx_config
  nginx -t >/dev/null
  systemctl reload nginx || true
  echo "错误：最终 nginx 配置仍暴露 HTTP :80，已恢复原配置" >&2
  exit 1
fi
# Keep rollback armed until the public endpoint passes standard TLS validation
# and the in-tunnel probe route returns an exact, empty 204. No -k or -L here:
# the system CA and the configured hostname must validate, and redirects fail.
if ! curl -sSf --resolve "${SERVER_NAME}:${PUBLIC_PORT}:127.0.0.1" \
  "https://${SERVER_NAME}:${PUBLIC_PORT}/healthz" >/dev/null; then
  echo "错误：公网证书链、有效期或主机名验证失败，恢复原网关配置" >&2
  exit 1
fi
: > "$PROBE_BODY"
PUBLIC_PROBE_STATUS="$(curl -sS --max-time 5 \
  --resolve "${SERVER_NAME}:${PUBLIC_PORT}:127.0.0.1" \
  --output "$PROBE_BODY" --write-out '%{http_code}' \
  "https://${SERVER_NAME}:${PUBLIC_PORT}/connectivity-check" || true)"
if [ "$PUBLIC_PROBE_STATUS" != "204" ] || [ -s "$PROBE_BODY" ]; then
  echo "错误：公网 connectivity-check 必须经系统 CA 返回空 HTTP 204（HTTP $PUBLIC_PROBE_STATUS），恢复原网关配置" >&2
  exit 1
fi
RESTORE_NEEDED=0
echo "已启用 https://${SERVER_NAME}:${PUBLIC_PORT} 的 Device API 精确路由白名单。"
echo "Admin API 仍仅可通过私有后端/SSH 隧道访问。"
echo "如使用 UFW，请分别确认并放行 ${PUBLIC_PORT}/tcp 的 IPv4/IPv6 规则。"
