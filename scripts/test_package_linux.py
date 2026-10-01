"""Tests for the Linux archive layout that the client updater has to accept.

    python -m unittest scripts.test_package_linux -v

The archive is the one place where file modes reach the user (a Windows-host
build has no execute bit on disk), so the modes are asserted on the tar
entries themselves, and the independent artifact verifier must agree.
"""

from __future__ import annotations

import shutil
import sys
import tarfile
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import package_linux  # noqa: E402
import verify_linux_artifact  # noqa: E402

SCRATCH = Path(__file__).resolve().parents[1] / "dist" / "test-package-linux"


class BuildArchiveTest(unittest.TestCase):
    def setUp(self) -> None:
        # Keep generated fixtures inside the ignored build-output tree.
        self.root = SCRATCH / uuid.uuid4().hex
        self.tree = self.root / "MyProxy-linux-x64"
        (self.tree / "Core").mkdir(parents=True)
        for relative in ("myproxy", "myproxy-gui", "Core/xray", "Core/VERSION.txt", "myproxy.dll"):
            (self.tree / relative).write_bytes(relative.encode("ascii"))
        self.archive = self.root / "MyProxy-linux-x64.tar.gz"

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def test_every_launched_program_is_executable_in_the_archive(self) -> None:
        package_linux.build_archive(self.tree, self.archive)

        with tarfile.open(self.archive, "r:gz") as bundle:
            modes = {member.name: member.mode for member in bundle.getmembers()}

        # The desktop entry launches myproxy-gui: shipping it 0644 means the
        # tray program cannot be started after `tar -xzf`.
        for relative in ("myproxy", "myproxy-gui", "Core/xray"):
            self.assertEqual(modes[f"MyProxy-linux-x64/{relative}"], 0o755, relative)
        for relative in ("Core/VERSION.txt", "myproxy.dll"):
            self.assertEqual(modes[f"MyProxy-linux-x64/{relative}"], 0o644, relative)

    def test_archive_contains_all_public_license_materials(self) -> None:
        package_linux.copy_public_license_materials(self.tree)
        package_linux.build_archive(self.tree, self.archive)

        with tarfile.open(self.archive, "r:gz") as bundle:
            names = {member.name for member in bundle.getmembers()}

        required = {
            f"MyProxy-linux-x64/{relative}"
            for relative in package_linux.release_verify.PUBLIC_RELEASE_MATERIALS
        }
        required.update(
            f"MyProxy-linux-x64/{path.relative_to(package_linux.ROOT).as_posix()}"
            for path in (package_linux.ROOT / "licenses").rglob("*")
            if path.is_file()
        )
        self.assertLessEqual(required, names)

    def test_the_independent_verifier_accepts_the_archive(self) -> None:
        package_linux.build_archive(self.tree, self.archive)
        package_linux.write_checksums(self.tree, self.archive)

        verify_linux_artifact.verify_archive(self.archive, "MyProxy-linux-x64")

    def test_the_verifier_rejects_a_non_executable_tray_program(self) -> None:
        package_linux.build_archive(self.tree, self.archive)
        package_linux.write_checksums(self.tree, self.archive)

        rewritten = self.root / "rewritten.tar.gz"
        with tarfile.open(self.archive, "r:gz") as source, tarfile.open(rewritten, "w:gz") as target:
            for member in source.getmembers():
                data = source.extractfile(member)
                if member.name.endswith("/myproxy-gui"):
                    member.mode = 0o644
                target.addfile(member, data)
        rewritten.replace(self.archive)
        package_linux.write_checksums(self.tree, self.archive)

        with self.assertRaises(verify_linux_artifact.ArtifactError):
            verify_linux_artifact.verify_archive(self.archive, "MyProxy-linux-x64")


if __name__ == "__main__":
    unittest.main()
