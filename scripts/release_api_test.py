#!/usr/bin/env python3
"""Release API e2e check; prints only status/version/field-existence, never secrets."""
import http.client, ipaddress, json, os, re, ssl, sys, uuid
from pathlib import Path
from urllib.parse import urlsplit

try:
    from scripts import release_verify
except ImportError:
    import release_verify

HOST = ""
PORT = 443
CLIENT_VERSION = os.environ.get("MYPROXY_CLIENT_VERSION", "")

_HOST_LABEL = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")
_SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


def ctx():
    return ssl.create_default_context(cafile=os.environ.get("MYPROXY_CA_FILE") or None)


def verified_connection():
    """Verify the certificate chain, validity and hostname before sending data."""
    conn = http.client.HTTPSConnection(HOST, PORT, timeout=15, context=ctx())
    conn.connect()
    return conn


def req(method, path, body=None, token=None):
    # The verified TLS handshake happens before request(), so pairing codes
    # and device tokens are never sent over an unverified connection.
    conn = verified_connection()
    try:
        headers = {"Accept": "application/json", "User-Agent": f"MyProxy/{CLIENT_VERSION} (Windows)"}
        if body is not None: headers["Content-Type"] = "application/json"
        if token: headers["Authorization"] = f"Bearer {token}"
        conn.request(method, path, json.dumps(body) if body is not None else None, headers)
        r = conn.getresponse(); data = r.read().decode()
        return r.status, (json.loads(data) if data else None)
    finally:
        conn.close()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def valid_host(value):
    """Accept an IP literal or a conventional DNS hostname only."""
    if not value or len(value) > 253 or any(char.isspace() for char in value):
        return False
    try:
        ipaddress.ip_address(value)
        return True
    except ValueError:
        pass
    hostname = value.rstrip(".")
    return bool(hostname) and all(_HOST_LABEL.fullmatch(label) for label in hostname.split("."))


def valid_version(value):
    """Accept the explicit three-part client version used by the release API."""
    return bool(_SEMVER.fullmatch(value))


def validate_public_config(config, label="config"):
    """Validate the public ServerProfile shape and deny private material."""
    require(isinstance(config, dict), f"{label} is not an object")
    for key in (
        "server", "port", "uuid", "security", "publicKey", "shortId", "sni",
        "fingerprint", "flow", "spiderX",
    ):
        require(key in config, f"{label} field missing: {key}")
    require(
        isinstance(config["server"], str) and bool(config["server"].strip()),
        f"{label} server is invalid",
    )
    require(
        isinstance(config["port"], int)
        and not isinstance(config["port"], bool)
        and 1 <= config["port"] <= 65535,
        f"{label} port is invalid",
    )
    try:
        uuid.UUID(config["uuid"])
    except (AttributeError, TypeError, ValueError) as exc:
        raise RuntimeError(f"{label} uuid is invalid") from exc
    require(config["security"] == "reality", f"{label} security is invalid")
    for key in ("publicKey", "shortId", "sni", "fingerprint", "flow", "spiderX"):
        require(
            isinstance(config[key], str) and bool(config[key].strip()),
            f"{label} field is empty or invalid: {key}",
        )
    require(
        "privatekey" not in json.dumps(config, ensure_ascii=False).lower(),
        f"privateKey appeared in {label}",
    )


def validate_claim_response(data):
    """Validate the non-secret shape of a successful claim response."""
    require(isinstance(data, dict), "claim response is not an object")
    require(
        {"deviceId", "deviceToken", "configVersion", "config"} <= set(data),
        "claim fields missing",
    )
    require(
        isinstance(data["deviceId"], str) and bool(data["deviceId"].strip()),
        "claim deviceId is empty or invalid",
    )
    require(
        isinstance(data["deviceToken"], str) and bool(data["deviceToken"].strip()),
        "claim deviceToken is empty or invalid",
    )
    require(
        isinstance(data["configVersion"], int)
        and not isinstance(data["configVersion"], bool)
        and data["configVersion"] > 0,
        "claim configVersion is invalid",
    )
    validate_public_config(data["config"], "claim config")
    require(
        "privatekey" not in json.dumps(data, ensure_ascii=False).lower(),
        "privateKey appeared in claim response",
    )


