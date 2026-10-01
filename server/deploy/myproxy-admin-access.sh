#!/bin/sh
set -eu
PATH=/usr/bin:/bin
export PATH
umask 077

if [ "${SSH_ORIGINAL_COMMAND-}" != "token" ]; then
    exit 126
fi

exec /usr/bin/sudo -n /usr/local/sbin/myproxy-admin-token
