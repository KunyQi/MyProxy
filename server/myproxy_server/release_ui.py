"""Dependency-free browser signing for the private release administration UI.

The helper has no network or storage API. Private keys remain in browser memory;
the caller may explicitly download the generated PKCS#8 PEM for offline backup.
Only the opaque signed manifest envelope and optional Linux artifact signature
are supplied to the release API. WebCrypto requires a secure context and native
Ed25519 support; unsupported browsers fail closed.
"""

from __future__ import annotations


RELEASE_SIGNING_JS = r"""(() => {
  "use strict";
  const MAX_MANIFEST_BYTES = 8192;
  const MAX_KEY_BYTES = 16384;
  const pemBegin = "-----BEGIN " + "PRIVATE KEY-----";
  const pemEnd = "-----END " + "PRIVATE KEY-----";
  const VERSION = /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
  const KEY_ID = /^[A-Za-z0-9._-]{1,64}$/;
  const HEX64 = /^[0-9a-fA-F]{64}$/;
  const SIGNATURE_TYPES = Object.freeze({windows: "authenticode", android: "apksigner", linux: "ed25519"});
  const utf8 = new TextEncoder();
  const signingDomain = utf8.encode("myproxy-release-manifest-v1\u0000");
  // Native File/Blob contents are immutable. Cache only the digest, never bytes or keys.
  const fileHashes = new WeakMap();

  function subtle() {
    if (window.isSecureContext === false || !window.crypto || !window.crypto.subtle) {
      throw new Error("本地签名需要 HTTPS 或 localhost 安全连接，以及支持 Ed25519 的浏览器。");
    }
    return window.crypto.subtle;
  }

  function cryptoError(error, fallback) {
    if (error && error.name === "NotSupportedError") {
      return new Error("当前浏览器不支持 Ed25519，请使用支持此算法的新版浏览器。");
    }
    return new Error(fallback);
  }

  function toBase64(bytes) {
    const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    let binary = "";
    for (let i = 0; i < data.length; i += 4096) {
      binary += String.fromCharCode(...data.subarray(i, i + 4096));
    }
    return window.btoa(binary);
  }

  function decodeBase64(value) {
    if (typeof value !== "string" || !value || value.length > MAX_KEY_BYTES * 2 ||
        !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) {
      throw new Error("Base64 数据无效。");
    }
    const binary = window.atob(value);
    if (window.btoa(binary) !== value) throw new Error("Base64 数据无效。");
    return Uint8Array.from(binary, character => character.charCodeAt(0));
  }

  function fromBase64Url(value) {
    if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/.test(value)) throw new Error("签名公钥格式无效。");
    const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
    return decodeBase64(base64 + "=".repeat((4 - base64.length % 4) % 4));
  }

  function hex(bytes) {
    return Array.from(new Uint8Array(bytes), value => value.toString(16).padStart(2, "0")).join("");
  }

  function signingKey(key) {
    if (!key || key.type !== "private" || !key.algorithm || key.algorithm.name !== "Ed25519" ||
        !key.usages || !key.usages.includes("sign")) {
      throw new Error("请选择可签名的 Ed25519 私钥。");
    }
    return key;
  }

  async function publicBytes(key) {
    signingKey(key);
    let jwk;
    try { jwk = await subtle().exportKey("jwk", key); }
    catch (error) { throw cryptoError(error, "无法读取签名密钥的公钥，请重新导入 PKCS#8 私钥。"); }
    // The private JWK is temporary and never returned. Only its public x survives.
    const raw = fromBase64Url(jwk.x);
    if (jwk.kty !== "OKP" || jwk.crv !== "Ed25519" || raw.length !== 32) throw new Error("签名公钥格式无效。");
    return raw;
  }

  async function generateKey() {
    const crypto = subtle();
    let pair;
    try { pair = await crypto.generateKey({name: "Ed25519"}, true, ["sign", "verify"]); }
    catch (error) { throw cryptoError(error, "无法生成本地签名密钥。"); }
    const publicKeyHex = hex(await subtle().exportKey("raw", pair.publicKey));
    const der = new Uint8Array(await subtle().exportKey("pkcs8", pair.privateKey));
    const base64 = toBase64(der);
    der.fill(0);
    const privateKeyPem = pemBegin + "\n" + base64.match(/.{1,64}/g).join("\n") + "\n" + pemEnd + "\n";
    return {key: pair.privateKey, publicKeyHex, privateKeyPem};
  }

  function validFile(file, maximum, allowEmpty = false) {
    if (!file || typeof file.arrayBuffer !== "function" || !Number.isSafeInteger(file.size) ||
        file.size < (allowEmpty ? 0 : 1) || (maximum !== undefined && file.size > maximum)) {
      throw new Error("请选择有效的本地文件。");
    }
  }

  async function fileBytes(file, maximum, allowEmpty = false) {
    validFile(file, maximum, allowEmpty);
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (bytes.length !== file.size) throw new Error("文件内容与大小不一致，请重新选择文件。");
    return bytes;
  }

  async function loadKey(file) {
    subtle();
    let bytes = await fileBytes(file, MAX_KEY_BYTES);
    const text = new TextDecoder().decode(bytes).trim();
    if (text.startsWith("-----")) {
      const pem = new RegExp("^" + pemBegin + "\\s+([A-Za-z0-9+/=\\s]+)\\s+" + pemEnd + "$").exec(text);
      if (!pem) throw new Error("请选择未加密的 PKCS#8 PRIVATE KEY PEM 或 DER 文件。");
      const decoded = decodeBase64(pem[1].replace(/\s/g, ""));
      bytes.fill(0);
      bytes = decoded;
    }
    let key;
    try { key = await subtle().importKey("pkcs8", bytes, {name: "Ed25519"}, true, ["sign"]); }
    catch (error) { throw cryptoError(error, "无法读取密钥，请选择未加密的 Ed25519 PKCS#8 私钥。"); }
    finally { bytes.fill(0); }
    return {key, publicKeyHex: hex(await publicBytes(key))};
  }

  async function hashFile(file) {
    const crypto = subtle();
    validFile(file, undefined, true);
    let pending = fileHashes.get(file);
    if (!pending) {
      pending = (async () => hex(await crypto.digest("SHA-256", await fileBytes(file, undefined, true))))();
      fileHashes.set(file, pending);
    }
    try { return await pending; }
    catch (error) { fileHashes.delete(file); throw error; }
  }

  function version(value, label) {
    if (typeof value !== "string" || !VERSION.test(value)) throw new Error(label + "须为有效版本号，例如 1.2.3。");
    return value;
  }

  function artifactUrl(value) {
    if (typeof value !== "string" || !/^https:\/\//i.test(value) || /[\\\s\u0000-\u001f\u007f]/.test(value)) {
      throw new Error("安装包地址须为不含凭据的 HTTPS 链接。");
    }
    let url;
    try { url = new URL(value); } catch (_) { throw new Error("安装包 HTTPS 地址无效。"); }
    if (url.protocol !== "https:" || !url.hostname || url.username || url.password) {
      throw new Error("安装包地址须为不含凭据的 HTTPS 链接。");
    }
    return value;
  }

  async function prepare({key, signingKeyId, file, platform, version: releaseVersion, channel, mandatory = false,
                          subjectSha256, artifactUrl: releaseUrl, minimumVersion} = {}) {
    const crypto = subtle();
    signingKey(key);
    if (!Object.prototype.hasOwnProperty.call(SIGNATURE_TYPES, platform)) throw new Error("请选择 Windows、Android 或 Linux 平台。");
    if (typeof signingKeyId !== "string" || !KEY_ID.test(signingKeyId)) throw new Error("签名密钥 ID 须为 1–64 位字母、数字、点、横线或下划线。");
    version(releaseVersion, "发布版本");
    if (channel !== "stable" && channel !== "beta") throw new Error("发布通道须为 stable 或 beta。");
    if (typeof mandatory !== "boolean") throw new Error("强制更新选项须为布尔值。");
    artifactUrl(releaseUrl);
    if (minimumVersion !== undefined && minimumVersion !== "") version(minimumVersion, "最低版本");
    let subject;
    if (platform === "linux") subject = hex(await crypto.digest("SHA-256", await publicBytes(key)));
    else {
      if (typeof subjectSha256 !== "string" || !HEX64.test(subjectSha256)) throw new Error("请填写安装包真实签名证书的 64 位 SHA-256 指纹。");
      subject = subjectSha256.toLowerCase();
    }
    validFile(file);
    let bytes;
    let artifactSha256;
    const cachedHash = fileHashes.get(file);
    if (cachedHash) {
      artifactSha256 = await cachedHash;
      if (platform === "linux") bytes = await fileBytes(file);
    } else {
      bytes = await fileBytes(file);
      const pendingHash = crypto.digest("SHA-256", bytes).then(hex);
      fileHashes.set(file, pendingHash);
      artifactSha256 = await pendingHash;
    }
    const artifactSize = file.size;
    const manifestDocument = {
      schemaVersion: 1, platform, version: releaseVersion, channel, mandatory,
      issuedAt: new Date().toISOString(),
      artifact: {url: releaseUrl, sha256: artifactSha256, size: artifactSize,
        signature: {type: SIGNATURE_TYPES[platform], subjectSha256: subject}},
    };
    if (minimumVersion !== undefined && minimumVersion !== "") manifestDocument.minimumVersion = minimumVersion;
    // Encode once. These exact bytes are signed and exported, never reserialized.
    const raw = utf8.encode(JSON.stringify(manifestDocument));
    if (raw.length > MAX_MANIFEST_BYTES) throw new Error("发布清单超过 8192 字节，请缩短安装包地址。");
    const message = new Uint8Array(signingDomain.length + raw.length);
    message.set(signingDomain);
    message.set(raw, signingDomain.length);
    let signature, artifactSignature;
    try {
      signature = toBase64(await crypto.sign({name: "Ed25519"}, key, message));
      if (platform === "linux") artifactSignature = toBase64(await crypto.sign({name: "Ed25519"}, key, bytes));
    } catch (error) { throw cryptoError(error, "本地签名失败，请重新选择有效私钥。"); }
    const prepared = {manifest: toBase64(raw), signature, signingKeyId, manifestDocument, artifactSha256, artifactSize};
    if (artifactSignature !== undefined) prepared.artifactSignature = artifactSignature;
    return prepared;
  }

  function exportEnvelope(prepared) {
    if (!prepared || typeof prepared.signingKeyId !== "string" || !KEY_ID.test(prepared.signingKeyId) ||
        decodeBase64(prepared.manifest).length > MAX_MANIFEST_BYTES || decodeBase64(prepared.signature).length !== 64) {
      throw new Error("已签名清单无效。");
    }
    return JSON.stringify({manifest: prepared.manifest, signature: prepared.signature, signingKeyId: prepared.signingKeyId}, null, 2);
  }

  window.MyProxyReleaseSigning = Object.freeze({generateKey, loadKey, hashFile, prepare, exportEnvelope, decodeBase64});
})();
""".encode("utf-8")
