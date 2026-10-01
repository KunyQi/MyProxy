#!/usr/bin/env python3
"""Verify that a Linux client artifact really is a **Linux-shaped** artifact.

    python scripts/verify_linux_artifact.py                 # dist/linux
    python scripts/verify_linux_artifact.py --dist <dir>

``scripts/package_linux.py`` produces ``MyProxy-linux-<arch>/`` and a
reproducible ``.tar.gz`` next to it.  This script answers one question about
that output: *is this the Linux form of the product, or is it the Windows form
with the names changed?*  The checks are structural on purpose -- they must hold
without running anything:

* the program is ``myproxy`` (no extension) and is a real ELF for the target
  architecture -- a Windows apphost is a PE file and would sail through a
  name-only check;
* the kernel is ``Core/xray`` -- **not** ``xray.exe`` -- is an ELF, and its
  SHA-256 equals the digest recorded in ``Core/VERSION.txt`` (the same contract
  ``CoreAssets`` enforces at startup);
* the geo databases are byte-identical to the Windows copies, because both
  clients must resolve the same tags;
* **no** ``*.exe`` anywhere in the tree or the archive;
* the archive carries 0755 on ``myproxy``, ``myproxy-gui`` and ``Core/xray``.  This is the check
  that matters for a Windows cross-build: NTFS has no execute bit, so the
  directory on disk shows 0666 there and only the tar entry's mode reaches the
  user.  ``tarfile`` writes the mode we set, so this is asserted explicitly.
"""

from __future__ import annotations

import argparse
import hashlib
import struct
import sys
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WINDOWS_CORE = ROOT / "windows" / "MyProxy" / "Assets" / "Core"
DEFAULT_DIST = ROOT / "dist" / "linux"
ELF_MACHINES = {0x3E: "x86-64", 0xB7: "aarch64", 0x28: "arm"}
ELF_KINDS = {1: "REL", 2: "EXEC", 3: "DYN(PIE)"}
# Programs that are launched directly -- by the user, systemd, or the desktop
# entry's Exec=myproxy-gui -- and therefore need 0755 in the archive.  Kept as
# an independent list rather than imported from package_linux.py: a verifier
# that reads the packager's own list would agree with the packager's mistakes.
EXECUTABLE_ENTRIES = ("myproxy", "myproxy-gui", "Core/xray")


class ArtifactError(RuntimeError):
    """An actionable artifact-shape failure."""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def elf_info(path: Path) -> dict[str, object]:
    header = path.read_bytes()[:64]
    if header[:4] != b"\x7fELF":
        return {"elf": False, "magic": header[:2].hex()}
    return {
        "elf": True,
        "bits": "64-bit" if header[4] == 2 else "32-bit",
        "kind": ELF_KINDS.get(struct.unpack("<H", header[16:18])[0], "?"),
        "machine": ELF_MACHINES.get(struct.unpack("<H", header[18:20])[0], "?"),
    }


