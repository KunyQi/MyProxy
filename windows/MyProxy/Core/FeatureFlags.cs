using System.Collections.Immutable;
using System.Text.Json;

namespace MyProxy.Core;

/// <summary>
/// 服务端下发的 feature flags：一张扁平开关表。
///
/// <para>
/// <b>flags 只能开关「已经随这个构建发出去的」代码路径。</b> 真正新增的代码
/// 只能走 signed release 发布。这不是风格建议——如果一个开关能让客户端开始
/// 做它出厂时不会做的事，那 Update Plane 的验签就被绕过去了，Control Plane
/// 又变回了能下发任意行为的通道。
/// </para>
///
/// <para>
/// 值只允许 string / number / boolean / null，且拒绝嵌套对象；服务端已经拦过
/// 一道，客户端再拦一道，因为「嵌套」正是把结构化指令夹带进一条只该传开关的
/// 通道的形状。
/// </para>
/// </summary>
public sealed class FeatureFlags
{
    public static FeatureFlags Empty { get; } = new(ImmutableDictionary<string, string>.Empty);

    private readonly ImmutableDictionary<string, string> _values;

    private FeatureFlags(ImmutableDictionary<string, string> values) => _values = values;

    public int Count => _values.Count;

    public IEnumerable<string> Names => _values.Keys;

    public bool IsEnabled(string name) =>
        _values.TryGetValue(name, out string? value) &&
        value.Equals("true", StringComparison.OrdinalIgnoreCase);

    public string GetString(string name, string fallback = "") =>
        _values.TryGetValue(name, out string? value) ? value : fallback;

    public long GetInt64(string name, long fallback = 0) =>
        _values.TryGetValue(name, out string? value) && long.TryParse(value, out long parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// 从心跳或 update 响应里的 JSON 对象构造。任何不合规的项被<b>丢弃</b>而不是
    /// 让整张表失败：一个服务端新加的开关不该让老客户端连已有的开关都读不到。
    /// </summary>
    public static FeatureFlags FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        ImmutableDictionary<string, string>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => "",
                // 嵌套对象与数组一律丢弃，见类型注释。
                _ => null
            };

            if (value is not null && property.Name.Length > 0 && property.Name.Length <= 64)
            {
                builder[property.Name] = value;
            }
        }

        return builder.Count == 0 ? Empty : new FeatureFlags(builder.ToImmutable());
    }

    /// <summary>
    /// 从本地缓存（settings.json）恢复。与 <see cref="FromJson"/> 同样的过滤：缓存只是上一次
    /// 心跳的副本，不该比服务端下发的那份多放进任何东西。
    /// </summary>
    public static FeatureFlags FromValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Empty;
        }

        ImmutableDictionary<string, string>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in values)
        {
            if (name.Length > 0 && name.Length <= 64 && value is not null)
            {
                builder[name] = value;
            }
        }

        return builder.Count == 0 ? Empty : new FeatureFlags(builder.ToImmutable());
    }

    /// <summary>写进本地缓存的形状。</summary>
    public Dictionary<string, string> ToDictionary() => new(_values, StringComparer.Ordinal);

    public override string ToString() =>
        _values.Count == 0 ? "(none)" : string.Join(", ", _values.Select(pair => $"{pair.Key}={pair.Value}"));
}

/// <summary>本构建认识的开关名。加开关时在这里登记，避免拼写漂移。</summary>
public static class KnownFeatureFlags
{
    /// <summary>
    /// 开启按服务类别的用量归因。默认关闭：它需要在 xray 配置里为每个类别
    /// 建一条带独立 tag 的出站，那是对数据面形状的改动，只应在管理员为某台
    /// 设备显式打开时才发生。
    /// </summary>
    public const string UsageCategories = "usageCategories";

    /// <summary>关闭自动更新检查（仍可手动检查）。</summary>
    public const string DisableAutoUpdate = "disableAutoUpdate";
}
