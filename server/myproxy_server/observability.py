"""Observability Plane: usage bucketing, counter deltas and coarse location.

A *leaf* module like :mod:`auth`, :mod:`db`, :mod:`xui` and :mod:`release`: no
configuration, no database, no imports from the rest of the package.  Every
rule that decides *what this plane is allowed to know* lives here, in pure
functions, so the rules are testable without a server and impossible to bypass
by writing to the tables from somewhere else.

Three limits are deliberate and must survive later edits:

1.  **The client's egress address is stored, and its history is bounded.**
    The operator needs the real address to answer an abuse complaint, to see
    that one account is being used from five places at once, and to recognise
    a session that moved.  So the address is kept as given
    (:func:`normalise_address`), the last few *distinct* addresses per device
    are kept alongside it, and the /24 or /48 prefix the coarse location view
    uses is derived from the address rather than replacing it
    (:func:`ip_prefix`).

    This reverses the original design on purpose, and the cost is real: a
    copy of this database now *is* a record of who connected from where, so
    backups, snapshots and exports carry personal data and have to be handled
    as such.  Two things still bound it.  The per-device history is capped
    (``Settings.device_address_history``), so a device carries a handful of
    recent addresses and not a permanent trail.  And addresses leave the
    server only through the Admin API -- the Device API neither accepts an
    address nor reads one back, so one device can never learn another's.

2.  **No hostname, URL or per-domain record is ever accepted.**  Service
    attribution arrives only as byte counts against a *closed vocabulary*
    (:data:`SERVICE_CATEGORIES`), the same closed-vocabulary discipline
    ``xui.XUI_ERROR_CODES`` uses at the privilege boundary.  A category
    outside the list is dropped, not stored under some "other" bucket and not
    echoed back.  Without this rule the plane quietly becomes a browsing
    history, which is a different product with a different threat model.

3.  **Geolocation is never looked up by calling anyone.**  The server has no
    GeoIP database and must not send user addresses to a third-party
    geolocation service to get one.  Country/region/city/ASN/ISP are therefore
    *supplied out of band* by an administrator and stored as given; what the
    server derives by itself is only the truncated prefix from (1).

Buckets are whole UTC hours.  Hourly is the coarsest resolution that still
answers "which hour of the day does this device actually use the service",
and the daily series the Admin API returns is summed from it rather than
stored twice.
"""

from __future__ import annotations

import ipaddress
import re
from datetime import datetime, timedelta, timezone
from typing import Any, Iterable


# Closed vocabulary.  Adding a member is a contract change: it must be added
# to the Admin API documentation and to both clients at the same time, or one
# client's traffic silently stops being attributable.
SERVICE_CATEGORIES = (
    "video",
    "social",
    "messaging",
    "web",
    "download",
    "other",
)

ACTIVITY_STATES = ("connected", "idle", "offline")

# A device that has not been heard from within this many seconds is no longer
# "connected".  Deliberately a small multiple of the Windows heartbeat period
# (60s) and under the Android one (5 min), so a single missed beat does not
# flip the state but a genuinely gone device does.
CONNECTED_WITHIN_SECONDS = 180
IDLE_WITHIN_SECONDS = 3600

# Hourly rows are pruned past this age.  Ninety days answers "usage trend over
# a quarter" while bounding the table for a deployment with a handful of
# devices.  Pruning happens on write, because this process has no scheduler.
DEFAULT_RETENTION_DAYS = 90

MAX_CATEGORY_ENTRIES = len(SERVICE_CATEGORIES)
# One hour of traffic on a link far faster than anything this product serves.
# A report above it is a bug or a forged counter, not a measurement.
MAX_REPORTED_BYTES_PER_BUCKET = 1 << 42  # 4 TiB

_TIMESTAMP_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
_GEO_FIELD_RE = re.compile(r"^[\w .,'()/-]{0,64}$", re.UNICODE)


class ObservabilityError(Exception):
    """A usage report or query was malformed.

    Like :class:`release.ManifestError`, the message is for the server log.
    The HTTP layer maps it to a generic ``BadRequest``.
    """


# ----------------------------------------------------------------------
# Time buckets
# ----------------------------------------------------------------------


