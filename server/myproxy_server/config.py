"""Settings loading for MyProxy Server.

The shared deployment.json supplies the HTTPS API origin, tunnel probe URLs,
and default data host. Environment variables with the ``MYPROXY_`` prefix
override runtime settings. Integer parsing is lenient for non-port values and
strict for ports.
"""

from __future__ import annotations

import ipaddress
import json
import os
from dataclasses import dataclass
from pathlib import Path
import re
from typing import Mapping
from urllib.parse import urlsplit


_DEFAULT_RESTART_COMMAND = ("systemctl", "restart", "x-ui")
DEFAULT_DEPLOYMENT_CONFIG = Path(__file__).resolve().parents[2] / "deployment.json"


def _parse_https_url(
    value: object,
    *,
    allow_path: bool,
    label: str,
) -> tuple[str, str, int]:
    """Validate an HTTPS URL and its authority without normalising input."""
    if not isinstance(value, str) or not value or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in value):
        raise ValueError(f"{label} must be an HTTPS URL without whitespace")
    try:
        url = urlsplit(value)
        port = 443 if url.port is None else url.port
        host = url.hostname
    except ValueError as exc:
        raise ValueError(f"{label} has an invalid host or port") from exc
    if (
        url.scheme != "https" or not host or url.username is not None
        or url.password is not None or (not allow_path and url.path not in ("", "/"))
        or url.query or url.fragment
        or "?" in value or "#" in value or "\\" in value or "%" in url.netloc
        or url.netloc.endswith(":") or not 1 <= port <= 65535
    ):
        if allow_path:
            raise ValueError(f"{label} must be a full HTTPS URL without credentials, query or fragment")
        raise ValueError(f"{label} must contain only an HTTPS scheme, host and optional port")
    if url.netloc.startswith("["):
        try:
            ipaddress.IPv6Address(host)
        except ValueError as exc:
            raise ValueError(f"{label} brackets require an IPv6 address") from exc
    try:
        ipaddress.ip_address(host)
    except ValueError:
        if all(re.fullmatch(r"(?:0x[0-9a-f]+|[0-9]+)", label, re.IGNORECASE) for label in host.split(".")):
            raise ValueError(f"{label} requires a canonical IPv4 address") from None
        if not re.fullmatch(r"(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", host):
            raise ValueError(f"{label} has an invalid hostname") from None
        if any(
            not host_label or len(host_label) > 63
            or host_label.startswith("-") or host_label.endswith("-")
            for host_label in host.split(".")
        ):
            raise ValueError(f"{label} has an invalid hostname")
    return value, host, port


def parse_api_origin(value: object) -> tuple[str, str, int]:
    """Validate the shared HTTPS origin without silently normalising input."""
    raw, host, port = _parse_https_url(value, allow_path=False, label="api_base_url")
    return raw.removesuffix("/"), host, port


def parse_connectivity_check_urls(value: object) -> tuple[str, ...]:
    """Validate configured URLs and remove exact duplicates without reordering."""
    if not isinstance(value, list) or not 1 <= len(value) <= 4:
        raise ValueError("connectivity_check_urls must contain 1-4 HTTPS URLs")
    urls: list[str] = []
    for index, item in enumerate(value):
        raw, _, _ = _parse_https_url(
            item,
            allow_path=True,
            label=f"connectivity_check_urls[{index}]",
        )
        if raw not in urls:
            urls.append(raw)
    return tuple(urls)


def _default_connectivity_check_urls(api_base_url: str) -> tuple[str, ...]:
    return (f"{api_base_url}/connectivity-check",)


def load_deployment_config(path: str | Path) -> tuple[str, str, int, tuple[str, ...]]:
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise ValueError("deployment.json is missing or invalid; set MYPROXY_DEPLOYMENT_CONFIG") from exc
    if not isinstance(data, dict) or "api_base_url" not in data:
        raise ValueError("deployment.json must contain api_base_url")
    if set(data) - {"api_base_url", "connectivity_check_urls"}:
        raise ValueError("deployment.json contains an unknown key")
    api_base_url, host, port = parse_api_origin(data["api_base_url"])
    connectivity_check_urls = (
        parse_connectivity_check_urls(data["connectivity_check_urls"])
        if "connectivity_check_urls" in data
        else _default_connectivity_check_urls(api_base_url)
    )
    return api_base_url, host, port, connectivity_check_urls


def _raw(env: Mapping[str, str], name: str) -> str | None:
    return env.get(f"MYPROXY_{name}")


def _get_str(env: Mapping[str, str], name: str, default: str) -> str:
    raw = _raw(env, name)
    if raw is None:
        return default
    return raw


