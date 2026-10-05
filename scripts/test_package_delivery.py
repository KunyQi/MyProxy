"""Pure tests for release packaging policy (no publish and no delivery output)."""

from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import subprocess
from pathlib import Path
from unittest.mock import patch

import package_delivery as packaging


class PackagePolicyTests(unittest.TestCase):

    def test_dotnet_resolution_uses_sdk_selected_by_global_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dotnet = root / "dotnet"
            dotnet.touch()
            (root / "global.json").write_text('{"sdk":{"version":"9.0.318"}}', encoding="utf-8")
            with patch.object(packaging, "ROOT", root), \
                    patch.object(packaging.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "9.0.318\n", "")) as run:
                self.assertEqual(packaging.resolve_dotnet(str(dotnet)), dotnet)
                self.assertEqual(run.call_args.args[0], [str(dotnet), "--version"])
                self.assertEqual(run.call_args.kwargs["cwd"], root)

    def test_dotnet_resolution_rejects_sdk_that_does_not_match_global_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dotnet = root / "dotnet"
            dotnet.touch()
            (root / "global.json").write_text('{"sdk":{"version":"9.0.318"}}', encoding="utf-8")
            with patch.object(packaging, "ROOT", root), \
                    patch.object(packaging.shutil, "which", return_value=None), \
                    patch.dict(packaging.os.environ, {}, clear=True), \
                    patch.object(packaging.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "8.0.424\n", "")):
                with self.assertRaisesRegex(packaging.PreflightError, "9.0.318 from global.json"):
                    packaging.resolve_dotnet(str(dotnet))

    def test_packaging_preflight_rejects_unconfigured_example_before_build(self) -> None:
        with patch.object(packaging, "release_version", return_value="0.1.0"), \
                patch.object(packaging.release_verify, "verify_deployment_config", side_effect=packaging.release_verify.ReleaseVerificationError("replace example origin")), \
                patch.object(packaging, "git_revision") as git_revision:
            with self.assertRaisesRegex(packaging.PreflightError, "replace example"):
                packaging.preflight(allow_dirty=False, dotnet=None, require_sdk=True)
            git_revision.assert_not_called()

    def test_release_docs_use_only_public_allowlist(self) -> None:
        names = {p.relative_to(packaging.ROOT).as_posix() for p in packaging.release_doc_sources()}
        expected = {
            "README.md", "docs/architecture.md", "docs/deployment.md", "docs/third-party.md",
            "server/README.md", "server/docs/openapi.yaml",
            *packaging.release_verify.PUBLIC_RELEASE_MATERIALS,
        }
        expected.update(
            path.relative_to(packaging.ROOT).as_posix()
            for path in (packaging.ROOT / "licenses").rglob("*")
            if path.is_file()
        )
        self.assertEqual(names, expected)
        self.assertFalse(any(name.startswith(".github/") for name in names))

    def test_release_docs_copy_license_materials_and_all_license_files(self) -> None:
        sources = packaging.release_doc_sources()
        expected = {path.relative_to(packaging.ROOT).as_posix() for path in sources}
        with tempfile.TemporaryDirectory() as directory:
            stage = Path(directory) / "delivery"

            def blob(source: Path, revision: str) -> bytes:
                return f"source={source.relative_to(packaging.ROOT).as_posix()} revision={revision}\n".encode()

            with patch.object(packaging, "head_blob", side_effect=blob):
                packaging.copy_release_docs(stage, "test-revision")

            copied = {
                path.relative_to(stage).as_posix()
                for path in stage.rglob("*")
                if path.is_file()
            }
            self.assertEqual(copied, expected)
            for relative in expected:
                self.assertIn(relative, (stage / Path(relative)).read_text(encoding="utf-8"))

    def test_build_and_standalone_server_get_the_same_config(self) -> None:
        data = b'{"api_base_url":"https://api.example.org:8443"}\n'
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            targets = (root / "source" / "deployment.json", root / "build-source" / "deployment.json", root / "server" / "deployment.json")
            with patch.object(packaging, "head_blob", return_value=data):
                for target in targets:
                    packaging.copy_deployment_config(target, "revision")
            self.assertEqual([target.read_bytes() for target in targets], [data] * 3)
            with patch.object(packaging, "head_blob", return_value=b'{"api_base_url":"https://api.example.invalid"}'):
                with self.assertRaises(packaging.PreflightError):
                    packaging.copy_deployment_config(root / "rejected" / "deployment.json")
            self.assertFalse((root / "rejected").exists())

    def test_delivery_build_keeps_relative_configuration_resource_available(self) -> None:
        config = b'{"api_base_url":"https://api.example.org:8443"}\n'
        project_text = packaging.PROJECT_SOURCE.read_text(encoding="utf-8")

        def copy_source(source, destination, revision):
            destination.mkdir(parents=True, exist_ok=True)
            if source.name == "MyProxy":
                (destination / "MyProxy.csproj").write_text(project_text, encoding="utf-8")

        def publish(command, **kwargs):
            project = Path(command[2])
            self.assertEqual(project.parent.parent.name, "windows")
            embedded = project.parent.parent.parent / "deployment.json"
            self.assertEqual(embedded.read_bytes(), config)
            self.assertIn("deployment.json", project.read_text(encoding="utf-8"))
            output = Path(command[command.index("-o") + 1])
            for name in packaging.EXPECTED_PUBLISH_FILES:
                path = output / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"build-output")

        with tempfile.TemporaryDirectory() as directory:
            delivery = Path(directory) / "delivery"
            with patch.object(packaging, "DELIVERY", delivery), \
                    patch.object(packaging, "assert_release_snapshot"), \
                    patch.object(packaging, "copy_tree_tracked", side_effect=copy_source), \
                    patch.object(packaging, "copy_release_docs"), \
                    patch.object(packaging, "head_blob", return_value=config), \
                    patch.object(packaging, "run", side_effect=publish), \
                    patch.object(packaging, "validate_runtime_config"), \
                    patch.object(packaging, "core_hashes", return_value={"xray.exe":"a" * 64}), \
                    patch.object(packaging, "validate_core_hashes"), \
                    patch.object(packaging, "signing_status", return_value="NotSigned"):
                stage, archive, sidecar = packaging.output_paths("0.1.0")
                packaging.build("0.1.0", "revision", Path("dotnet"), stage, archive, sidecar)
            for name in ("deployment.json", "server/deployment.json", "source/deployment.json"):
                self.assertEqual((stage / name).read_bytes(), config)
            self.assertTrue((stage / "source/windows/MyProxy/MyProxy.csproj").is_file())
            self.assertTrue(archive.is_file())

    def test_version_parser_requires_semver(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "version.txt"
            path.write_text('public const string Version = "1.2.3";', encoding="utf-8")
            self.assertEqual(packaging.version_from(path, packaging.VERSION_PATTERN), "1.2.3")
            path.write_text('public const string Version = "latest";', encoding="utf-8")
            with self.assertRaises(packaging.PreflightError):
                packaging.version_from(path, packaging.VERSION_PATTERN)

    def test_zip_metadata_and_order_are_reproducible(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            stage = root / "MyProxy-v0.1.0"
            stage.mkdir()
            (stage / "b.txt").write_text("b", encoding="utf-8")
            (stage / "a.txt").write_text("a", encoding="utf-8")
            first = root / "first.zip"
            second = root / "second.zip"
            packaging.create_reproducible_zip(stage, first)
            packaging.create_reproducible_zip(stage, second)
            digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(digest(first), digest(second))

    def test_tracked_copy_excludes_untracked_sensitive_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "server"
            source.mkdir()
            tracked = source / "README.md"
            tracked.write_text("tracked", encoding="utf-8")
            (source / "credentials.json").write_text("secret", encoding="utf-8")
            with patch.object(packaging, "tracked_files", return_value=[tracked]), \
                    patch.object(packaging, "head_blob", return_value=b"tracked"):
                packaging.copy_tree_tracked(source, root / "destination")
            self.assertTrue((root / "destination" / "README.md").is_file())
            self.assertFalse((root / "destination" / "credentials.json").exists())

    def test_tracked_copy_uses_exact_revision_blob(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "server"
            source.mkdir()
            tracked = source / "README.md"
            tracked.write_text("worktree", encoding="utf-8")
            with patch.object(packaging, "tracked_files", return_value=[tracked]), \
                    patch.object(packaging, "head_blob", return_value=b"HEAD-BLOB") as blob:
                packaging.copy_tree_tracked(source, root / "destination", "deadbeef")
            blob.assert_called_once_with(tracked, "deadbeef")
            self.assertEqual((root / "destination" / "README.md").read_bytes(), b"HEAD-BLOB")

    def test_revision_tree_does_not_require_deleted_worktree_file(self) -> None:
        deleted = packaging.ROOT / "server" / "deleted-from-worktree.py"
        entry = b"100644 blob " + (b"a" * 40) + b"\tserver/deleted-from-worktree.py\0"
        completed = subprocess.CompletedProcess([], 0, stdout=entry, stderr=b"")
        with patch.object(packaging.subprocess, "run", return_value=completed):
            self.assertEqual(
                packaging.tracked_files(packaging.ROOT / "server", "deadbeef"),
                [deleted],
            )

    def test_tracked_copy_rejects_crlf_linux_deploy_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "server"
            source.mkdir()
            deploy = source / "deploy.sh"
            deploy.write_text("#!/bin/sh\n", encoding="utf-8")
            with patch.object(packaging, "tracked_files", return_value=[deploy]), \
                    patch.object(packaging, "head_blob", return_value=b"#!/bin/sh\r\n"):
                with self.assertRaises(packaging.PreflightError):
                    packaging.copy_tree_tracked(source, root / "destination")

    def test_forbidden_suffixes_are_policy_enforced(self) -> None:
        self.assertTrue(packaging.is_ignored(Path("server/data/myproxy.db")))
        self.assertTrue(packaging.is_ignored(Path("Server/Secrets/Pairing_Code.txt")))
        self.assertTrue(packaging.is_ignored(Path("windows/BIN/MyProxy.exe")))
        self.assertTrue(packaging.is_ignored(Path("windows/TESTRESULTS/output.xml")))
        self.assertTrue(packaging.is_ignored(Path("server/.env.production")))
        self.assertTrue(packaging.is_ignored(Path("server/id_rsa")))
        self.assertTrue(packaging.is_ignored(Path("server/admin.env")))
        self.assertTrue(packaging.is_ignored(Path("server/tls/private.key")))
        self.assertFalse(packaging.is_ignored(Path("server/myproxy_server/app.py")))

    def test_existing_delivery_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            existing = root / "MyProxy-v0.1.0.zip"
            existing.write_bytes(b"old")
            with self.assertRaises(packaging.PreflightError):
                packaging.ensure_outputs_available(root / "stage", existing, root / "sidecar")

    def test_core_hash_manifest_rejects_tampering(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            core = Path(directory) / "Core"
            core.mkdir()
            asset = core / "xray.exe"
            asset.write_bytes(b"xray")
            (core / "geoip.dat").write_bytes(b"geoip")
            (core / "geosite.dat").write_bytes(b"geosite")
            hashes = {name: packaging.sha256(core / name)
                      for name in ("xray.exe", "geoip.dat", "geosite.dat")}
            (core / "VERSION.txt").write_text(
                "\n".join(f"{name}={digest}" for name, digest in hashes.items()) + "\n",
                encoding="utf-8",
            )
            packaging.validate_core_hashes(core)
            asset.write_bytes(b"tampered")
            with self.assertRaises(packaging.PreflightError):
                packaging.validate_core_hashes(core)

    def test_runtime_config_requires_desktop8_and_safe_binaryformatter(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "runtimeconfig.json"
            document = {
                "runtimeOptions": {
                    "frameworks": [
                        {"name": "Microsoft.NETCore.App", "version": "8.0.0"},
                        {"name": "Microsoft.WindowsDesktop.App", "version": "8.0.0"},
                    ],
                    "configProperties": {
                        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": False
                    },
                }
            }
            path.write_text(json.dumps(document), encoding="utf-8")
            packaging.validate_runtime_config(path)
            document["runtimeOptions"]["configProperties"][
                "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"
            ] = True
            path.write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaises(packaging.PreflightError):
                packaging.validate_runtime_config(path)
            document["runtimeOptions"]["configProperties"] = []
            path.write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaises(packaging.PreflightError):
                packaging.validate_runtime_config(path)

    def test_secret_scanner_rejects_high_confidence_material(self) -> None:
        key_name = "api" + "Key"
        key_value = "0123456789abcdefghijklmnopqrstuvwxyz"
        with self.assertRaises(packaging.PreflightError):
            packaging.scan_secret_blob(
                Path("server/config.json"),
                ('{"' + key_name + '":"' + key_value + '"}').encode(),
            )
        pem = "-----BEGIN " + "PRIVATE KEY-----\nmaterial\n-----END " + "PRIVATE KEY-----"
        with self.assertRaises(packaging.PreflightError):
            packaging.scan_secret_blob(
                Path("server/tls/key.txt"),
                pem.encode(),
            )

    def test_secret_scanner_allows_documentary_placeholders(self) -> None:
        packaging.scan_secret_blob(
            Path("docs/example.md"),
            b'Example config: {"privateKey":"", "password":"<replace-me>"}',
        )

    def test_packaging_sources_are_safe_to_scan(self) -> None:
        for name in ("package_delivery.py", "test_package_delivery.py"):
            path = Path(__file__).with_name(name)
            packaging.scan_secret_blob(path, path.read_bytes())

    def test_release_source_text_inputs_are_safe_to_scan(self) -> None:
        paths: set[Path] = set(packaging.release_doc_sources())
        for source in (
            packaging.ROOT / "windows" / "MyProxy",
            packaging.ROOT / "server",
            packaging.ROOT / "scripts",
        ):
            paths.update(path for path in source.rglob("*") if path.is_file()
                         and not packaging.is_ignored(path.relative_to(packaging.ROOT)))
        paths.add(Path(__file__))
        for path in sorted(paths):
            if path.is_file() and path.suffix.lower() in packaging.SECRET_SCAN_SUFFIXES:
                packaging.scan_secret_blob(path, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
