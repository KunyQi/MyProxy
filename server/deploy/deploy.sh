#!/usr/bin/env bash
# MyProxy Server 部署脚本：从本地仓库上传到 VPS，安装并启动 myproxy-api 服务。
# 用法：MYPROXY_DEPLOY_CONFIRM=YES MYPROXY_VPS_HOST=... \
#   MYPROXY_DEVICE_API_TLS_CERT=/path/to/fullchain.pem \
#   MYPROXY_SSH_KEY=... bash deploy/deploy.sh
set -euo pipefail

VPS_HOST="${MYPROXY_VPS_HOST:-}"
SSH_KEY="${MYPROXY_SSH_KEY:-}"
VPS_USER="root"
DEVICE_API_SERVER_NAME="${MYPROXY_DEVICE_API_SERVER_NAME:-}"
DEVICE_API_TLS_MODE="${MYPROXY_DEVICE_API_TLS_MODE:-public-ca}"
DEVICE_API_TLS_CERT="${MYPROXY_DEVICE_API_TLS_CERT:-}"
DEVICE_API_TLS_KEY="${MYPROXY_DEVICE_API_TLS_KEY:-}"
DEVICE_API_PUBLIC_PORT="${MYPROXY_DEVICE_API_PUBLIC_PORT:-}"

SSH_OPTS=(
  -i "$SSH_KEY"
  -o BatchMode=yes
  -o IdentitiesOnly=yes
  -o StrictHostKeyChecking=yes
  -o ConnectTimeout=10
  -o ServerAliveInterval=15
  -o ServerAliveCountMax=3
)

if [ "${MYPROXY_DEPLOY_CONFIRM:-}" != "YES" ]; then
  echo "错误：部署是破坏性远程操作；请显式设置 MYPROXY_DEPLOY_CONFIRM=YES" >&2
  exit 1
fi
if [ -z "$VPS_HOST" ] || [[ ! "$VPS_HOST" =~ ^[A-Za-z0-9.-]+$ ]] || [[ "$VPS_HOST" != *.* ]]; then
  echo "错误：必须显式设置 MYPROXY_VPS_HOST（需为 IP/域名，不能留空或含空白）" >&2
  exit 1
fi
if [ -z "$SSH_KEY" ]; then
  echo "错误：必须显式设置 MYPROXY_SSH_KEY；不会默认使用本机私钥" >&2
  exit 1
fi
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOYMENT_CONFIG="${MYPROXY_DEPLOYMENT_CONFIG:-$SERVER_DIR/../deployment.json}"
REMOTE_DIR="/opt/myproxy-api"
REMOTE_TMP=""
ADMIN_TOKEN_TMP=""

cleanup() {
  if [ -n "$ADMIN_TOKEN_TMP" ] && [ -f "$ADMIN_TOKEN_TMP" ]; then
    rm -f -- "$ADMIN_TOKEN_TMP"
  fi
  if [ -n "$REMOTE_TMP" ]; then
    if [[ "$REMOTE_TMP" =~ ^/tmp/myproxy-api-deploy\.[A-Za-z0-9]+$ ]]; then
      ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
        "rm -rf -- '$REMOTE_TMP'" >/dev/null 2>&1 || true
    fi
  fi
}
trap cleanup EXIT

if [ ! -f "$SSH_KEY" ]; then
  echo "错误：SSH 私钥不存在：$SSH_KEY" >&2
  exit 1
fi

if [ ! -d "$SERVER_DIR/myproxy_server" ] || [ -L "$SERVER_DIR/myproxy_server" ]; then
  echo "错误：本地 myproxy_server 目录缺失或为符号链接：$SERVER_DIR/myproxy_server" >&2
  exit 1
fi
if [ ! -d "$SERVER_DIR/deploy" ] || [ -L "$SERVER_DIR/deploy" ]; then
  echo "错误：本地 deploy 目录缺失或为符号链接：$SERVER_DIR/deploy" >&2
  exit 1
fi

# Upload an explicit runtime allowlist. Never recursively copy a developer's
# working directory, which may contain untracked caches or misplaced secrets.
SERVER_MODULE_FILES=(
  __init__.py
  __main__.py
  admin_ui.py
  app.py
  auth.py
  config.py
  db.py
  observability.py
  release.py
  server.py
  xui.py
  xui_helper.py
)
SERVER_UPLOADS=()
for module_name in "${SERVER_MODULE_FILES[@]}"; do
  module_path="$SERVER_DIR/myproxy_server/$module_name"
  if [ ! -f "$module_path" ] || [ -L "$module_path" ]; then
    echo "错误：Server 运行时文件缺失或为符号链接：$module_path" >&2
    exit 1
  fi
  SERVER_UPLOADS+=("$module_path")
done

DEPLOY_UPLOADS=(
  "$DEPLOYMENT_CONFIG"
  "$SERVER_DIR/deploy/myproxy-api.service"
  "$SERVER_DIR/deploy/myproxy-xui-helper.service"
  "$SERVER_DIR/deploy/myproxy-xui-helper.env"
  "$SERVER_DIR/deploy/nginx-device-api.conf.template"
  "$SERVER_DIR/deploy/install-device-api-proxy.sh"
)
for upload_path in "${DEPLOY_UPLOADS[@]}"; do
  if [ ! -f "$upload_path" ] || [ -L "$upload_path" ]; then
    echo "错误：部署资源缺失或为符号链接：$upload_path" >&2
    exit 1
  fi
done
if [ -L "$SERVER_DIR/run_local.py" ]; then
  echo "错误：可选 run_local.py 不能是符号链接：$SERVER_DIR/run_local.py" >&2
  exit 1
fi

if command -v python3 >/dev/null 2>&1 && python3 --version >/dev/null 2>&1; then
  PYTHON_BIN="python3"
elif command -v python >/dev/null 2>&1 && python --version >/dev/null 2>&1; then
  PYTHON_BIN="python"
else
  echo "错误：本地需要可用的 python3/python 用于生成 Admin Token。" >&2
  exit 1
fi

# Resolve the deployment origin before contacting SSH.
DEPLOYMENT_ORIGIN="$(PYTHONPATH="$SERVER_DIR" MYPROXY_DEPLOYMENT_CONFIG="$DEPLOYMENT_CONFIG" \
  "$PYTHON_BIN" -c 'from myproxy_server.config import Settings; s=Settings.from_env(); s.require_configured_deployment(); print(s.api_host); print(s.api_port)')"
DEPLOYMENT_HOST="${DEPLOYMENT_ORIGIN%%$'\n'*}"
DEPLOYMENT_PORT="${DEPLOYMENT_ORIGIN##*$'\n'}"
if { [ -n "$DEVICE_API_SERVER_NAME" ] && [ "$DEVICE_API_SERVER_NAME" != "$DEPLOYMENT_HOST" ]; } || \
   { [ -n "$DEVICE_API_PUBLIC_PORT" ] && [ "$DEVICE_API_PUBLIC_PORT" != "$DEPLOYMENT_PORT" ]; }; then
  echo "错误：公网名称/端口必须与 deployment.json api_base_url 一致" >&2
  exit 1
