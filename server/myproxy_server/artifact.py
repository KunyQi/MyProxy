"""Bounded release artifact storage. No private keys or signing operations.

Only a verified manifest supplies the destination, size and digest. Temporary
files remain private and become downloadable only after the registry publishes
their release. Linux signatures cover the raw tarball, as the updater expects.
"""

import base64
import hashlib
import os
from pathlib import Path
import posixpath
import re
import stat
import tempfile
from urllib.parse import unquote, urlsplit

from . import release

CHUNK_BYTES = 1024 * 1024
EXTENSIONS = {"windows": "zip", "android": "apk", "linux": "tar.gz"}
_DIGEST_RE = re.compile(r"[0-9a-f]{64}\Z")
_VERSION_RE = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\Z")


class ArtifactError(Exception):
    def __init__(self, code: str, status: int):
        super().__init__(code)
        self.code, self.status = code, status


def filename(record: dict) -> str:
    platform, version = record.get("platform"), record.get("version", "")
    if platform not in EXTENSIONS or not isinstance(version, str) or not _VERSION_RE.fullmatch(version):
        raise ArtifactError("ManifestRejected", 400)
    return f"myproxy-{platform}-{version}.{EXTENSIONS[platform]}"


def verify_file_signature(path: Path, public_key: bytes, signature: bytes) -> bool:
    """Ed25519 verification with the message hash streamed from disk.

    Identical equation and encoding checks to release.ed25519_verify, without
    allocating a tarball-sized byte string to compute SHA512(R || A || M).
    """
    if len(public_key) != 32 or len(signature) != 64:
        return False
    point_a = release._point_decompress(public_key)
    point_r = release._point_decompress(signature[:32])
    s = int.from_bytes(signature[32:], "little")
    if point_a is None or point_r is None or s >= release._L:
        return False
    digest = hashlib.sha512(signature[:32] + public_key)
    try:
        with _open_regular(path) as stream:
            for chunk in iter(lambda: stream.read(CHUNK_BYTES), b""):
                digest.update(chunk)
    except (OSError, ArtifactError):
        return False
    k = int.from_bytes(digest.digest(), "little") % release._L
    return release._point_equal(
        release._point_mul(s, release._BASE),
        release._point_add(point_r, release._point_mul(k, point_a)),
    )


def _open_regular(path: Path):
    # O_NOFOLLOW protects the final component on Linux; the explicit lstat
    # check also keeps local Windows development from following symlinks.
    if not stat.S_ISREG(path.lstat().st_mode):
        raise ArtifactError("NotFound", 404)
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0))
    if not stat.S_ISREG(os.fstat(fd).st_mode):
        os.close(fd)
        raise ArtifactError("NotFound", 404)
    return os.fdopen(fd, "rb")


