"""Execute the release wizard against a small DOM and a protocol-aware fake API."""

from __future__ import annotations

from html.parser import HTMLParser
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from myproxy_server import admin_ui


class _Ids(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = []
        self.patterns = {}

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if attributes.get("pattern"):
            self.patterns[attributes.get("id")] = attributes["pattern"]
        for name, value in attrs:
            if name == "id":
                self.ids.append(value)


HARNESS = r"""
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const {webcrypto} = require('node:crypto');
const source = fs.readFileSync(process.argv[2], 'utf8');
const ids = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const scenario = process.argv[4];
const nodes = new Map();
const downloads = [], downloadNames = [];
class DownloadURL extends URL {
  static createObjectURL(blob) { downloads.push(blob); return 'blob:release-test'; }
  static revokeObjectURL() {}
}
class DomNode {
  constructor(id = '') {
    this.id = id; this.value = ''; this.textContent = ''; this.hidden = false;
    this.checked = false; this.disabled = false; this.files = []; this.children = [];
    this.options = []; this.dataset = {}; this.listeners = {};
    this.classList = {toggle() {}, add() {}};
  }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this.options = children; }
  setAttribute() {}
  addEventListener(type, listener) { this.listeners[type] = listener; }
  querySelector() { return new DomNode(); }
  reportValidity() { return true; }
  click() { if (this.download) downloadNames.push(this.download); }
  remove() {}
  get selectedOptions() { return this.options.filter(option => option.value === this.value); }
  get elements() { return ids.filter(id => id.startsWith('wizard-')).map(id => node(id)); }
}
function node(id) {
  if (!nodes.has(id)) nodes.set(id, new DomNode(id));
  return nodes.get(id);
}
for (const id of ids) node(id);
function Option(text, value) { this.textContent = text; this.value = value; }
const hash = 'a'.repeat(64), publicHex = 'b'.repeat(64), subject = 'c'.repeat(64);
const file = {name: 'MyProxy.zip', size: 3};
const settings = {
  artifactUploadEnabled: true, artifactBaseUrl: 'https://api.example/client/releases',
  maxArtifactBytes: 536870912, trustedSigningKeys: [{keyId: 'rel-local', publicKeyHex: publicHex}],
  artifactExtensions: {windows: 'zip', android: 'apk', linux: 'tar.gz'},
};
let records = [], calls = [], registrations = 0, fail = '', lostRegistration = false;
let assignments = [];
const helper = {
  async generateKey() { return {key: {private: 'DO-NOT-UPLOAD'}, publicKeyHex: publicHex, privateKeyPem: 'PRIVATE-PEM'}; },
  async loadKey() { return {key: {private: 'DO-NOT-UPLOAD'}, publicKeyHex: publicHex}; },
  async hashFile() { return hash; },
  async prepare(options) {
    const signature = {type: options.platform === 'linux' ? 'ed25519' : options.platform === 'android' ? 'apksigner' : 'authenticode', subjectSha256: subject};
    const document = {
      platform: options.platform, version: options.version, channel: options.channel,
      mandatory: options.mandatory, minimumVersion: options.minimumVersion,
      artifact: {url: options.artifactUrl, sha256: hash, size: file.size, signature},
    };
    return {manifest: Buffer.from(JSON.stringify(document)).toString('base64'), signature: 'SIGNED',
      signingKeyId: options.signingKeyId, manifestDocument: document, artifactSha256: hash,
      artifactSize: file.size, ...(options.platform === 'linux' ? {artifactSignature: Buffer.alloc(64).toString('base64')} : {})};
  },
  decodeBase64(value) { return new Uint8Array(Buffer.from(value, 'base64')); },
  exportEnvelope(prepared) { return JSON.stringify({manifest: prepared.manifest, signature: prepared.signature, signingKeyId: prepared.signingKeyId}); },
};
const response = (payload, status = 200) => ({ok: status < 400, status, async text() { return JSON.stringify(payload); }});
async function fetch(path, options) {
  calls.push({path, ...options});
  assert.equal(options.headers.Authorization, 'Bearer SESSION');
  if (fail && path.endsWith(fail)) { fail = ''; return response({error: {message: 'test failure'}}, 500); }
  if (path.startsWith('/api/admin/release?') || path === '/api/admin/release' && options.method === 'GET') return response({releases: records});
  if (path === '/api/admin/release' && options.method === 'POST') {
    registrations++;
    const envelope = JSON.parse(options.body);
    assert.deepEqual(Object.keys(envelope).sort(), ['manifest', 'note', 'signature', 'signingKeyId']);
    assert.ok(!options.body.includes('DO-NOT-UPLOAD'));
    const document = JSON.parse(Buffer.from(envelope.manifest, 'base64').toString());
    const entry = {id: 'rel_1', platform: document.platform, version: document.version,
      channel: document.channel, mandatory: document.mandatory, minimumVersion: document.minimumVersion,
      artifactSha256: document.artifact.sha256, artifactSize: document.artifact.size,
      artifactUrl: document.artifact.url, platformSignatureSubjectSha256: document.artifact.signature.subjectSha256,
      signingKeyId: envelope.signingKeyId, status: 'draft', artifactReady: false, platformSignatureReady: false};
    records.push(entry);
    if (lostRegistration) { lostRegistration = false; throw new Error('connection lost after commit'); }
    return response(entry);
  }
  if (path.endsWith('/artifact')) {
    assert.equal(records[0].status, 'draft');
    assert.equal(options.headers['Content-Type'], 'application/octet-stream');
    assert.equal(options.body, file);
    records[0].artifactReady = true;
    return response(records[0]);
  }
  if (path.endsWith('/artifact-signature')) {
    assert.equal(records[0].artifactReady, true);
    assert.equal(options.body.byteLength, 64);
    assert.equal(options.headers['Content-Type'], 'application/octet-stream');
    records[0].platformSignatureReady = true;
    return response(records[0]);
  }
  if (path.endsWith('/publish')) { records[0].status = 'published'; return response(records[0]); }
  if (path === '/api/admin/assignment' && options.method === 'POST') {
    assert.equal(records[0].status, 'published');
    const assignment = JSON.parse(options.body);
    assert.equal(assignment.targetId, 'dev_1');
    assert.equal(assignment.releaseId, 'rel_1');
    if (scenario === 'flags-preserved') {
      assert.deepEqual(assignment.featureFlags, {usageCategories: true, disableAutoUpdate: true});
    }
    assignments = [{...assignment, featureFlags: JSON.stringify(assignment.featureFlags)}];
    return response({id: 'assignment_1'});
  }
  const payloads = {
    '/healthz': {ok: true, version: '0.1.0'}, '/api/admin/user': {users: [{id: 'user_1', username: 'Alice', status: 'active'}]},
    '/api/admin/binding': {bindings: []}, '/api/admin/device': {devices: [{id: 'dev_1', deviceName: 'Phone', userId: 'user_1', status: 'active', platform: node('wizard-platform').value}]},
    '/api/admin/config': {}, '/api/admin/latest': {}, '/api/admin/latest/android': {}, '/api/admin/latest/linux': {},
    '/api/admin/assignment': {assignments}, '/api/admin/release-audit?limit=50': {entries: []},
    '/api/admin/activity': {devices: []}, '/api/admin/release-settings': settings,
  };
  assert.ok(path in payloads, path);
  return response(payloads[path]);
}
const window = {
  isSecureContext: true, crypto: webcrypto, MyProxyReleaseSigning: helper,
  clearTimeout() {}, setTimeout() {return 1;}, addEventListener() {},
  location: {hash: '', pathname: '/admin/'}, history: {replaceState() {}},
  confirm() {throw new Error('the publish wizard must not add a second confirmation');},
};
const context = {
  window, document: {getElementById: node, createElement: () => new DomNode(),
    querySelector: () => new DomNode(), querySelectorAll: () => [], body: new DomNode()}, Option, fetch, console,
  URL: DownloadURL, Blob, Uint8Array, Error, Date, Intl, URLSearchParams,
  navigator: {clipboard: {async writeText() {}}},
};
vm.runInNewContext(source.replace('void enterFromSsh();\n})();',
  'globalThis.ui = {state, wizard, prepareWizard, publishWizard, clearWizard, invalidateWizard, selectWizardKey, lock, updateWizardControls};\n})();'), context);
const ui = context.ui;
assert.ok(ui);
ui.state.token = 'SESSION'; ui.state.releaseSettings = settings;
ui.state.users = [{id: 'user_1', username: 'Alice', status: 'active'}];
ui.state.devices = [{id: 'dev_1', deviceName: 'Phone', userId: 'user_1', platform: 'windows', status: 'active'}];
ui.wizard.key = {key: {private: 'DO-NOT-UPLOAD'}, publicKeyHex: publicHex, privateKeyPem: 'PRIVATE-PEM'};
node('wizard-platform').value = 'windows'; node('wizard-version').value = '1.2.3';
node('wizard-channel').value = 'stable'; node('wizard-key-id').value = 'rel-local';
node('wizard-subject').value = subject; node('wizard-scope').value = 'device';
node('wizard-target').value = 'dev_1'; node('wizard-target').options = [new Option('Phone', 'dev_1')];
node('wizard-file').files = [file]; node('wizard-managed').checked = true;
(async () => {
  if (scenario === 'local-key') {
    await ui.selectWizardKey(helper => helper.generateKey());
    assert.equal(node('wizard-file').files[0], file);
    assert.equal(node('wizard-public-key').value, publicHex);
    assert.match(node('wizard-linux-key-config').value, /^[a-f0-9]{64}:[a-f0-9]{64}$/);
    assert.equal(calls.length, 0);
    ui.lock('logout');
    assert.equal(ui.wizard.key, null);
    assert.equal(node('wizard-public-key').value, '');
    return;
  }
  if (scenario === 'insecure') {
    window.isSecureContext = false;
    await assert.rejects(ui.prepareWizard(), /localhost/);
    assert.equal(calls.length, 0);
    return;
  }
  if (scenario === 'linux' || scenario === 'export') { file.name = 'MyProxy.tar.gz'; node('wizard-platform').value = 'linux'; }
  if (scenario === 'external') {
    settings.artifactUploadEnabled = false; node('wizard-managed').checked = false;
    node('wizard-external-url').value = 'https://releases.example/MyProxy.zip';
  }
  if (scenario === 'flags-preserved') {
    ui.state.assignments = [{scope: 'device', targetId: 'dev_1', platform: 'windows', featureFlags: '{"usageCategories":true}'}];
    assignments = [{scope: 'device', targetId: 'dev_1', platform: 'windows', featureFlags: '{"usageCategories":true,"disableAutoUpdate":true}'}];
  }
  await ui.prepareWizard();
  assert.equal(calls.length, 0, 'review is entirely local');
  assert.equal(node('wizard-review').hidden, false);
  assert.ok(node('wizard-summary').children.some(row => row.children.some(child => child.textContent.includes(hash))));
  assert.ok(node('wizard-summary').children.some(row => row.children.some(child => child.textContent.includes('dev_1'))));
  if (scenario === 'export') {
    node('wizard-export').listeners.click();
    assert.equal(downloads.length, 2);
    const envelope = JSON.parse(await downloads[0].text());
    assert.deepEqual(Object.keys(envelope).sort(), ['manifest', 'signature', 'signingKeyId']);
    assert.ok(!JSON.stringify(envelope).includes('PRIVATE'));
    assert.equal(await downloads[1].text(), ui.wizard.prepared.artifactSignature + '\n');
    assert.equal(downloads[1].type, 'text/plain');
    assert.equal(downloadNames[1], 'myproxy-linux-1.2.3.tar.gz.sig');
    assert.equal(Buffer.from((await downloads[1].text()).trim(), 'base64').length, 64);
    assert.equal(calls.length, 0);
    return;
  }
  if (scenario === 'untrusted') {
    settings.trustedSigningKeys = [];
    await assert.rejects(ui.publishWizard(), /公钥/);
    assert.equal(calls.length, 0);
    assert.ok(!helper.exportEnvelope(ui.wizard.prepared).includes('PRIVATE'));
    return;
  }
  if (scenario === 'conflict') {
    records = [{platform: 'windows', version: '1.2.3', status: 'draft', artifactSha256: 'd'.repeat(64)}];
    await assert.rejects(ui.publishWizard(), /新版本号/);
    assert.equal(registrations, 0);
    return;
  }
  if (scenario === 'upload-retry') fail = '/artifact';
  if (scenario === 'assignment-retry') fail = '/assignment';
  if (scenario === 'lost-registration') lostRegistration = true;
  if (fail) {
    await assert.rejects(ui.publishWizard(), /test failure/);
    assert.equal(registrations, 1);
    assert.ok(ui.wizard.release);
    if (scenario === 'assignment-retry') assert.equal(ui.wizard.published, true);
    assert.match(node('wizard-progress').textContent, /阶段会保留/);
  }
  await ui.publishWizard();
  assert.equal(registrations, 1);
  assert.equal(ui.wizard.published, true); assert.equal(ui.wizard.assigned, true);
  if (scenario === 'flags-preserved') {
    assert.equal(ui.wizard.snapshot.assignment.featureFlags.usageCategories, true);
    assert.equal(ui.wizard.appliedFlags.disableAutoUpdate, true);
    assert.match(node('wizard-progress').textContent, /最新的功能开关/);
  }
  const paths = calls.filter(call => call.method === 'POST').map(call => call.path);
  assert.equal(paths.filter(path => path.endsWith('/publish')).length, 1);
  if (scenario === 'external') assert.ok(!paths.some(path => path.endsWith('/artifact')));
  if (scenario === 'linux') assert.ok(paths.indexOf('/api/admin/release/rel_1/artifact') < paths.indexOf('/api/admin/release/rel_1/artifact-signature'));
  if (scenario === 'assignment-retry') assert.equal(paths.filter(path => path.endsWith('/artifact')).length, 1);
  assert.equal(node('wizard-publish').disabled, true);
})().catch(error => { console.error(error); process.exitCode = 1; });
"""


class ReleaseWizardTests(unittest.TestCase):
    def test_html_version_patterns_accept_signed_manifest_versions(self):
        node = shutil.which("node")
        if not node:
            self.skipTest("Node.js is needed to verify HTML v-mode patterns")
        audit = _Ids()
        audit.feed(admin_ui.ADMIN_HTML.decode())
        patterns = [audit.patterns[name] for name in ("wizard-version", "wizard-minimum")]
        script = """
const assert = require('node:assert/strict');
for (const pattern of JSON.parse(process.argv[1])) {
  const regex = new RegExp('^(?:' + pattern + ')$', 'v');
  for (const version of ['1.2.3', '1.2.3-beta.1', '1.2.3+build.9', '1.2.3-beta.1+build.9']) assert.ok(regex.test(version), version);
  for (const version of ['1.2', '1.2.3-', '1.2.3+', 'https://example.com']) assert.ok(!regex.test(version), version);
}
"""
        result = subprocess.run([node, "-e", script, json.dumps(patterns)], capture_output=True,
                                text=True, encoding="utf-8", timeout=20, check=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_release_wizard_protocol_and_retry_behaviour(self):
        node = shutil.which("node")
        if not node:
            self.skipTest("Node.js is needed to execute UI behaviour tests")
        audit = _Ids()
        audit.feed(admin_ui.ADMIN_HTML.decode())
        self.assertEqual(len(audit.ids), len(set(audit.ids)))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "app.js").write_bytes(admin_ui.ADMIN_JS)
            (root / "ids.json").write_text(json.dumps(audit.ids), encoding="utf-8")
            (root / "harness.js").write_text(HARNESS, encoding="utf-8")
            for scenario in ("local-key", "insecure", "untrusted", "conflict", "success", "linux", "export",
                             "external", "upload-retry", "assignment-retry", "lost-registration", "flags-preserved"):
                with self.subTest(scenario=scenario):
                    result = subprocess.run(
                        [node, str(root / "harness.js"), str(root / "app.js"), str(root / "ids.json"), scenario],
                        capture_output=True, text=True, encoding="utf-8", timeout=20, check=False,
                    )
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