fi
DEVICE_API_SERVER_NAME="$DEPLOYMENT_HOST"
DEVICE_API_PUBLIC_PORT="$DEPLOYMENT_PORT"
DEVICE_API_TLS_CERT="${DEVICE_API_TLS_CERT:-/etc/letsencrypt/live/$DEVICE_API_SERVER_NAME/fullchain.pem}"
DEVICE_API_TLS_KEY="${DEVICE_API_TLS_KEY:-/etc/letsencrypt/live/$DEVICE_API_SERVER_NAME/privkey.pem}"
if [[ ! "$DEVICE_API_SERVER_NAME" =~ ^[A-Za-z0-9.-]+$ ]] || [[ "$DEVICE_API_SERVER_NAME" != *.* ]]; then
  echo "错误：网关部署要求 DNS 名称或 IPv4 地址" >&2
  exit 1
fi
if [ "$DEVICE_API_TLS_MODE" != "public-ca" ]; then
  echo "错误：公网 TLS 使用 public-ca 和系统信任库" >&2
  exit 1
fi
if [[ ! "$DEVICE_API_TLS_CERT" =~ ^/[A-Za-z0-9._/-]+$ ]] || \
   [[ ! "$DEVICE_API_TLS_KEY" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
  echo "错误：公网 TLS 证书/私钥必须是安全的远程绝对路径" >&2
  exit 1
fi
if [ "$DEVICE_API_TLS_CERT" = /etc/myproxy/tls/server.crt ] || \
   [ "$DEVICE_API_TLS_KEY" = /etc/myproxy/tls/server.key ]; then
  echo "错误：公网 CA 证书与私有 loopback 后端证书必须使用独立路径" >&2
  exit 1
fi

echo "==> 部署目标：${VPS_USER}@${VPS_HOST}"
echo "==> SSH 私钥：${SSH_KEY}"
echo "==> 本地仓库目录：${SERVER_DIR}"
echo "==> 检查 SSH 连通性..."
ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" "true"

echo "==> 前置检查公网 Device API 依赖..."
if ! ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
  "command -v nginx >/dev/null && command -v openssl >/dev/null && command -v curl >/dev/null && command -v python3 >/dev/null"; then
  echo "错误：VPS 缺少 nginx/openssl/curl/python3；请先执行 apt-get install -y nginx openssl curl python3 后重试。" >&2
  exit 1
fi
if ! ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
  "test -f '$DEVICE_API_TLS_CERT' && test -f '$DEVICE_API_TLS_KEY'"; then
  echo "错误：VPS 缺少公网 TLS 证书或私钥：$DEVICE_API_TLS_CERT / $DEVICE_API_TLS_KEY" >&2
  exit 1
fi
echo "==> 创建随机远程临时目录..."
REMOTE_TMP="$(ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
  'umask 077; d="$(mktemp -d /tmp/myproxy-api-deploy.XXXXXX)"; chmod 700 "$d"; printf "%s" "$d"')"
if [[ ! "$REMOTE_TMP" =~ ^/tmp/myproxy-api-deploy\.[A-Za-z0-9]+$ ]]; then
  echo "错误：远程临时目录不符合安全约束" >&2
  exit 1
fi
echo "==> 远程临时目录已创建"

echo "==> 按运行时白名单上传 myproxy_server/"
ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
  "install -d -m 0700 '$REMOTE_TMP/myproxy_server'"
scp "${SSH_OPTS[@]}" "${SERVER_UPLOADS[@]}" \
  "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/myproxy_server/"

echo "==> 上传 run_local.py（可选，存在才上传）"
if [ -f "$SERVER_DIR/run_local.py" ]; then
  scp "${SSH_OPTS[@]}" "$SERVER_DIR/run_local.py" "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/run_local.py"
else
  echo "==> 未发现 run_local.py，跳过上传（本地开发入口，可选）"
fi

echo "==> 上传 deployment.json（共享 API/helper 配置）"
scp "${SSH_OPTS[@]}" "$DEPLOYMENT_CONFIG" "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/deployment.json"

echo "==> 上传 myproxy-api.service"
scp "${SSH_OPTS[@]}" \
  "$SERVER_DIR/deploy/myproxy-api.service" \
  "$SERVER_DIR/deploy/myproxy-xui-helper.service" \
  "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/"

echo "==> 上传 Device API nginx 反代模板与安装器"
scp "${SSH_OPTS[@]}" \
  "$SERVER_DIR/deploy/nginx-device-api.conf.template" \
  "$SERVER_DIR/deploy/install-device-api-proxy.sh" \
  "$SERVER_DIR/deploy/myproxy-xui-helper.env" \
  "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/"

echo "==> 检查远程 /etc/myproxy-api/admin.env 是否已存在"
if ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" "test -L /etc/myproxy-api/admin.env"; then
  echo "错误：远程 admin.env 是符号链接，拒绝继续" >&2
  exit 1
fi
if ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" "test -f /etc/myproxy-api/admin.env"; then
  echo "==> 现有 /etc/myproxy-api/admin.env 将在事务快照后保留并校验"
  echo "==> 远程预检现有 Admin Token 形状（不打印明文）"
  if ! ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" \
    "python3 -c 'import pathlib,re; p=pathlib.Path(\"/etc/myproxy-api/admin.env\"); active=[x for x in p.read_text(encoding=\"utf-8\").splitlines() if x.strip() and not x.lstrip().startswith(\"#\")]; parsed=[x.split(\"=\",1) for x in active if \"=\" in x]; names=(\"MYPROXY_ADMIN_TOKEN\",\"MYPROXY_DEVICE_TOKEN_SECRET\"); values={n:[v.strip() for k,v in parsed if k==n] for n in names}; valid=len(parsed)==len(active) and all(k in names for k,v in parsed) and all(len(values[n])==1 and re.fullmatch(r\"[0-9a-fA-F]{64}\",values[n][0]) for n in names); raise SystemExit(0 if valid else 1)'"; then
    echo "错误：已有 admin.env 必须只含唯一的 64 hex Admin/Device Token（可含注释或空行）；不会接受额外环境变量、自动修复或轮换，尚未停止服务。" >&2
    exit 1
  fi
else
  echo "==> 本地生成 Admin Token 与独立设备签名密钥（不打印明文）..."
  ADMIN_TOKEN="$("$PYTHON_BIN" -c 'import secrets; print(secrets.token_hex(32))')"
  DEVICE_TOKEN_SECRET="$("$PYTHON_BIN" -c 'import secrets; print(secrets.token_hex(32))')"
  ADMIN_TOKEN_TMP="$(mktemp)"
  chmod 600 "$ADMIN_TOKEN_TMP"
  printf 'MYPROXY_ADMIN_TOKEN=%s\nMYPROXY_DEVICE_TOKEN_SECRET=%s\n' \
    "$ADMIN_TOKEN" "$DEVICE_TOKEN_SECRET" > "$ADMIN_TOKEN_TMP"
  scp "${SSH_OPTS[@]}" "$ADMIN_TOKEN_TMP" "${VPS_USER}@${VPS_HOST}:${REMOTE_TMP}/admin.env"
  unset ADMIN_TOKEN
  unset DEVICE_TOKEN_SECRET
  echo "==> Admin Token 已保存到 /etc/myproxy-api/admin.env（root:myproxy 0640）"
fi

echo "==> 远程安装并启动服务..."
ssh "${SSH_OPTS[@]}" "${VPS_USER}@${VPS_HOST}" bash -s -- \
  "$REMOTE_TMP" \
  "$DEVICE_API_SERVER_NAME" \
  "$DEVICE_API_TLS_MODE" \
  "$DEVICE_API_TLS_CERT" \
  "$DEVICE_API_TLS_KEY" \
  "$DEVICE_API_PUBLIC_PORT" <<'REMOTE_EOF'
set -euo pipefail

REMOTE_TMP="$1"
DEVICE_API_SERVER_NAME="$2"
DEVICE_API_TLS_MODE="$3"
DEVICE_API_TLS_CERT="$4"
DEVICE_API_TLS_KEY="$5"
DEVICE_API_PUBLIC_PORT="$6"
BACKEND_TLS_CERT="/etc/myproxy/tls/server.crt"
BACKEND_TLS_KEY="/etc/myproxy/tls/server.key"
REMOTE_DIR="/opt/myproxy-api"
STATE_DIR="/var/lib/myproxy-api"
UNIT_PATH="/etc/systemd/system/myproxy-api.service"
XUI_HELPER_UNIT_PATH="/etc/systemd/system/myproxy-xui-helper.service"
NGINX_CONFIG_PATH="/etc/nginx/conf.d/myproxy-device-api.conf"
NGINX_DEFAULT_SITE_PATH="/etc/nginx/sites-enabled/default"
ADMIN_ENV_PATH="/etc/myproxy-api/admin.env"
XUI_HELPER_ENV_PATH="/etc/myproxy-api/xui-helper.env"
ROLLBACK_DIR="$REMOTE_TMP/.rollback"
CONNECTIVITY_PROBE_BODY="$REMOTE_TMP/connectivity-check.body"
TS="$(date -u +%Y%m%d%H%M%S)"
DEPLOY_COMMITTED=0
ROLLBACK_ARMED=0
SNAPSHOT_READY=0
REMOTE_DIR_EXISTED=0
UNIT_EXISTED=0
NGINX_CONFIG_EXISTED=0
NGINX_DEFAULT_SITE_EXISTED=0
ADMIN_ENV_EXISTED=0
XUI_HELPER_UNIT_EXISTED=0
XUI_HELPER_ENV_EXISTED=0
STATE_DIR_EXISTED=0
MYPROXY_WAS_ACTIVE=0
MYPROXY_WAS_ENABLED=0
XUI_HELPER_WAS_ACTIVE=0
XUI_HELPER_WAS_ENABLED=0
MYPROXY_GROUP_CREATED=0
MYPROXY_USER_CREATED=0
NGINX_WAS_ACTIVE=0
NGINX_WAS_ENABLED=0
UFW_RULE_ADDED=0
UFW_AVAILABLE=0
UFW_ACTIVE=0
UFW_V4_820_BEFORE="unavailable"
UFW_V6_820_BEFORE="unavailable"
UFW_V4_820_ADDED=0
UFW_V6_820_ADDED=0
ADMIN_ENV_DIR_EXISTED=0
ADMIN_ENV_DIR_MODE=""
ADMIN_ENV_DIR_OWNER=""
TLS_ROOT_MODE=""
TLS_ROOT_OWNER=""
TLS_DIR_MODE=""
TLS_DIR_OWNER=""
TLS_CERT_MODE=""
TLS_CERT_OWNER=""
TLS_KEY_MODE=""
TLS_KEY_OWNER=""

if [[ ! "$REMOTE_TMP" =~ ^/tmp/myproxy-api-deploy\.[A-Za-z0-9]+$ ]]; then
  echo "错误：拒绝清理未知远程路径" >&2
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
    echo "错误：TLS 元数据快照不完整，拒绝静默恢复" >&2
    return 1
  fi
  # Public renewal-managed certificates are read by nginx; only backend TLS metadata is changed.
for tls_target in /etc/myproxy /etc/myproxy/tls \
    "$BACKEND_TLS_CERT" "$BACKEND_TLS_KEY"; do
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
    echo "错误：无法恢复 /etc/myproxy 原 owner" >&2
    restore_status=1
  fi
  if ! chmod "$TLS_ROOT_MODE" /etc/myproxy; then
    echo "错误：无法恢复 /etc/myproxy 原 mode" >&2
    restore_status=1
  fi
  if ! chown "$TLS_DIR_OWNER" /etc/myproxy/tls; then
    echo "错误：无法恢复 /etc/myproxy/tls 原 owner" >&2
    restore_status=1
  fi
  if ! chmod "$TLS_DIR_MODE" /etc/myproxy/tls; then
    echo "错误：无法恢复 /etc/myproxy/tls 原 mode" >&2
    restore_status=1
  fi
  if ! chown "$TLS_CERT_OWNER" "$BACKEND_TLS_CERT"; then
    echo "错误：无法恢复 TLS 证书原 owner" >&2
    restore_status=1
  fi
  if ! chmod "$TLS_CERT_MODE" "$BACKEND_TLS_CERT"; then
    echo "错误：无法恢复 TLS 证书原 mode" >&2
    restore_status=1
  fi
  if ! chown "$TLS_KEY_OWNER" "$BACKEND_TLS_KEY"; then
    echo "错误：无法恢复 TLS 私钥原 owner" >&2
    restore_status=1
  fi
  if ! chmod "$TLS_KEY_MODE" "$BACKEND_TLS_KEY"; then
    echo "错误：无法恢复 TLS 私钥原 mode" >&2
    restore_status=1
  fi
  return "$restore_status"
}