def _get_int(env: Mapping[str, str], name: str, default: int) -> int:
    raw = _raw(env, name)
    if raw is None or raw == "":
        return default
    try:
        return int(raw)
    except (TypeError, ValueError):
        return default


def _get_port(env: Mapping[str, str], name: str, default: int) -> int:
    raw = _raw(env, name)
    if raw is None or raw == "":
        return default
    try:
        value = int(raw)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"MYPROXY_{name} must be an integer, got {raw!r}") from exc
    if not 1 <= value <= 65535:
        raise ValueError(f"MYPROXY_{name} must be in range 1-65535, got {value}")
    return value


def _get_restart_command(env: Mapping[str, str]) -> tuple[str, ...]:
    raw = _raw(env, "XUI_RESTART_COMMAND")
    if raw is None:
        return _DEFAULT_RESTART_COMMAND
    parts = tuple(raw.split())
    if not parts:
        return _DEFAULT_RESTART_COMMAND
    return parts


def _get_ip_tuple(env: Mapping[str, str], name: str) -> tuple[str, ...]:
    raw = _raw(env, name)
    if raw is None or not raw.strip():
        return ()
    values: list[str] = []
    for item in raw.split(","):
        text = item.strip()
        if not text:
            continue
        try:
            values.append(ipaddress.ip_address(text).compressed)
        except ValueError as exc:
            raise ValueError(f"MYPROXY_{name} contains an invalid IP address") from exc
    return tuple(values)


