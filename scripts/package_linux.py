#!/usr/bin/env python3
"""Package the Linux client into a deterministic tarball.

    python scripts/package_linux.py --preflight
    python scripts/package_linux.py --build --rid linux-x64

``--preflight`` is the gate that must pass before anything is shipped, and it
fails closed on the two things a Linux client cannot fake:

* the kernel digest in ``linux/MyProxyLinux/assets/VERSION.txt`` must be filled
  in *and* match the binary next to it (see ``scripts/fetch_xray_linux.py``);
* ``scripts/release_verify.py check`` must pass, which is where the version
  lock, deployment configuration, geo tags and route contract are enforced.

``--build`` additionally publishes the CLI self-contained, lays the verified
Core assets next to it, writes a signing notice, and produces a reproducible
``.tar.gz`` plus checksums. This local archive is unsigned; distributors must
provide their own release signing and publishing process.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import os
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_verify  # noqa: E402  (same-directory helper, imported on purpose)

ROOT = Path(__file__).resolve().parents[1]
LINUX_ROOT = ROOT / "linux" / "MyProxyLinux"
ASSETS = LINUX_ROOT / "assets"
CORE = ROOT / "windows" / "MyProxy" / "Assets" / "Core"
CLI_PROJECT = LINUX_ROOT / "cli" / "MyProxy.Linux.Cli.csproj"
GUI_PROJECT = LINUX_ROOT / "gui" / "MyProxy.Linux.Gui.csproj"
DIST = ROOT / "dist" / "linux"
# 产物名里的架构部分：linux-x64 → x64（不要再拼一遍 "linux"）。
ARCHIVE_NAME = "MyProxy-linux-{arch}.tar.gz"
SIGNING_NOTICE = (
    "Unsigned local packaging output.\n"
    "This archive has build checksums but no release signature and no formal release tag.\n"
    "Do not present it as a formal release.\n"
)
# Fixed metadata makes the archive byte-identical for identical inputs, which is
# what lets the checksum be compared across machines at all.
FIXED_MTIME = 0
FIXED_MODE = 0o644
FIXED_EXEC_MODE = 0o755
# Entries that must be executable in the archive (paths relative to the tree
# root).  The tar entry's mode is the only mode a user ever sees -- the on-disk
# tree is meaningless on a Windows host -- so every program a user or the
# desktop entry launches has to be listed here.  scripts/verify_linux_artifact.py
# asserts the same list.
EXECUTABLE_ENTRIES = ("myproxy", "myproxy-gui", "Core/xray")


class PackageError(RuntimeError):
    """An actionable packaging failure."""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def preflight(rid: str) -> str:
    """Verify everything that must hold before a Linux artifact may exist."""
    version = release_verify.check_all(require_configured=True)

    missing = [path for path in (CLI_PROJECT, GUI_PROJECT) if not path.is_file()]
    if missing:
        raise PackageError(f"Linux projects are missing: {', '.join(str(p) for p in missing)}")

    manifest = release_verify.parse_linux_core_manifest(ASSETS / "VERSION.txt")
    digest = manifest.get("xray", "")
    if not release_verify.HEX64.fullmatch(digest):
        raise PackageError(
            "assets/VERSION.txt has no xray digest; run "
            "python scripts/fetch_xray_linux.py --rid linux-x64 before packaging"
        )

    binary = ASSETS / "xray"
    if not binary.is_file():
        raise PackageError(f"the kernel binary is missing: {binary.relative_to(ROOT)}")
    actual = sha256(binary)
    if actual != digest:
        raise PackageError(
            f"kernel digest mismatch: {binary.relative_to(ROOT)} is {actual}, VERSION.txt says {digest}"
        )

    for name in ("geoip.dat", "geosite.dat"):
        if not (CORE / name).is_file():
            raise PackageError(f"shared geo database is missing: {(CORE / name).relative_to(ROOT)}")

    print(f"preflight ok: MyProxy {version} for {rid}")
    return version


def resolve_dotnet() -> str:
    """Use MYPROXY_DOTNET, the optional local SDK, or dotnet on PATH."""
    override = os.environ.get("MYPROXY_DOTNET")
    if override:
        return override

    for candidate in (ROOT / ".dotnet" / "dotnet.exe", ROOT / ".dotnet" / "dotnet"):
        if candidate.is_file():
            return str(candidate)

    return "dotnet"


def publish(output: Path, rid: str) -> None:
    """发布 CLI 与 GUI 到**同一棵**目录树。

    为什么放一起：桌面项 `myproxy-gui.desktop` 的 `Exec=myproxy-gui` 必须真的存在，
    否则「装好了却没有托盘程序」。两者同 TFM 同 RID，自包含发布共享的框架 DLL
    逐字节相同，唯一的区别只是各自的 apphost 与 `*.deps.json`——所以一个目录里放两个
    可执行文件是安全的，也让用户只需要解一份包。
    """
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)

    # CLI 先发布：它带 Core/ 与 `myproxy`，是主体。
    for project, label in ((CLI_PROJECT, "myproxy"), (GUI_PROJECT, "myproxy-gui")):
        command = [
            resolve_dotnet(),
            "publish",
            str(project),
            "--configuration",
            "Release",
            "--runtime",
            rid,
            "--self-contained",
            "true",
            "--nologo",
            "--output",
            str(output),
        ]
        print(f"running ({label}):", " ".join(command))
        completed = subprocess.run(command, cwd=ROOT, check=False)
        if completed.returncode != 0:
            raise PackageError(
                f"dotnet publish failed for {label}; the Linux runtime pack must be restorable"
            )

    verify_published_apphost(output)


def verify_published_apphost(output: Path) -> None:
    """发布产物必须是 **Linux 形状**：`myproxy`（无后缀）且是 ELF。

    这条检查看着多余，其实挡的是一类真实事故：`--runtime` 写错、或有人
    用 Windows 的 RID 发布，产物里就会躺着一个 `myproxy.exe`——它照样能被打进
    tar.gz，用户解开后照着说明敲 `./myproxy status` 只会得到「没有这个文件」，
    而打包记录上一切正常（Windows 的可执行文件**没有**可执行位，所以连「权限不对」
    这条线索都不会出现）。客户端自己的 `LinuxUpdateApplier.IsPlausibleInstallTree`
    在更新时用的是同一份判据（`myproxy` + `Core/xray`），两处必须一致。
    """
    for name in ("myproxy", "myproxy-gui"):
        apphost = output / name
        if not apphost.is_file():
            exe = sorted(path.name for path in output.glob("*.exe"))
            hint = f"（发现 {exe}，说明按 Windows 的形状发布了）" if exe else ""
            raise PackageError(f"published output has no `{name}` apphost{hint}: {output}")

        with apphost.open("rb") as stream:
            magic = stream.read(4)
        if magic != b"\x7fELF":
            raise PackageError(
                f"`{name}` is not an ELF executable; the publish did not target Linux"
            )


def lay_out_core(output: Path) -> None:
    """Copy the verified Core payload next to the executable.

    The kernel is written as ``Core/xray`` -- **not** ``xray.exe``.  That name is
    not cosmetic: ``CoreAssets`` looks for ``xray`` and refuses to start a kernel
    it cannot hash-verify, so a Windows-shaped name would fail at runtime with
    ``XrayMissing`` rather than at package time.

    Both executables also get the exec bit here.  On a native Linux publish the
    apphost already has it, but **cross-publishing from Windows does not** (NTFS
    has no such bit; Python sees 0666), and the tree on disk is what a
    `tar`-less user might copy around.  The archive's own modes are what really
    matter (see ``build_archive``), so this is belt-and-braces.
    """
    core = output / "Core"
    core.mkdir(exist_ok=True)

    for name in ("xray",):
        target = core / name
        shutil.copy2(ASSETS / name, target)
        make_executable(target)

    for name in ("geoip.dat", "geosite.dat", "LICENSE"):
        shutil.copy2(CORE / name, core / name)

    shutil.copy2(ASSETS / "VERSION.txt", core / "VERSION.txt")
    make_executable(output / "myproxy")
    make_executable(output / "myproxy-gui")

    # 桌面集成：`.desktop` 与图标。
    # 图标是从 Windows 的 `Assets/app.ico` 里**按字节取出**的内嵌 PNG（那是一个
    # PNG 压缩的 ICO，Vista+ 允许这种形态）——两端的应用图标因此是同一张图，
    # 不是重画的近似品。
    packaging = LINUX_ROOT / "packaging"
    shutil.copy2(packaging / "myproxy-gui.desktop", output / "myproxy-gui.desktop")
    shutil.copy2(packaging / "myproxy.png", output / "myproxy.png")


def copy_public_license_materials(output: Path) -> None:
    """Place every required notice and license under the Linux app directory."""
    release_verify.verify_public_license_materials(ROOT)
    relative_paths = set(release_verify.PUBLIC_RELEASE_MATERIALS)
    license_root = ROOT / "licenses"
    relative_paths.update(
        path.relative_to(ROOT).as_posix()
        for path in license_root.rglob("*")
        if path.is_file()
    )

    for relative in sorted(relative_paths):
        source = ROOT / Path(relative)
        if source.is_symlink() or not source.is_file():
            raise PackageError(f"required public license material is missing: {relative}")
        target = output / Path(relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)


def make_executable(path: Path) -> None:
    """尽力补上可执行位；Windows 上这是空操作（没有这个概念）。"""
    try:
        current = path.stat().st_mode
        path.chmod(current | 0o111)
    except OSError:
        pass


def write_notice(output: Path) -> None:
    (output / "SIGNING-NOTICE.txt").write_text(SIGNING_NOTICE, encoding="utf-8", newline="\n")


def build_archive(output: Path, archive: Path) -> None:
    """Create a reproducible tar.gz: sorted entries, fixed owner and mtime."""
    if archive.exists():
        archive.unlink()

    entries = sorted(path for path in output.rglob("*") if path.is_file())
    with tarfile.open(archive, "w:gz", format=tarfile.PAX_FORMAT) as bundle:
        for path in entries:
            relative = path.relative_to(output.parent).as_posix()
            info = bundle.gettarinfo(str(path), arcname=relative)
            info.mtime = FIXED_MTIME
            info.uid = 0
            info.gid = 0
            info.uname = ""
            info.gname = ""
            in_tree = path.relative_to(output).as_posix()
            info.mode = FIXED_EXEC_MODE if in_tree in EXECUTABLE_ENTRIES else FIXED_MODE
            with path.open("rb") as stream:
                bundle.addfile(info, stream)


def write_checksums(output: Path, archive: Path) -> None:
    lines = []
    for path in sorted(p for p in output.rglob("*") if p.is_file()):
        lines.append(f"{sha256(path)}  {path.relative_to(output.parent).as_posix()}")
    (output.parent / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")
    (archive.with_name(archive.name + ".sha256")).write_text(
        f"{sha256(archive)}  {archive.name}\n", encoding="ascii", newline="\n"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", default="linux-x64", choices=["linux-x64", "linux-arm64"])
    parser.add_argument("--output", type=Path, default=DIST)
    parser.add_argument("--preflight", action="store_true", help="verify inputs only; write nothing")
    parser.add_argument("--build", action="store_true", help="publish and package")
    args = parser.parse_args(argv)

    if not args.preflight and not args.build:
        parser.error("choose --preflight or --build")

    try:
        preflight(args.rid)
        if args.preflight:
            return 0

        arch = args.rid.removeprefix("linux-")
        output = args.output / f"MyProxy-linux-{arch}"
        publish(output, args.rid)
        lay_out_core(output)
        copy_public_license_materials(output)
        write_notice(output)

        archive = args.output / ARCHIVE_NAME.format(arch=arch)
        build_archive(output, archive)
        write_checksums(output, archive)

        print(f"archive: {archive.relative_to(ROOT)}")
        print(f"sha256:  {sha256(archive)}")
        print(f"install: tar -xzf {archive.name} -C ~/.local/share")
        print(f"         ~/.local/share/MyProxy-linux-{arch}/myproxy status")
        print("desktop: install -Dm644 myproxy-gui.desktop ~/.local/share/applications/myproxy-gui.desktop")
        print("         install -Dm644 myproxy.png ~/.local/share/icons/hicolor/16x16/apps/myproxy.png")
        return 0
    except (PackageError, release_verify.ReleaseVerificationError) as exc:
        print(f"PACKAGING FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