ufw_rule_present() {
  local family="$1"
  case "$family" in
    v4)
      printf '%s\n' "$UFW_STATUS" | grep -Eq "^[[:space:]]*${DEVICE_API_PUBLIC_PORT}/tcp[[:space:]]+ALLOW"
      ;;
    v6)
      printf '%s\n' "$UFW_STATUS" | grep -Eq "^[[:space:]]*${DEVICE_API_PUBLIC_PORT}/tcp[[:space:]]+\\(v6\\)[[:space:]]+ALLOW"
      ;;
    *)
      return 2
      ;;
  esac
}

capture_ufw_820_snapshot() {
  UFW_AVAILABLE=0
  UFW_ACTIVE=0
  UFW_V4_820_BEFORE="unavailable"
  UFW_V6_820_BEFORE="unavailable"
  if ! command -v ufw >/dev/null 2>&1; then
    printf 'available=0\nactive=0\nv4_820=unavailable\nv6_820=unavailable\n' \
      >"$ROLLBACK_DIR/ufw-820.before"
    return 0
  fi
  UFW_AVAILABLE=1
  if ! UFW_STATUS="$(LC_ALL=C ufw status 2>/dev/null)"; then
    return 1
  fi
  if printf '%s\n' "$UFW_STATUS" | grep -q '^Status: active'; then
    UFW_ACTIVE=1
    if ufw_rule_present v4; then UFW_V4_820_BEFORE="present"; else UFW_V4_820_BEFORE="absent"; fi
    if ufw_rule_present v6; then UFW_V6_820_BEFORE="present"; else UFW_V6_820_BEFORE="absent"; fi
  else
    UFW_V4_820_BEFORE="inactive"
    UFW_V6_820_BEFORE="inactive"
  fi
  printf 'available=%s\nactive=%s\nv4_820=%s\nv6_820=%s\n' \
    "$UFW_AVAILABLE" "$UFW_ACTIVE" "$UFW_V4_820_BEFORE" "$UFW_V6_820_BEFORE" \
    >"$ROLLBACK_DIR/ufw-820.before"
}

