"""Run the shipped browser helper with native Node WebCrypto, then Python verification.

Keys are random test-only keys held in process memory. Nothing is published and
private material is never written to a file or included in test output.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import subprocess
import unittest

from myproxy_server import release
from myproxy_server.release_ui import RELEASE_SIGNING_JS


_EXERCISE = r"""
import {webcrypto} from 'node:crypto';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const digestLengths = [];
const nativeSubtle = webcrypto.subtle;
const subtle = new Proxy(nativeSubtle, {get(target, name) {
  if (name === 'digest') return (algorithm, bytes) => {
    digestLengths.push(bytes.byteLength);
    return nativeSubtle.digest(algorithm, bytes);
  };
  const value = target[name];
  return typeof value === 'function' ? value.bind(target) : value;
}});
const unavailable = () => { throw new Error('Network, storage or logging was accessed'); };
const window = {
  crypto: {subtle}, isSecureContext: true,
  atob: value => Buffer.from(value, 'base64').toString('binary'),
  btoa: value => Buffer.from(value, 'binary').toString('base64'),
};
Object.defineProperties(window, {
  localStorage: {get: unavailable}, sessionStorage: {get: unavailable},
  indexedDB: {get: unavailable}, fetch: {value: unavailable},
});
const sandbox = {window, TextEncoder, TextDecoder, Uint8Array, ArrayBuffer, URL, Date,
  console: {log: unavailable, warn: unavailable, error: unavailable}, fetch: unavailable,
};
vm.runInNewContext(SOURCE, sandbox);
const helper = window.MyProxyReleaseSigning;
const pemBegin = '-----BEGIN ' + 'PRIVATE KEY-----';
const pemEnd = '-----END ' + 'PRIVATE KEY-----';
const generated = await helper.generateKey();
assert.match(generated.publicKeyHex, /^[a-f0-9]{64}$/);
assert(generated.privateKeyPem.startsWith(pemBegin + '\n'));
const pemFile = new Blob([generated.privateKeyPem]);
const loaded = await helper.loadKey(pemFile);
assert.equal(loaded.publicKeyHex, generated.publicKeyHex);
const derBytes = await nativeSubtle.exportKey('pkcs8', generated.key);
const loadedDer = await helper.loadKey(new Blob([derBytes]));
assert.equal(loadedDer.publicKeyHex, generated.publicKeyHex);
const artifactBytes = new TextEncoder().encode('non-executable artifact fixture for local signing');
let artifactReads = 0;
const immutableBlob = new Blob([artifactBytes]);
const file = {
  name: '<img src=x onerror=alert(1)>.apk', size: immutableBlob.size,
  arrayBuffer: () => { artifactReads++; return immutableBlob.arrayBuffer(); },
};
const artifactHash = await helper.hashFile(file);
assert.equal(await helper.hashFile(file), artifactHash);
assert.equal(artifactReads, 1);
const args = {
  key: loaded.key, signingKeyId: 'browser-test-key', file,
  platform: 'windows', version: '1.2.3-beta.1+build.9', channel: 'beta', mandatory: true,
  minimumVersion: '1.0.0', subjectSha256: 'AB'.repeat(32),
  artifactUrl: 'https://releases.example.invalid/' + artifactHash + '/myproxy-windows-1.2.3.zip',
};
const valid = [];
for (const platform of ['windows', 'android', 'linux']) {
  const prepared = await helper.prepare({...args, platform});
  assert.equal(prepared.artifactSha256, artifactHash);
  assert.equal(prepared.artifactSize, artifactBytes.length);
  const envelope = JSON.parse(helper.exportEnvelope(prepared));
  assert.deepEqual(Object.keys(envelope), ['manifest', 'signature', 'signingKeyId']);
  assert.equal(envelope.manifest, prepared.manifest);
  assert.equal(envelope.signature, prepared.signature);
  assert.equal(helper.decodeBase64(prepared.signature).length, 64);
  assert(!helper.exportEnvelope(prepared).includes('PRIVATE KEY'));
  assert(!JSON.stringify(prepared.manifestDocument).includes(file.name));
  if (platform === 'linux') assert.equal(helper.decodeBase64(prepared.artifactSignature).length, 64);
  else assert.equal(prepared.artifactSignature, undefined);
  valid.push(prepared);
}
assert.equal(artifactReads, 2, 'cached Windows/Android preparation must not read again; Linux needs raw bytes');
assert.equal(digestLengths.filter(length => length === artifactBytes.length).length, 1);
const negatives = {};
async function rejects(name, action) {
  try { await action(); negatives[name] = false; }
  catch (_) { negatives[name] = true; }
}
const mutations = {
  'wrong-platform': {platform: 'darwin'}, 'prototype-platform': {platform: '__proto__'},
  'wrong-channel': {channel: 'nightly'}, 'invalid-version': {version: '1.2'},
  'invalid-minimum-version': {minimumVersion: 'latest'}, 'invalid-key-id': {signingKeyId: 'bad key'},
  'non-boolean-mandatory': {mandatory: 'true'}, 'invalid-certificate-hash': {subjectSha256: 'ab'},
  'empty-file': {file: new Blob([])}, 'invalid-key': {key: null},
  'http-url': {artifactUrl: 'http://example.invalid/a.zip'},
  'script-url': {artifactUrl: 'javascript:alert(1)'},
  'credentials-url': {artifactUrl: 'https://user:password@example.invalid/a.zip'},
  'backslash-url': {artifactUrl: 'https:\\\\example.invalid/a.zip'},
  'control-url': {artifactUrl: 'https://example.invalid/a\n.zip'},
  'oversize-manifest': {artifactUrl: 'https://example.invalid/' + 'x'.repeat(9000)},
};
for (const [name, mutation] of Object.entries(mutations)) await rejects(name, () => helper.prepare({...args, ...mutation}));
await rejects('malformed-pem', () => helper.loadKey(new Blob([pemBegin + '\nnot-base64\n' + pemEnd])));
await rejects('malformed-der', () => helper.loadKey(new Blob([new Uint8Array([1, 2, 3])])));
await rejects('oversize-key', () => helper.loadKey(new Blob([new Uint8Array(16385)])));
await rejects('public-key-file', async () => helper.loadKey(new Blob([await nativeSubtle.exportKey('spki',
  await nativeSubtle.importKey('raw', Buffer.from(generated.publicKeyHex, 'hex'), 'Ed25519', true, ['verify']))])));
