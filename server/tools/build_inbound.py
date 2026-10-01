"""Generate a 3x-ui inbound from explicitly supplied operator configuration."""
import json, os, re, sys, uuid as uuid_mod


def require_env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        print(f"error: missing required environment variable: {name}", file=sys.stderr)
        raise SystemExit(1)
    return value


client_uuid = require_env("VUUID")
subid = require_env("SUBID")
priv = require_env("PRIV")
pub = require_env("PUB")
shortid = require_env("SHORTID")
sni = require_env("MYPROXY_REALITY_SNI")
destination = os.environ.get("MYPROXY_REALITY_DEST", f"{sni}:443")
try:
    port = int(os.environ.get("MYPROXY_INBOUND_PORT", "8443"))
except ValueError:
    raise SystemExit("error: MYPROXY_INBOUND_PORT must be an integer") from None
if not 1 <= port <= 65535:
    raise SystemExit("error: MYPROXY_INBOUND_PORT must be in range 1-65535")
if not re.fullmatch(r"[A-Za-z0-9.-]+", sni):
    raise SystemExit("error: MYPROXY_REALITY_SNI must be a DNS hostname")

try:
    uuid_mod.UUID(client_uuid)
except ValueError:
    print("error: VUUID must be a valid UUID", file=sys.stderr)
    raise SystemExit(1)

if not re.fullmatch(r"(?:[0-9a-fA-F]{2}){1,8}", shortid):
    raise SystemExit("error: SHORTID must be 2 to 16 hexadecimal characters in byte pairs")

print("warning: output contains the REALITY private key; never commit or share it", file=sys.stderr)

settings = {
    "clients": [{
        "id": client_uuid, "flow": "xtls-rprx-vision", "email": "user1",
        "limitIp": 0, "totalGB": 0, "expiryTime": 0, "enable": True,
        "tgId": 0, "subId": subid, "reset": 0
    }],
    "decryption": "none",
    "fallbacks": []
}

stream = {
    "network": "tcp",
    "security": "reality",
    "realitySettings": {
        "show": False, "xver": 0,
        "dest": destination,
        "serverNames": [sni],
        "privateKey": priv,
        "shortIds": [shortid],
        "settings": {"publicKey": pub, "fingerprint": "chrome", "serverName": "", "spiderX": "/"}
    },
    "tcpSettings": {"acceptProxyProtocol": False, "header": {"type": "none"}}
}

sniffing = {"enabled": True, "destOverride": ["http", "tls", "quic"], "metadataOnly": False, "routeOnly": False}

payload = {
    "up": 0, "down": 0, "total": 0,
    "remark": "vless-reality-vision",
    "enable": True, "expiryTime": 0,
    "listen": os.environ.get("MYPROXY_INBOUND_LISTEN", ""), "port": port, "protocol": "vless",
    "settings": json.dumps(settings),
    "streamSettings": json.dumps(stream),
    "sniffing": json.dumps(sniffing)
}

print(json.dumps(payload))
