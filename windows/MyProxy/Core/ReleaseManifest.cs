using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace MyProxy.Core;

/// <summary>manifest 被拒绝的原因。只用于日志与内部判断，不直接展示给用户。</summary>
public enum ManifestRejection
{
    None,
    MalformedEnvelope,
    UnknownSigningKey,
    BadSignature,
    MalformedDocument,
    UnsupportedSchema,
    WrongPlatform,
    BadVersion,
    BadArtifact
}

/// <summary>一份验签通过并通过结构校验的 release manifest。</summary>
public sealed record ReleaseManifest(
    string Platform,
    string Version,
    string Channel,
    bool Mandatory,
    string ArtifactUrl,
    string ArtifactSha256,
    long ArtifactSize,
    string PlatformSignatureType,
    string PlatformSignatureSubjectSha256,
    string IssuedAt,
    string MinimumVersion);

/// <summary>验签与解析的结果。失败时 <see cref="Manifest"/> 为 null。</summary>
public sealed record ManifestVerification(ManifestRejection Rejection, ReleaseManifest? Manifest = null)
{
    public bool Ok => Rejection == ManifestRejection.None && Manifest is not null;
}

/// <summary>
/// release manifest 的验签与结构校验。纯逻辑，无 I/O。
///
/// <para>
/// <b>manifest 以原始字节验签，任何环节都不得重新序列化。</b>
/// 签名覆盖 <see cref="SigningDomain"/> + manifest 的原始 UTF-8 字节。三端
/// （Python 服务端、C# 与 Kotlin 客户端）的 JSON 序列化器在键序、浮点格式、
/// Unicode 转义与分隔空白上的任何一点差异，都会变成「一端验签通过、另一端
/// 失败」的长期故障源。所以这里拿到 base64 先解码、先验签，验过之后才允许
/// 把那串字节交给 JSON 解析器。
/// </para>
///
/// <para>
/// 域前缀与 <c>auth.derive_device_token</c> 的 <c>myproxy-claim-v1\0</c> 同一套路：
/// 一份 release 签名永远不能被当作别的 MyProxy 结构的签名重放。
/// </para>
/// </summary>
public static class ReleaseManifestVerifier
{
    /// <summary>签名消息的域前缀，必须与服务端逐字节一致。</summary>
    public static readonly byte[] SigningDomain =
        Encoding.ASCII.GetBytes("myproxy-release-manifest-v1\0");

    public const int SchemaVersion = 1;
    public const string WindowsPlatform = ClientReleasePlatform.WindowsPlatform;
    public const string WindowsSignatureType = ClientReleasePlatform.AuthenticodeSignatureType;
    public const int MaxManifestBytes = 8192;

    /// <summary>
    /// 验签并解析。<paramref name="trustedKeys"/> 是内置公钥表，
    /// <b>不得在运行时下载</b>，签名公钥随客户端发布。
    ///
    /// <para>
    /// 不传 <paramref name="target"/> 时按 Windows 处理，这是本方法原有的行为，
    /// 也是 Windows 端与既有测试依赖的行为。Linux 端传
    /// <see cref="ClientReleasePlatform.Linux"/>：平台名与允许的产物签名类型
    /// 都由它给出（见 <see cref="ClientReleasePlatform"/>）。
    /// </para>
    /// </summary>
    public static ManifestVerification Verify(
        string? manifestBase64,
        string? signatureBase64,
        string? signingKeyId,
        IReadOnlyDictionary<string, byte[]> trustedKeys,
        ClientReleasePlatform? target = null)
    {
        ClientReleasePlatform platform = target ?? ClientReleasePlatform.Windows;

        if (trustedKeys.Count == 0)
        {
            // fail closed：没有可信公钥就不可能有可信的更新。
            return new(ManifestRejection.UnknownSigningKey);
        }

        if (string.IsNullOrEmpty(signingKeyId) ||
            !trustedKeys.TryGetValue(signingKeyId, out byte[]? publicKey))
        {
            return new(ManifestRejection.UnknownSigningKey);
        }

        if (!TryDecodeBase64(manifestBase64, MaxManifestBytes, out byte[] manifestBytes) ||
            !TryDecodeBase64(signatureBase64, 64, out byte[] signature) ||
            signature.Length != 64)
        {
            return new(ManifestRejection.MalformedEnvelope);
        }

        byte[] signed = new byte[SigningDomain.Length + manifestBytes.Length];
        SigningDomain.CopyTo(signed, 0);
        manifestBytes.CopyTo(signed, SigningDomain.Length);

        if (!Ed25519.Verify(publicKey, signed, signature))
        {
            return new(ManifestRejection.BadSignature);
        }

        return Parse(manifestBytes, platform);
    }

