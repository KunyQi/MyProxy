#!/usr/bin/env python3
"""Create a real pairing code on an explicitly selected VPS.

Usage: set MYPROXY_DEPLOY_CONFIRM=YES, then supply --host, --ssh-key,
--admin-url and --username. The target must expose a trusted HTTPS Admin API
to the remote SSH host. --ca-file optionally names its remote CA bundle.

Never prints the code itself.  The confirmation gate prevents an accidental
write to a production Admin API when the target environment is not visible.
"""

from __future__ import annotations

import base64
import argparse
import json
import os
import pathlib
import re
import shlex
import subprocess
import sys
import tempfile
from collections.abc import Mapping, Sequence
from typing import Any, Callable

try:
    from scripts import release_verify
except ImportError:
    import release_verify


def _remote_script(ttl: int, username: str, display_name: str,
                   device_template: str, admin_url: str, ca_file: str | None) -> str:
    return f'''
import json, ssl, urllib.request, urllib.parse
token=""
with open('/etc/myproxy-api/admin.env') as f:
    for line in f:
        if line.startswith('MYPROXY_ADMIN_TOKEN='):
            token=line.split('=',1)[1].strip()
ctx=ssl.create_default_context(cafile={ca_file!r})
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None
opener=urllib.request.build_opener(urllib.request.HTTPSHandler(context=ctx),NoRedirect)
def req(method,path,obj=None):
    data=json.dumps(obj).encode() if obj is not None else None
    r=urllib.request.Request({admin_url!r}+path, data=data, method=method)
    r.add_header('Content-Type','application/json')
    r.add_header('Authorization','Bearer '+token)
    with opener.open(r, timeout=15) as resp:
        return json.loads(resp.read().decode())
users=req('GET','/api/admin/user?username='+urllib.parse.quote({username!r},safe=''))['users']
if users:
    user=users[0]
else:
    user=req('POST','/api/admin/user',{{"username":{username!r},"displayName":{display_name!r}}})
b=req('POST','/api/admin/binding',{{"userId":user["id"],"deviceTemplate":{device_template!r},"expiresInSeconds":{ttl}}})
print(b["code"])
'''


def _write_pairing_file(path: pathlib.Path, code: str) -> None:
    """Atomically write the local secret with restrictive Unix permissions."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=str(path.parent))
    try:
        fchmod = getattr(os, "fchmod", None)
        if os.name != "nt" and fchmod is not None:
            try:
                fchmod(fd, 0o600)
            except (AttributeError, NotImplementedError, OSError):
                # The final chmod below still protects Unix files if the
                # descriptor-level operation is unavailable.
                pass
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(code + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        if os.name != "nt":
            os.chmod(temp_name, 0o600)
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)


def main(
    argv: Sequence[str] | None = None,
    *,
    env: Mapping[str, str] | None = None,
    runner: Callable[..., Any] = subprocess.run,
) -> int:
    values = dict(os.environ if env is None else env)
    if values.get("MYPROXY_DEPLOY_CONFIRM") != "YES":
        raise SystemExit("set MYPROXY_DEPLOY_CONFIRM=YES to create a real pairing code")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=values.get("MYPROXY_VPS_HOST", ""))
    parser.add_argument("--ssh-key", default=values.get("MYPROXY_SSH_KEY", ""))
    parser.add_argument("--username", required=True)
    parser.add_argument("--display-name")
    parser.add_argument("--device-template", choices=("windows", "android", "linux"), default="windows")
    parser.add_argument("--admin-url", required=True)
    parser.add_argument("--ca-file", help="CA bundle path on the remote SSH host")
    parser.add_argument("--output", default="secrets/pairing_code.txt")
    parser.add_argument("--ttl", type=int, default=604800)
    args = parser.parse_args(list(sys.argv[1:] if argv is None else argv))
    host, key = args.host, args.ssh_key
    if not host or any(ch.isspace() for ch in host):
        raise SystemExit("set MYPROXY_VPS_HOST explicitly (without whitespace)")
    if not key:
        raise SystemExit("set MYPROXY_SSH_KEY explicitly; no private-key default is used")

    out = args.output
    try:
        admin_url = release_verify.deployment_origin({"api_base_url":args.admin_url}, require_configured=True)
    except release_verify.ReleaseVerificationError as exc:
        raise SystemExit(f"invalid --admin-url: {exc}") from exc
    ttl = args.ttl
    if not 60 <= ttl <= 604800:
        raise SystemExit("ttl_seconds must be between 60 and 604800")

    if not args.username.strip() or len(args.username) > 64 or any(ord(c) < 32 for c in args.username):
        raise SystemExit("--username must contain 1 to 64 characters without controls")
    remote_py = _remote_script(ttl, args.username, args.display_name or args.username,
                               args.device_template, admin_url, args.ca_file)
    b64 = base64.b64encode(remote_py.encode()).decode()
    remote_command = f"printf '%s' {shlex.quote(b64)} | base64 -d | python3"
    command = [
        "ssh", "-i", key, "-o", "BatchMode=yes",
        "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes",
        "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3", f"root@{host}", remote_command,
    ]
    result = runner(
        command,
        capture_output=True,
        text=True,
        errors="replace",
        encoding="utf-8",
        timeout=60,
    )
    if result.returncode != 0:
        print(f"CREATE_FAIL remote SSH command exited {result.returncode}", file=sys.stderr)
        return 1

    code = result.stdout.strip()
    if not re.fullmatch(r"[A-Z0-9]{4}-[A-Z0-9]{4}", code):
        print("CREATE_FAIL invalid pairing code response", file=sys.stderr)
        return 1
    _write_pairing_file(pathlib.Path(out), code)
    print(f"saved pairing code to {out} (length {len(code)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
