#!/usr/bin/env python3
"""Build a reproducible MyProxy Server + Windows client delivery archive.

The default command is intentionally conservative: it requires a clean git
worktree, a real .NET SDK, and refuses to overwrite an existing delivery.
``--preflight`` performs checks only; ``--dry-run`` performs the same checks
and prints the planned operations without publishing or creating an archive.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

try:
    from scripts import release_verify
except ImportError:
    import release_verify


ROOT = Path(__file__).resolve().parents[1]
VERSION_SOURCE = ROOT / "windows" / "MyProxy" / "App" / "AppInfo.cs"
SERVER_VERSION_SOURCE = ROOT / "server" / "myproxy_server" / "__init__.py"
PROJECT_SOURCE = ROOT / "windows" / "MyProxy" / "MyProxy.csproj"
CORE_SOURCE = ROOT / "windows" / "MyProxy" / "Assets" / "Core"
DELIVERY = ROOT / "delivery"
VERSION_PATTERN = re.compile(r'(?m)^\s*public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
SERVER_VERSION_PATTERN = re.compile(r'(?m)^\s*__version__\s*=\s*["\']([^"\']+)["\']')
PROJECT_VERSION_PATTERNS = {
    "Version": re.compile(r"(?m)^\s*<Version>\s*([^<]+?)\s*</Version>\s*$"),
    "FileVersion": re.compile(r"(?m)^\s*<FileVersion>\s*([^<]+?)\s*</FileVersion>\s*$"),
    "AssemblyVersion": re.compile(r"(?m)^\s*<AssemblyVersion>\s*([^<]+?)\s*</AssemblyVersion>\s*$"),
    "InformationalVersion": re.compile(r"(?m)^\s*<InformationalVersion>\s*([^<]+?)\s*</InformationalVersion>\s*$"),
}
SEMVER_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")

EXPECTED_PUBLISH_FILES = (
    "MyProxy.exe",
    "MyProxy.dll",
    "MyProxy.deps.json",
    "MyProxy.runtimeconfig.json",
    "Core/xray.exe",
    "Core/LICENSE",
    "Core/geoip.dat",
    "Core/geosite.dat",
)

# These are intentionally path-based. A delivery must never contain local
# runtime state even when a developer created it outside the git index.
IGNORED_DIR_NAMES = {
    ".git", ".vs", ".vscode", "__pycache__", "bin", "obj", "testresults",
    "publish", "data", "logs", "secrets", "dist", "delivery", ".venv", "venv",
    "build", ".gradle", ".idea", ".review-build", ".agents", ".codex", ".aws",
}
IGNORED_FILE_NAMES = {
    ".env", "admin.env", "local.properties", "vps_credentials.txt", "pairing_code.txt",
    "server.crt", "id_ed25519", "id_ed25519.pub", "id_rsa", "id_rsa.pub",
    "id_ecdsa", "id_ecdsa.pub", "id_dsa", "id_dsa.pub",
}
IGNORED_SUFFIXES = (
    ".pyc", ".db", ".db-shm", ".db-wal", ".sqlite", ".sqlite3", ".log",
    ".key", ".pem", ".ppk", ".pfx", ".p12", ".zip", ".apk", ".crt", ".cer",
)
LINUX_TEXT_SUFFIXES = (".sh", ".service", ".conf.template", ".yaml")
SECRET_SCAN_SUFFIXES = {
    ".bat", ".cmd", ".cs", ".conf", ".cfg", ".ini", ".json", ".kt", ".md", ".ps1",
    ".py", ".properties", ".service", ".sh", ".template", ".toml", ".txt", ".xml",
    ".xaml", ".yaml", ".yml",
}
SECRET_CONFIG_SUFFIXES = {".conf", ".cfg", ".ini", ".json", ".properties", ".toml", ".txt", ".yaml", ".yml"}
SENSITIVE_CONFIG_NAME_PATTERN = re.compile(
    r"(?:credential|secret|password|passwd|access[_-]?token|api[_-]?key|private[_-]?key|\.aws)",
    re.IGNORECASE,
)
SECRET_PATTERNS = (
    ("PEM private key", re.compile(r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----")),
    ("AWS access key", re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
    ("GitHub token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{20,}\b")),
    ("Slack token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b")),
    ("credential assignment", re.compile(
        r"\b(?:private[_-]?key|client[_-]?secret|access[_-]?token|api[_-]?key|password|passwd|secret)"
        r"\b[\"']?\s*[:=]\s*[\"']([A-Za-z0-9+/=_\-.]{20,})[\"']", re.IGNORECASE
    )),
)


class PreflightError(RuntimeError):
    """An actionable release-preparation failure."""


def version_from(path: Path, pattern: re.Pattern[str]) -> str:
    text = path.read_text(encoding="utf-8")
    match = pattern.search(text)
    if not match:
        raise PreflightError(f"version declaration not found: {path}")
    version = match.group(1)
    if not SEMVER_PATTERN.fullmatch(version):
        raise PreflightError(f"invalid semver {version!r} in {path}")
    return version


def release_version() -> str:
    """Read the Windows product version and enforce Server parity."""
    windows_version = version_from(VERSION_SOURCE, VERSION_PATTERN)
    server_version = version_from(SERVER_VERSION_SOURCE, SERVER_VERSION_PATTERN)
    if windows_version != server_version:
        raise PreflightError(
            f"version mismatch: Windows={windows_version}, Server={server_version}; "
            f"update {VERSION_SOURCE} and {SERVER_VERSION_SOURCE} together"
        )
    project_text = PROJECT_SOURCE.read_text(encoding="utf-8")
    project_versions = {
        name: pattern.search(project_text).group(1).strip()
        if pattern.search(project_text) else None
        for name, pattern in PROJECT_VERSION_PATTERNS.items()
    }
    missing = [name for name, value in project_versions.items() if value is None]
    if missing:
        raise PreflightError(
            f"{PROJECT_SOURCE} must declare <{missing[0]}> matching AppInfo.Version; "
            "implicit SDK version metadata is not release-safe"
        )
    def metadata_matches(name: str, value: str) -> bool:
        # Assembly/FileVersion commonly use the CLR four-component form.
        return value == windows_version or (
            name in {"AssemblyVersion", "FileVersion"} and value == windows_version + ".0"
        )

    mismatched = {name: value for name, value in project_versions.items()
                  if not metadata_matches(name, value)}
    if mismatched:
        raise PreflightError(
            f"project metadata version mismatch: expected {windows_version}, got {mismatched}"
        )
    return windows_version


def output_paths(version: str) -> tuple[Path, Path, Path]:
    stage = DELIVERY / f"MyProxy-v{version}"
    archive = DELIVERY / f"MyProxy-v{version}.zip"
    sidecar = DELIVERY / f"MyProxy-v{version}.zip.sha256"
    return stage, archive, sidecar


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def core_hashes(manifest: Path) -> dict[str, str]:
    if not manifest.is_file():
        raise PreflightError(f"core asset manifest is missing: {manifest}")
    hashes: dict[str, str] = {}
    for line in manifest.read_text(encoding="utf-8").splitlines():
        if "=" not in line:
            continue
        name, value = line.split("=", 1)
        if name in {"xray.exe", "geoip.dat", "geosite.dat"}:
            if not re.fullmatch(r"[0-9a-f]{64}", value):
                raise PreflightError(f"invalid SHA-256 in {manifest}: {name}")
            hashes[name] = value
    missing = {"xray.exe", "geoip.dat", "geosite.dat"} - hashes.keys()
    if missing:
        raise PreflightError(f"core asset manifest is incomplete: {', '.join(sorted(missing))}")
    return hashes


def validate_core_hashes(core: Path, manifest: Path | None = None,
                         expected: dict[str, str] | None = None) -> None:
    expected = expected or core_hashes(manifest or core / "VERSION.txt")
    for name, digest in expected.items():
        path = core / name
        if not path.is_file():
            raise PreflightError(f"core asset is missing: {path}")
        actual = sha256(path)
        if actual != digest:
            raise PreflightError(f"core asset SHA-256 mismatch for {path}: expected {digest}, got {actual}")


def validate_runtime_config(path: Path) -> None:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PreflightError(f"invalid publish runtimeconfig: {path}: {exc}") from exc
    options = document.get("runtimeOptions", {})
    if not isinstance(options, dict):
        raise PreflightError(f"invalid runtimeOptions.frameworks in {path}")
    frameworks = options.get("frameworks", [])
    if not isinstance(frameworks, list):
        raise PreflightError(f"invalid runtimeOptions.frameworks in {path}")
    framework_versions = {
        item.get("name"): str(item.get("version", ""))
        for item in frameworks if isinstance(item, dict)
    }
    for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"):
        if not re.fullmatch(r"8\.(?:\d+)(?:\.\d+)?", framework_versions.get(name, "")):
            raise PreflightError(f"publish runtimeconfig must target {name} 8.x")
    config_properties = options.get("configProperties", {})
    if not isinstance(config_properties, dict):
        raise PreflightError(f"invalid runtimeOptions.configProperties in {path}")
    unsafe = config_properties.get(
        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"
    )
    if unsafe is not False:
        raise PreflightError("publish runtimeconfig must disable unsafe BinaryFormatter serialization")


def scan_secret_blob(path: Path, data: bytes) -> None:
    """Reject high-confidence committed credentials while avoiding binary noise."""
    if path.suffix.lower() not in SECRET_SCAN_SUFFIXES:
        return
    text = data.decode("utf-8", errors="ignore")
    # PEM/key and vendor token signatures are high-confidence even in docs.
    for label, pattern in SECRET_PATTERNS[:4]:
        if pattern.search(text):
            raise PreflightError(f"possible {label} in tracked release file: {path}")
    if path.suffix.lower() in SECRET_CONFIG_SUFFIXES and SENSITIVE_CONFIG_NAME_PATTERN.search(path.name):
        raise PreflightError(f"sensitive-named config is not allowed in delivery: {path}")
    assignment = SECRET_PATTERNS[4][1].search(text)
    if assignment and not looks_like_placeholder(assignment.group(1)):
        raise PreflightError(f"possible credential assignment in tracked release file: {path}")


def looks_like_placeholder(value: str) -> bool:
    upper = value.upper()
    return any(marker in upper for marker in (
        "TEST", "EXAMPLE", "DUMMY", "PLACEHOLDER", "REPLACE", "CHANGE_ME", "CHANGEME", "DO_NOT", "YOUR_"
    ))


def git_status() -> str:
    safe_root = ROOT.as_posix()
    try:
        result = subprocess.run(
            ["git", "-c", f"safe.directory={safe_root}", "-c", "core.safecrlf=false",
             "status", "--porcelain=v1", "--untracked-files=all"],
            cwd=ROOT, check=True, capture_output=True, text=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        raise PreflightError(f"cannot inspect git worktree: {exc}") from exc
    return result.stdout.strip()


def git_revision() -> str:
    safe_root = ROOT.as_posix()
    try:
        result = subprocess.run(
            ["git", "-c", f"safe.directory={safe_root}", "rev-parse", "HEAD"],
            cwd=ROOT, check=True, capture_output=True, text=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        raise PreflightError(f"cannot determine git revision: {exc}") from exc
    return result.stdout.strip()


def assert_release_snapshot(expected_revision: str) -> None:
    """Abort if the clean commit selected by preflight changed before publish."""
    if expected_revision.endswith("-dirty"):
        raise PreflightError("formal publish cannot use a dirty release snapshot")
    current_status = git_status()
    current_revision = git_revision()
    if current_status or current_revision != expected_revision:
        raise PreflightError(
            "release snapshot changed after preflight; restart from a clean checkout "
            f"(expected {expected_revision}, current {current_revision}, dirty={bool(current_status)})"
        )


def resolve_dotnet(explicit: str | None = None) -> Path:
    required_sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit))
    if os.environ.get("MYPROXY_DOTNET"):
        candidates.append(Path(os.environ["MYPROXY_DOTNET"]))
    candidates.append(ROOT / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet"))
    found = shutil.which("dotnet")
    if found:
        candidates.append(Path(found))

    seen: set[Path] = set()
    for candidate in candidates:
        candidate = candidate if candidate.is_absolute() else (ROOT / candidate)
        candidate = candidate.resolve()
        if candidate in seen or not candidate.exists():
            continue
        seen.add(candidate)
        result = subprocess.run(
            [str(candidate), "--version"], cwd=ROOT,
            capture_output=True, text=True,
        )
        if result.returncode == 0 and result.stdout.strip() == required_sdk:
            return candidate
    raise PreflightError(
        f".NET SDK {required_sdk} from global.json is required; runtime-only dotnet is insufficient. "
        "Install the pinned SDK or set MYPROXY_DOTNET to a matching dotnet executable."
    )


def is_ignored(path: Path) -> bool:
    if any(part.lower() in IGNORED_DIR_NAMES for part in path.parts):
        return True
    name = path.name.lower()
    return (name in IGNORED_FILE_NAMES or name.startswith(".env.") or
            any(name.endswith(suffix) for suffix in IGNORED_SUFFIXES))


def tracked_files(source: Path, revision_ref: str = "HEAD") -> list[Path]:
    """Return only tracked files below source; ignored local state is excluded structurally."""
    try:
        relative_root = source.relative_to(ROOT).as_posix()
    except ValueError as exc:
        raise PreflightError(f"release source must be inside repository: {source}") from exc
    safe_root = ROOT.as_posix()
    try:
        from_worktree = revision_ref == "HEAD"
        command = (["git", "-c", f"safe.directory={safe_root}", "ls-files", "-z", "--", relative_root]
                   if from_worktree else
                   ["git", "-c", f"safe.directory={safe_root}", "ls-tree", "-r", "-z",
                    revision_ref, "--", relative_root])
        result = subprocess.run(
            command,
            cwd=ROOT, check=True, capture_output=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        raise PreflightError(f"cannot enumerate tracked release files under {source}: {exc}") from exc
    files: list[Path] = []
    for raw in result.stdout.split(b"\0"):
        if not raw:
            continue
        if from_worktree:
            relative = raw.decode("utf-8")
            mode = None
        else:
            try:
                metadata, encoded_path = raw.split(b"\t", 1)
                mode, object_type, _object_id = metadata.decode("ascii").split(" ", 2)
                relative = encoded_path.decode("utf-8")
            except (ValueError, UnicodeError) as exc:
                raise PreflightError("cannot parse tracked release tree") from exc
            if mode not in {"100644", "100755"} or object_type != "blob":
                raise PreflightError(f"non-regular file is not allowed in delivery input: {relative}")
        path = ROOT / relative
        if from_worktree:
            if path.is_symlink():
                raise PreflightError(f"symlink is not allowed in delivery input: {path}")
            if not path.is_file():
                raise PreflightError(f"tracked release file is missing from worktree: {path}")
        files.append(path)
    return files


def validate_linux_text_eol(path: Path, data: bytes | None = None) -> None:
    if path.suffix.lower() == ".template":
        is_linux_text = path.name.lower().endswith(".conf.template")
    else:
        is_linux_text = path.suffix.lower() in {".sh", ".service", ".yaml"}
    if is_linux_text and b"\r" in (data if data is not None else path.read_bytes()):
        raise PreflightError(f"Linux deployment file contains CRLF/mixed EOL: {path}; normalize to LF")


def head_blob(path: Path, revision_ref: str = "HEAD") -> bytes:
    relative = path.relative_to(ROOT).as_posix()
    try:
        result = subprocess.run(
            ["git", "-c", f"safe.directory={ROOT.as_posix()}", "show", f"{revision_ref}:{relative}"],
            cwd=ROOT, check=True, capture_output=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        raise PreflightError(f"cannot read tracked release blob {relative}: {exc}") from exc
    return result.stdout


def copy_tree_tracked(source: Path, destination: Path, revision_ref: str = "HEAD") -> None:
    """Copy only tracked files, rejecting symlinks and unsafe Linux EOLs."""
    if not source.is_dir():
        raise PreflightError(f"required source directory is missing: {source}")
    files = tracked_files(source, revision_ref)
    for item in files:
        if item.is_symlink():
            raise PreflightError(f"symlink is not allowed in delivery input: {item}")
        relative = item.relative_to(source)
        if is_ignored(relative):
            continue
        data = head_blob(item, revision_ref)
        validate_linux_text_eol(item, data)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)


def assert_safe_tree(root: Path) -> None:
    unsafe: list[str] = []
    for item in root.rglob("*"):
        relative = item.relative_to(root)
        if item.is_symlink() or is_ignored(relative):
            unsafe.append(relative.as_posix())
    if unsafe:
        raise PreflightError("forbidden files in delivery: " + ", ".join(sorted(unsafe)))


def validate_source_assets(revision_ref: str = "HEAD") -> None:
    required = [
        ROOT / "windows" / "MyProxy" / "MyProxy.csproj",
        ROOT / "server" / "myproxy_server" / "__init__.py",
        ROOT / "deployment.json",
        ROOT / "windows" / "MyProxy" / "Assets" / "Core" / "xray.exe",
        ROOT / "windows" / "MyProxy" / "Assets" / "Core" / "geoip.dat",
        ROOT / "windows" / "MyProxy" / "Assets" / "Core" / "geosite.dat",
    ]
    for path in required:
        if not path.is_file():
            raise PreflightError(f"required release input is missing: {path}")
    validate_core_hashes(CORE_SOURCE)
    try:
        release_verify.deployment_origin(json.loads(head_blob(ROOT / "deployment.json", revision_ref)), require_configured=True)
    except (json.JSONDecodeError, release_verify.ReleaseVerificationError) as exc:
        raise PreflightError(f"invalid committed deployment.json: {exc}") from exc
    # The archive is sourced from HEAD blobs, so enforce LF on Linux deploy
    # inputs before any publish work starts.
    for source in (ROOT / "windows" / "MyProxy", ROOT / "server", ROOT / "scripts"):
        for path in tracked_files(source, revision_ref):
            data = head_blob(path, revision_ref)
            validate_linux_text_eol(path, data)
            scan_secret_blob(path, data)
    for path in release_doc_sources():
        data = head_blob(path, revision_ref)
        validate_linux_text_eol(path, data)
        scan_secret_blob(path, data)


def ensure_outputs_available(stage: Path, archive: Path, sidecar: Path) -> None:
    for target in (stage, archive, sidecar):
        if target.exists():
            raise PreflightError(
                f"delivery output already exists: {target}; remove existing output manually before retrying"
            )


def signing_status(path: Path) -> str:
    """Report signing without ever implying that this script signs binaries."""
    if not path.is_file():
        return "NOT_CHECKED (no publish executable yet)"
    certificate_present = authenticode_certificate_present(path)
    if certificate_present is False:
        return "NotSigned (manual acceptance decision required; package script does not sign)"
    if certificate_present is None:
        return "NOT_CHECKED (not a PE file; manual review required)"
    powershell = shutil.which("powershell") or shutil.which("pwsh")
    if not powershell:
        return "Signed-present (PowerShell unavailable; certificate identity/expiry require manual review)"
    escaped_path = str(path).replace("'", "''")
    command = f"(Get-AuthenticodeSignature -LiteralPath '{escaped_path}').Status"
    result = subprocess.run(
        [powershell, "-NoProfile", "-NonInteractive", "-Command", command],
        cwd=ROOT, capture_output=True, text=True,
    )
    status = result.stdout.strip() if result.returncode == 0 else "UNKNOWN"
    if status != "Valid":
        if not status:
            return "Signed-present (Authenticode status unavailable; manual review required)"
        return f"{status or 'UNKNOWN'} (manual decision required; package script does not sign)"
    return "Valid (certificate identity/expiry still require manual review)"


def authenticode_certificate_present(path: Path) -> bool | None:
    """Read the PE security-directory bit without trusting a certificate."""
    try:
        data = path.read_bytes()
        if len(data) < 0x40 or data[:2] != b"MZ":
            return None
        pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
        if pe_offset + 4 + 20 + 2 > len(data) or data[pe_offset:pe_offset + 4] != b"PE\x00\x00":
            return None
        optional = pe_offset + 4 + 20
        magic = struct.unpack_from("<H", data, optional)[0]
        directory = optional + (96 if magic == 0x10B else 112 if magic == 0x20B else 0)
        if not directory or directory + 8 * 5 > len(data):
            return None
        _file_offset, size = struct.unpack_from("<II", data, directory + 8 * 4)
        return bool(size)
    except (OSError, struct.error):
        return None


def preflight(*, allow_dirty: bool, dotnet: str | None, require_sdk: bool) -> tuple[str, str, Path, Path, Path]:
    version = release_version()
    try:
        release_verify.verify_deployment_config(require_configured=True)
    except release_verify.ReleaseVerificationError as exc:
        raise PreflightError(str(exc)) from exc
    try:
        release_verify.verify_public_license_materials()
    except release_verify.ReleaseVerificationError as exc:
        raise PreflightError(str(exc)) from exc
    revision_ref = git_revision()
    status = git_status()
    if status and not allow_dirty:
        first = status.splitlines()[0]
        raise PreflightError(
            "worktree is dirty; commit the release inputs first "
            f"(first change: {first!r}), or pass --allow-dirty for read-only local checks"
        )
    validate_source_assets(revision_ref)
    if not status:
        assert_release_snapshot(revision_ref)
    dotnet_path = resolve_dotnet(dotnet) if require_sdk else Path(dotnet or "dotnet")
    revision = revision_ref + ("-dirty" if status else "")
    stage, archive, sidecar = output_paths(version)
    ensure_outputs_available(stage, archive, sidecar)
    return version, revision, dotnet_path, stage, archive


def run(cmd: list[str], *, cwd: Path = ROOT) -> None:
    print("+", " ".join(str(part) for part in cmd), flush=True)
    subprocess.run(cmd, cwd=cwd, check=True)


def write_checksums(stage: Path) -> None:
    lines: list[str] = []
    for path in sorted(stage.rglob("*")):
        if path.is_file() and path.name != "SHA256SUMS.txt":
            lines.append(f"{sha256(path)}  {path.relative_to(stage).as_posix()}")
    (stage / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def create_reproducible_zip(stage: Path, archive: Path) -> None:
    """Write sorted entries with fixed metadata so the same stage hashes identically."""
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED,
                         compresslevel=9, strict_timestamps=False) as output:
        for path in sorted(stage.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(stage).as_posix()
            info = zipfile.ZipInfo(f"{stage.name}/{relative}", date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            output.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def release_doc_sources() -> list[Path]:
    docs = [
        ROOT / "README.md",
        ROOT / "docs" / "architecture.md",
        ROOT / "docs" / "deployment.md",
        ROOT / "docs" / "third-party.md",
        ROOT / "server" / "README.md",
        ROOT / "server" / "docs" / "openapi.yaml",
    ]
    docs.extend(ROOT / Path(relative) for relative in release_verify.PUBLIC_RELEASE_MATERIALS)
    # The Android core build also redistributes the Go dependency license
    # texts. Keep every file under licenses/ in the public desktop archive.
    license_root = ROOT / "licenses"
    if license_root.is_dir():
        docs.extend(path for path in license_root.rglob("*") if path.is_file())
    return list(dict.fromkeys(docs))


def copy_release_docs(stage: Path, revision_ref: str = "HEAD") -> None:
    for source in release_doc_sources():
        target = stage / source.relative_to(ROOT)
        target.parent.mkdir(parents=True, exist_ok=True)
        data = head_blob(source, revision_ref)
        validate_linux_text_eol(source, data)
        scan_secret_blob(source, data)
        target.write_bytes(data)


def copy_deployment_config(destination: Path, revision_ref: str = "HEAD") -> None:
    """Copy the same validated config into build and standalone Server trees."""
    data = head_blob(ROOT / "deployment.json", revision_ref)
    try:
        release_verify.deployment_origin(json.loads(data), require_configured=True)
    except (json.JSONDecodeError, release_verify.ReleaseVerificationError) as exc:
        raise PreflightError(f"invalid committed deployment.json: {exc}") from exc
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)


def build(version: str, revision: str, dotnet: Path, stage: Path, archive: Path, sidecar: Path) -> None:
    DELIVERY.mkdir(parents=True, exist_ok=True)
    temp_root = Path(tempfile.mkdtemp(prefix=f".MyProxy-v{version}-", dir=DELIVERY))
    temp_stage = temp_root / stage.name
    temp_archive = temp_root / archive.name
    temp_sidecar = temp_root / sidecar.name
    try:
        temp_stage.mkdir(parents=True)
        build_source = temp_root / "build-source" / "windows" / "MyProxy"
        assert_release_snapshot(revision)
        copy_tree_tracked(ROOT / "windows" / "MyProxy", temp_stage / "source" / "windows" / "MyProxy", revision)
        copy_tree_tracked(ROOT / "server", temp_stage / "server", revision)
        copy_tree_tracked(ROOT / "scripts", temp_stage / "scripts", revision)
        copy_release_docs(temp_stage, revision)
        copy_tree_tracked(ROOT / "windows" / "MyProxy", build_source, revision)
        copy_deployment_config(temp_stage / "source" / "deployment.json", revision)
        copy_deployment_config(temp_stage / "deployment.json", revision)
        copy_deployment_config(temp_stage / "server" / "deployment.json", revision)
        copy_deployment_config(build_source.parents[1] / "deployment.json", revision)
        assert_release_snapshot(revision)
        publish = temp_stage / "windows-x64"
        run([str(dotnet), "publish", str(build_source / "MyProxy.csproj"), "-c", "Release",
             "-r", "win-x64", "--self-contained", "false",
             "-p:PublishSingleFile=false", "-o", str(publish)])
        assert_release_snapshot(revision)
        for relative in EXPECTED_PUBLISH_FILES:
            if not (publish / relative).is_file():
                raise PreflightError(f"publish output is incomplete; missing {relative}")
        validate_runtime_config(publish / "MyProxy.runtimeconfig.json")
        source_hashes = core_hashes(temp_stage / "source" / "windows" / "MyProxy" / "Assets" / "Core" / "VERSION.txt")
        validate_core_hashes(publish / "Core", expected=source_hashes)
        tui_signing = signing_status(publish / "MyProxy.exe")
        core_signing = signing_status(publish / "Core" / "xray.exe")
        print(f"SIGNING_STATUS=MyProxy.exe {tui_signing}")
        print(f"SIGNING_STATUS=Core/xray.exe {core_signing}")

        (temp_stage / "DELIVERY-README.txt").write_text(
            f"MyProxy v{version} delivery\n"
            f"source revision: {revision}\n"
            "windows-x64/  -> framework-dependent win-x64 Windows client publish output (entry: MyProxy.exe)\n"
            "Runtime       -> target machine requires .NET 8 Desktop Runtime x64\n"
            "source/       -> Windows client source (runtime/build state excluded)\n"
            "server/       -> Control Plane API source, tests and deploy scripts\n"
            "scripts/      -> release preflight, API test and packaging helpers\n"
            f"Authenticode MyProxy.exe: {tui_signing}\n"
            f"Authenticode Core/xray.exe: {core_signing}\n"
            f"Release eligible: {'no (dirty source)' if revision.endswith('-dirty') else 'manual approval required'}\n"
            "No credentials, private keys, local databases or runtime state are included.\n",
            encoding="utf-8", newline="\n",
        )
        assert_safe_tree(temp_stage)
        write_checksums(temp_stage)
        create_reproducible_zip(temp_stage, temp_archive)
        temp_sidecar.write_text(f"{sha256(temp_archive)}  {archive.name}\n", encoding="ascii", newline="\n")

        committed: list[Path] = []
        ensure_outputs_available(stage, archive, sidecar)
        stage.parent.mkdir(parents=True, exist_ok=True)
        try:
            temp_stage.rename(stage)
            committed.append(stage)
            temp_archive.rename(archive)
            committed.append(archive)
            temp_sidecar.rename(sidecar)
            committed.append(sidecar)
        except Exception:
            for target in committed:
                if target.is_dir():
                    shutil.rmtree(target, ignore_errors=True)
                else:
                    target.unlink(missing_ok=True)
            raise
        print("PACKAGE_OK", archive, archive.stat().st_size)
        print("SHA256", sha256(archive))
    finally:
        # Existing delivery outputs are untouched on all failures before the
        # final rename; only this invocation's staging directory is removed.
        shutil.rmtree(temp_root, ignore_errors=True)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true", help="validate release inputs only")
    parser.add_argument("--dry-run", action="store_true", help="preflight and print planned work only")
    parser.add_argument("--allow-dirty", action="store_true", help="allow dirty worktree for --preflight/--dry-run only")
    parser.add_argument("--dotnet", help="dotnet executable (otherwise MYPROXY_DOTNET, repo SDK, PATH)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        if args.allow_dirty and not (args.preflight or args.dry_run):
            raise PreflightError("--allow-dirty is restricted to --preflight/--dry-run; formal packages require a clean worktree")
        version, revision, dotnet, stage, archive = preflight(
            allow_dirty=args.allow_dirty,
            dotnet=args.dotnet,
            require_sdk=True,
        )
        sidecar = output_paths(version)[2]
        print(f"PREFLIGHT_OK version={version} revision={revision} dotnet={dotnet}")
        release_bin = ROOT / "windows" / "MyProxy" / "bin" / "Release" / "net8.0-windows"
        print(f"EXISTING_BUILD_SIGNING_STATUS=MyProxy.exe {signing_status(release_bin / 'MyProxy.exe')} (non-authoritative; existing build may be stale)")
        print(f"EXISTING_BUILD_SIGNING_STATUS=Core/xray.exe {signing_status(release_bin / 'Core' / 'xray.exe')} (non-authoritative; existing build may be stale)")
        if args.preflight or args.dry_run:
            print(f"PLAN publish -> {stage / 'windows-x64'}")
            print(f"PLAN archive -> {archive}")
            print("PLAN no final output created")
            return 0
        build(version, revision, dotnet, stage, archive, sidecar)
        return 0
    except (PreflightError, OSError, subprocess.CalledProcessError) as exc:
        print(f"PREFLIGHT_FAILED: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