rollback_ufw_820_rules() {
  [ "$UFW_ACTIVE" -eq 1 ] || return 0
  if [ "$UFW_V4_820_ADDED" -eq 1 ] && [ "$UFW_V4_820_BEFORE" = "absent" ]; then
    if ! ufw --force delete allow from 0.0.0.0/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp >/dev/null 2>&1; then
      echo "错误：无法精确撤销本次新增 UFW IPv4 ${DEVICE_API_PUBLIC_PORT}/tcp 规则，请人工检查" >&2
    fi
  fi
  if [ "$UFW_V6_820_ADDED" -eq 1 ] && [ "$UFW_V6_820_BEFORE" = "absent" ]; then
    if ! ufw --force delete allow from ::/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp >/dev/null 2>&1; then
      echo "错误：无法精确撤销本次新增 UFW IPv6 ${DEVICE_API_PUBLIC_PORT}/tcp 规则，请人工检查" >&2
    fi
  fi
}

assert_myproxy_group_boundary() {
  if [ "$(id -gn myproxy)" != "myproxy" ]; then
    echo "错误：已有 myproxy 用户的主组不是 myproxy，拒绝降权部署" >&2
    return 1
  fi
  if ! MYPROXY_GROUPS="$(id -Gn myproxy 2>/dev/null)"; then
    echo "错误：无法读取 myproxy 用户 supplementary groups，拒绝降权部署" >&2
    return 1
  fi
  MYPROXY_EXTRA_GROUPS=""
  for MYPROXY_GROUP_NAME in $MYPROXY_GROUPS; do
    if [ "$MYPROXY_GROUP_NAME" != "myproxy" ]; then
      MYPROXY_EXTRA_GROUPS="${MYPROXY_EXTRA_GROUPS:+$MYPROXY_EXTRA_GROUPS }$MYPROXY_GROUP_NAME"
    fi
  done
  if [ -n "$MYPROXY_EXTRA_GROUPS" ]; then
    echo "错误：既有 myproxy 用户属于 myproxy 之外的 supplementary groups（$MYPROXY_EXTRA_GROUPS），拒绝部署" >&2
    return 1
  fi
}

rollback_remote() {
  local rollback_status=0 health_status
  set +e
  echo "错误：部署未通过最终验收，开始恢复部署前状态..." >&2

  # If snapshotting itself failed, no file mutation has happened yet; only
  # restore the old process state after the consistency stop below.
  systemctl stop myproxy-api >/dev/null 2>&1 || true
  systemctl stop myproxy-xui-helper >/dev/null 2>&1 || true
  # Restore TLS metadata before anything that may start an old backend.  The
  # old helper/API must never observe the new ownership or mode boundary.
  if ! restore_tls_metadata; then
    rollback_status=1
  fi
  if [ "$SNAPSHOT_READY" -eq 1 ]; then
    # Remove the new nginx listener before restarting the previous backend.
    restore_snapshot_file \
      "$NGINX_CONFIG_EXISTED" "$ROLLBACK_DIR/nginx.conf" "$NGINX_CONFIG_PATH"
    restore_snapshot_file \
      "$NGINX_DEFAULT_SITE_EXISTED" "$ROLLBACK_DIR/nginx-default-site" "$NGINX_DEFAULT_SITE_PATH"
    if [ "$NGINX_WAS_ACTIVE" -eq 1 ]; then
      if nginx -t >/dev/null 2>&1; then
        systemctl reload nginx >/dev/null 2>&1 || true
      else
        echo "错误：旧 nginx 配置恢复后校验失败，请人工检查" >&2
      fi
    else
      systemctl stop nginx >/dev/null 2>&1 || true
    fi

    if [ "$REMOTE_DIR_EXISTED" -eq 1 ]; then
      rm -rf -- "$REMOTE_DIR"
      tar xzf "$ROLLBACK_DIR/remote-dir.tar.gz" -C /opt
    else
      rm -rf -- "$REMOTE_DIR"
    fi
    restore_snapshot_file "$UNIT_EXISTED" "$ROLLBACK_DIR/myproxy-api.service" "$UNIT_PATH"
    restore_snapshot_file "$ADMIN_ENV_EXISTED" "$ROLLBACK_DIR/admin.env" "$ADMIN_ENV_PATH"
    restore_snapshot_file "$XUI_HELPER_UNIT_EXISTED" "$ROLLBACK_DIR/myproxy-xui-helper.service" "$XUI_HELPER_UNIT_PATH"
    restore_snapshot_file "$XUI_HELPER_ENV_EXISTED" "$ROLLBACK_DIR/xui-helper.env" "$XUI_HELPER_ENV_PATH"
    if [ "$STATE_DIR_EXISTED" -eq 1 ]; then
      rm -rf -- "$STATE_DIR"
      tar xzf "$ROLLBACK_DIR/state-dir.tar.gz" -C /var/lib
    else
      rm -rf -- "$STATE_DIR"
    fi
    if [ "$ADMIN_ENV_DIR_EXISTED" -eq 1 ]; then
      chmod "$ADMIN_ENV_DIR_MODE" /etc/myproxy-api >/dev/null 2>&1 || \
        echo "错误：无法恢复 /etc/myproxy-api 原 mode，请人工检查" >&2
      chown "$ADMIN_ENV_DIR_OWNER" /etc/myproxy-api >/dev/null 2>&1 || \
        echo "错误：无法恢复 /etc/myproxy-api 原 owner，请人工检查" >&2
    else
      rmdir -- /etc/myproxy-api >/dev/null 2>&1 || true
    fi
    systemctl daemon-reload >/dev/null 2>&1 || true
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
      echo "错误：旧 myproxy-xui-helper 未能恢复为 active，请立即人工检查" >&2
      rollback_status=1
    fi
  else
    if ! systemctl stop myproxy-xui-helper >/dev/null 2>&1 || \
       systemctl is-active --quiet myproxy-xui-helper; then
      echo "错误：myproxy-xui-helper 未能恢复为 inactive，请立即人工检查" >&2
      rollback_status=1
    fi
  fi
  if [ "$MYPROXY_WAS_ACTIVE" -eq 1 ]; then
    if ! systemctl start myproxy-api >/dev/null 2>&1 || \
       ! systemctl is-active --quiet myproxy-api; then
      echo "错误：旧 myproxy-api 未能恢复为 active，请立即人工检查" >&2
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
      echo "错误：myproxy-api 未能恢复为 inactive，请立即人工检查" >&2
      rollback_status=1
    fi
  fi
  if [ "$NGINX_WAS_ENABLED" -eq 1 ]; then
    systemctl enable nginx >/dev/null 2>&1 || true
  else
    systemctl disable nginx >/dev/null 2>&1 || true
  fi
  if [ "$UFW_RULE_ADDED" -eq 1 ]; then
    # The shorthand UFW port rule couples IPv4 and IPv6.  Delete only the
    # family-specific rules added by this txn so a pre-existing rule in the
    # other address family cannot be removed.
    rollback_ufw_820_rules
  fi
  if [ "$MYPROXY_USER_CREATED" -eq 1 ] && id -u myproxy >/dev/null 2>&1; then
    userdel myproxy >/dev/null 2>&1 || true
  fi
  if [ "$MYPROXY_GROUP_CREATED" -eq 1 ] && getent group myproxy >/dev/null 2>&1; then
    groupdel myproxy >/dev/null 2>&1 || true
  fi
  echo "==> [远程] 自动回滚流程结束" >&2
  return "$rollback_status"
}