    private static ManifestVerification Parse(byte[] manifestBytes, ClientReleasePlatform target)
    {
        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestBytes);
        }
        catch (JsonException)
        {
            return new(ManifestRejection.MalformedDocument);
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(ManifestRejection.MalformedDocument);
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != SchemaVersion)
            {
                return new(ManifestRejection.UnsupportedSchema);
            }

            string platform = ReadString(root, "platform");
            if (platform != target.Name)
            {
                // 服务端已经按平台解析过，但客户端仍然自己再判一次：一份指给
                // 另一个平台的 manifest 被装上来，是没人会察觉的那种错。
                return new(ManifestRejection.WrongPlatform);
            }

            string version = ReadString(root, "version");
            if (!TryParseVersion(version, out _))
            {
                return new(ManifestRejection.BadVersion);
            }

            string channel = ReadString(root, "channel");
            if (channel is not ("stable" or "beta"))
            {
                return new(ManifestRejection.MalformedDocument);
            }

            bool mandatory = root.TryGetProperty("mandatory", out JsonElement mandatoryElement)
                && mandatoryElement.ValueKind == JsonValueKind.True;

            string issuedAt = ReadString(root, "issuedAt");
            if (issuedAt.Length == 0)
            {
                return new(ManifestRejection.MalformedDocument);
            }

            string minimumVersion = ReadString(root, "minimumVersion");
            if (minimumVersion.Length > 0 && !TryParseVersion(minimumVersion, out _))
            {
                return new(ManifestRejection.BadVersion);
            }

            if (!root.TryGetProperty("artifact", out JsonElement artifact) ||
                artifact.ValueKind != JsonValueKind.Object)
            {
                return new(ManifestRejection.BadArtifact);
            }

            string url = ReadString(artifact, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUrl) ||
                parsedUrl.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(parsedUrl.UserInfo))
            {
                return new(ManifestRejection.BadArtifact);
            }

            string sha256 = ReadString(artifact, "sha256");
            if (!IsLowercaseHex64(sha256))
            {
                return new(ManifestRejection.BadArtifact);
            }

            if (!artifact.TryGetProperty("size", out JsonElement sizeElement) ||
                sizeElement.ValueKind != JsonValueKind.Number ||
                !sizeElement.TryGetInt64(out long size) ||
                size <= 0)
            {
                return new(ManifestRejection.BadArtifact);
            }

            if (!artifact.TryGetProperty("signature", out JsonElement signatureBlock) ||
                signatureBlock.ValueKind != JsonValueKind.Object)
            {
                return new(ManifestRejection.BadArtifact);
            }

            string signatureType = ReadString(signatureBlock, "type");
            if (!target.Accepts(signatureType))
            {
                return new(ManifestRejection.BadArtifact);
            }

            string subject = ReadString(signatureBlock, "subjectSha256");
            if (!IsLowercaseHex64(subject))
            {
                return new(ManifestRejection.BadArtifact);
            }

            return new(
                ManifestRejection.None,
                new ReleaseManifest(
                    platform,
                    version,
                    channel,
                    mandatory,
                    url,
                    sha256,
                    size,
                    signatureType,
                    subject,
                    issuedAt,
                    minimumVersion));
        }
    }

    /// <summary>
    /// 语义版本的前三段。服务端允许 <c>-beta.1</c> 之类的后缀，而
    /// <see cref="System.Version"/> 解析不了，所以这里先截到第一个 <c>-</c> 或 <c>+</c>。
    /// </summary>
    public static bool TryParseVersion(string? value, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int cut = value.IndexOfAny(new[] { '-', '+' });
        string core = cut < 0 ? value : value[..cut];
        string[] parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        return Version.TryParse(core, out Version? result) && Assign(result, out parsed);
    }

    private static bool Assign(Version? source, out Version parsed)
    {
        parsed = source ?? new Version(0, 0, 0);
        return source is not null;
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool IsLowercaseHex64(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char ch in value)
        {
            bool ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeBase64(string? value, int maxBytes, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value) || value.Length > maxBytes * 2)
        {
            return false;
        }

        byte[] buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(value.Length)];
        if (!Convert.TryFromBase64String(value, buffer, out int written) || written > maxBytes)
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
