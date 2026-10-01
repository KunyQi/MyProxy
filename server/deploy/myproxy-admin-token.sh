#!/bin/sh
set -eu
PATH=/usr/bin:/bin
export PATH
umask 077

env_file=/etc/myproxy-api/admin.env
[ -f "$env_file" ] && [ ! -L "$env_file" ] || exit 1
# The API runs as ``myproxy`` and systemd reads this EnvironmentFile before
# dropping into the service.  Keep the one deployed ownership contract exact:
# root owns the file, only the myproxy group may read it, and nobody may write
# through this helper.
[ "$(stat -c '%U:%G:%a' -- "$env_file")" = "root:myproxy:640" ] || exit 1

token_count="$(grep -c '^MYPROXY_ADMIN_TOKEN=' "$env_file" || true)"
[ "$token_count" -eq 1 ] || exit 1
token="$(awk -F= '$1 == "MYPROXY_ADMIN_TOKEN" { print substr($0, index($0, "=") + 1) }' "$env_file")"
case "$token" in
    ''|*[!0-9a-fA-F]*) exit 1 ;;
esac
[ "${#token}" -eq 64 ] || exit 1
printf '%s\n' "$token"
unset token
unset token_count