finish_remote() {
  local status="$?"
  trap - EXIT
  if [ "$ROLLBACK_ARMED" -eq 1 ] && [ "$DEPLOY_COMMITTED" -ne 1 ]; then
    if ! rollback_remote; then
      echo "错误：自动回滚未完全恢复，请立即人工检查" >&2
      status=1
    fi
    if [ "$status" -eq 0 ]; then
      status=1
    fi
  fi
  rm -rf -- "$REMOTE_TMP"
  exit "$status"
}
trap finish_remote EXIT

PYTHONPATH="$REMOTE_TMP" MYPROXY_DEPLOYMENT_CONFIG="$REMOTE_TMP/deployment.json" \
  python3 -c 'from myproxy_server.config import Settings; s=Settings.from_env(); s.require_configured_deployment()'

echo "==> [远程] 校验并快照部署前状态"
if [ -L "$REMOTE_DIR" ] || [ -L "$STATE_DIR" ] || [ -L /etc/myproxy-api ] || \
   [ -L "$UNIT_PATH" ] || [ -L "$NGINX_CONFIG_PATH" ] || \
   [ -L "$ADMIN_ENV_PATH" ] || [ -L "$XUI_HELPER_ENV_PATH" ] || \
   [ -L "$XUI_HELPER_UNIT_PATH" ] || [ -L "$REMOTE_DIR/deployment.json" ]; then
  echo "错误：部署目标不能是符号链接" >&2
  exit 1
fi
if [ -e "$NGINX_DEFAULT_SITE_PATH" ] && [ ! -f "$NGINX_DEFAULT_SITE_PATH" ] && [ ! -L "$NGINX_DEFAULT_SITE_PATH" ]; then
  echo "错误：nginx 默认站点目标类型异常，拒绝覆盖" >&2
  exit 1
fi
if { [ -e "$REMOTE_DIR" ] && [ ! -d "$REMOTE_DIR" ]; } || \
   { [ -e "$STATE_DIR" ] && [ ! -d "$STATE_DIR" ]; } || \
   { [ -e /etc/myproxy-api ] && [ ! -d /etc/myproxy-api ]; } || \
   { [ -e "$UNIT_PATH" ] && [ ! -f "$UNIT_PATH" ]; } || \
   { [ -e "$NGINX_CONFIG_PATH" ] && [ ! -f "$NGINX_CONFIG_PATH" ]; } || \
   { [ -e "$ADMIN_ENV_PATH" ] && [ ! -f "$ADMIN_ENV_PATH" ]; } || \
   { [ -e "$XUI_HELPER_ENV_PATH" ] && [ ! -f "$XUI_HELPER_ENV_PATH" ]; } || \
   { [ -e "$XUI_HELPER_UNIT_PATH" ] && [ ! -f "$XUI_HELPER_UNIT_PATH" ]; } || \
   { [ -e "$REMOTE_DIR/deployment.json" ] && [ ! -f "$REMOTE_DIR/deployment.json" ]; }; then
  echo "错误：部署目标类型异常，拒绝覆盖" >&2
  exit 1
fi
# Public renewal-managed certificates are read by nginx; only backend TLS metadata is changed.
for tls_target in /etc/myproxy /etc/myproxy/tls "$BACKEND_TLS_CERT" "$BACKEND_TLS_KEY"; do
  if [ -L "$tls_target" ]; then
    echo "错误：TLS 目标不能是符号链接：$tls_target" >&2
    exit 1
  fi
done
if [ ! -d /etc/myproxy ] || [ ! -d /etc/myproxy/tls ] || \
   [ ! -f "$BACKEND_TLS_CERT" ] || [ ! -f "$BACKEND_TLS_KEY" ]; then
  echo "错误：TLS 目录或文件类型异常，拒绝继续" >&2
  exit 1
fi

[ -e "$REMOTE_DIR" ] && REMOTE_DIR_EXISTED=1
[ -e /etc/myproxy-api ] && ADMIN_ENV_DIR_EXISTED=1
[ -e "$UNIT_PATH" ] && UNIT_EXISTED=1
[ -e "$NGINX_CONFIG_PATH" ] && NGINX_CONFIG_EXISTED=1
[ -e "$NGINX_DEFAULT_SITE_PATH" ] || [ -L "$NGINX_DEFAULT_SITE_PATH" ] && NGINX_DEFAULT_SITE_EXISTED=1
[ -e "$ADMIN_ENV_PATH" ] && ADMIN_ENV_EXISTED=1
[ -e "$XUI_HELPER_ENV_PATH" ] && XUI_HELPER_ENV_EXISTED=1
[ -e "$XUI_HELPER_UNIT_PATH" ] && XUI_HELPER_UNIT_EXISTED=1
[ -e "$STATE_DIR" ] && STATE_DIR_EXISTED=1
if [ "$ADMIN_ENV_DIR_EXISTED" -eq 1 ]; then
  if ! ADMIN_ENV_DIR_MODE="$(stat -c '%a' /etc/myproxy-api)" || \
     ! ADMIN_ENV_DIR_OWNER="$(stat -c '%u:%g' /etc/myproxy-api)"; then
    echo "错误：无法读取 /etc/myproxy-api 原 mode/owner，拒绝继续" >&2
    exit 1
  fi
fi
TLS_ROOT_MODE="$(stat -c '%a' /etc/myproxy)"
TLS_ROOT_OWNER="$(stat -c '%u:%g' /etc/myproxy)"
TLS_DIR_MODE="$(stat -c '%a' /etc/myproxy/tls)"
TLS_DIR_OWNER="$(stat -c '%u:%g' /etc/myproxy/tls)"
TLS_CERT_MODE="$(stat -c '%a' "$BACKEND_TLS_CERT")"
TLS_CERT_OWNER="$(stat -c '%u:%g' "$BACKEND_TLS_CERT")"
TLS_KEY_MODE="$(stat -c '%a' "$BACKEND_TLS_KEY")"
TLS_KEY_OWNER="$(stat -c '%u:%g' "$BACKEND_TLS_KEY")"
systemctl is-active --quiet myproxy-api && MYPROXY_WAS_ACTIVE=1 || true
systemctl is-enabled --quiet myproxy-api && MYPROXY_WAS_ENABLED=1 || true
systemctl is-active --quiet myproxy-xui-helper && XUI_HELPER_WAS_ACTIVE=1 || true
systemctl is-enabled --quiet myproxy-xui-helper && XUI_HELPER_WAS_ENABLED=1 || true
systemctl is-active --quiet nginx && NGINX_WAS_ACTIVE=1 || true
systemctl is-enabled --quiet nginx && NGINX_WAS_ENABLED=1 || true

install -d -m 0700 "$ROLLBACK_DIR"
ROLLBACK_ARMED=1
if id -u myproxy >/dev/null 2>&1; then
  assert_myproxy_group_boundary
