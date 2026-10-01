#!/usr/bin/env python3
"""Unit tests for the repository-only release policy checks."""

from __future__ import annotations

import hashlib
import json
import re
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

try:
    from scripts import release_verify as policy
except ImportError:  # unittest discover -s scripts adds scripts to sys.path
    import release_verify as policy


class ReleaseVerifyTests(unittest.TestCase):

    def write_public_license_fixture(self, root: Path) -> None:
        for relative in policy.PUBLIC_RELEASE_MATERIALS:
            path = root / relative
            if relative in {
                policy.LICENSE_SOURCE_MANIFEST,
                policy.GO_LICENSE_INDEX,
                policy.ANDROID_UPSTREAM_SOURCE_MANIFEST,
                policy.ANDROID_BUILD_INFO,
                policy.CORE_BUILD_INFO,
            }:
                continue
            path.parent.mkdir(parents=True, exist_ok=True)
            marker = policy.PUBLIC_LICENSE_TEXTS.get(relative)
            text = (marker + "\n" + "Complete license terms and conditions. " * 40
                    if marker else "Supporting public release material.\n")
            path.write_text(text, encoding="utf-8")

        module_licenses = {
            "example/android/LICENSE": "Android module license text.\n",
            "example/desktop/LICENSE": "Desktop module license text.\n",
        }
        for relative, text in module_licenses.items():
            path = root / "licenses" / "go-modules" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        android_record = ("example.org/shared", "v1.0.0")
        desktop_record = ("example.org/desktop", "v2.0.0")
        android_devel = ("example.org/android-local", "(devel)")
        android_placeholder = (
            "example.org/android-placeholder", "v0.0.0-00010101000000-000000000000"
        )
        desktop_devel = ("example.org/xray-core", "(devel)")
        go_index = {
            "modules": [
                {
                    "path": android_record[0],
                    "version": android_record[1],
                    "source_zip": "https://proxy.golang.org/example.org/shared/@v/v1.0.0.zip",
                    "zip_sha256": "a" * 64,
                    "licenses": [{
                        "file": "example/android/LICENSE",
                        "sha256": hashlib.sha256(
                            (root / "licenses" / "go-modules" / "example/android/LICENSE").read_bytes()
                        ).hexdigest(),
                    }],
                },
                {
                    "path": desktop_record[0],
                    "version": desktop_record[1],
                    "source_zip": "https://proxy.golang.org/example.org/desktop/@v/v2.0.0.zip",
                    "zip_sha256": "b" * 64,
                    "licenses": [{
                        "file": "example/desktop/LICENSE",
                        "sha256": hashlib.sha256(
                            (root / "licenses" / "go-modules" / "example/desktop/LICENSE").read_bytes()
                        ).hexdigest(),
                    }],
                },
            ],
            "skipped": [
                {"path": android_devel[0], "version": android_devel[1], "reason": "local source"},
                {"path": android_placeholder[0], "version": android_placeholder[1], "reason": "zero version"},
                {"path": desktop_devel[0], "version": desktop_devel[1], "reason": "devel core"},
            ],
            "failures": [],
        }
        (root / policy.GO_LICENSE_INDEX).write_text(
            json.dumps(go_index), encoding="utf-8"
        )

        upstream_root = root / policy.ANDROID_UPSTREAM_ROOT
        upstream_files = []
        for relative in policy.PUBLIC_RELEASE_MATERIALS:
            source_path = Path(relative)
            upstream_prefix = Path(policy.ANDROID_UPSTREAM_ROOT)
            if (source_path.is_relative_to(upstream_prefix) and
                    relative != policy.ANDROID_UPSTREAM_SOURCE_MANIFEST):
                name = source_path.relative_to(upstream_prefix).as_posix()
                path = upstream_root / source_path.relative_to(upstream_prefix)
                upstream_files.append({
                    "file": name,
                    "source": "https://source.example/" + name,
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                })
        upstream_manifest = {
            "project": "AndroidLibXrayLite",
            "tag": "v26.9.9",
            "commit": "d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29",
            "license": "LGPL-3.0",
            "source_archive": "https://source.example/AndroidLibXrayLite.zip",
            "files": upstream_files,
        }
        (root / policy.ANDROID_UPSTREAM_SOURCE_MANIFEST).write_text(
            json.dumps(upstream_manifest), encoding="utf-8"
        )
        build_info = {
            "go_version": "go1.27.1",
            "modules": [
                {"path": android_record[0], "version": android_record[1]},
                {"path": android_devel[0], "version": android_devel[1]},
                {"path": android_placeholder[0], "version": android_placeholder[1]},
            ],
        }
        (root / policy.ANDROID_BUILD_INFO).write_text(
            json.dumps(build_info), encoding="utf-8"
        )
        core_build_info = {
            "components": [
                {
                    "file": "windows/MyProxy/Assets/Core/xray.exe",
                    "go_version": "go1.27.1",
                    "modules": [
                        {"path": android_record[0], "version": android_record[1]},
                        {"path": desktop_record[0], "version": desktop_record[1]},
                        {"path": desktop_devel[0], "version": desktop_devel[1]},
                    ],
                },
                {
                    "file": "linux/MyProxyLinux/assets/xray",
                    "go_version": "go1.27.1",
                    "modules": [
                        {"path": desktop_record[0], "version": desktop_record[1]},
                        {"path": desktop_devel[0], "version": desktop_devel[1]},
                    ],
                },
            ]
        }
        (root / policy.CORE_BUILD_INFO).write_text(
            json.dumps(core_build_info), encoding="utf-8"
        )

        source_entries = []
        for relative in policy.PUBLIC_LICENSE_TEXTS:
            if not relative.startswith("licenses/"):
                continue
            path = root / relative
            source_entries.append({
                "file": path.name,
                "source": "https://licenses.example/" + path.name,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            })
        (root / policy.LICENSE_SOURCE_MANIFEST).write_text(
            json.dumps({"licenses": source_entries}), encoding="utf-8"
        )

    def test_ci_covers_main_and_pull_requests_without_auto_release(self) -> None:
        workflow = (policy.ROOT / ".github" / "workflows" / "ci-cross-platform.yml").read_text(encoding="utf-8")
        self.assertIn("branches: [main]", workflow)
        self.assertIn("pull_request:", workflow)
        for lane in ("server", "android", "linux", "windows"):
            self.assertIn(f"  {lane}:", workflow)
        self.assertNotIn("package_delivery.py --preflight", workflow)
        self.assertFalse((policy.ROOT / ".github" / "workflows" / "release.yml").exists())

    def test_https_origins_preserve_ports_and_allow_example_for_ci(self) -> None:
        for origin in ("https://api.example.invalid", "https://api.example.org", "https://api.example.org:8443", "https://[2001:db8::1]:443"):
            self.assertEqual(policy.deployment_origin({"api_base_url": origin}), origin)
        with self.assertRaisesRegex(policy.ReleaseVerificationError, "example"):
            policy.deployment_origin({"api_base_url":"https://api.example.invalid"}, require_configured=True)
        policy.deployment_origin({"api_base_url":"https://api.example.org"}, require_configured=True)
        self.assertEqual(policy.deployment_origin({"api_base_url":"https://api.example.org:8443/"}), "https://api.example.org:8443")

    def test_optional_connectivity_urls_accept_paths_and_remain_strict(self) -> None:
        document = {
            "api_base_url": "https://api.example.org",
            "connectivity_check_urls": [
                "https://probe.example.net/empty-204",
                "https://[2001:db8::2]:8443/network/check",
            ],
        }
        self.assertEqual(policy.deployment_origin(document), "https://api.example.org")
        invalid_values = (
            None,
            "https://probe.example.net/check",
            [],
            ["https://probe.example.net/check"] * 5,
            ["http://probe.example.net/check"],
            ["https://user:pass@probe.example.net/check"],
            ["https://probe.example.net/check?"],
            ["https://probe.example.net/check?x=1"],
            ["https://probe.example.net/check#"],
            ["https://probe.example.net/check#fragment"],
            [" https://probe.example.net/check"],
            ["https://probe.example.net/check\n"],
            ["https://probe.example.net/\x01check"],
            ["https://probe.example.net/check\\path"],
            ["https://bad_host.example.net/check"],
            ["https://probe.example.net:0/check"],
            ["https://probe.example.net:/check"],
            ["https://[broken]/check"],
            ["https://127.1/check"],
        )
        for urls in invalid_values:
            with self.subTest(urls=urls), self.assertRaises(policy.ReleaseVerificationError):
                policy.deployment_origin({
                    "api_base_url": "https://api.example.org",
                    "connectivity_check_urls": urls,
                })

    def test_https_origin_rejects_invalid_or_ambiguous_input(self) -> None:
        cases = (
            "", "http://api.example.org", " https://api.example.org", "https://api.example.org\n",
            "https://user:password@api.example.org", "https://api.example.org/path", "https://api.example.org//",
            "https://api.example.org?", "https://api.example.org?x=1", "https://api.example.org#",
            "https://api.example.org#fragment", "https://api.example.org:", "https://api.example.org:0",
            "https://api.example.org:65536", "https://api.example.org:no", "https://bad_host.example.org",
            "https://api.example.org\\path", "https://%61pi.example.org", "https://[invalid]", None, True,
            "https://api.example.org.", "https://127.1", "https://0177.0.0.1", "https://123", "https://256.0.0.1",
            "https://0x7f000001", "https://0x7f.0.0.1", "https://[fe80::1%25eth0]", "\x01https://api.example.org", "https://api.example.org\x7f",
            "https://[v1.example.org]", "https://[vF.test]",
        )
        for origin in cases:
            with self.subTest(origin=origin), self.assertRaises(policy.ReleaseVerificationError):
                policy.deployment_origin({"api_base_url": origin})
        for document in ([], {}, {"api_base_url":"https://api.example.org", "unexpected":True}):
            with self.subTest(document=document), self.assertRaises(policy.ReleaseVerificationError):
                policy.deployment_origin(document)

    def test_config_file_rejects_malformed_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "deployment.json"
            config.write_text("{broken}", encoding="utf-8")
            with patch.object(policy, "DEPLOYMENT_CONFIG", config), self.assertRaisesRegex(policy.ReleaseVerificationError, "JSON"):
                policy.verify_deployment_config()
    def test_current_cross_platform_inputs_are_consistent(self) -> None:
        self.assertEqual(policy.release_version(), "0.1.0")
        policy.verify_core()
        policy.verify_android_vendor()
        policy.verify_android_toolchain()
        policy.verify_contract()
        policy.verify_deployment_config()

    def test_public_license_materials_are_present_and_substantive(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_public_license_fixture(root)
            policy.verify_public_license_materials(root)

            (root / "THIRD_PARTY_NOTICES.md").unlink()
            with self.assertRaisesRegex(policy.ReleaseVerificationError, "THIRD_PARTY_NOTICES.md"):
                policy.verify_public_license_materials(root)

            self.write_public_license_fixture(root)
            (root / "licenses" / "MPL-2.0.txt").write_text(
                policy.PUBLIC_LICENSE_TEXTS["licenses/MPL-2.0.txt"], encoding="utf-8"
            )
            with self.assertRaisesRegex(policy.ReleaseVerificationError, "substantive"):
                policy.verify_public_license_materials(root)

    def test_go_module_license_index_hashes_every_distributed_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_public_license_fixture(root)
            policy.verify_public_license_materials(root)

            (root / "licenses" / "go-modules" / "example" / "android" / "LICENSE").write_text(
                "modified module license\n", encoding="utf-8"
            )
            with self.assertRaisesRegex(policy.ReleaseVerificationError, "SHA-256 mismatch"):
                policy.verify_public_license_materials(root)

    def test_go_module_inventory_matches_android_and_desktop_build_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_public_license_fixture(root)
            policy.verify_public_license_materials(root)

            path = root / policy.CORE_BUILD_INFO
            build_info = json.loads(path.read_text(encoding="utf-8"))
            build_info["components"][0]["modules"].append({
                "path": "example.org/unindexed-desktop-module",
                "version": "v9.9.9",
            })
            path.write_text(json.dumps(build_info), encoding="utf-8")
            with self.assertRaisesRegex(policy.ReleaseVerificationError, "Android and desktop build metadata"):
                policy.verify_public_license_materials(root)

    def test_a_longer_route_does_not_stand_in_for_a_shorter_one(self) -> None:
        # /api/device/update/report used to satisfy the check for
        # /api/device/update, so deleting the latter passed the gate.
        only_report = "  /api/device/update/report:\n    post: {}\n"
        self.assertFalse(policy.mentions_route(only_report, "/api/device/update"))
        self.assertTrue(policy.mentions_route(only_report, "/api/device/update/report"))
        self.assertTrue(policy.mentions_route("  /api/device/update:\n", "/api/device/update"))
        self.assertTrue(
            policy.mentions_route('.url("${base}/api/device/update")', "/api/device/update")
        )
        self.assertTrue(
            policy.mentions_route("### `GET /api/device/update`（设备 Bearer）", "/api/device/update")
        )
        self.assertTrue(
            policy.mentions_route("GET /client/android/latest.json", "/client/android/latest.json")
        )

    def test_openapi_routes_must_be_declared_paths_not_prose(self) -> None:
        spec = (
            "paths:\n"
            "  /api/device/update/report:\n"
            "    post:\n"
            "      description: the manifest still goes through `GET /api/device/update`\n"
        )
        self.assertFalse(policy.declares_openapi_path(spec, "/api/device/update"))
        self.assertTrue(policy.declares_openapi_path(spec, "/api/device/update/report"))
        self.assertTrue(policy.declares_openapi_path("  /api/device/update:\r\n", "/api/device/update"))

    def test_android_gradle_wrapper_is_pinned_and_hashed(self) -> None:
        policy.verify_android_toolchain()
        properties = policy.read_text(policy.ANDROID_WRAPPER_PROPERTIES)
        self.assertIn(policy.EXPECTED_GRADLE_DISTRIBUTION, properties)
        self.assertIn(
            f"distributionSha256Sum={policy.EXPECTED_GRADLE_DISTRIBUTION_SHA256}",
            properties,
        )
        self.assertEqual(policy.sha256(policy.ANDROID_WRAPPER_JAR), policy.EXPECTED_GRADLE_WRAPPER_JAR_SHA256)

    def test_windows_deps_runtime_packs_are_extracted_from_safe_zip(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive_path = root / "MyProxy-windows-self-contained-win-x64-v0.1.0.zip"
            payload = {
                "targets": {
                    ".NETCoreApp,Version=v8.0/win-x64": {
                        "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/8.0.15": {},
                        "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/8.0.15": {},
                    }
                }
            }
            with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("MyProxy.deps.json", json.dumps(payload))
            components = policy.windows_runtime_components(root)
            self.assertEqual(
                {component["name"]: component["version"] for component in components},
                {
                    "Microsoft.NETCore.App.Runtime.win-x64": "8.0.15",
                    "Microsoft.WindowsDesktop.App.Runtime.win-x64": "8.0.15",
                },
            )
            self.assertEqual(
                {component["purl"] for component in components},
                {
                    "pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@8.0.15",
                    "pkg:nuget/Microsoft.WindowsDesktop.App.Runtime.win-x64@8.0.15",
                },
            )
            with zipfile.ZipFile(archive_path, "a") as archive:
                archive.writestr("nested/MyProxy.deps.json", json.dumps(payload))
            with self.assertRaises(policy.ReleaseVerificationError):
                policy.windows_runtime_components(root)

    def test_tag_requires_exact_version_and_matches_sources(self) -> None:
        self.assertEqual(policy.tag_version("refs/tags/v1.2.3"), "1.2.3")
        self.assertEqual(policy.verify_tag("v0.1.0"), "0.1.0")
        with self.assertRaises(policy.ReleaseVerificationError):
            policy.tag_version("v0.1.0-server")
        with self.assertRaises(policy.ReleaseVerificationError):
            policy.verify_tag("v0.1.1")

    def test_vendor_manifest_rejects_tampering(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "VERSION.txt"
            manifest.write_text(
                "libv2ray.aar\n  SHA-256: " + "a" * 64 + "\n"
                "libv2ray-sources.jar\n  SHA-256: " + "b" * 64 + "\n",
                encoding="utf-8",
            )
            self.assertEqual(policy.parse_vendor_manifest(manifest)["libv2ray.aar"], "a" * 64)
            manifest.write_text("libv2ray.aar\n  SHA-256: not-a-hash\n", encoding="utf-8")
            with self.assertRaises(policy.ReleaseVerificationError):
                policy.parse_vendor_manifest(manifest)

    def test_checksums_and_sbom_are_deterministic_and_exclude_manifests(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "b.txt").write_text("b", encoding="utf-8")
            (root / "nested").mkdir()
            (root / "nested" / "a.txt").write_text("a", encoding="utf-8")
            checksum = policy.write_checksums(root)
            lines = checksum.read_text(encoding="utf-8").splitlines()
            self.assertEqual([line.split("  ", 1)[1] for line in lines], ["b.txt", "nested/a.txt"])
            bom = policy.write_sbom(root, "0.1.0")
            policy.write_checksums(root)
            checksums = (root / "SHA256SUMS.txt").read_text(encoding="utf-8")
            self.assertIn("sbom.cdx.json", checksums)
            document = json.loads(bom.read_text(encoding="utf-8"))
            self.assertEqual(document["bomFormat"], "CycloneDX")
            self.assertEqual(document["specVersion"], "1.5")
            self.assertRegex(
                document["serialNumber"],
                r"^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            )
            names = {item["name"] for item in document["components"]}
            self.assertIn("nested/a.txt", names)
            self.assertNotIn("SHA256SUMS.txt", names)
            self.assertNotIn("Microsoft.NET.Test.Sdk", names)
            self.assertNotIn("junit", names)
            self.assertNotIn("ui-tooling", names)
            self.assertIn("okhttp", names)

            # Formal SBOMs describe shipped dependencies, not CI-only test or
            # debug tooling from the shared catalogs/projects.


if __name__ == "__main__":
    unittest.main()
