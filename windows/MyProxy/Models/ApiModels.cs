using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;

namespace MyProxy.Models;

public sealed class BindResult
{
    [JsonPropertyName("device")]
    public DeviceConfig Device { get; set; } = new();

    [JsonPropertyName("profile")]
    public ServerProfile Profile { get; set; } = new();

    [JsonPropertyName("configVersion")]
    public long ConfigVersion { get; set; }
}

public sealed class LatencyResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; set; }

    [JsonPropertyName("error")]
    public ErrorCode Error { get; set; }
}

public sealed class HeartbeatResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("configVersion")]
    public long ConfigVersion { get; set; }

    [JsonPropertyName("serverTime")]
    public DateTimeOffset? ServerTime { get; set; }

    /// <summary>
    /// 本设备当前生效的 release 标识；没有指派时为 null。**只有身份**，
    /// 签名 manifest 仍须从 /api/device/update 取回并在本地验签。
    /// </summary>
    [JsonPropertyName("release")]
    public AssignedReleaseInfo? Release { get; set; }

    /// <summary>
    /// 生效的 feature flags。原样保留为 JsonElement，由
    /// <see cref="MyProxy.Core.FeatureFlags.FromJson"/> 解析——服务端可能加了
    /// 本构建不认识的开关，解析器丢弃它们而不是让整张表失败。
    /// </summary>
    [JsonPropertyName("featureFlags")]
    public JsonElement FeatureFlags { get; set; }
}

/// <summary>心跳与 update 响应里的 release 身份。</summary>
public class AssignedReleaseInfo
{
    [JsonPropertyName("releaseId")]
    public string ReleaseId { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    /// <summary>命中的指派层级：device / user / platform。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
}

/// <summary>
/// /api/device/update 里的 release 载荷。
///
/// <see cref="Manifest"/> 与 <see cref="Signature"/> 是 base64，**原样传递、
/// 原样验签**：任何一次重新序列化都会让签名失效。
/// </summary>
public sealed class AssignedReleasePayload : AssignedReleaseInfo
{
    [JsonPropertyName("manifest")]
    public string Manifest { get; set; } = "";

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    [JsonPropertyName("signingKeyId")]
    public string SigningKeyId { get; set; } = "";
}

public sealed class DeviceUpdateResponse
{
    [JsonPropertyName("update")]
    public AssignedReleasePayload? Update { get; set; }

    [JsonPropertyName("featureFlags")]
    public JsonElement FeatureFlags { get; set; }

    [JsonPropertyName("serverTime")]
    public DateTimeOffset? ServerTime { get; set; }
}

public sealed class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    /// <summary>
    /// 存在 platform 层指派时，latest.json 额外带上签名 manifest。
    /// 这条路径不需要设备令牌，是「更新检查不能完全依赖 Control Plane 在线」
    /// 的落点：拿不到令牌时仍能取到并验证一份签名 manifest。
    /// </summary>
    [JsonPropertyName("releaseId")]
    public string ReleaseId { get; set; } = "";

    [JsonPropertyName("manifest")]
    public string Manifest { get; set; } = "";

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    [JsonPropertyName("signingKeyId")]
    public string SigningKeyId { get; set; } = "";

    /// <summary>是否带着可验签的 manifest。没有就不提供任何更新，连提示也不给。</summary>
    [JsonIgnore]
    public bool HasSignedManifest =>
        Manifest.Length > 0 && Signature.Length > 0 && SigningKeyId.Length > 0;
}