fi
if [ "$MYPROXY_WAS_ACTIVE" -eq 1 ]; then
  systemctl stop myproxy-api
  if systemctl is-active --quiet myproxy-api; then
    echo "错误：无法停止旧 myproxy-api，拒绝制作不一致快照" >&2
    exit 1
  fi
fi
if [ "$REMOTE_DIR_EXISTED" -eq 1 ]; then
  # Includes deployment.json: configuration is restored with runtime code.
  tar czf "$ROLLBACK_DIR/remote-dir.tar.gz" -C /opt myproxy-api
fi
if [ "$STATE_DIR_EXISTED" -eq 1 ]; then
  tar czf "$ROLLBACK_DIR/state-dir.tar.gz" -C /var/lib myproxy-api
fi
if [ "$UNIT_EXISTED" -eq 1 ]; then
  cp -a -- "$UNIT_PATH" "$ROLLBACK_DIR/myproxy-api.service"
fi
if [ "$NGINX_CONFIG_EXISTED" -eq 1 ]; then
  cp -a -- "$NGINX_CONFIG_PATH" "$ROLLBACK_DIR/nginx.conf"
fi
if [ "$NGINX_DEFAULT_SITE_EXISTED" -eq 1 ]; then
  cp -a -- "$NGINX_DEFAULT_SITE_PATH" "$ROLLBACK_DIR/nginx-default-site"
fi
if [ "$ADMIN_ENV_EXISTED" -eq 1 ]; then
  cp -a -- "$ADMIN_ENV_PATH" "$ROLLBACK_DIR/admin.env"
fi
if [ "$XUI_HELPER_UNIT_EXISTED" -eq 1 ]; then
  cp -a -- "$XUI_HELPER_UNIT_PATH" "$ROLLBACK_DIR/myproxy-xui-helper.service"
fi
if [ "$XUI_HELPER_ENV_EXISTED" -eq 1 ]; then
  cp -a -- "$XUI_HELPER_ENV_PATH" "$ROLLBACK_DIR/xui-helper.env"
fi
SNAPSHOT_READY=1

if ! capture_ufw_820_snapshot; then
  echo "错误：无法快照 UFW IPv4/IPv6 ${DEVICE_API_PUBLIC_PORT}/tcp 状态，拒绝继续" >&2
  exit 1
fi

XUI_HELPER_ENV_CREATED=0

echo "==> [远程] 创建 /opt/myproxy-api 与 /etc/myproxy-api"
if ! getent group myproxy >/dev/null 2>&1; then
  groupadd --system myproxy
  MYPROXY_GROUP_CREATED=1
fi
if ! id -u myproxy >/dev/null 2>&1; then
  useradd --system --gid myproxy --home-dir /var/lib/myproxy \
    --shell /usr/sbin/nologin myproxy
  MYPROXY_USER_CREATED=1
fi
assert_myproxy_group_boundary
install -d -m 0750 -o root -g myproxy "$REMOTE_DIR" /etc/myproxy-api
# Keep the database directory root-only for the complete migration.  The
# unprivileged API account must not be able to race path checks, replace the
# migration temporary, or plant a symlink before the durable rename.
install -d -m 0700 -o root -g root "$STATE_DIR"

if [ -d "$REMOTE_DIR" ] && [ -n "$(ls -A "$REMOTE_DIR" 2>/dev/null || true)" ]; then
  echo "==> [远程] 备份现有 $REMOTE_DIR -> /opt/myproxy-api.bak.$TS.tar.gz"
  tar czf "/opt/myproxy-api.bak.$TS.tar.gz" -C /opt --exclude='myproxy-api.bak.*' myproxy-api
  chmod 0600 "/opt/myproxy-api.bak.$TS.tar.gz"
else
  echo "==> [远程] 首次部署（$REMOTE_DIR 为空或不存在），跳过备份"
fi

echo "==> [远程] 安装代码到 $REMOTE_DIR（清除旧占位实现，备份已在上一步完成）"
rm -rf "$REMOTE_DIR/myproxy_server"
cp -a "$REMOTE_TMP/myproxy_server" "$REMOTE_DIR/myproxy_server"
install -m 0640 -o root -g myproxy "$REMOTE_TMP/deployment.json" "$REMOTE_DIR/deployment.json"
chown -R root:myproxy "$REMOTE_DIR/myproxy_server"
find "$REMOTE_DIR/myproxy_server" -type d -exec chmod 0750 {} +
find "$REMOTE_DIR/myproxy_server" -type f -exec chmod 0640 {} +
# 旧版占位服务遗留文件：备份已含，不再使用。
rm -f "$REMOTE_DIR/server.py" "$REMOTE_DIR/myproxy_api.py" "$REMOTE_DIR/myproxy-api.db" "$REMOTE_DIR/myproxy-api.db-shm" "$REMOTE_DIR/myproxy-api.db-wal"
if [ -f "$REMOTE_TMP/run_local.py" ]; then
  cp -a "$REMOTE_TMP/run_local.py" "$REMOTE_DIR/run_local.py"
else
  echo "==> [远程] 未上传 run_local.py，跳过（保留旧文件或首次无该文件）"
fi

echo "==> [远程] 安装公网 Device API 反代部署文件"
install -d -m 0750 "$REMOTE_DIR/deploy"
install -m 0644 "$REMOTE_TMP/nginx-device-api.conf.template" \
  "$REMOTE_DIR/deploy/nginx-device-api.conf.template"
install -m 0750 "$REMOTE_TMP/install-device-api-proxy.sh" \
  "$REMOTE_DIR/deploy/install-device-api-proxy.sh"

echo "==> [远程] 安装 systemd service"
install -m 0644 "$REMOTE_TMP/myproxy-xui-helper.service" "$XUI_HELPER_UNIT_PATH"
install -m 0644 "$REMOTE_TMP/myproxy-api.service" /etc/systemd/system/myproxy-api.service
if [ -L "$XUI_HELPER_ENV_PATH" ] || { [ -e "$XUI_HELPER_ENV_PATH" ] && [ ! -f "$XUI_HELPER_ENV_PATH" ]; }; then
  echo "错误：x-ui helper 配置类型异常，拒绝覆盖" >&2
  exit 1
fi
if [ ! -e "$XUI_HELPER_ENV_PATH" ]; then
  echo "==> [远程] 安装 root-only x-ui profile 配置"
  install -m 0600 -o root -g root "$REMOTE_TMP/myproxy-xui-helper.env" "$XUI_HELPER_ENV_PATH"
  XUI_HELPER_ENV_CREATED=1
else
  chown root:root "$XUI_HELPER_ENV_PATH"
  chmod 0600 "$XUI_HELPER_ENV_PATH"
fi

if [ -f "$REMOTE_TMP/admin.env" ]; then
  echo "==> [远程] 安装 /etc/myproxy-api/admin.env（root:myproxy mode 640）"
  install -m 0640 -o root -g myproxy "$REMOTE_TMP/admin.env" /etc/myproxy-api/admin.env
else
  echo "==> [远程] /etc/myproxy-api/admin.env 已存在，保留现状"
fi

echo "==> [远程] 校验独立设备签名密钥（不打印明文；既有 admin.env 不改写）"
python3 - <<'PY'
import re
from pathlib import Path