class ArtifactStore:
    def __init__(self, settings):
        self.root = Path(settings.release_artifact_dir or (Path(settings.db_path).parent / "releases"))
        origin = settings.release_artifact_origin or settings.api_base_url
        self.base_url = origin.rstrip("/") + "/client/releases"
        self.max_bytes = settings.release_artifact_max_bytes

    def url(self, record: dict) -> str:
        digest = record.get("artifactSha256", "")
        if not isinstance(digest, str) or not _DIGEST_RE.fullmatch(digest):
            raise ArtifactError("ManifestRejected", 400)
        return f"{self.base_url}/{digest}/{filename(record)}"

    def is_local(self, record: dict) -> bool:
        return record.get("artifactUrl") == self.url(record)

    def uses_local_namespace(self, record: dict) -> bool:
        # Host case, an explicit default port or escaped path components do
        # not turn a URL on our managed endpoint into external hosting. Such
        # aliases must be rejected unless the signed URL is exactly canonical.
        actual, expected = urlsplit(str(record.get("artifactUrl", ""))), urlsplit(self.base_url)
        try:
            same_origin = (
                actual.scheme.lower(), (actual.hostname or "").lower(), actual.port or 443
            ) == (
                expected.scheme.lower(), (expected.hostname or "").lower(), expected.port or 443
            )
        except ValueError:
            return False
        decoded = unquote(actual.path)
        return same_origin and any(
            path == expected.path or path.startswith(expected.path + "/")
            for path in (decoded, posixpath.normpath(decoded))
        )

    def _path(self, record: dict, create: bool = False) -> Path:
        if not self.is_local(record):
            raise ArtifactError("Conflict", 409)
        folder = self.root / record["artifactSha256"]
        if create:
            self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
            folder.mkdir(exist_ok=True, mode=0o700)
        # Only controlled digest/filename components enter the filesystem.
        # Reject child symlinks even when their target happens to exist.
        if folder.exists() or folder.is_symlink():
            if not stat.S_ISDIR(folder.lstat().st_mode):
                raise ArtifactError("Conflict", 409)
        if folder.resolve().parent != self.root.resolve():
            raise ArtifactError("Conflict", 409)
        return folder / filename(record)

    def ready(self, record: dict) -> dict:
        local = self.is_local(record)
        ready = signature_ready = False
        if local:
            try:
                path = self._path(record)
                ready = stat.S_ISREG(path.lstat().st_mode) and path.stat().st_size == record["artifactSize"]
                sig_path = Path(str(path) + ".sig")
                signature_ready = ready and (record["platform"] != "linux" or (
                    stat.S_ISREG(sig_path.lstat().st_mode) and sig_path.stat().st_size == 89
                ))
            except (OSError, ArtifactError):
                pass
        return {"artifactManaged": local, "artifactReady": ready, "platformSignatureReady": signature_ready}

    @staticmethod
    def _matches(path: Path, record: dict) -> bool:
        try:
            with _open_regular(path) as stream:
                if os.fstat(stream.fileno()).st_size != record["artifactSize"]:
                    return False
                digest = hashlib.sha256()
                for chunk in iter(lambda: stream.read(CHUNK_BYTES), b""):
                    digest.update(chunk)
                return digest.hexdigest() == record["artifactSha256"]
        except (OSError, ArtifactError):
            return False

    def upload(self, record: dict, stream, length: int) -> None:
        if record["status"] != "draft" or self.max_bytes <= 0:
            raise ArtifactError("Conflict", 409)
        if length != record["artifactSize"] or length <= 0 or length > self.max_bytes:
            raise ArtifactError("ManifestRejected", 400)
        path = self._path(record, create=True)
        fd, temp_name = tempfile.mkstemp(prefix=".upload-", dir=path.parent)
        try:
            digest = hashlib.sha256()
            with os.fdopen(fd, "wb") as output:
                remaining = length
                while remaining:
                    chunk = stream.read(min(remaining, CHUNK_BYTES))
                    if not chunk:
                        raise ArtifactError("BadRequest", 400)
                    if len(chunk) > remaining:
                        raise ArtifactError("BadRequest", 400)
                    output.write(chunk)
                    digest.update(chunk)
                    remaining -= len(chunk)
                output.flush()
                os.fsync(output.fileno())
            if digest.hexdigest() != record["artifactSha256"]:
                raise ArtifactError("ManifestRejected", 400)
            try:
                # Hard-link creation is atomic and refuses to overwrite even
                # an existing symlink. A matching draft upload is idempotent.
                os.link(temp_name, path)
            except FileExistsError:
                if not self._matches(path, record):
                    raise ArtifactError("Conflict", 409)
        finally:
            try:
                os.unlink(temp_name)
            except FileNotFoundError:
                pass

    def _linux_key(self, record: dict, keys: dict[str, bytes]) -> bytes:
        key = keys.get(record["signingKeyId"], b"")
        if record["platform"] != "linux" or record["platformSignatureType"] != "ed25519" or (
            len(key) != 32 or hashlib.sha256(key).hexdigest() != record["platformSignatureSubjectSha256"]
        ):
            raise ArtifactError("ManifestRejected", 400)
        return key

    def upload_signature(self, record: dict, signature: bytes, keys: dict[str, bytes]) -> None:
        if record["status"] != "draft" or self.max_bytes <= 0:
            raise ArtifactError("Conflict", 409)
        path = self._path(record)
        key = self._linux_key(record, keys)
        if not self._matches(path, record):
            raise ArtifactError("Conflict", 409)
        if not verify_file_signature(path, key, signature):
            raise ArtifactError("ManifestRejected", 400)
        sig_path = Path(str(path) + ".sig")
        encoded = base64.b64encode(signature) + b"\n"
        fd, temp_name = tempfile.mkstemp(prefix=".signature-", dir=path.parent)
        try:
            with os.fdopen(fd, "wb") as output:
                output.write(encoded)
                output.flush()
                os.fsync(output.fileno())
            try:
                os.link(temp_name, sig_path)
            except FileExistsError:
                try:
                    with _open_regular(sig_path) as current:
                        if current.read(90) != encoded:
                            raise ArtifactError("Conflict", 409)
                except OSError:
                    raise ArtifactError("Conflict", 409)
        finally:
            os.unlink(temp_name)

    def require_publishable(self, record: dict, keys: dict[str, bytes]) -> None:
        if not self.is_local(record):
            # External/manual hosting remains compatible. A malformed URL
            # claiming our managed namespace must never bypass upload checks.
            if self.uses_local_namespace(record):
                raise ArtifactError("ManifestRejected", 400)
            return
        path = self._path(record)
        if not self._matches(path, record):
            raise ArtifactError("Conflict", 409)
        if record["platform"] == "linux":
            key = self._linux_key(record, keys)
            try:
                with _open_regular(Path(str(path) + ".sig")) as stream:
                    signature = base64.b64decode(stream.read(90).strip(), validate=True)
            except (OSError, ValueError, ArtifactError):
                raise ArtifactError("Conflict", 409)
            if not verify_file_signature(path, key, signature):
                raise ArtifactError("Conflict", 409)

    def open_download(self, record: dict, basename: str):
        expected = filename(record)
        if record["status"] != "published" or basename not in (
            expected, expected + ".sig" if record["platform"] == "linux" else expected
        ):
            raise ArtifactError("NotFound", 404)
        path = self._path(record)
        if basename.endswith(".sig"):
            path = Path(str(path) + ".sig")
        try:
            stream = _open_regular(path)
        except (OSError, ArtifactError):
            raise ArtifactError("NotFound", 404)
        size = os.fstat(stream.fileno()).st_size
        if size != (89 if basename.endswith(".sig") else record["artifactSize"]):
            stream.close()
            raise ArtifactError("NotFound", 404)
        return stream, size
