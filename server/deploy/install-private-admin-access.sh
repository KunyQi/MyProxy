#!/usr/bin/env bash
set -Eeuo pipefail
PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
umask 077

if [ "$#" -ne 3 ] || [ "$(id -u)" -ne 0 ]; then
  echo "usage: sudo $0 <ed25519-public-key> <access-helper> <token-helper>" >&2
  exit 2
fi

PUBLIC_KEY_FILE="$1"
ACCESS_HELPER_SOURCE="$2"
TOKEN_HELPER_SOURCE="$3"
ACCOUNT="myproxy-admin"
ACCOUNT_HOME="/var/lib/myproxy-admin"
ACCOUNT_SSH="$ACCOUNT_HOME/.ssh"
ACCOUNT_KEYS="$ACCOUNT_SSH/authorized_keys"
ROOT_KEYS="/root/.ssh/authorized_keys"
ACCESS_HELPER="/usr/local/sbin/myproxy-admin-access"
TOKEN_HELPER="/usr/local/sbin/myproxy-admin-token"
SSHD_DROPIN="/etc/ssh/sshd_config.d/90-myproxy-admin.conf"
SUDOERS_FILE="/etc/sudoers.d/myproxy-admin"

for command_name in awk grep install passwd ssh-keygen stat sudo systemctl useradd userdel visudo; do
  command -v "$command_name" >/dev/null 2>&1 || exit 3
done
[ -x /usr/sbin/sshd ] || exit 3
for source_file in "$PUBLIC_KEY_FILE" "$ACCESS_HELPER_SOURCE" "$TOKEN_HELPER_SOURCE"; do
  [ -f "$source_file" ] && [ ! -L "$source_file" ] || exit 3
done
[ "$(awk 'END { print NR }' "$PUBLIC_KEY_FILE")" -eq 1 ] || exit 3
/usr/bin/ssh-keygen -l -f "$PUBLIC_KEY_FILE" >/dev/null 2>&1 || exit 3
[ ! -e "$ROOT_KEYS" ] || { [ -f "$ROOT_KEYS" ] && [ ! -L "$ROOT_KEYS" ]; } || exit 3
if id "$ACCOUNT" >/dev/null 2>&1; then
  echo "$ACCOUNT already exists; audit the current installation instead of overwriting it" >&2
  exit 4
fi
for target in "$ACCOUNT_HOME" "$ACCESS_HELPER" "$TOKEN_HELPER" "$SSHD_DROPIN" "$SUDOERS_FILE"; do
  [ ! -e "$target" ] && [ ! -L "$target" ] || exit 4
done

read -r key_type key_blob _ < "$PUBLIC_KEY_FILE"
[ "$key_type" = "ssh-ed25519" ] || exit 3
case "$key_blob" in
  ''|*[!A-Za-z0-9+/=]*) exit 3 ;;
esac

backup_dir="$(mktemp -d /root/.myproxy-admin-install-backup.XXXXXX)"
case "$backup_dir" in
  /root/.myproxy-admin-install-backup.*) ;;
  *) exit 3 ;;
esac
if [ -e "$ROOT_KEYS" ]; then
  cp -a "$ROOT_KEYS" "$backup_dir/root-authorized-keys"
fi

reload_sshd() {
  systemctl reload ssh 2>/dev/null || systemctl reload sshd
}

cleanup_backup() {
  case "$backup_dir" in
    /root/.myproxy-admin-install-backup.*) rm -rf -- "$backup_dir" ;;
  esac
}

rollback() {
  status="${1:-$?}"
  trap - ERR INT TERM
  set +e
  rm -f -- "$SSHD_DROPIN" "$SUDOERS_FILE" "$ACCESS_HELPER" "$TOKEN_HELPER" "$ACCOUNT_KEYS"
  if [ -f "$backup_dir/root-authorized-keys" ]; then
    cp -a "$backup_dir/root-authorized-keys" "$ROOT_KEYS"
  fi
  rmdir "$ACCOUNT_SSH" "$ACCOUNT_HOME" 2>/dev/null || true
  userdel "$ACCOUNT" 2>/dev/null || true
  /usr/sbin/sshd -t && reload_sshd >/dev/null 2>&1 || true
  cleanup_backup
  exit "$status"
}
trap rollback ERR INT TERM