path = Path("/etc/myproxy-api/admin.env")
allowed = {"MYPROXY_ADMIN_TOKEN", "MYPROXY_DEVICE_TOKEN_SECRET"}
values = {}
for raw in path.read_text(encoding="utf-8").splitlines():
    if not raw.strip() or raw.lstrip().startswith("#"):
        continue
    if "=" not in raw:
        raise SystemExit("admin.env contains a malformed assignment")
    name, value = raw.split("=", 1)
    if name not in allowed:
        raise SystemExit("admin.env contains a forbidden assignment")
    values.setdefault(name, []).append(value.strip())
for name in sorted(allowed):
    entries = values.get(name, [])
    if len(entries) != 1 or not re.fullmatch(r"[0-9a-fA-F]{64}", entries[0]):
        raise SystemExit(f"admin.env {name} is missing, duplicated or invalid")
PY
chown root:myproxy /etc/myproxy-api/admin.env
chmod 0640 /etc/myproxy-api/admin.env

if [ "$XUI_HELPER_ENV_CREATED" -eq 1 ]; then
  echo "==> [远程] 迁移既有非敏感 x-ui profile 覆盖到 root-only helper 配置"
  python3 - <<'PY'
from pathlib import Path
import os
import tempfile

allowed = {
    "MYPROXY_XUI_DB_PATH", "MYPROXY_XUI_INBOUND_ID", "MYPROXY_SERVER_HOST",
    "MYPROXY_SERVER_PORT", "MYPROXY_PUBLIC_KEY", "MYPROXY_SHORT_ID",
    "MYPROXY_SNI", "MYPROXY_FINGERPRINT", "MYPROXY_FLOW", "MYPROXY_SPIDER_X",
}
helper = Path("/etc/myproxy-api/xui-helper.env")
admin = Path("/etc/myproxy-api/admin.env")
values = {}
for path in (helper, admin):
    for raw in path.read_text(encoding="utf-8").splitlines():
        if not raw or raw.lstrip().startswith("#") or "=" not in raw:
            continue
        name, value = raw.split("=", 1)
        if name in allowed and "\n" not in value and "\r" not in value:
            values[name] = value
lines = [f"{name}={values[name]}" for name in sorted(values)]
fd, temporary = tempfile.mkstemp(prefix=".xui-helper.env.", dir=str(helper.parent))
try:
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, helper)
finally:
    if os.path.exists(temporary):
        os.unlink(temporary)
PY
  chown root:root "$XUI_HELPER_ENV_PATH"
  chmod 0600 "$XUI_HELPER_ENV_PATH"
fi

echo "==> [远程] 固定 TLS 目录与文件权限（API 可穿越、其他用户不可读）"
if [ -L /etc/myproxy ] || [ -L /etc/myproxy/tls ] || \
   [ ! -d /etc/myproxy ] || [ ! -d /etc/myproxy/tls ]; then
  echo "错误：/etc/myproxy 或 /etc/myproxy/tls 不是安全目录" >&2
  exit 1
fi
chown root:myproxy /etc/myproxy /etc/myproxy/tls
chmod 0750 /etc/myproxy /etc/myproxy/tls
chown root:myproxy "$BACKEND_TLS_CERT" "$BACKEND_TLS_KEY"
chmod 0640 "$BACKEND_TLS_CERT" "$BACKEND_TLS_KEY"

echo "==> [远程] 准备独立可写数据库目录（myproxy:myproxy mode 600）"
if [ -L "$STATE_DIR" ] || { [ -e "$STATE_DIR" ] && [ ! -d "$STATE_DIR" ]; }; then
  echo "错误：数据库目录是符号链接或类型异常，拒绝继续" >&2
  exit 1
fi
if [ -L "$STATE_DIR/myproxy.db" ]; then
  echo "错误：数据库路径是符号链接，拒绝继续" >&2
  exit 1
fi
for legacy_db in "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-wal" "$REMOTE_DIR/myproxy.db-shm"; do
  if [ -L "$legacy_db" ]; then
    echo "错误：旧数据库路径是符号链接，拒绝迁移：$legacy_db" >&2
    exit 1
  fi
  if [ -e "$legacy_db" ] && [ ! -f "$legacy_db" ]; then
    echo "错误：旧数据库路径类型异常，拒绝迁移：$legacy_db" >&2
    exit 1
  fi
done
if [ ! -f "$REMOTE_DIR/myproxy.db" ] && {
  [ -e "$REMOTE_DIR/myproxy.db-wal" ] || [ -e "$REMOTE_DIR/myproxy.db-shm" ];
}; then
  echo "错误：发现无主旧 SQLite WAL/SHM，拒绝清理并要求人工恢复：$REMOTE_DIR/myproxy.db" >&2
  exit 1
fi
if [ -e "$REMOTE_DIR/myproxy.db" ] && [ -e "$STATE_DIR/myproxy.db" ]; then
  echo "错误：旧数据库与新状态数据库同时存在，拒绝猜测迁移是否完成" >&2
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
if [ "$DB_MIGRATED" -eq 1 ]; then
  # Remove legacy files only after backup(), integrity_check, fsync and the
  # atomic replace above have all succeeded.
  rm -f -- "$REMOTE_DIR/myproxy.db" "$REMOTE_DIR/myproxy.db-shm" "$REMOTE_DIR/myproxy.db-wal"
fi
if [ -L "$STATE_DIR/myproxy.db" ] || \
   { [ -e "$STATE_DIR/myproxy.db" ] && [ ! -f "$STATE_DIR/myproxy.db" ]; }; then
  echo "错误：canonical 数据库迁移后不是安全普通文件，拒绝继续" >&2
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
  echo "错误：canonical 数据库创建后不是安全普通文件，拒绝继续" >&2
  exit 1
fi
chown myproxy:myproxy "$STATE_DIR/myproxy.db"
chmod 0600 "$STATE_DIR/myproxy.db"
# Only after migration, cleanup, and canonical-file validation may the API
# account receive ownership of the state directory.
chown myproxy:myproxy "$STATE_DIR"
chmod 0700 "$STATE_DIR"

echo "==> [远程] daemon-reload + enable + restart myproxy-api"
systemctl daemon-reload
systemctl enable myproxy-xui-helper
systemctl enable myproxy-api
systemctl restart myproxy-xui-helper
systemctl restart myproxy-api

echo "==> [远程] 等待服务启动..."
sleep 2
systemctl is-active myproxy-api

echo "==> [远程] 验证私有后端 https://127.0.0.1:1820/healthz"
curl -sk "https://127.0.0.1:1820/healthz"
echo
CONNECTIVITY_UPSTREAM_STATUS="$(curl --noproxy '*' -ksS --max-time 5 \
  -o "$CONNECTIVITY_PROBE_BODY" -w '%{http_code}' \
  "https://127.0.0.1:1820/connectivity-check" || true)"
if [ "$CONNECTIVITY_UPSTREAM_STATUS" != "204" ] || [ -s "$CONNECTIVITY_PROBE_BODY" ]; then
  echo "错误：私有后端 connectivity-check 必须返回空 HTTP 204（HTTP $CONNECTIVITY_UPSTREAM_STATUS）" >&2
  exit 1
fi
READY_STATUS="$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:1820/readyz")"
if [ "$READY_STATUS" != "200" ]; then
  echo "错误：私有 readiness 验收失败（HTTP $READY_STATUS）" >&2
  exit 1