def read_manifest(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip()
    return values


def check(name: str, condition: bool, detail: str = "") -> None:
    print(f"{'ok  ' if condition else 'FAIL'} {name}{(' — ' + detail) if detail else ''}")
    if not condition:
        raise ArtifactError(name)


def verify_tree(tree: Path, expected_arch: str) -> None:
    if not tree.is_dir():
        raise ArtifactError(f"artifact directory is missing: {tree}")

    manifest = read_manifest(tree / "Core" / "VERSION.txt")
    windows_host = sys.platform == "win32"

    apphost = tree / "myproxy"
    check("program is `myproxy` (no extension)", apphost.is_file())
    info = elf_info(apphost)
    check("`myproxy` is a Linux executable for the target architecture",
          bool(info.get("elf")) and info.get("machine") == expected_arch, str(info))
    if not windows_host:
        check("`myproxy` carries the execute bit", bool(apphost.stat().st_mode & 0o111))
    else:
        print("     (Windows host: the on-disk mode is meaningless here; the tar entry is asserted below)")

    # 托盘程序必须在同一棵树里：桌面项的 Exec=myproxy-gui 指向它，
    # 少了它等于「装好了却没有托盘」。
    tray = tree / "myproxy-gui"
    check("tray program `myproxy-gui` is present", tray.is_file())
    tray_info = elf_info(tray)
    check("`myproxy-gui` is a Linux executable for the target architecture",
          bool(tray_info.get("elf")) and tray_info.get("machine") == expected_arch, str(tray_info))
    if not windows_host:
        check("`myproxy-gui` carries the execute bit", bool(tray.stat().st_mode & 0o111))

    xray = tree / "Core" / "xray"
    check("kernel is `Core/xray` (not `xray.exe`)", xray.is_file())
    info = elf_info(xray)
    check("`Core/xray` is an ELF for the target architecture",
          bool(info.get("elf")) and info.get("machine") == expected_arch, str(info))
    if not windows_host:
        check("`Core/xray` carries the execute bit", bool(xray.stat().st_mode & 0o111))
    check("`Core/xray` matches the digest in VERSION.txt",
          sha256(xray) == manifest.get("xray", ""),
          f"{sha256(xray)[:16]}… vs {manifest.get('xray', '')[:16]}…")

    for name in ("geoip.dat", "geosite.dat"):
        check(f"{name} is byte-identical to the Windows copy",
              sha256(tree / "Core" / name) == sha256(WINDOWS_CORE / name))
        check(f"{name} matches the recorded digest",
              sha256(tree / "Core" / name) == manifest.get(name, ""))

    exes = sorted(path.relative_to(tree).as_posix() for path in tree.rglob("*.exe"))
    check("no `*.exe` anywhere in the tree", not exes, ", ".join(exes))
    check("signing notice is present", (tree / "SIGNING-NOTICE.txt").is_file())

    # 桌面集成必须自洽：`.desktop` 里 Exec 指的那个程序要真的在同一棵树里，
    # 否则用户「装好了却没有托盘程序」——这类断链在打包时看不出来。
    desktop = tree / "myproxy-gui.desktop"
    check("desktop entry is present", desktop.is_file())
    entries: dict[str, str] = {}
    for raw_line in desktop.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            entries.setdefault(key, value)
    check("desktop entry declares Type=Application", entries.get("Type") == "Application")
    check("desktop entry has a name", bool(entries.get("Name")))
    check("desktop entry has a Chinese name (interface language)", bool(entries.get("Name[zh_CN]")))
    executable = entries.get("Exec", "").split()[0] if entries.get("Exec") else ""
    check("desktop entry Exec points at a program shipped in the tree",
          bool(executable) and (tree / executable).is_file(), entries.get("Exec", "(missing)"))
    icon = tree / (entries.get("Icon", "") + ".png") if entries.get("Icon") else None
    check("desktop entry icon is shipped next to it",
          icon is not None and icon.is_file())


def verify_archive(archive: Path, expected_prefix: str) -> None:
    if not archive.is_file():
        raise ArtifactError(f"archive is missing: {archive}")

    with tarfile.open(archive, "r:gz") as bundle:
        members = bundle.getmembers()

    names = [member.name for member in members]
    check("no `*.exe` inside the archive", not any(name.endswith(".exe") for name in names))
    check(f"archive entries live under {expected_prefix}/",
          bool(names) and all(name.startswith(expected_prefix + "/") for name in names),
          names[0] if names else "")
    modes = {member.name: member.mode for member in members}
    rendered = " ".join(
        f"{name}={oct(modes.get(f'{expected_prefix}/{name}', 0))}" for name in EXECUTABLE_ENTRIES
    )
    check("archive marks every launched program as 0755",
          all(modes.get(f"{expected_prefix}/{name}") == 0o755 for name in EXECUTABLE_ENTRIES),
          rendered)

    checksum_file = archive.with_name(archive.name + ".sha256")
    check("archive has a `.sha256` companion", checksum_file.is_file())
    recorded = checksum_file.read_text(encoding="ascii").split()[0]
    check("archive digest matches its `.sha256`", sha256(archive) == recorded, sha256(archive))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dist", type=Path, default=DEFAULT_DIST, help="packaging output directory")
    parser.add_argument("--arch", default="x86-64", choices=["x86-64", "aarch64"], help="expected ELF machine")
    args = parser.parse_args(argv)

    arch_suffix = "x64" if args.arch == "x86-64" else "arm64"
    tree = args.dist / f"MyProxy-linux-{arch_suffix}"
    archive = args.dist / f"MyProxy-linux-{arch_suffix}.tar.gz"

    try:
        verify_tree(tree, args.arch)
        verify_archive(archive, f"MyProxy-linux-{arch_suffix}")
    except ArtifactError as exc:
        print(f"\nARTIFACT VERIFICATION FAILED: {exc}", file=sys.stderr)
        return 1

    print("\nLinux artifact verified: this is the Linux form of the product.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
