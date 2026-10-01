#!/usr/bin/env python3
"""Trim an Xray geoip.dat / geosite.dat down to a handful of tags.

The routing rules on both clients reference a small, closed set of tags: four
for the mode itself (``geoip:cn``, ``geoip:private``, ``geosite:cn``,
``geosite:geolocation-!cn``) plus the service categories the Observability
Plane attributes traffic with.  ``KEEP`` below is the whole list.

**A tag a routing rule uses but this tool drops is a fatal bug, not a smaller
file.**  Xray resolves every ``geosite:``/``geoip:`` reference when it loads
its config and refuses to start with "code not found" if one is missing, so
the connection fails outright -- and the category rules are only emitted when
the server turns the ``usage.categories`` feature flag on, which means the
breakage appears long after the build that caused it.  ``release_verify.py``'s
``verify_geo_tags`` reads the tags back out of both clients' sources and
checks them against the shipped databases so that cannot ship.

Everything else in the shipped databases -- hundreds of country blocks in
geoip.dat, thousands of service categories in geosite.dat -- is dead weight
in this product.  This tool drops it, cutting the two files from ~29 MB to a
few MB, which is the single biggest lever on the artifact size.

## Why a byte-level filter and not a re-encoder

Both files are protobuf ``*List { repeated Entry entry = 1 }`` messages, and
each ``Entry``'s first field is a string ``country_code`` (the tag).  The
compiled ``.dat`` has already flattened every ``include`` between categories,
so a tag is a self-contained list with no cross-references left -- keeping a
tag means keeping exactly its own record and nothing else.

So this tool never decodes an entry's payload.  It reads the top-level
length-delimited records, looks only at each one's ``country_code``, and
re-emits the kept records' **original bytes** unchanged.  A kept tag is
therefore byte-for-byte what Xray shipped, which is what lets us keep trusting
it after the trim: we removed records, we did not rewrite any.

The result is deterministic (input order preserved), so the same input always
produces the same output and the same hash -- which matters because the output
goes back under the VERSION.txt hash lock.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path


class ProtoError(ValueError):
    """The input was not shaped like a geoip/geosite database."""


def _read_varint(buf: bytes, pos: int) -> tuple[int, int]:
    """Decode a base-128 varint at ``pos``; return (value, new_pos)."""
    result = 0
    shift = 0
    while True:
        if pos >= len(buf):
            raise ProtoError("truncated varint")
        byte = buf[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, pos
        shift += 7
        if shift > 63:
            raise ProtoError("varint too long")


def _skip_field(buf: bytes, pos: int, wire_type: int) -> int:
    """Advance past one field of the given wire type."""
    if wire_type == 0:  # varint
        _, pos = _read_varint(buf, pos)
        return pos
    if wire_type == 1:  # 64-bit
        return pos + 8
    if wire_type == 2:  # length-delimited
        length, pos = _read_varint(buf, pos)
        return pos + length
    if wire_type == 5:  # 32-bit
        return pos + 4
    raise ProtoError(f"unsupported wire type {wire_type}")


def _country_code(record: bytes) -> str:
    """Pull field 1 (country_code, a string) out of one entry.

    Walks every field rather than assuming field 1 comes first, so a database
    that orders the message differently still resolves correctly.
    """
    pos = 0
    while pos < len(record):
        tag, pos = _read_varint(record, pos)
        field_number = tag >> 3
        wire_type = tag & 0x07
        if field_number == 1 and wire_type == 2:
            length, pos = _read_varint(record, pos)
            return record[pos : pos + length].decode("utf-8", "replace")
        pos = _skip_field(record, pos, wire_type)
    raise ProtoError("entry has no country_code (field 1)")


def _iter_entries(data: bytes):
    """Yield (country_code, raw_record_slice) for each top-level entry.

    ``raw_record_slice`` is the exact ``tag + length + payload`` byte range, so
    re-concatenating the kept slices reproduces a valid database without any
    re-encoding.
    """
    pos = 0
    while pos < len(data):
        start = pos
        tag, pos = _read_varint(data, pos)
        field_number = tag >> 3
        wire_type = tag & 0x07
        if field_number != 1 or wire_type != 2:
            raise ProtoError(
                f"unexpected top-level field {field_number}/{wire_type}; "
                "not a geoip/geosite database"
            )
        length, pos = _read_varint(data, pos)
        end = pos + length
        if end > len(data):
            raise ProtoError("truncated entry payload")
        record = data[pos:end]
        yield _country_code(record), data[start:end]
        pos = end


def trim(data: bytes, keep: set[str]) -> tuple[bytes, list[str], list[str]]:
    """Return (trimmed_bytes, kept_tags, dropped_tags).

    ``keep`` is matched case-insensitively; the databases store tags upper-cased
    (``CN``, ``PRIVATE``, ``GEOLOCATION-!CN``) but the callers name them the way
    the routing rules do.
    """
    wanted = {tag.upper() for tag in keep}
    out = bytearray()
    kept: list[str] = []
    dropped: list[str] = []
    for code, raw in _iter_entries(data):
        if code.upper() in wanted:
            out += raw
            kept.append(code)
        else:
            dropped.append(code)
    return bytes(out), kept, dropped


# The tags each database is allowed to keep, named exactly as the routing rules
# reference them.  This list and the clients' routing code have to agree in both
# directions: a tag here that no rule uses is dead weight, and a tag a rule uses
# that is missing here makes Xray refuse to start.  ``release_verify.py``'s
# ``verify_geo_tags`` enforces the second direction against the built files.
#
# The category tags come from the Observability Plane's service attribution
# (windows/MyProxy/Core/ServiceCategories.cs and the Kotlin file beside it).
# They only reach a config when the ``usage.categories`` feature flag is on,
# which is exactly why they cannot be left to manual review.
CATEGORY_GEOSITES = {
    # cat-video
    "youtube",
    "netflix",
    "disney",
    "bilibili",
    # cat-social
    "tiktok",
    "facebook",
    "twitter",
    "instagram",
    # cat-messaging
    "telegram",
    "whatsapp",
    "signal",
}

KEEP = {
    "geoip.dat": {"cn", "private"},
    "geosite.dat": {"cn", "geolocation-!cn"} | CATEGORY_GEOSITES,
}


def _resolve_keep(path: Path, override: list[str] | None) -> set[str]:
    if override:
        return set(override)
    if path.name not in KEEP:
        raise SystemExit(
            f"don't know which tags to keep for {path.name}; pass --keep explicitly"
        )
    return KEEP[path.name]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="source .dat")
    parser.add_argument(
        "-o", "--output", type=Path, help="destination (default: overwrite input)"
    )
    parser.add_argument(
        "--keep",
        action="append",
        help="tag to keep; repeatable. Defaults by filename (geoip/geosite).",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify the trimmed output re-parses and holds exactly the kept tags",
    )
    args = parser.parse_args(argv)

    data = args.input.read_bytes()
    keep = _resolve_keep(args.input, args.keep)
    trimmed, kept, dropped = trim(data, keep)

    missing = {t.upper() for t in keep} - {k.upper() for k in kept}
    if missing:
        raise SystemExit(
            f"requested tags not found in {args.input.name}: {sorted(missing)}"
        )

    if args.check:
        # Re-parse the output and confirm it contains the kept tags and nothing
        # else -- a self-check that the byte surgery produced a valid database.
        recheck = [code for code, _ in _iter_entries(trimmed)]
        if {c.upper() for c in recheck} != {k.upper() for k in kept}:
            raise SystemExit("re-parse of trimmed output did not match kept tags")

    destination = args.output or args.input
    destination.write_bytes(trimmed)

    before, after = len(data), len(trimmed)
    digest = hashlib.sha256(trimmed).hexdigest()
    print(f"{args.input.name}: kept {sorted(kept)}  dropped {len(dropped)} tags")
    print(
        f"  {before:>10,} -> {after:>10,} bytes"
        f"  ({100 * after / before:.1f}%, saved {(before - after) / 1_048_576:.1f} MB)"
    )
    print(f"  sha256={digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