@dataclass(frozen=True)
class Settings:
    db_path: str = "/var/lib/myproxy-api/myproxy.db"
    admin_token: str = ""
    device_token_secret: str = ""
    listen_host: str = "127.0.0.1"
    listen_port: int = 1820
    tls_cert: str = "/etc/myproxy/tls/server.crt"
    tls_key: str = "/etc/myproxy/tls/server.key"
    xui_db_path: str = "/etc/x-ui/x-ui.db"
    xui_inbound_id: int = 1
    deployment_config: str = str(DEFAULT_DEPLOYMENT_CONFIG)
    api_base_url: str = "https://api.example.invalid"
    api_host: str = "api.example.invalid"
    api_port: int = 443
    connectivity_check_urls: tuple[str, ...] = ("https://api.example.invalid/connectivity-check",)
    server_host: str = "api.example.invalid"
    server_port: int | None = None
    public_key: str = ""
    short_id: str = ""
    sni: str = ""
    fingerprint: str = "chrome"
    flow: str = "xtls-rprx-vision"
    spider_x: str = "/"
    xui_restart_command: tuple[str, ...] = _DEFAULT_RESTART_COMMAND
    binding_default_ttl_seconds: int = 3600
    claim_window_seconds: int = 600
    claim_max_per_ip: int = 20
    claim_max_failures_per_code: int = 5
    trusted_proxy_ips: tuple[str, ...] = ()
    xui_helper_socket: str = ""
    # ``keyId:hex,...`` -- Ed25519 public keys trusted to sign release
    # manifests.  Empty means the Update Plane refuses to register releases at
    # all; see release.verify_and_parse for why that is the safe default.
    release_signing_keys: str = ""
    # Empty directory uses the database's parent / releases. A zero limit
    # disables browser uploads while preserving externally hosted releases.
    release_artifact_dir: str = ""
    release_artifact_max_bytes: int = 512 * 1024 * 1024
    # An optional HTTPS download origin keeps artifact TLS independent from
    # the existing API origin. Empty preserves the shared API origin.
    release_artifact_origin: str = ""
    # Observability Plane.  Retention is enforced on write (this process has
    # no scheduler), and the sweep interval throttles how often a heartbeat is
    # allowed to trigger an x-ui counter read.
    usage_retention_days: int = 90
    usage_ingest_interval_seconds: int = 300
    # How many distinct egress addresses to keep per device, newest first.
    # This is the only bound on the address history, so lowering it is the
    # lever for keeping less; 0 keeps the current address and no history.
    device_address_history: int = 10

    def __post_init__(self) -> None:
        if self.release_artifact_origin != "":
            try:
                origin, host, _ = parse_api_origin(self.release_artifact_origin)
            except ValueError as exc:
                raise ValueError(
                    "MYPROXY_RELEASE_ARTIFACT_ORIGIN must contain only an HTTPS scheme, host and optional port"
                ) from exc
            if host.lower() == "invalid" or host.lower().endswith(".invalid"):
                raise ValueError("MYPROXY_RELEASE_ARTIFACT_ORIGIN must use a configured HTTPS origin")
            object.__setattr__(self, "release_artifact_origin", origin)

    def require_configured_deployment(self) -> None:
        if self.api_host == "example.invalid" or self.api_host.endswith(".invalid"):
            raise ValueError("deployment.json api_base_url is not configured; replace the example HTTPS origin")

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> "Settings":
        data: Mapping[str, str] = os.environ if env is None else env

        deployment_config = _get_str(data, "DEPLOYMENT_CONFIG", str(DEFAULT_DEPLOYMENT_CONFIG))
        api_base_url, api_host, api_port, connectivity_check_urls = load_deployment_config(deployment_config)
        # An empty optional override must not erase the shared deployment host.
        server_host = _raw(data, "SERVER_HOST") or api_host
        if any(c.isspace() for c in server_host):
            raise ValueError("MYPROXY_SERVER_HOST must be a hostname or IP without whitespace")

        xui_db_path = _get_str(data, "XUI_DB_PATH", cls.xui_db_path)
        xui_inbound_id = _get_int(data, "XUI_INBOUND_ID", cls.xui_inbound_id)
        server_port_raw = _raw(data, "SERVER_PORT")
        server_port = (
            None
            if server_port_raw is None or server_port_raw == ""
            else _get_port(data, "SERVER_PORT", 443)
        )

        return cls(
            deployment_config=deployment_config,
            api_base_url=api_base_url,
            api_host=api_host,
            api_port=api_port,
            connectivity_check_urls=connectivity_check_urls,
            db_path=_get_str(data, "DB_PATH", cls.db_path),
            admin_token=_get_str(data, "ADMIN_TOKEN", cls.admin_token),
            device_token_secret=_get_str(
                data, "DEVICE_TOKEN_SECRET", cls.device_token_secret
            ),
            listen_host=_get_str(data, "LISTEN_HOST", cls.listen_host),
            listen_port=_get_port(data, "LISTEN_PORT", cls.listen_port),
            tls_cert=_get_str(data, "TLS_CERT", cls.tls_cert),
            tls_key=_get_str(data, "TLS_KEY", cls.tls_key),
            xui_db_path=xui_db_path,
            xui_inbound_id=xui_inbound_id,
            server_host=server_host,
            server_port=server_port,
            public_key=_get_str(data, "PUBLIC_KEY", cls.public_key),
            short_id=_get_str(data, "SHORT_ID", cls.short_id),
            sni=_get_str(data, "SNI", cls.sni),
            fingerprint=_get_str(data, "FINGERPRINT", cls.fingerprint),
            flow=_get_str(data, "FLOW", cls.flow),
            spider_x=_get_str(data, "SPIDER_X", cls.spider_x),
            xui_restart_command=_get_restart_command(data),
            binding_default_ttl_seconds=_get_int(
                data, "BINDING_DEFAULT_TTL_SECONDS", cls.binding_default_ttl_seconds
            ),
            claim_window_seconds=_get_int(
                data, "CLAIM_WINDOW_SECONDS", cls.claim_window_seconds
            ),
            claim_max_per_ip=_get_int(
                data, "CLAIM_MAX_PER_IP", cls.claim_max_per_ip
            ),
            claim_max_failures_per_code=_get_int(
                data, "CLAIM_MAX_FAILURES_PER_CODE", cls.claim_max_failures_per_code
            ),
            trusted_proxy_ips=_get_ip_tuple(data, "TRUSTED_PROXY_IPS"),
            xui_helper_socket=_get_str(data, "XUI_HELPER_SOCKET", ""),
            release_signing_keys=_get_str(
                data, "RELEASE_SIGNING_KEYS", cls.release_signing_keys
            ),
            release_artifact_dir=_get_str(data, "RELEASE_ARTIFACT_DIR", cls.release_artifact_dir),
            release_artifact_max_bytes=_get_int(
                data, "RELEASE_ARTIFACT_MAX_BYTES", cls.release_artifact_max_bytes
            ),
            release_artifact_origin=_get_str(
                data, "RELEASE_ARTIFACT_ORIGIN", cls.release_artifact_origin
            ),
            usage_retention_days=_get_int(
                data, "USAGE_RETENTION_DAYS", cls.usage_retention_days
            ),
            usage_ingest_interval_seconds=_get_int(
                data,
                "USAGE_INGEST_INTERVAL_SECONDS",
                cls.usage_ingest_interval_seconds,
            ),
            device_address_history=_get_int(
                data, "DEVICE_ADDRESS_HISTORY", cls.device_address_history
            ),
        )
