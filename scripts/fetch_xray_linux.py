#!/usr/bin/env python3
"""Fetch the Linux xray-core binary and record its SHA-256.

The Linux client refuses to start a kernel it cannot verify (see
``linux/MyProxyLinux/core/Services/CoreAssets.cs``): the digest in
``linux/MyProxyLinux/assets/VERSION.txt`` is the only thing standing between the
user's REALITY credentials and "whatever binary happens to be on disk".  That
digest can only be computed after the upstream archive has been downloaded and
unpacked, so it is filled in here rather than by hand.

    python scripts/fetch_xray_linux.py --rid linux-x64
    python scripts/fetch_xray_linux.py --check

``--check`` never touches the network: it verifies the binary already in the
repository against the recorded digest, which is what the packaging preflight
uses.  A mismatch is a hard failure -- re-download rather than edit the digest.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import os
import re
import shutil
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "linux" / "MyProxyLinux" / "assets"
MANIFEST = ASSETS / "VERSION.txt"
BINARY = ASSETS / "xray"
RELEASE_URL = "https://github.com/XTLS/Xray-core/releases/download"
HEX64 = re.compile(r"^[0-9a-f]{64}$")

# Upstream asset names per runtime identifier.  Anything not listed here has no
# recorded archive digest and must be passed in explicitly with --zip-sha256,
# so a new architecture cannot be added by accident.
RID_ASSETS = {
    "linux-x64": "Xray-linux-64.zip",
    "linux-arm64": "Xray-linux-arm64-v8a.zip",
}
# The digest recorded in VERSION.txt belongs to this one; other RIDs need an
# explicit --zip-sha256 that someone verified out of band.
PRIMARY_RID = "linux-x64"


class FetchError(RuntimeError):
    """An actionable failure."""


def read_manifest() -> dict[str, str]:
    values: dict[str, str] = {}
    for raw_line in MANIFEST.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if line.startswith("#") or "=" not in line:
            continue
        name, value = (part.strip() for part in line.split("=", 1))
        values[name] = value
    return values


def write_manifest(values: dict[str, str], original: str) -> None:
    """Rewrite only the ``xray=`` line, atomically, keeping the comments."""
    lines = original.splitlines()
    replaced = False
    for index, line in enumerate(lines):
        if line.strip().startswith("xray="):
            lines[index] = f"xray={values['xray']}"
            replaced = True
    if not replaced:
        raise FetchError("VERSION.txt has no xray= line to fill in")

    temporary = MANIFEST.with_suffix(".txt.tmp")
    temporary.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    os.replace(temporary, MANIFEST)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def download(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "myproxy-fetch-xray-linux"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


def load_archive(source: Path | None, url: str) -> bytes:
    if source is not None:
        if not source.is_file():
            raise FetchError(f"archive not found: {source}")
        return source.read_bytes()
    try:
        return download(url)
    except OSError as exc:  # URLError is an OSError subclass
        raise FetchError(f"cannot download {url}: {exc}") from exc


def extract_binary(archive: bytes) -> bytes:
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            names = [name for name in bundle.namelist() if Path(name).name == "xray"]
            if len(names) != 1:
                raise FetchError(f"expected exactly one xray entry in the archive, found {len(names)}")
            data = bundle.read(names[0])
    except zipfile.BadZipFile as exc:
        raise FetchError("the downloaded file is not a ZIP archive") from exc

    if len(data) < 1_000_000:
        raise FetchError(f"the extracted xray binary is implausibly small ({len(data)} bytes)")
    if not data.startswith(b"\x7fELF"):
        raise FetchError("the extracted xray binary is not an ELF executable")
    return data


def check() -> int:
    values = read_manifest()
    digest = values.get("xray", "")
    if not HEX64.fullmatch(digest):
        print(
            "PREFLIGHT FAILED: VERSION.txt has no xray digest; run "
            "python scripts/fetch_xray_linux.py --rid linux-x64",
            file=sys.stderr,
        )
        return 1
    if not BINARY.is_file():
        print(f"PREFLIGHT FAILED: {BINARY.relative_to(ROOT)} is missing", file=sys.stderr)
        return 1

    actual = sha256_bytes(BINARY.read_bytes())
    if actual != digest:
        print(
            f"PREFLIGHT FAILED: {BINARY.relative_to(ROOT)} is {actual}, VERSION.txt says {digest}",
            file=sys.stderr,
        )
        return 1

    print(f"xray {values.get('xray-core', '?')} digest verified: {digest}")
    return 0


def fetch(rid: str, source: Path | None, zip_sha256: str | None) -> int:
    values = read_manifest()
    original = MANIFEST.read_text(encoding="utf-8")

    asset = RID_ASSETS.get(rid)
    if asset is None:
        raise FetchError(f"no upstream asset is known for {rid}; add it to RID_ASSETS with a verified digest")

    if zip_sha256 is None:
        if rid != PRIMARY_RID:
            raise FetchError(
                f"{rid} has no recorded archive digest; pass --zip-sha256 with a value you verified"
            )
        zip_sha256 = values.get("zip_sha256", "")
    if not HEX64.fullmatch(zip_sha256):
        raise FetchError("the archive digest must be 64 lowercase hex characters")

    version = values.get("xray-core", "")
    if not version.startswith("v"):
        raise FetchError("VERSION.txt has no xray-core version to download")

    url = f"{RELEASE_URL}/{version}/{asset}"
    print(f"fetching {url}")
    archive = load_archive(source, url)

    actual_zip = sha256_bytes(archive)
    if actual_zip != zip_sha256:
        raise FetchError(
            f"archive digest mismatch: expected {zip_sha256}, got {actual_zip}; "
            "refusing to unpack an archive that is not the one this repository pinned"
        )

    binary = extract_binary(archive)
    digest = sha256_bytes(binary)

    with tempfile.NamedTemporaryFile(dir=ASSETS, delete=False) as handle:
        handle.write(binary)
        temporary = Path(handle.name)
    os.chmod(temporary, 0o755)
    shutil.move(str(temporary), BINARY)

    values["xray"] = digest
    write_manifest(values, original)

    print(f"wrote {BINARY.relative_to(ROOT)} ({len(binary)} bytes)")
    print(f"recorded xray={digest}")
    print("commit both files: the digest without the binary fails the packaging preflight")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", default=PRIMARY_RID, choices=sorted(RID_ASSETS), help="target runtime identifier")
    parser.add_argument("--archive", type=Path, help="use a local archive instead of downloading")
    parser.add_argument("--zip-sha256", help="digest of the archive; required for non-primary RIDs")
    parser.add_argument("--check", action="store_true", help="verify the checked-in binary without network access")
    args = parser.parse_args(argv)

    try:
        if args.check:
            return check()
        return fetch(args.rid, args.archive, args.zip_sha256)
    except FetchError as exc:
        print(f"FETCH FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