def validate_config_response(data, expected_version=None):
    """Validate a device config response, including its public-only profile."""
    require(isinstance(data, dict), "config response is not an object")
    if expected_version is not None:
        require(
            data.get("configVersion") == expected_version,
            "config version mismatch",
        )
    validate_public_config(data.get("config"), "config response")
    require(
        "privatekey" not in json.dumps(data, ensure_ascii=False).lower(),
        "privateKey appeared in config response",
    )


def validate_latest_response(data):
    """Validate the public update-manifest shape without downloading it."""
    require(isinstance(data, dict), "latest response is not an object")
    require(
        {"version", "downloadUrl", "sha256", "mandatory"} <= set(data),
        "latest fields missing",
    )
    require(valid_version(data["version"]), "latest version is invalid")
    require(isinstance(data["downloadUrl"], str), "latest downloadUrl is invalid")
    digest = data["sha256"]
    require(
        isinstance(digest, str)
        and (not digest or bool(re.fullmatch(r"[0-9a-f]{64}", digest))),
        "latest sha256 is invalid",
    )
    require(isinstance(data["mandatory"], bool), "latest mandatory is invalid")


def main():
    global HOST, PORT
    config_path = Path(os.environ.get("MYPROXY_DEPLOYMENT_CONFIG") or release_verify.DEPLOYMENT_CONFIG)
    origin = release_verify.deployment_origin(
        json.loads(config_path.read_text(encoding="utf-8")), require_configured=True
    )
    parsed = urlsplit(origin)
    HOST, PORT = parsed.hostname, parsed.port or 443
    if not valid_version(CLIENT_VERSION):
        raise RuntimeError("set MYPROXY_CLIENT_VERSION to a valid x.y.z version")
    if os.environ.get("MYPROXY_RELEASE_TEST_CONFIRM") != "YES":
        raise RuntimeError(
            "set MYPROXY_RELEASE_TEST_CONFIRM=YES before consuming a pairing code"
        )
    # Validate TLS before reading the pairing code; every request also verifies
    # the peer and http.client never follows an HTTP redirect automatically.
    connection = verified_connection()
    connection.close()
    print("TLS: chain and hostname verified")

    code = Path(os.environ.get("PAIRING_CODE_FILE", "secrets/pairing_code.txt")).read_text().strip()
    require(bool(re.fullmatch(r"[A-Z0-9]{4}-[A-Z0-9]{4}", code)), "pairing code file is invalid")
    st, d = req("POST", "/api/device/claim",
                {"pairingCode": code, "deviceName": "E2E-BENCH",
                 "platform": "windows", "clientVersion": CLIENT_VERSION})
    require(st == 200, f"claim failed with HTTP {st}")
    validate_claim_response(d)
    print(f"claim: 200 configVersion={d['configVersion']} configFieldsOk privateKeyAbsent")
    st2, d2 = req("GET", "/api/device/config", token=d["deviceToken"])
    require(st2 == 200, f"config failed with HTTP {st2}")
    validate_config_response(d2, d["configVersion"])
    print(f"config: 200 configVersion={d2['configVersion']}")
    st3, d3 = req("POST", "/api/device/claim",
                  {"pairingCode": "ZZZZ-ZZZZ", "deviceName": "X",
                   "platform": "windows", "clientVersion": CLIENT_VERSION})
    require(st3 == 400 and isinstance(d3, dict) and d3.get("error", {}).get("code") == "PairingInvalid", "bad-code check failed")
    print("bad-code: 400 PairingInvalid")
    st4, d4 = req("GET", "/api/device/config", token="tok_wrong")
    require(st4 == 401 and isinstance(d4, dict) and d4.get("error", {}).get("code") == "TokenInvalid", "bad-token check failed")
    print("bad-token: 401 TokenInvalid")
    st5, d5 = req("GET", "/client/windows/latest.json")
    require(st5 == 200, f"latest check failed with HTTP {st5}")
    validate_latest_response(d5)
    print(f"latest: 200 version={d5['version']}")
    st6, d6 = req("GET", "/healthz")
    require(st6 == 200, f"health check failed with HTTP {st6}")
    require(isinstance(d6, dict) and d6.get("ok") is True, "health response is invalid")
    print("ALL RELEASE API CHECKS PASSED")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print("FAIL:", type(e).__name__, e); sys.exit(1)