fi

echo "==> [远程] 安装并验证公网 Device API 精确路由网关"
# Ubuntu's stock sites-enabled/default is commonly a public HTTP :80 server.
# Disable it inside this transaction; the snapshot above restores it on any
# failure.  A custom :80 listener is rejected below instead of silently
# exposing an unreviewed default site.
if [ -e "$NGINX_DEFAULT_SITE_PATH" ] || [ -L "$NGINX_DEFAULT_SITE_PATH" ]; then
  echo "==> [远程] 移除 nginx 默认站点（纳入事务快照）"
  rm -f -- "$NGINX_DEFAULT_SITE_PATH"
fi
MYPROXY_DEVICE_API_SERVER_NAME="$DEVICE_API_SERVER_NAME" \
MYPROXY_DEPLOY_CONFIRM=YES \
MYPROXY_DEVICE_API_TLS_MODE="$DEVICE_API_TLS_MODE" \
MYPROXY_DEVICE_API_TLS_CERT="$DEVICE_API_TLS_CERT" \
MYPROXY_DEVICE_API_TLS_KEY="$DEVICE_API_TLS_KEY" \
MYPROXY_DEVICE_API_PUBLIC_PORT="$DEVICE_API_PUBLIC_PORT" \
  MYPROXY_DEVICE_API_UPSTREAM_PORT=1820 \
  bash "$REMOTE_DIR/deploy/install-device-api-proxy.sh"

if [ -e "$NGINX_DEFAULT_SITE_PATH" ] || [ -L "$NGINX_DEFAULT_SITE_PATH" ]; then
  echo "错误：nginx 默认站点在公网验收时仍处于启用状态" >&2
  exit 1
fi
if ! NGINX_CONFIG_DUMP="$(nginx -T 2>/dev/null)"; then
  echo "错误：无法读取最终 nginx 配置，拒绝提交公网部署" >&2
  exit 1
fi
if printf '%s\n' "$NGINX_CONFIG_DUMP" | grep -Eq '^[[:space:]]*listen[[:space:]]+([^[:space:];]+:)?80([[:space:];]|$)'; then
  echo "错误：最终 nginx 配置仍暴露 HTTP :80；拒绝提交公网部署" >&2
  exit 1
fi
echo "==> [远程] nginx 默认站点已禁用且未发现任何 :80 listener"

HEALTH_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/healthz")"
CONNECTIVITY_PUBLIC_STATUS="$(curl -sS --max-time 5 \
  --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o "$CONNECTIVITY_PROBE_BODY" -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/connectivity-check" || true)"
if [ -s "$CONNECTIVITY_PROBE_BODY" ]; then
  CONNECTIVITY_BODY_STATE="nonempty"
else
  CONNECTIVITY_BODY_STATE="empty"
fi
ADMIN_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/api/admin/user")"
ADMIN_UI_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/admin")"
ADMIN_UI_SLASH_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/admin/")"
ADMIN_UI_JS_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/admin/app.js")"
READY_PUBLIC_STATUS="$(curl -sS --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/readyz")"
# nginx matches the normalised path but forwards the raw one: this reaches the
# whitelisted /api/device/claim location while the backend sees /api/admin/...
# The backend must refuse it with 404 (not 401, which would mean it was routed
# as Admin).  --path-as-is stops curl from normalising it first.
ADMIN_DOTDOT_STATUS="$(curl -sS --path-as-is -X POST --resolve "${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}:127.0.0.1" \
  -o /dev/null -w '%{http_code}' \
  "https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}/api/admin/../device/claim")"
if [ "$HEALTH_STATUS" != "200" ] || [ "$CONNECTIVITY_PUBLIC_STATUS" != "204" ] || \
   [ "$CONNECTIVITY_BODY_STATE" != "empty" ] || [ "$ADMIN_STATUS" != "404" ] || \
   [ "$ADMIN_UI_STATUS" != "404" ] || [ "$ADMIN_UI_SLASH_STATUS" != "404" ] || \
   [ "$ADMIN_UI_JS_STATUS" != "404" ] || [ "$READY_PUBLIC_STATUS" != "404" ] || \
   [ "$ADMIN_DOTDOT_STATUS" != "404" ]; then
  echo "错误：公网网关验收失败（health=$HEALTH_STATUS connectivity-check=$CONNECTIVITY_PUBLIC_STATUS body=$CONNECTIVITY_BODY_STATE admin=$ADMIN_STATUS admin_ui=$ADMIN_UI_STATUS admin_ui_slash=$ADMIN_UI_SLASH_STATUS admin_ui_js=$ADMIN_UI_JS_STATUS readyz=$READY_PUBLIC_STATUS admin_dotdot=$ADMIN_DOTDOT_STATUS）" >&2
  exit 1
fi
echo "==> [远程] 公网 health=200、connectivity-check=204 空响应、Admin API/UI/readyz=404，路由边界验证通过"

# Change UFW only after the locally-routed gateway and the :80 negative check
# have passed.  IPv4 and IPv6 are snapshotted and changed independently so a
# failure cannot remove a pre-existing rule from the other family.
if [ "$UFW_ACTIVE" -eq 1 ]; then
  echo "==> [远程] UFW 已启用，分别校验 ${DEVICE_API_PUBLIC_PORT}/tcp IPv4/IPv6 规则"
  # The historical `ufw allow "${DEVICE_API_PUBLIC_PORT}/tcp"` form is not
  # used: it couples the two families and makes precise rollback impossible.
  if [ "$UFW_V4_820_BEFORE" != "present" ]; then
    UFW_V4_820_ADDED=1
    UFW_RULE_ADDED=1
    if ! ufw allow from 0.0.0.0/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp; then
      exit 1
    fi
  fi
  if [ "$UFW_V6_820_BEFORE" != "present" ]; then
    UFW_V6_820_ADDED=1
    UFW_RULE_ADDED=1
    if ! ufw allow from ::/0 to any port "$DEVICE_API_PUBLIC_PORT" proto tcp; then
      exit 1
    fi
  fi
  if ! UFW_STATUS="$(LC_ALL=C ufw status 2>/dev/null)"; then
    echo "错误：UFW 放行后无法读取状态，启动回滚" >&2
    exit 1
  fi
  if ! ufw_rule_present v4 || ! ufw_rule_present v6; then
    echo "错误：UFW 未同时确认 ${DEVICE_API_PUBLIC_PORT}/tcp IPv4 与 IPv6 规则，启动回滚" >&2
    exit 1
  fi
else
  if [ "$UFW_AVAILABLE" -eq 1 ]; then
    echo "==> [远程] UFW 未启用；请在云防火墙中确认 ${DEVICE_API_PUBLIC_PORT}/tcp 已放行"
  else
    echo "==> [远程] 未安装 UFW；请在云防火墙中确认 ${DEVICE_API_PUBLIC_PORT}/tcp 已放行"
  fi
fi
DEPLOY_COMMITTED=1

echo "==> [远程] 安装完成"
REMOTE_EOF

echo "==> 部署完成：${VPS_USER}@${VPS_HOST}"
echo "==> 公网 Device API 已启用：https://${DEVICE_API_SERVER_NAME}:${DEVICE_API_PUBLIC_PORT}"
echo "==> 公网证书链、有效期和主机名验证通过；/api/admin 保持私有。"