useradd --system --user-group --home-dir "$ACCOUNT_HOME" --shell /bin/sh --no-create-home "$ACCOUNT"
passwd -l "$ACCOUNT" >/dev/null
install -d -o root -g root -m 0755 "$ACCOUNT_HOME"
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 0700 "$ACCOUNT_SSH"

restricted_line="restrict,port-forwarding,permitopen=\"127.0.0.1:1820\",command=\"$ACCESS_HELPER\" ssh-ed25519 $key_blob myproxy-restricted-admin"
printf '%s\n' "$restricted_line" > "$backup_dir/account-authorized-keys"
install -o "$ACCOUNT" -g "$ACCOUNT" -m 0600 "$backup_dir/account-authorized-keys" "$ACCOUNT_KEYS"

if [ -f "$ROOT_KEYS" ]; then
  root_keys_tmp="$(mktemp /root/.ssh/authorized_keys.myproxy.XXXXXX)"
  awk -v blob="$key_blob" '
    {
      matches = 0
      for (i = 1; i < NF; i++) {
        if ($i == "ssh-ed25519" && $(i + 1) == blob) matches = 1
      }
      if (!matches) print
    }
  ' "$ROOT_KEYS" > "$root_keys_tmp"
  install -o root -g root -m 0600 "$root_keys_tmp" "$ROOT_KEYS"
  rm -f -- "$root_keys_tmp"
fi

install -o root -g root -m 0755 "$ACCESS_HELPER_SOURCE" "$ACCESS_HELPER"
install -o root -g root -m 0700 "$TOKEN_HELPER_SOURCE" "$TOKEN_HELPER"

printf '%s\n' "$ACCOUNT ALL=(root) NOPASSWD: $TOKEN_HELPER" > "$backup_dir/sudoers"
visudo -cf "$backup_dir/sudoers" >/dev/null
install -o root -g root -m 0440 "$backup_dir/sudoers" "$SUDOERS_FILE"

cat > "$backup_dir/sshd-dropin" <<'EOF'
Match User myproxy-admin
    AuthenticationMethods publickey
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    PubkeyAuthentication yes
    AllowTcpForwarding local
    AllowStreamLocalForwarding no
    PermitOpen 127.0.0.1:1820
    PermitListen none
    GatewayPorts no
    PermitTTY no
    X11Forwarding no
    AllowAgentForwarding no
    PermitTunnel no
    PermitUserRC no
    ForceCommand /usr/local/sbin/myproxy-admin-access
EOF
install -o root -g root -m 0644 "$backup_dir/sshd-dropin" "$SSHD_DROPIN"

visudo -cf /etc/sudoers >/dev/null
/usr/sbin/sshd -t
reload_sshd

/usr/sbin/sshd -T -C user="$ACCOUNT",host=localhost,addr=127.0.0.1 > "$backup_dir/effective-sshd"
for expected in \
  "authenticationmethods publickey" \
  "passwordauthentication no" \
  "kbdinteractiveauthentication no" \
  "allowtcpforwarding local" \
  "allowstreamlocalforwarding no" \
  "permitopen 127.0.0.1:1820" \
  "permitlisten none" \
  "gatewayports no" \
  "permittty no" \
  "x11forwarding no" \
  "allowagentforwarding no" \
  "permittunnel no" \
  "permituserrc no" \
  "forcecommand /usr/local/sbin/myproxy-admin-access"
do
  grep -Fqx "$expected" "$backup_dir/effective-sshd" || rollback 5
done

token="$(sudo -u "$ACCOUNT" env SSH_ORIGINAL_COMMAND=token "$ACCESS_HELPER")"
case "$token" in
  ''|*[!0-9a-fA-F]*) rollback 5 ;;
esac
[ "${#token}" -eq 64 ] || rollback 5
token=""

trap - ERR
set +e
forbidden_output="$(sudo -u "$ACCOUNT" env SSH_ORIGINAL_COMMAND=id "$ACCESS_HELPER" 2>/dev/null)"
forbidden_status=$?
set -e
trap rollback ERR
[ "$forbidden_status" -eq 126 ] && [ -z "$forbidden_output" ] || rollback 5

trap - ERR INT TERM
cleanup_backup
echo "PRIVATE_ADMIN_ACCESS_INSTALLED"
