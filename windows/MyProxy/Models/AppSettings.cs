using System.Text.Json.Serialization;
using MyProxy;
using MyProxy.Core;

namespace MyProxy.Models;

public sealed class AppSettings
{
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    [JsonPropertyName("autoUpdateCheck")]
    public bool AutoUpdateCheck { get; set; }

    [JsonPropertyName("proxyMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Rule;

    /// <summary>
    /// 界面皮肤。默认 Classic：升级上来的用户不该因为版本更新就换了一张脸，
    /// 换皮肤必须是他自己在设置里做的动作。
    /// </summary>
    [JsonPropertyName("uiTheme")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UiTheme UiTheme { get; set; } = UiTheme.Classic;

    // API 目标不在这里：它是编译期常量（AppInfo.BuiltInTargetBaseUrl），用户不选服务器。

    [JsonPropertyName("clientInstanceId")]
    public string ClientInstanceId { get; set; } = "";

    /// <summary>
    /// 上一次心跳带回的 feature flags 的副本。权威永远是服务端，这里只为让进程重启后的
    /// <b>第一次</b>连接就用上它们：生成 xray 配置时要看的开关（比如类别归因）以前要等
    /// 连上之后的第一次心跳才知道，那时配置早已生成，常驻托盘的设备于是永远用不上。
    /// </summary>
    [JsonPropertyName("featureFlags")]
    public Dictionary<string, string> FeatureFlags { get; set; } = new(StringComparer.Ordinal);
}