const wrongAlgorithm = await nativeSubtle.generateKey({name: 'ECDSA', namedCurve: 'P-256'}, true, ['sign', 'verify']);
await rejects('wrong-key-algorithm', () => helper.prepare({...args, key: wrongAlgorithm.privateKey}));
window.isSecureContext = false;
await rejects('insecure-context', () => helper.generateKey());
window.isSecureContext = true;
window.crypto = {subtle: {generateKey() { const error = new Error('unsupported'); error.name = 'NotSupportedError'; throw error; }}};
await rejects('unsupported-ed25519', () => helper.generateKey());
window.crypto = {subtle};
const other = await helper.generateKey();
process.stdout.write(JSON.stringify({
  publicKeyHex: generated.publicKeyHex, otherPublicKeyHex: other.publicKeyHex,
  artifactBase64: Buffer.from(artifactBytes).toString('base64'), artifactHash,
  valid, negatives, cacheReads: artifactReads, artifactDigestCount: digestLengths.filter(length => length === artifactBytes.length).length,
}));
"""


class ReleaseUiSigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        node = os.environ.get("MYPROXY_TEST_NODE") or shutil.which("node")
        if not node:
            raise unittest.SkipTest("Node is required for native WebCrypto integration")
        source = "const SOURCE = " + json.dumps(RELEASE_SIGNING_JS.decode("utf-8")) + ";\n" + _EXERCISE
        completed = subprocess.run(
            [node, "--input-type=module", "-"], input=source,
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
        if completed.returncode:
            raise AssertionError("Browser signing integration failed: " + completed.stderr)
        cls.results = json.loads(completed.stdout)
        cls.public_key = bytes.fromhex(cls.results["publicKeyHex"])
        cls.keys = {"browser-test-key": cls.public_key}
        cls.artifact = base64.b64decode(cls.results["artifactBase64"])

    def test_generated_platform_manifests_pass_production_verifier(self) -> None:
        for prepared in self.results["valid"]:
            with self.subTest(platform=prepared["manifestDocument"]["platform"]):
                summary, doc = release.verify_and_parse(
                    prepared["manifest"], prepared["signature"], prepared["signingKeyId"], self.keys,
                )
                self.assertEqual(doc, prepared["manifestDocument"])
                self.assertEqual(summary["artifactSha256"], hashlib.sha256(self.artifact).hexdigest())
                self.assertEqual(summary["artifactSize"], len(self.artifact))
                self.assertEqual(summary["minimumVersion"], "1.0.0")
                self.assertEqual(summary["mandatory"], True)
                self.assertEqual(summary["platformSignatureType"], release.PLATFORM_SIGNATURE_TYPES[doc["platform"]])
                if doc["platform"] != "linux":
                    self.assertEqual(summary["platformSignatureSubjectSha256"], "ab" * 32)

    def test_linux_signature_and_subject_cover_actual_bytes_and_public_key(self) -> None:
        prepared = self.results["valid"][2]
        signature = base64.b64decode(prepared["artifactSignature"], validate=True)
        self.assertEqual(len(signature), 64)
        self.assertTrue(release.ed25519_verify(self.public_key, self.artifact, signature))
        self.assertFalse(release.ed25519_verify(self.public_key, self.artifact + b"changed", signature))
        self.assertEqual(prepared["manifestDocument"]["artifact"]["signature"]["subjectSha256"],
                         hashlib.sha256(self.public_key).hexdigest())

    def test_signature_covers_opaque_json_and_domain_prefix(self) -> None:
        prepared = self.results["valid"][0]
        raw = base64.b64decode(prepared["manifest"], validate=True)
        signature = base64.b64decode(prepared["signature"], validate=True)
        self.assertTrue(release.ed25519_verify(self.public_key, release.SIGNING_DOMAIN + raw, signature))
        self.assertFalse(release.ed25519_verify(self.public_key, raw, signature))
        for tampered in (raw + b" ", raw.replace(b"1.2.3", b"9.9.9", 1)):
            with self.subTest(tampering=tampered[-16:]):
                with self.assertRaises(release.ManifestError):
                    release.verify_and_parse(base64.b64encode(tampered).decode(), prepared["signature"], "browser-test-key", self.keys)

    def test_wrong_and_unknown_keys_are_rejected(self) -> None:
        prepared = self.results["valid"][0]
        wrong_keys = {"browser-test-key": bytes.fromhex(self.results["otherPublicKeyHex"])}
        for key_id, keys in (("browser-test-key", wrong_keys), ("unknown", self.keys), ("browser-test-key", {})):
            with self.subTest(key_id=key_id, configured_keys=len(keys)):
                with self.assertRaises(release.ManifestError):
                    release.verify_and_parse(prepared["manifest"], prepared["signature"], key_id, keys)

    def test_invalid_browser_inputs_fail_closed(self) -> None:
        self.assertGreaterEqual(len(self.results["negatives"]), 23)
        for name, rejected in self.results["negatives"].items():
            with self.subTest(case=name):
                self.assertTrue(rejected)

    def test_artifact_hash_is_reused_and_private_material_is_not_exported(self) -> None:
        self.assertEqual(self.results["artifactDigestCount"], 1)
        self.assertEqual(self.results["cacheReads"], 2)
        self.assertNotIn("PRIVATE KEY", json.dumps(self.results))
        for forbidden in ("fetch(", "localStorage", "sessionStorage", "indexedDB", "console."):
            with self.subTest(capability=forbidden):
                self.assertNotIn(forbidden, RELEASE_SIGNING_JS.decode("utf-8"))


if __name__ == "__main__":
    unittest.main()
