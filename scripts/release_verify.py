#!/usr/bin/env python3
"""Fail-closed checks used by MyProxy CI and formal releases.

This module intentionally has no third-party dependencies.  It validates the
release inputs which are shared by the Windows, Android and Server artifacts:
version parity, tag naming, API routes, shared deployment configuration,
the bundled Xray assets, and the vendored Android AAR.  It also provides small
deterministic helpers for release checksums and a CycloneDX SBOM.

The script never signs or publishes artifacts. CI accepts the example endpoint;
production packagers require an explicitly configured HTTPS origin.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import uuid
import zipfile
from pathlib import Path
from urllib.parse import urlsplit
import ipaddress


ROOT = Path(__file__).resolve().parents[1]
WINDOWS_APP_INFO = ROOT / "windows" / "MyProxy" / "App" / "AppInfo.cs"
WINDOWS_PROJECT = ROOT / "windows" / "MyProxy" / "MyProxy.csproj"
WINDOWS_MANIFEST = ROOT / "windows" / "MyProxy" / "app.manifest"
DEPLOYMENT_CONFIG = ROOT / "deployment.json"
ANDROID_GRADLE = ROOT / "android" / "MyProxyAndroid" / "app" / "build.gradle.kts"
ANDROID_API_CONFIG = (
    ROOT
    / "android"
    / "MyProxyAndroid"
    / "app"
    / "src"
    / "main"
    / "java"
    / "com"
    / "myproxy"
    / "android"
    / "data"
    / "api"
    / "ApiConfig.kt"
)
ANDROID_API_CLIENT = ANDROID_API_CONFIG.with_name("MyProxyApiClient.kt")
ANDROID_AAR = ROOT / "android" / "MyProxyAndroid" / "vendor" / "libv2ray" / "libv2ray.aar"
ANDROID_SOURCES = ANDROID_AAR.with_name("libv2ray-sources.jar")
ANDROID_VENDOR_MANIFEST = ANDROID_AAR.with_name("VERSION.txt")
ANDROID_WRAPPER_PROPERTIES = ROOT / "android" / "MyProxyAndroid" / "gradle" / "wrapper" / "gradle-wrapper.properties"
ANDROID_WRAPPER_JAR = ROOT / "android" / "MyProxyAndroid" / "gradle" / "wrapper" / "gradle-wrapper.jar"
SERVER_VERSION = ROOT / "server" / "myproxy_server" / "__init__.py"
OPENAPI = ROOT / "server" / "docs" / "openapi.yaml"
WINDOWS_CORE = ROOT / "windows" / "MyProxy" / "Assets" / "Core"
ANDROID_ASSETS = ROOT / "android" / "MyProxyAndroid" / "app" / "src" / "main" / "assets"
PUBLIC_RELEASE_MATERIALS = (
    "LICENSE",
    "THIRD_PARTY_NOTICES.md",
    "licenses/LGPL-3.0.txt",
    "licenses/GPL-3.0.txt",
    "licenses/Go-BSD-3-Clause.txt",
    "licenses/MPL-2.0.txt",
    "licenses/Apache-2.0.txt",
    "licenses/SOURCES.json",
    "licenses/go-modules/INDEX.json",
    "licenses/CORE-BUILD-INFO.json",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "CHANGELOG.md",
    "docs/maintaining.md",
    "docs/android-core-rebuild.md",
    "android/MyProxyAndroid/vendor/libv2ray/SOURCE.md",
    "android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/SOURCES.json",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/README.md",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/LICENSE",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_utils.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_main.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_certSha256.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_android.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/go.sum",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/go.mod",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/gen_assets.sh",
)
PUBLIC_LICENSE_TEXTS = {
    "LICENSE": "MIT License",
    "licenses/LGPL-3.0.txt": "GNU LESSER GENERAL PUBLIC LICENSE",
    "licenses/GPL-3.0.txt": "GNU GENERAL PUBLIC LICENSE",
    "licenses/Go-BSD-3-Clause.txt": "Redistribution and use in source and binary forms",
    "licenses/MPL-2.0.txt": "Mozilla Public License Version 2.0",
    "licenses/Apache-2.0.txt": "Apache License",
}
LICENSE_SOURCE_MANIFEST = "licenses/SOURCES.json"
GO_LICENSE_INDEX = "licenses/go-modules/INDEX.json"
ANDROID_BUILD_INFO = "android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json"
CORE_BUILD_INFO = "licenses/CORE-BUILD-INFO.json"
ANDROID_UPSTREAM_ROOT = "android/MyProxyAndroid/vendor/libv2ray/upstream"
ANDROID_UPSTREAM_SOURCE_MANIFEST = f"{ANDROID_UPSTREAM_ROOT}/SOURCES.json"
LINUX_ROOT = ROOT / "linux" / "MyProxyLinux"
LINUX_APP_INFO = LINUX_ROOT / "core" / "App" / "AppInfo.cs"
LINUX_PROJECT = LINUX_ROOT / "core" / "MyProxy.Linux.Core.csproj"
LINUX_CLI_PROJECT = LINUX_ROOT / "cli" / "MyProxy.Linux.Cli.csproj"
LINUX_GUI_PROJECT = LINUX_ROOT / "gui" / "MyProxy.Linux.Gui.csproj"
LINUX_CORE_MANIFEST = LINUX_ROOT / "assets" / "VERSION.txt"
# The Linux client compiles the shared Windows sources instead of copying them
# (see LINUX_SHARED_SOURCES).  These are the ones that carry the contract: the
# routes it calls, the routing rules whose geo tags must exist, and the client
# state machine whose transitions are a spec.  Linking them is what makes
# "Linux cannot drift from Windows" a structural fact rather than a promise.
LINUX_SHARED_SOURCES = (
    ("Core", "XrayConfigGenerator.cs"),
    ("Core", "ServiceCategories.cs"),
    ("Core", "ConnectionStateMachine.cs"),
    ("Core", "ReleaseManifest.cs"),
    ("Core", "Ed25519.cs"),
    ("Core", "FeatureFlags.cs"),
    ("Services", "BindingService.cs"),
    ("Services", "ConfigService.cs"),
    ("Services", "UpdateService.cs"),
    ("Services", "UsageReporter.cs"),
    ("Services", "ConnectionController.cs"),
    ("Services", "HttpClientFactory.cs"),
    ("Services", "TlsFailure.cs"),
    ("Core", "DeploymentConfiguration.cs"),
    ("App", "ReleaseSigningKeys.cs"),
)
# Every source file that may name a geoip:/geosite: tag in a routing rule.
# The shipped databases are trimmed, so a tag named here but absent from the
# .dat makes Xray refuse to load the config; see verify_geo_tags.
# The Linux client has no entry here on purpose: it compiles these very files.
GEO_TAG_SOURCES = (
    ROOT / "windows" / "MyProxy" / "Core" / "XrayConfigGenerator.cs",
    ROOT / "windows" / "MyProxy" / "Core" / "ServiceCategories.cs",
    ROOT / "android" / "MyProxyAndroid" / "app" / "src" / "main" / "java" / "com"
    / "myproxy" / "android" / "xray" / "XrayConfigGenerator.kt",
    ROOT / "android" / "MyProxyAndroid" / "app" / "src" / "main" / "java" / "com"
    / "myproxy" / "android" / "domain" / "observability" / "ServiceCategories.kt",
)
# Only quoted literals count.  The routing code writes the tags as string
# literals; the prose around them mentions the same names unquoted, and a
# comment must not be able to fail the build or to widen what gets shipped.
GEO_TAG_LITERAL = re.compile(r'"(geoip|geosite):([^"]+)"')

SEMVER = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$"
)
HEX64 = re.compile(r"^[0-9a-f]{64}$")
REQUIRED_ROUTES = (
    "/healthz",
    "/api/device/claim",
    "/api/device/config",
    "/api/device/heartbeat",
    "/client/windows/latest.json",
    "/client/android/latest.json",
    "/client/linux/latest.json",
)
# Update Plane and Observability Plane routes.  These are in WINDOWS_ROUTES
# and ANDROID_ROUTES below only because both clients now call them: that list
# asserts what the clients actually implement, so putting a route there before
# the code exists would make the gate pass on a promise instead of on code.
UPDATE_PLANE_ROUTES = (
    "/api/device/update",
    "/api/device/update/report",
)
OBSERVABILITY_ROUTES = ("/api/device/usage",)
SERVER_ROUTES = (
    "/healthz",
    "/readyz",
    "/connectivity-check",
    *REQUIRED_ROUTES[1:],
    *UPDATE_PLANE_ROUTES,
    *OBSERVABILITY_ROUTES,
)
CLIENT_ROUTES = (
    "/api/device/claim",
    "/api/device/config",
    "/api/device/heartbeat",
    "/client/windows/latest.json",
    "/client/android/latest.json",
    "/client/linux/latest.json",
)
WINDOWS_ROUTES = (
    "/api/device/claim",
    "/api/device/config",
    "/api/device/heartbeat",
    "/client/windows/latest.json",
    *UPDATE_PLANE_ROUTES,
    *OBSERVABILITY_ROUTES,
)
ANDROID_ROUTES = (
    "/api/device/claim",
    "/api/device/config",
    "/api/device/heartbeat",
    "/client/android/latest.json",
    *UPDATE_PLANE_ROUTES,
    *OBSERVABILITY_ROUTES,
)
# The Linux client calls these routes from the *shared* services it compiles
# (BindingService / ConfigService / UpdateService / UsageReporter), so the list
# is asserted against those files rather than against a Linux-only API client.
LINUX_ROUTES = (
    "/api/device/claim",
    "/api/device/config",
    "/api/device/heartbeat",
    "/client/linux/latest.json",
    *UPDATE_PLANE_ROUTES,
    *OBSERVABILITY_ROUTES,
)
REQUIRED_AAR_ENTRIES = (
    "jni/arm64-v8a/libgojni.so",
    "assets/geoip.dat",
    "assets/geosite.dat",
)
WINDOWS_RUNTIME_PACKAGES = {
    # MyProxy.deps.json prefixes framework runtime-pack keys with
    # ``runtimepack.``, while the actual NuGet package IDs (and therefore
    # CycloneDX purls) do not contain that internal prefix.
    "runtimepack.Microsoft.NETCore.App.Runtime.win-x64": "Microsoft.NETCore.App.Runtime.win-x64",
    "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64": "Microsoft.WindowsDesktop.App.Runtime.win-x64",
}
EXPECTED_GRADLE_DISTRIBUTION = "gradle-9.8.0-bin.zip"
EXPECTED_GRADLE_DISTRIBUTION_SHA256 = "bafd5ce9cfaea0fbccfdc8439a1ac42fbd4cd9c89dc9a988228d8a2639a58e6c"
EXPECTED_GRADLE_WRAPPER_JAR_SHA256 = "238e777fcddd7e34f9708186085def2abd6e08e658505b38718d79d74c21abd5"


class ReleaseVerificationError(RuntimeError):
    """An actionable release policy failure."""


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except OSError as exc:
        raise ReleaseVerificationError(f"required release input is unreadable: {path}") from exc


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1 << 20), b""):
                digest.update(chunk)
    except OSError as exc:
        raise ReleaseVerificationError(f"cannot hash release input: {path}") from exc
    return digest.hexdigest()


def verify_public_license_materials(root: Path = ROOT) -> None:
    """Require substantive license texts and their provenance in every release input tree."""
    material_bytes: dict[str, bytes] = {}
    for relative in PUBLIC_RELEASE_MATERIALS:
        path = root / Path(relative)
        try:
            data = path.read_bytes()
        except OSError as exc:
            raise ReleaseVerificationError(
                f"required public license material is missing or unreadable: {relative}"
            ) from exc
        if not data.strip():
            raise ReleaseVerificationError(f"required public license material is empty: {relative}")
        material_bytes[relative] = data

    # Reject files that contain only a title/header where the full license text
    # is required. The distinct body markers also catch a swapped license file.
    for relative, marker in PUBLIC_LICENSE_TEXTS.items():
        try:
            text = material_bytes[relative].decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ReleaseVerificationError(f"license text is not UTF-8: {relative}") from exc
        if len(text.strip()) < 512 or marker not in text:
            raise ReleaseVerificationError(
                f"public license file does not contain a substantive {marker!r} text: {relative}"
            )

    try:
        source_manifest = json.loads(material_bytes[LICENSE_SOURCE_MANIFEST].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReleaseVerificationError("licenses/SOURCES.json is not valid UTF-8 JSON") from exc
    entries = source_manifest.get("licenses") if isinstance(source_manifest, dict) else None
    if not isinstance(entries, list):
        raise ReleaseVerificationError("licenses/SOURCES.json must contain a licenses array")

    expected_files = {Path(path).name for path in PUBLIC_LICENSE_TEXTS if path.startswith("licenses/")}
    sources: dict[str, dict[str, object]] = {}
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("file"), str):
            raise ReleaseVerificationError("licenses/SOURCES.json contains an invalid license entry")
        name = entry["file"]
        if name in sources:
            raise ReleaseVerificationError(f"licenses/SOURCES.json repeats {name}")
        sources[name] = entry
    if sources.keys() != expected_files:
        missing = sorted(expected_files - sources.keys())
        unexpected = sorted(sources.keys() - expected_files)
        raise ReleaseVerificationError(
            f"licenses/SOURCES.json license list mismatch; missing={missing}, unexpected={unexpected}"
        )
    for name, entry in sources.items():
        source = entry.get("source")
        digest = entry.get("sha256")
        relative = f"licenses/{name}"
        if not isinstance(source, str) or not source.startswith("https://"):
            raise ReleaseVerificationError(f"licenses/SOURCES.json has no HTTPS source for {name}")
        if not isinstance(digest, str) or not HEX64.fullmatch(digest.lower()):
            raise ReleaseVerificationError(f"licenses/SOURCES.json has an invalid SHA-256 for {name}")
        actual = hashlib.sha256(material_bytes[relative]).hexdigest()
        if digest.lower() != actual:
            raise ReleaseVerificationError(
                f"licenses/SOURCES.json SHA-256 mismatch for {name}: expected {digest}, got {actual}"
            )

    try:
        upstream_sources = json.loads(material_bytes[ANDROID_UPSTREAM_SOURCE_MANIFEST].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReleaseVerificationError(
            "Android libv2ray upstream SOURCES.json is not valid UTF-8 JSON"
        ) from exc
    upstream_files = upstream_sources.get("files") if isinstance(upstream_sources, dict) else None
    archive_url = upstream_sources.get("source_archive") if isinstance(upstream_sources, dict) else None
    commit = upstream_sources.get("commit") if isinstance(upstream_sources, dict) else None
    if (not isinstance(upstream_files, list) or not isinstance(archive_url, str) or
            not archive_url.startswith("https://") or not isinstance(commit, str) or
            not re.fullmatch(r"[0-9a-fA-F]{40}", commit)):
        raise ReleaseVerificationError("Android libv2ray upstream SOURCES.json is incomplete")
    upstream_prefix = Path(ANDROID_UPSTREAM_ROOT)
    expected_upstream_files = {
        Path(relative).relative_to(upstream_prefix).as_posix()
        for relative in PUBLIC_RELEASE_MATERIALS
        if Path(relative).is_relative_to(upstream_prefix) and relative != ANDROID_UPSTREAM_SOURCE_MANIFEST
    }
    recorded_upstream_files: set[str] = set()
    for entry in upstream_files:
        if not isinstance(entry, dict):
            raise ReleaseVerificationError("Android libv2ray upstream SOURCES.json has an invalid file entry")
        name = entry.get("file")
        source = entry.get("source")
        expected_digest = entry.get("sha256")
        if (not isinstance(name, str) or not isinstance(source, str) or not source.startswith("https://") or
                not isinstance(expected_digest, str) or not HEX64.fullmatch(expected_digest.lower())):
            raise ReleaseVerificationError("Android libv2ray upstream SOURCES.json has incomplete file metadata")
        relative_path = Path(name)
        if relative_path.is_absolute() or ".." in relative_path.parts:
            raise ReleaseVerificationError(f"Android libv2ray upstream SOURCES.json has an unsafe path: {name}")
        normalized = relative_path.as_posix()
        if normalized in recorded_upstream_files:
            raise ReleaseVerificationError(f"Android libv2ray upstream SOURCES.json repeats {normalized}")
        recorded_upstream_files.add(normalized)
        path = root / upstream_prefix / relative_path
        try:
            file_data = path.read_bytes()
        except OSError as exc:
            raise ReleaseVerificationError(f"Android libv2ray upstream source is missing: {normalized}") from exc
        if hashlib.sha256(file_data).hexdigest() != expected_digest.lower():
            raise ReleaseVerificationError(f"Android libv2ray upstream source SHA-256 mismatch: {normalized}")
    if recorded_upstream_files != expected_upstream_files:
        missing = sorted(expected_upstream_files - recorded_upstream_files)
        unexpected = sorted(recorded_upstream_files - expected_upstream_files)
        raise ReleaseVerificationError(
            f"Android libv2ray upstream source inventory mismatch; missing={missing}, unexpected={unexpected}"
        )

    try:
        go_index = json.loads(material_bytes[GO_LICENSE_INDEX].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReleaseVerificationError("licenses/go-modules/INDEX.json is not valid UTF-8 JSON") from exc
    modules = go_index.get("modules") if isinstance(go_index, dict) else None
    skipped = go_index.get("skipped") if isinstance(go_index, dict) else None
    failures = go_index.get("failures") if isinstance(go_index, dict) else None
    if (not isinstance(modules, list) or not isinstance(skipped, list) or
            not isinstance(failures, list) or failures):
        raise ReleaseVerificationError(
            "licenses/go-modules/INDEX.json must contain modules/skipped arrays and an empty failures array"
        )

    indexed_license_files: set[str] = set()
    indexed_modules: set[tuple[str, str]] = set()
    for module in modules:
        if not isinstance(module, dict):
            raise ReleaseVerificationError("licenses/go-modules/INDEX.json contains an invalid module entry")
        module_path = module.get("path")
        module_version = module.get("version")
        source_zip = module.get("source_zip")
        zip_digest = module.get("zip_sha256")
        module_licenses = module.get("licenses")
        if (not isinstance(module_path, str) or not module_path or
                not isinstance(module_version, str) or not module_version or
                (module_path, module_version) in indexed_modules or
                not isinstance(source_zip, str) or not source_zip.startswith("https://") or
                not isinstance(zip_digest, str) or not HEX64.fullmatch(zip_digest.lower()) or
                not isinstance(module_licenses, list) or not module_licenses):
            raise ReleaseVerificationError("licenses/go-modules/INDEX.json contains incomplete module provenance")
        indexed_modules.add((module_path, module_version))
        for license_entry in module_licenses:
            if not isinstance(license_entry, dict):
                raise ReleaseVerificationError("licenses/go-modules/INDEX.json contains an invalid license entry")
            relative_name = license_entry.get("file")
            expected_digest = license_entry.get("sha256")
            if not isinstance(relative_name, str) or not isinstance(expected_digest, str) or not HEX64.fullmatch(expected_digest.lower()):
                raise ReleaseVerificationError("licenses/go-modules/INDEX.json contains incomplete license metadata")
            relative_path = Path(relative_name)
            if relative_path.is_absolute() or ".." in relative_path.parts:
                raise ReleaseVerificationError(f"licenses/go-modules/INDEX.json has an unsafe path: {relative_name}")
            normalized = relative_path.as_posix()
            if normalized in indexed_license_files:
                raise ReleaseVerificationError(f"licenses/go-modules/INDEX.json repeats {normalized}")
            indexed_license_files.add(normalized)
            path = root / "licenses" / "go-modules" / relative_path
            try:
                license_data = path.read_bytes()
            except OSError as exc:
                raise ReleaseVerificationError(f"indexed Go module license is missing: {normalized}") from exc
            if not license_data.strip():
                raise ReleaseVerificationError(f"indexed Go module license is empty: {normalized}")
            actual_digest = hashlib.sha256(license_data).hexdigest()
            if expected_digest.lower() != actual_digest:
                raise ReleaseVerificationError(
                    f"indexed Go module license SHA-256 mismatch for {normalized}: "
                    f"expected {expected_digest}, got {actual_digest}"
                )

    go_license_root = root / "licenses" / "go-modules"
    present_license_files = {
        path.relative_to(go_license_root).as_posix()
        for path in go_license_root.rglob("*")
        if path.is_file() and path.name != "INDEX.json"
    }
    if present_license_files != indexed_license_files:
        missing = sorted(indexed_license_files - present_license_files)
        unindexed = sorted(present_license_files - indexed_license_files)
        raise ReleaseVerificationError(
            f"Go module license inventory mismatch; missing={missing}, unindexed={unindexed}"
        )

    try:
        build_info = json.loads(material_bytes[ANDROID_BUILD_INFO].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReleaseVerificationError("Android libv2ray BUILD-INFO.json is not valid UTF-8 JSON") from exc
    build_modules = build_info.get("modules") if isinstance(build_info, dict) else None
    go_version = build_info.get("go_version") if isinstance(build_info, dict) else None
    if not isinstance(go_version, str) or not go_version or not isinstance(build_modules, list):
        raise ReleaseVerificationError("Android libv2ray BUILD-INFO.json is incomplete")

    try:
        core_build_info = json.loads(material_bytes[CORE_BUILD_INFO].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReleaseVerificationError("licenses/CORE-BUILD-INFO.json is not valid UTF-8 JSON") from exc
    components = core_build_info.get("components") if isinstance(core_build_info, dict) else None
    if not isinstance(components, list) or not components:
        raise ReleaseVerificationError("licenses/CORE-BUILD-INFO.json must contain components")

    versioned_sources: set[tuple[str, str]] = set()
    development_sources: set[tuple[str, str]] = set()

    def collect_module_records(entries: object, label: str) -> None:
        if not isinstance(entries, list):
            raise ReleaseVerificationError(f"{label} has no modules array")
        for entry in entries:
            if not isinstance(entry, dict):
                raise ReleaseVerificationError(f"{label} has an invalid module entry")
            path = entry.get("path")
            version = entry.get("version")
            if not isinstance(path, str) or not path or not isinstance(version, str) or not version:
                raise ReleaseVerificationError(f"{label} has an incomplete module entry")
            record = (path, version)
            if version == "(devel)" or version == "v0.0.0-00010101000000-000000000000":
                development_sources.add(record)
            else:
                versioned_sources.add(record)

    collect_module_records(build_modules, "Android libv2ray BUILD-INFO.json")
    for component in components:
        if not isinstance(component, dict) or not isinstance(component.get("file"), str):
            raise ReleaseVerificationError("licenses/CORE-BUILD-INFO.json has an invalid component")
        collect_module_records(
            component.get("modules"),
            f"licenses/CORE-BUILD-INFO.json component {component['file']}",
        )

    skipped_records: set[tuple[str, str]] = set()
    for entry in skipped:
        if not isinstance(entry, dict):
            raise ReleaseVerificationError("licenses/go-modules/INDEX.json has an invalid skipped entry")
        path = entry.get("path")
        version = entry.get("version")
        reason = entry.get("reason")
        if (not isinstance(path, str) or not path or not isinstance(version, str) or not version or
                not isinstance(reason, str) or not reason):
            raise ReleaseVerificationError("licenses/go-modules/INDEX.json has an incomplete skipped entry")
        skipped_records.add((path, version))
    if indexed_modules != versioned_sources:
        missing = sorted(versioned_sources - indexed_modules)
        unexpected = sorted(indexed_modules - versioned_sources)
        raise ReleaseVerificationError(
            f"Go module license inventory disagrees with Android and desktop build metadata; "
            f"missing={missing}, unexpected={unexpected}"
        )
    if skipped_records != development_sources:
        missing = sorted(development_sources - skipped_records)
        unexpected = sorted(skipped_records - development_sources)
        raise ReleaseVerificationError(
            f"Go module skipped list disagrees with Android and desktop build metadata; "
            f"missing={missing}, unexpected={unexpected}"
        )


def one_value(path: Path, pattern: str, label: str) -> str:
    match = re.search(pattern, read_text(path), re.MULTILINE)
    if not match:
        raise ReleaseVerificationError(f"{label} declaration not found: {path}")
    value = match.group(1).strip()
    if not SEMVER.fullmatch(value):
        raise ReleaseVerificationError(f"invalid {label} {value!r} in {path}")
    return value


def release_version() -> str:
    """Require every shipped client and Server version to be identical."""
    values = {
        "Windows AppInfo": one_value(
            WINDOWS_APP_INFO,
            r'^\s*public\s+const\s+string\s+Version\s*=\s*"([^"]+)"',
            "Windows version",
        ),
        "Linux AppInfo": one_value(
            LINUX_APP_INFO,
            r'^\s*public\s+const\s+string\s+Version\s*=\s*"([^"]+)"',
            "Linux version",
        ),
        "Server": one_value(
            SERVER_VERSION,
            r'^\s*__version__\s*=\s*["\']([^"\']+)["\']',
            "Server version",
        ),
        "Android versionName": one_value(
            ANDROID_GRADLE,
            r'^\s*versionName\s*=\s*"([^"]+)"',
            "Android versionName",
        ),
        "Android ApiConfig": one_value(
            ANDROID_API_CONFIG,
            r'^\s*const\s+val\s+VERSION\s*=\s*"([^"]+)"',
            "Android API version",
        ),
    }
    if len(set(values.values())) != 1:
        rendered = ", ".join(f"{name}={value}" for name, value in values.items())
        raise ReleaseVerificationError(f"cross-platform version mismatch: {rendered}")

    expected_version = values["Windows AppInfo"]
    _verify_project_versions(WINDOWS_PROJECT, expected_version, "Windows")
    # The Linux tree keeps one project per artifact (library / CLI / tray GUI),
    # so every one of them carries the same four Version elements.
    for project, label in (
        (LINUX_PROJECT, "Linux core"),
        (LINUX_CLI_PROJECT, "Linux CLI"),
        (LINUX_GUI_PROJECT, "Linux GUI"),
    ):
        if project.is_file():
            _verify_project_versions(project, expected_version, label)
    return expected_version


def _verify_project_versions(project: Path, expected: str, label: str) -> None:
    project_text = read_text(project)
    for name in ("Version", "FileVersion", "AssemblyVersion", "InformationalVersion"):
        match = re.search(rf"^\s*<{name}>\s*([^<]+?)\s*</{name}>\s*$", project_text, re.MULTILINE)
        if not match:
            raise ReleaseVerificationError(f"{label} project is missing <{name}>")
        project_value = match.group(1).strip()
        accepted = {expected}
        if name in {"FileVersion", "AssemblyVersion"}:
            accepted.add(expected + ".0")
        if project_value not in accepted:
            raise ReleaseVerificationError(
                f"{label} project version mismatch for {name}: expected {sorted(accepted)}, got {project_value}"
            )


def verify_windows_release_manifest() -> None:
    """Require the GUI release entry point to request administrator rights."""
    project = read_text(WINDOWS_PROJECT)
    if "<ApplicationManifest>app.manifest</ApplicationManifest>" not in project:
        raise ReleaseVerificationError(
            "Windows project must embed app.manifest for the formal GUI release"
        )
    manifest = read_text(WINDOWS_MANIFEST)
    marker = '<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />'
    if marker not in manifest:
        raise ReleaseVerificationError(
            "Windows release app.manifest must request requireAdministrator"
        )


def tag_version(tag: str) -> str:
    """Return the semver represented by an exact cross-platform release tag."""
    value = (tag or "").strip()
    if value.startswith("refs/tags/"):
        value = value[len("refs/tags/") :]
    if not re.fullmatch(r"v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)", value):
        raise ReleaseVerificationError(
            f"formal releases require an exact v<major>.<minor>.<patch> tag, got {tag!r}"
        )
    version = value[1:]
    if not SEMVER.fullmatch(version):
        raise ReleaseVerificationError(f"invalid release tag version: {tag!r}")
    return version


def verify_tag(tag: str) -> str:
    version = release_version()
    tagged = tag_version(tag)
    if tagged != version:
        raise ReleaseVerificationError(
            f"tag/version mismatch: tag={tagged}, source={version}; refusing formal release"
        )
    return version


def parse_core_manifest(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw_line in read_text(path).splitlines():
        if "=" not in raw_line:
            continue
        name, value = (part.strip() for part in raw_line.split("=", 1))
        if name in {"xray.exe", "geoip.dat", "geosite.dat"}:
            if not HEX64.fullmatch(value):
                raise ReleaseVerificationError(f"invalid core SHA-256 for {name} in {path}")
            values[name] = value
    missing = {"xray.exe", "geoip.dat", "geosite.dat"} - values.keys()
    if missing:
        raise ReleaseVerificationError(f"core manifest is missing: {', '.join(sorted(missing))}")
    return values


def verify_core(core: Path = WINDOWS_CORE) -> None:
    expected = parse_core_manifest(core / "VERSION.txt")
    for name, digest in expected.items():
        path = core / name
        if not path.is_file():
            raise ReleaseVerificationError(f"bundled core asset is missing: {path}")
        actual = sha256(path)
        if actual != digest:
            raise ReleaseVerificationError(
                f"bundled core SHA-256 mismatch for {name}: expected {digest}, got {actual}"
            )


def _database_tags(path: Path) -> set[str]:
    """Read the tags present in a compiled geoip/geosite database."""
    # trim_geodata owns the format; importing it keeps one parser rather than
    # a second one here that could disagree with the tool that wrote the file.
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    try:
        import trim_geodata
    finally:
        sys.path.pop(0)

    try:
        data = path.read_bytes()
        return {code.upper() for code, _ in trim_geodata._iter_entries(data)}
    except (OSError, trim_geodata.ProtoError) as exc:
        raise ReleaseVerificationError(f"cannot read geo database {path}: {exc}") from exc


def verify_geo_tags() -> None:
    """Every geo tag a routing rule names must exist in the shipped database.

    The databases are trimmed (scripts/trim_geodata.py), so this is the check
    that keeps the trim honest.  Xray resolves every ``geoip:``/``geosite:``
    reference when it loads a config and refuses to start with "code not
    found" if one is missing -- and the service-category rules are only
    emitted when the server enables the ``usage.categories`` feature flag, so
    a tag dropped from the trim would break connections for everyone long
    after the build that dropped it, with nothing in CI to catch it.

    Both clients are scanned, and both sets are checked against both shipped
    copies, because the two copies are required to be the same bytes.
    """
    referenced: dict[str, set[str]] = {"geoip.dat": set(), "geosite.dat": set()}
    for source in GEO_TAG_SOURCES:
        if not source.is_file():
            raise ReleaseVerificationError(f"geo tag source is missing: {source}")
        for kind, tag in GEO_TAG_LITERAL.findall(read_text(source)):
            referenced[f"{kind}.dat"].add(tag.strip().upper())

    if not referenced["geosite.dat"] or not referenced["geoip.dat"]:
        raise ReleaseVerificationError(
            "no geo tags were found in the routing sources; the scan is broken"
        )

    for name, wanted in referenced.items():
        windows_copy = WINDOWS_CORE / name
        android_copy = ANDROID_ASSETS / name
        for copy in (windows_copy, android_copy):
            if not copy.is_file():
                raise ReleaseVerificationError(f"geo database is missing: {copy}")

        # The two clients must ship the same trim output.  A drift here would
        # make one platform fail on a tag the other resolves fine.
        if sha256(windows_copy) != sha256(android_copy):
            raise ReleaseVerificationError(
                f"Windows and Android {name} differ; they must be the same trim output"
            )

        present = _database_tags(windows_copy)
        missing = sorted(wanted - present)
        if missing:
            raise ReleaseVerificationError(
                f"{name} is missing tags the routing rules reference: {missing}; "
                "add them to trim_geodata.KEEP and re-trim from the upstream file"
            )


def parse_vendor_manifest(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    current: str | None = None
    for raw_line in read_text(path).splitlines():
        line = raw_line.strip()
        if line in {"libv2ray.aar", "libv2ray-sources.jar"}:
            current = line
            continue
        match = re.fullmatch(r"SHA-256:\s*([0-9a-fA-F]{64})", line)
        if current and match:
            values[current] = match.group(1).lower()
            current = None
    missing = {"libv2ray.aar", "libv2ray-sources.jar"} - values.keys()
    if missing:
        raise ReleaseVerificationError(f"Android vendor manifest is missing: {', '.join(sorted(missing))}")
    return values


def verify_android_vendor() -> None:
    expected = parse_vendor_manifest(ANDROID_VENDOR_MANIFEST)
    for name, path in (("libv2ray.aar", ANDROID_AAR), ("libv2ray-sources.jar", ANDROID_SOURCES)):
        if not path.is_file():
            raise ReleaseVerificationError(f"Android vendor asset is missing: {path}")
        actual = sha256(path)
        if actual != expected[name]:
            raise ReleaseVerificationError(
                f"Android vendor SHA-256 mismatch for {name}: expected {expected[name]}, got {actual}"
            )
    try:
        with zipfile.ZipFile(ANDROID_AAR) as archive:
            names = set(archive.namelist())
    except (OSError, zipfile.BadZipFile) as exc:
        raise ReleaseVerificationError(f"Android AAR is not a readable ZIP: {ANDROID_AAR}") from exc
    missing = set(REQUIRED_AAR_ENTRIES) - names
    if missing:
        raise ReleaseVerificationError(f"Android AAR is missing required entries: {', '.join(sorted(missing))}")


def verify_android_toolchain() -> None:
    """Pin both the Gradle distribution and the checked-in wrapper JAR."""
    properties = read_text(ANDROID_WRAPPER_PROPERTIES)
    distribution_line = next(
        (line for line in properties.splitlines() if line.startswith("distributionUrl=")),
        "",
    )
    distribution = distribution_line.split("=", 1)[-1].rsplit("/", 1)[-1]
    checksum = re.search(r"^distributionSha256Sum=([0-9a-fA-F]{64})$", properties, re.MULTILINE)
    if distribution != EXPECTED_GRADLE_DISTRIBUTION:
        raise ReleaseVerificationError(
            f"Android Gradle wrapper must use {EXPECTED_GRADLE_DISTRIBUTION}: {ANDROID_WRAPPER_PROPERTIES}"
        )
    if not checksum or checksum.group(1).lower() != EXPECTED_GRADLE_DISTRIBUTION_SHA256:
        raise ReleaseVerificationError(
            f"Android Gradle distributionSha256Sum mismatch: {ANDROID_WRAPPER_PROPERTIES}"
        )
    actual = sha256(ANDROID_WRAPPER_JAR)
    if actual != EXPECTED_GRADLE_WRAPPER_JAR_SHA256:
        raise ReleaseVerificationError(
            f"Android Gradle wrapper JAR SHA-256 mismatch: expected {EXPECTED_GRADLE_WRAPPER_JAR_SHA256}, got {actual}"
        )


def deployment_origin(document: object, *, require_configured: bool = False) -> str:
    """Validate deployment keys and return the API HTTPS origin."""
    if (
        not isinstance(document, dict)
        or "api_base_url" not in document
        or set(document) - {"api_base_url", "connectivity_check_urls"}
    ):
        raise ReleaseVerificationError(
            "deployment.json must contain api_base_url and only supported keys"
        )
    value = document["api_base_url"]
    parsed, host = _parse_deployment_https_url(
        value,
        label="api_base_url",
        allow_path=False,
    )
    if "connectivity_check_urls" in document:
        _validate_connectivity_check_urls(document["connectivity_check_urls"])

    if require_configured and (host.lower() == "invalid" or host.lower().endswith(".invalid")):
        raise ReleaseVerificationError("replace the example api_base_url in deployment.json before production packaging")
    return value.rstrip("/")


def _parse_deployment_https_url(
    value: object,
    *,
    label: str,
    allow_path: bool,
):
    """Apply the shared strict HTTPS authority rules to an origin or URL."""
    if not isinstance(value, str) or not value or any(char.isspace() or ord(char) < 32 or ord(char) == 127 for char in value):
        kind = "URL" if allow_path else "origin"
        raise ReleaseVerificationError(f"{label} must be a nonempty HTTPS {kind} without whitespace")
    try:
        parsed = urlsplit(value)
        port = parsed.port
        host = parsed.hostname
    except ValueError as exc:
        raise ReleaseVerificationError(f"{label} has an invalid host or port") from exc
    if (parsed.scheme != "https" or not host or parsed.username is not None
            or parsed.password is not None or (not allow_path and parsed.path not in ("", "/"))
            or parsed.query or parsed.fragment
            or "?" in value or "#" in value or "\\" in value or "%" in parsed.netloc
            or (port is not None and not 1 <= port <= 65535) or parsed.netloc.endswith(":")):
        if allow_path:
            raise ReleaseVerificationError(
                f"{label} must be an HTTPS URL without credentials, query or fragment"
            )
        raise ReleaseVerificationError(
            f"{label} must be an HTTPS origin without credentials, path, query or fragment"
        )
    if parsed.netloc.startswith("["):
        try:
            ipaddress.IPv6Address(host)
        except ValueError as exc:
            raise ReleaseVerificationError(f"{label} brackets require an IPv6 address") from exc
    try:
        ipaddress.ip_address(host)
    except ValueError:
        if all(re.fullmatch(r"(?:0x[0-9a-f]+|[0-9]+)", label, re.IGNORECASE) for label in host.split(".")):
            raise ReleaseVerificationError(f"{label} requires a canonical IPv4 address") from None
        if len(host) > 253 or any(not re.fullmatch(
                r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label
        ) for label in host.split(".")):
            raise ReleaseVerificationError(f"{label} has an invalid hostname")
    return parsed, host


def _validate_connectivity_check_urls(value: object) -> None:
    if not isinstance(value, list) or not 1 <= len(value) <= 4:
        raise ReleaseVerificationError("connectivity_check_urls must contain 1-4 HTTPS URLs")
    for index, url in enumerate(value):
        _parse_deployment_https_url(
            url,
            label=f"connectivity_check_urls[{index}]",
            allow_path=True,
        )


def verify_deployment_config(*, require_configured: bool = False) -> str:
    try:
        document = json.loads(read_text(DEPLOYMENT_CONFIG))
    except json.JSONDecodeError as exc:
        raise ReleaseVerificationError("deployment.json is not valid JSON") from exc
    return deployment_origin(document, require_configured=require_configured)


def verify_deployment_consumers() -> None:
    """Check build inputs, ensuring each platform uses the root configuration."""
    inputs = (
        (WINDOWS_PROJECT, ("deployment.json", "MyProxy.Deployment.json")),
        (LINUX_PROJECT, ("deployment.json", "MyProxy.Deployment.json")),
        (WINDOWS_APP_INFO, ("DeploymentConfiguration",)),
        (LINUX_APP_INFO, ("DeploymentConfiguration",)),
        (ANDROID_GRADLE, (
            "../../deployment.json",
            "api_base_url",
            "connectivity_check_urls",
            "DEPLOYMENT_API_BASE_URL",
            "DEPLOYMENT_CONNECTIVITY_CHECK_URLS",
        )),
        (ANDROID_API_CONFIG, (
            "BuildConfig.DEPLOYMENT_API_BASE_URL",
            "DEPLOYMENT_CONNECTIVITY_CHECK_URLS",
        )),
    )
    for path, markers in inputs:
        text = read_text(path)
        if any(marker not in text for marker in markers):
            raise ReleaseVerificationError(f"platform does not consume the shared deployment configuration: {path}")
    shared = read_text(ROOT / "windows" / "MyProxy" / "Core" / "DeploymentConfiguration.cs")
    for marker in ("MyProxy.Deployment.json", "api_base_url", "connectivity_check_urls"):
        if marker not in shared:
            raise ReleaseVerificationError(
                f"shared client configuration does not load deployment resource marker: {marker}"
            )
    server_config = read_text(ROOT / "server" / "myproxy_server" / "config.py")
    for marker in (
        "MYPROXY_DEPLOYMENT_CONFIG",
        "load_deployment_config",
        "api_base_url",
        "connectivity_check_urls",
    ):
        if marker not in server_config:
            raise ReleaseVerificationError(f"Server does not consume deployment configuration: {marker}")


def mentions_route(text: str, route: str) -> bool:
    """Whether ``text`` names ``route`` itself, not merely a longer route.

    A plain substring test let ``/api/device/update/report`` satisfy the check
    for ``/api/device/update``: deleting the latter from openapi.yaml or from
    the Android client passed this gate.  The route must be followed by
    something that cannot continue a path -- ``:`` in openapi.yaml, a closing
    quote in Kotlin, a backtick or space in the Markdown contract.
    """
    return re.search(re.escape(route) + r"(?![\w/.-])", text) is not None


def declares_openapi_path(openapi: str, route: str) -> bool:
    """Whether openapi.yaml declares ``route`` as a path key under ``paths:``.

    Stricter than :func:`mentions_route` on purpose: the spec's prose also
    names routes (e.g. "the signed manifest still goes through
    `GET /api/device/update`"), and a sentence is not a declared endpoint.
    """
    pattern = r"^  " + re.escape(route) + r":[ \t]*\r?$"
    return re.search(pattern, openapi, re.MULTILINE) is not None


def verify_contract() -> None:
    openapi = read_text(OPENAPI)
    android_client = read_text(ANDROID_API_CLIENT)
    windows_services = "\n".join(read_text(ROOT / "windows" / "MyProxy" / "Services" / name)
                                  for name in ("BindingService.cs", "ConfigService.cs", "UpdateService.cs", "UsageReporter.cs"))
    for route in SERVER_ROUTES:
        if not declares_openapi_path(openapi, route):
            raise ReleaseVerificationError(f"OpenAPI is missing required route: {route}")
    for route in WINDOWS_ROUTES:
        actual_route = "/client/{TargetPlatform.Name}/latest.json" if route == "/client/windows/latest.json" else route
        if not mentions_route(windows_services, actual_route):
            raise ReleaseVerificationError(f"Windows client code is missing required route: {route}")
    for route in ANDROID_ROUTES:
        if not mentions_route(android_client, route):
            raise ReleaseVerificationError(f"Android API client is missing required route: {route}")

    verify_deployment_config()
    verify_deployment_consumers()


def verify_publish_core(publish_dir: Path) -> None:
    """Check the loose Core directory copied into a Windows publish output."""
    core = publish_dir / "Core"
    source_manifest = parse_core_manifest(WINDOWS_CORE / "VERSION.txt")
    # VERSION.txt is intentionally excluded from the shipped Windows output;
    # if a packaging variant includes it, compare it as an additional guard.
    published_manifest_path = core / "VERSION.txt"
    if published_manifest_path.is_file():
        published_manifest = parse_core_manifest(published_manifest_path)
        if published_manifest != source_manifest:
            raise ReleaseVerificationError(
                "published Core/VERSION.txt does not match the repository core manifest"
            )
    for name, digest in source_manifest.items():
        path = core / name
        if not path.is_file():
            raise ReleaseVerificationError(f"published core asset is missing: {path}")
        actual = sha256(path)
        if actual != digest:
            raise ReleaseVerificationError(
                f"published core SHA-256 mismatch for {name}: expected {digest}, got {actual}"
            )


def verify_windows_single_file(publish_dir: Path) -> None:
    """Fail closed unless a Windows publish directory contains one EXE only.

    The embedded Xray payload is verified from the repository before publish;
    runtime extraction verifies it again when the GUI first starts. There must
    be no adjacent Core directory, DLL, runtimeconfig, PDB, or signing file in
    the end-user Windows artifact.
    """
    if not publish_dir.is_dir():
        raise ReleaseVerificationError(f"Windows publish directory is missing: {publish_dir}")
    files = sorted(path for path in publish_dir.rglob("*") if path.is_file())
    directories = sorted(path for path in publish_dir.rglob("*") if path.is_dir())
    if directories or len(files) != 1 or files[0].name != "MyProxy.exe":
        rendered = [path.relative_to(publish_dir).as_posix() for path in files]
        raise ReleaseVerificationError(
            "Windows single-file publish must contain exactly MyProxy.exe and no directories; "
            f"files={rendered}, directories={len(directories)}"
        )
    try:
        header = files[0].read_bytes()[:2]
    except OSError as exc:
        raise ReleaseVerificationError(f"cannot read Windows single-file publish: {files[0]}") from exc
    if header != b"MZ":
        raise ReleaseVerificationError(f"Windows single-file publish is not a PE executable: {files[0]}")
    verify_core()


def iter_files(root: Path, *, include_sbom: bool = False):
    if not root.is_dir():
        raise ReleaseVerificationError(f"artifact directory is missing: {root}")
    for path in sorted(root.rglob("*")):
        if path.is_file() and path.name != "SHA256SUMS.txt" and (
            include_sbom or path.name != "sbom.cdx.json"
        ):
            yield path


def write_checksums(root: Path, output: Path | None = None) -> Path:
    output = output or root / "SHA256SUMS.txt"
    lines = []
    for path in iter_files(root, include_sbom=True):
        relative = path.relative_to(root).as_posix()
        lines.append(f"{sha256(path)}  {relative}")
    output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return output


def _android_catalog_components(catalog: str, build_gradle: str | None = None) -> list[dict]:
    versions = dict(re.findall(r'^([A-Za-z0-9_-]+)\s*=\s*"([^"]+)"', catalog, re.MULTILINE))
    section_match = re.search(
        r"(?ms)^\[libraries\]\s*\n(.*?)(?=^\[[^\n]+\]\s*$|\Z)",
        catalog,
    )
    if not section_match:
        raise ReleaseVerificationError("Android version catalog is missing [libraries]")

    selected: set[str] | None = None
    if build_gradle is not None:
        selected = set()
        for line in build_gradle.splitlines():
            if not re.match(r"^\s*(?:implementation|api|runtimeOnly)\s*\(", line):
                continue
            selected.update(re.findall(r"\blibs\.([A-Za-z0-9_.]+)", line))

    components: list[dict] = []
    seen_selected: set[str] = set()
    for raw_line in section_match.group(1).splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        entry = re.fullmatch(r"([A-Za-z0-9_.-]+)\s*=\s*\{(.*)\}", line)
        if not entry:
            raise ReleaseVerificationError(f"unparseable Android catalog library entry: {raw_line}")
        alias, body = entry.groups()
        accessor = alias.replace("-", ".").replace("_", ".")
        if selected is not None and accessor not in selected:
            continue
        seen_selected.add(accessor)
        fields = dict(re.findall(r'([A-Za-z][A-Za-z0-9_.-]*)\s*=\s*"([^"]*)"', body))
        group = fields.get("group")
        name = fields.get("name")
        if not group or not name:
            raise ReleaseVerificationError(f"Android catalog entry lacks group/name: {alias}")
        version_ref = fields.get("version.ref")
        direct_version = fields.get("version")
        if version_ref and direct_version:
            raise ReleaseVerificationError(f"Android catalog entry has both version and version.ref: {alias}")
        version = direct_version
        properties = [{"name": "myproxy:catalog-alias", "value": alias}]
        if version_ref:
            version = versions.get(version_ref)
            if not version:
                raise ReleaseVerificationError(f"Android catalog version.ref is undefined: {alias} -> {version_ref}")
        if not version:
            properties.append({"name": "myproxy:version-source", "value": "compose-bom"})
        component = {"type": "library", "group": group, "name": name, "purl": f"pkg:maven/{group}/{name}"}
        if version:
            component["version"] = version
            component["purl"] += f"@{version}"
        component["properties"] = properties
        components.append(component)
    if selected is not None:
        missing = selected - seen_selected
        if missing:
            raise ReleaseVerificationError(
                "Android release dependency alias is absent from the version catalog: "
                + ", ".join(sorted(missing))
            )
    return components


def windows_runtime_components(root: Path) -> list[dict]:
    """Extract runtime packs from a legacy Windows ZIP, if one is present.

    Formal Windows releases are now a single bundled EXE, so they have no
    standalone MyProxy.deps.json and correctly contribute no runtime-pack
    entries here.
    """
    archives = sorted(
        path
        for path in root.rglob("*.zip")
        if path.is_file() and path.name.startswith("MyProxy-windows-self-contained-win-x64-")
    )
    if not archives:
        return []
    if len(archives) != 1:
        raise ReleaseVerificationError(f"expected one formal Windows ZIP for SBOM, found {len(archives)}")
    try:
        with zipfile.ZipFile(archives[0]) as archive:
            entries = [info for info in archive.infolist() if info.filename.rsplit("/", 1)[-1] == "MyProxy.deps.json"]
            if len(entries) != 1 or entries[0].filename != "MyProxy.deps.json" or entries[0].is_dir() or entries[0].file_size > 8 * 1024 * 1024:
                raise ReleaseVerificationError("Windows ZIP must contain one safe root MyProxy.deps.json")
            payload = json.loads(archive.read(entries[0]).decode("utf-8"))
    except ReleaseVerificationError:
        raise
    except (OSError, KeyError, RuntimeError, UnicodeDecodeError, json.JSONDecodeError, zipfile.BadZipFile) as exc:
        raise ReleaseVerificationError(f"cannot safely read MyProxy.deps.json from {archives[0]}") from exc

    targets = payload.get("targets") if isinstance(payload, dict) else None
    if not isinstance(targets, dict):
        raise ReleaseVerificationError("MyProxy.deps.json has no valid targets object")
    found: dict[str, str] = {}
    for target in targets.values():
        if not isinstance(target, dict):
            raise ReleaseVerificationError("MyProxy.deps.json target is not an object")
        for package_key in target:
            if not isinstance(package_key, str):
                raise ReleaseVerificationError("MyProxy.deps.json package key is not a string")
            for deps_key in WINDOWS_RUNTIME_PACKAGES:
                prefix = deps_key + "/"
                if package_key.startswith(prefix):
                    runtime_version = package_key[len(prefix) :]
                    if not SEMVER.fullmatch(runtime_version):
                        raise ReleaseVerificationError(f"invalid runtime pack version: {package_key}")
                    if deps_key in found and found[deps_key] != runtime_version:
                        raise ReleaseVerificationError(f"multiple versions found for runtime pack: {deps_key}")
                    found[deps_key] = runtime_version
    missing = set(WINDOWS_RUNTIME_PACKAGES) - found.keys()
    if missing:
        raise ReleaseVerificationError(f"MyProxy.deps.json is missing runtime packs: {', '.join(sorted(missing))}")
    return [
        {
            "type": "library",
            "name": nuget_package,
            "version": found[deps_key],
            "purl": f"pkg:nuget/{nuget_package}@{found[deps_key]}",
            "properties": [
                {"name": "myproxy:source", "value": "MyProxy.deps.json"},
                {"name": "myproxy:deps-key", "value": deps_key},
            ],
        }
        for deps_key, nuget_package in WINDOWS_RUNTIME_PACKAGES.items()
    ]


def _dependency_components(root: Path | None = None) -> list[dict]:
    components: list[dict] = []
    gradle = read_text(ROOT / "android" / "MyProxyAndroid" / "gradle" / "libs.versions.toml")
    components.extend(_android_catalog_components(gradle, read_text(ANDROID_GRADLE)))
    components.extend(
        [
            {"type": "library", "name": "Python standard library", "version": ">=3.10"},
            {"type": "file", "name": "vendor/libv2ray/libv2ray.aar", "version": "26.9.9", "hashes": [{"alg": "SHA-256", "content": sha256(ANDROID_AAR)}]},
            {"type": "file", "name": "windows/MyProxy/Assets/Core/xray.exe", "version": "bundled", "hashes": [{"alg": "SHA-256", "content": sha256(WINDOWS_CORE / "xray.exe")}]} ,
        ]
    )
    if root is not None:
        components.extend(windows_runtime_components(root))
    return sorted(components, key=lambda item: (item.get("group", ""), item["name"], item.get("version", "")))


def write_sbom(root: Path, version: str, output: Path | None = None) -> Path:
    output = output or root / "sbom.cdx.json"
    components = _dependency_components(root)
    components.extend(
        {
            "type": "file",
            "name": path.relative_to(root).as_posix(),
            "version": version,
            "hashes": [{"alg": "SHA-256", "content": sha256(path)}],
        }
        for path in iter_files(root)
    )
    components = sorted(components, key=lambda item: (item["type"], item.get("group", ""), item["name"], item.get("version", "")))
    document = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, f'myproxy:{version}')}",
        "version": 1,
        "metadata": {"component": {"type": "application", "name": "MyProxy", "version": version}},
        "components": components,
    }
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return output


def parse_linux_core_manifest(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw_line in read_text(path).splitlines():
        line = raw_line.strip()
        if line.startswith("#") or "=" not in line:
            continue
        name, value = (part.strip() for part in line.split("=", 1))
        if name in {"xray-core", "zip", "zip_sha256", "xray", "geoip.dat", "geosite.dat"}:
            values[name] = value
    return values


def verify_linux_client() -> None:
    """Verify shared client code, deployment input, and Linux core checksums."""
    project = read_text(LINUX_PROJECT)
    for folder, name in LINUX_SHARED_SOURCES:
        exact = f"windows\\MyProxy\\{folder}\\{name}"
        # Core/ and Models/ are linked as a folder glob (everything in them is
        # platform-neutral), Services/ file by file.  Either way the file must
        # be compiled from the Windows tree -- never copied into linux/.
        globbed = f"windows\\MyProxy\\{folder}\\*.cs"
        if exact not in project and globbed not in project:
            raise ReleaseVerificationError(
                f"Linux project does not compile the shared source {folder}/{name}; "
                "the two clients must not carry separate copies"
            )

    # The one Core file that must NOT be linked: it generates Windows Task
    # Scheduler XML.
    if "AutostartTask.cs" not in project:
        raise ReleaseVerificationError(
            "Linux project must exclude Core/AutostartTask.cs (Windows Task Scheduler XML)"
        )

    for path in sorted(LINUX_ROOT.rglob("*")):
        if not path.is_file() or path.suffix not in {".cs", ".csproj", ".kt", ".py"}:
            continue
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        text = read_text(path)
        for marker in ("CurrentProductionSha256", "RELEASE_CERTIFICATE_SHA256_PINS", "Sha256Pins"):
            if re.search(rf"\b{marker}\s*(=|:)", text):
                raise ReleaseVerificationError(
                    f"{path} redefines legacy TLS pins; use standard certificate validation"
                )

    # The geo databases are linked from the Windows assets, not copied: a third
    # copy is a third thing that can drift.  Build outputs are excluded -- the
    # csproj legitimately copies them into bin/ next to the executable.
    for name in ("geoip.dat", "geosite.dat"):
        copies = [
            path
            for path in LINUX_ROOT.rglob(name)
            if path.is_file() and not any(part in {"bin", "obj"} for part in path.parts)
        ]
        if copies:
            rendered = ", ".join(str(path.relative_to(ROOT)) for path in copies)
            raise ReleaseVerificationError(
                f"Linux tree carries its own {name} ({rendered}); link windows/MyProxy/Assets/Core/{name}"
            )
        if f"windows\\MyProxy\\Assets\\Core\\{name}" not in project:
            raise ReleaseVerificationError(
                f"Linux project does not reference windows/MyProxy/Assets/Core/{name}"
            )

    windows_manifest = parse_core_manifest(WINDOWS_CORE / "VERSION.txt")
    windows_full = parse_linux_core_manifest(WINDOWS_CORE / "VERSION.txt")
    linux_manifest = parse_linux_core_manifest(LINUX_CORE_MANIFEST)

    if not linux_manifest.get("xray-core"):
        raise ReleaseVerificationError("Linux core manifest is missing xray-core")
    if linux_manifest["xray-core"] != windows_full.get("xray-core"):
        raise ReleaseVerificationError(
            "Windows and Linux ship different xray-core versions: "
            f"{windows_full.get('xray-core')} vs {linux_manifest['xray-core']}"
        )

    for name in ("geoip.dat", "geosite.dat"):
        if linux_manifest.get(name, "").lower() != windows_manifest.get(name, ""):
            raise ReleaseVerificationError(
                f"Linux core manifest disagrees with Windows about {name}: "
                f"{linux_manifest.get(name, '')!r} vs {windows_manifest.get(name, '')!r}"
            )

    digest = linux_manifest.get("xray", "")
    if digest and not HEX64.fullmatch(digest):
        raise ReleaseVerificationError(f"Linux core manifest has an invalid xray digest: {digest!r}")

    zip_digest = linux_manifest.get("zip_sha256", "")
    if not HEX64.fullmatch(zip_digest):
        raise ReleaseVerificationError(
            "Linux core manifest must record the upstream archive's SHA-256 (zip_sha256)"
        )


def verify_linux_contract() -> None:
    """Linux 客户端调用的路由与它自称的平台。

    The Linux client has no API client of its own: it compiles the shared
    services, and the only platform-specific piece is ``AppInfo.Platform``.
    That constant decides three things at once -- the platform reported in
    claim/heartbeat, the ``/client/<platform>/latest.json`` path, and the
    ``platform`` field the manifest verifier accepts -- so it is asserted here
    together with the routes the shared services actually call.
    """
    linux_info = read_text(LINUX_APP_INFO)

    if not re.search(r'const\s+string\s+Platform\s*=\s*"linux"', linux_info):
        raise ReleaseVerificationError('Linux AppInfo must declare Platform = "linux"')

    verify_deployment_consumers()

    cli_project = read_text(LINUX_PROJECT)
    for folder, name in (
        ("Services", "BindingService.cs"),
        ("Services", "ConfigService.cs"),
        ("Services", "UpdateService.cs"),
        ("Services", "UsageReporter.cs"),
    ):
        if f"windows\\MyProxy\\{folder}\\{name}" not in cli_project:
            raise ReleaseVerificationError(
                f"Linux client does not compile {name}; LINUX_ROUTES cannot be asserted"
            )

    update_service = read_text(ROOT / "windows" / "MyProxy" / "Services" / "UpdateService.cs")
    if "/client/{TargetPlatform.Name}/latest.json" not in update_service:
        raise ReleaseVerificationError(
            "UpdateService must build the public update path from the client's platform, "
            "otherwise the Linux build reads the Windows metadata"
        )
    for route in UPDATE_PLANE_ROUTES:
        if route not in update_service:
            raise ReleaseVerificationError(f"UpdateService is missing required route: {route}")

    binding = read_text(ROOT / "windows" / "MyProxy" / "Services" / "BindingService.cs")
    if "/api/device/claim" not in binding:
        raise ReleaseVerificationError("BindingService is missing required route: /api/device/claim")

    config = read_text(ROOT / "windows" / "MyProxy" / "Services" / "ConfigService.cs")
    for route in ("/api/device/config", "/api/device/heartbeat"):
        if route not in config:
            raise ReleaseVerificationError(f"ConfigService is missing required route: {route}")

    usage = read_text(ROOT / "windows" / "MyProxy" / "Services" / "UsageReporter.cs")
    for route in OBSERVABILITY_ROUTES:
        if route not in usage:
            raise ReleaseVerificationError(f"UsageReporter is missing required route: {route}")

    platforms = read_text(ROOT / "server" / "myproxy_server" / "release.py")
    if '"linux"' not in platforms:
        raise ReleaseVerificationError(
            "the Server does not accept platform 'linux'; the Linux client cannot be assigned a release"
        )


def check_all(tag: str | None = None, *, require_configured: bool = False) -> str:
    version = verify_tag(tag) if tag is not None else release_version()
    verify_deployment_config(require_configured=require_configured)
    verify_public_license_materials()
    verify_windows_release_manifest()
    verify_core()
    verify_geo_tags()
    verify_android_vendor()
    verify_android_toolchain()
    verify_contract()
    verify_linux_client()
    verify_linux_contract()
    return version


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check", help="verify cross-platform release inputs")
    check.add_argument("--tag", help="exact formal tag; required by the release workflow")
    check.add_argument("--require-configured", action="store_true", help="reject the example origin before production packaging")
    core = sub.add_parser("verify-core", help="verify a published Core directory")
    core.add_argument("--publish-dir", type=Path, required=True)
    single = sub.add_parser(
        "verify-windows-single-file",
        help="verify that a Windows publish directory contains exactly one MyProxy.exe",
    )
    single.add_argument("--publish-dir", type=Path, required=True)
    checksums = sub.add_parser("checksums", help="write deterministic SHA256SUMS.txt")
    checksums.add_argument("--root", type=Path, required=True)
    checksums.add_argument("--output", type=Path)
    sbom = sub.add_parser("sbom", help="write a deterministic CycloneDX SBOM")
    sbom.add_argument("--root", type=Path, required=True)
    sbom.add_argument("--version", required=True)
    sbom.add_argument("--output", type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    try:
        args = parse_args(argv)
        if args.command == "check":
            version = check_all(args.tag, require_configured=args.require_configured)
            print(f"RELEASE_POLICY_OK version={version}")
        elif args.command == "verify-core":
            verify_publish_core(args.publish_dir)
            print(f"CORE_HASHES_OK publish={args.publish_dir}")
        elif args.command == "verify-windows-single-file":
            verify_windows_single_file(args.publish_dir)
            print(f"WINDOWS_SINGLE_FILE_OK publish={args.publish_dir}")
        elif args.command == "checksums":
            output = write_checksums(args.root, args.output)
            print(f"CHECKSUMS_WRITTEN {output}")
        elif args.command == "sbom":
            output = write_sbom(args.root, args.version, args.output)
            print(f"SBOM_WRITTEN {output}")
        return 0
    except ReleaseVerificationError as exc:
        print(f"RELEASE_POLICY_ERROR: {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"RELEASE_POLICY_ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