def parse_timestamp(value: Any) -> datetime:
    if not isinstance(value, str) or not _TIMESTAMP_RE.fullmatch(value):
        raise ObservabilityError("timestamp must be YYYY-MM-DDTHH:MM:SSZ")
    return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(
        tzinfo=timezone.utc
    )


def format_timestamp(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def hour_bucket(value: str) -> str:
    """Return the whole-UTC-hour bucket a timestamp falls in."""
    moment = parse_timestamp(value)
    return format_timestamp(moment.replace(minute=0, second=0, microsecond=0))


def day_bucket(value: str) -> str:
    moment = parse_timestamp(value)
    return format_timestamp(
        moment.replace(hour=0, minute=0, second=0, microsecond=0)
    )


def bucket_range(start: str, end: str, granularity: str) -> list[str]:
    """Every bucket label in ``[start, end]``, inclusive, oldest first.

    Returned even when a bucket has no rows: a trend with holes silently
    dropped reads as "no traffic then" and "no data then" the same way, and
    those are different answers.
    """
    if granularity not in ("hour", "day"):
        raise ObservabilityError("granularity must be hour or day")
    first = parse_timestamp(hour_bucket(start) if granularity == "hour" else day_bucket(start))
    last = parse_timestamp(hour_bucket(end) if granularity == "hour" else day_bucket(end))
    if last < first:
        raise ObservabilityError("range end precedes range start")
    step = timedelta(hours=1) if granularity == "hour" else timedelta(days=1)
    # Bound the walk so a wide range cannot be used to make the server build a
    # multi-million element list.
    limit = 24 * 400 if granularity == "hour" else 400
    labels: list[str] = []
    cursor = first
    while cursor <= last:
        if len(labels) >= limit:
            raise ObservabilityError("requested range is too wide")
        labels.append(format_timestamp(cursor))
        cursor += step
    return labels


def retention_cutoff(now: str, days: int = DEFAULT_RETENTION_DAYS) -> str:
    return format_timestamp(parse_timestamp(now) - timedelta(days=max(1, days)))


# ----------------------------------------------------------------------
# Counter deltas
# ----------------------------------------------------------------------


def counter_delta(previous: int, current: int) -> int:
    """Bytes transferred between two readings of a cumulative counter.

    x-ui's counters reset whenever Xray restarts, and a reset looks exactly
    like a counter going backwards.  Treating the new value as the delta is
    the only choice that neither invents traffic (which ``current - previous``
    would do by going negative and then being clamped somewhere else) nor
    loses the post-reset traffic entirely (which returning 0 would do).
    """
    if not isinstance(previous, int) or isinstance(previous, bool):
        previous = 0
    if not isinstance(current, int) or isinstance(current, bool):
        return 0
    if current < 0:
        return 0
    if previous < 0 or current < previous:
        return current
    return current - previous


def clamp_reported_bytes(value: Any) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise ObservabilityError("byte counts must be integers")
    if value < 0:
        raise ObservabilityError("byte counts must not be negative")
    if value > MAX_REPORTED_BYTES_PER_BUCKET:
        raise ObservabilityError("byte count is implausibly large")
    return value


def normalise_categories(value: Any) -> dict[str, int]:
    """Validate a client's per-category byte report.

    Unknown categories are rejected rather than folded into ``other``: a
    client that has learned a category this server does not know is a version
    skew that an administrator needs to see, and silently re-bucketing it
    would hide the skew behind plausible-looking numbers.
    """
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ObservabilityError("categories must be an object")
    if len(value) > MAX_CATEGORY_ENTRIES:
        raise ObservabilityError("too many categories")
    result: dict[str, int] = {}
    for key, item in value.items():
        if key not in SERVICE_CATEGORIES:
            raise ObservabilityError("unknown service category")
        result[key] = clamp_reported_bytes(item)
    return result


# ----------------------------------------------------------------------
# Client address and coarse location
# ----------------------------------------------------------------------


def _usable_address(address: Any) -> ipaddress.IPv4Address | ipaddress.IPv6Address | None:
    """Parse an address, rejecting the ones that say nothing about a client.

    Loopback and the unspecified address appear in tests and whenever the
    request came through the local gateway without a trusted forwarding
    header.  They identify nobody, so recording them would only add noise and
    make "this device has no address yet" indistinguishable from "this device
    connects from 127.0.0.1".
    """
    if not isinstance(address, str) or not address:
        return None
    try:
        parsed = ipaddress.ip_address(address.strip())
    except ValueError:
        return None
    if parsed.is_loopback or parsed.is_unspecified:
        return None
    return parsed


def normalise_address(address: Any) -> str:
    """Canonicalise a client address for storage, or return "" to skip it.

    The compressed form is what gets stored, so the same IPv6 client written
    two different ways does not become two rows in the history.
    """
    parsed = _usable_address(address)
    return "" if parsed is None else parsed.compressed


def ip_prefix(address: Any) -> str:
    """Truncate an address to the coarsest useful network prefix.

    IPv4 keeps /24 and IPv6 keeps /48.  This is no longer a privacy measure --
    the full address is stored next to it (see limit 1 in the module
    docstring) -- but the prefix is still what the coarse location view groups
    by, and it is what stays meaningful when a residential address rotates
    within its ISP's block.
    """
    parsed = _usable_address(address)
    if parsed is None:
        return ""
    if parsed.version == 4:
        return str(ipaddress.ip_network(f"{parsed}/24", strict=False))
    return str(ipaddress.ip_network(f"{parsed}/48", strict=False))


def validate_geo(value: Any) -> dict[str, str]:
    """Validate administrator-supplied location enrichment.

    Free text, bounded and character-restricted.  The server never derives
    these itself (see the module docstring); it only stores what an operator
    states, which is why the validation is about shape, not plausibility.
    """
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ObservabilityError("geo must be an object")
    allowed = ("country", "region", "city", "asn", "isp")
    result: dict[str, str] = {}
    for key in allowed:
        item = value.get(key)
        if item is None:
            continue
        if not isinstance(item, str) or not _GEO_FIELD_RE.fullmatch(item):
            raise ObservabilityError(f"geo field {key} is invalid")
        result[key] = item.strip()
    unknown = set(value) - set(allowed)
    if unknown:
        raise ObservabilityError("unknown geo field")
    return result


# ----------------------------------------------------------------------
# Activity
# ----------------------------------------------------------------------


def activity_state(last_seen_at: Any, now: str) -> str:
    """Derive connected / idle / offline from the last heartbeat.

    Derived rather than stored so it cannot go stale: a device that stops
    beating because it lost power never gets to tell anyone it is gone, and a
    stored flag would keep claiming it is connected forever.
    """
    if not isinstance(last_seen_at, str) or not last_seen_at:
        return "offline"
    try:
        seen = parse_timestamp(last_seen_at)
        current = parse_timestamp(now)
    except ObservabilityError:
        return "offline"
    age = (current - seen).total_seconds()
    if age < 0:
        # Clock skew: a future timestamp means the device just reported.
        return "connected"
    if age <= CONNECTED_WITHIN_SECONDS:
        return "connected"
    if age <= IDLE_WITHIN_SECONDS:
        return "idle"
    return "offline"


# ----------------------------------------------------------------------
# Series assembly
# ----------------------------------------------------------------------


def build_series(
    rows: Iterable[dict[str, Any]],
    labels: list[str],
    granularity: str,
) -> list[dict[str, Any]]:
    """Fold hourly rows onto the requested labels, keeping empty buckets."""
    totals: dict[str, dict[str, int]] = {
        label: {"uplinkBytes": 0, "downlinkBytes": 0} for label in labels
    }
    for row in rows:
        bucket = row.get("bucketStart") or ""
        label = bucket if granularity == "hour" else day_bucket(bucket)
        slot = totals.get(label)
        if slot is None:
            continue
        slot["uplinkBytes"] += int(row.get("uplinkBytes") or 0)
        slot["downlinkBytes"] += int(row.get("downlinkBytes") or 0)
    return [
        {
            "bucketStart": label,
            "uplinkBytes": totals[label]["uplinkBytes"],
            "downlinkBytes": totals[label]["downlinkBytes"],
            "totalBytes": totals[label]["uplinkBytes"]
            + totals[label]["downlinkBytes"],
        }
        for label in labels
    ]
