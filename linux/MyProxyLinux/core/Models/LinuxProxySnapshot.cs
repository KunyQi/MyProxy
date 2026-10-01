using System.Text.Json.Serialization;

namespace MyProxy.Models;

/// <summary>
/// Linux 上的系统代理快照。
///
/// <para>
/// 与 Windows 的 <see cref="ProxySnapshot"/> 是同一个概念的两种形状：那边是注册表里的
/// 四个值（ProxyEnable / ProxyServer / ProxyOverride / AutoConfigURL），这边是
/// 「桌面环境的代理设置」加「我们写出去的环境变量文件」。
/// </para>
///
/// <para>
/// <b>只记录我们真的改过的东西。</b>快照里出现没管过的键，恢复时就会把第三方
/// 设置一起写回去——那些值在连接期间可能已经被别的程序改过，覆盖它们等于把用户的
/// 公司代理或 PAC 地址改掉。这一点 Windows 端用同样的小心处理（见
/// <c>WindowsProxyService.RestoreSnapshotOrDisableFallback</c>）。
/// </para>
/// </summary>
public sealed class LinuxProxySnapshot
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>抓快照时探测到的桌面后端：gnome / kde / none（可以同时有多个）。</summary>
    [JsonPropertyName("backends")]
    public List<string> Backends { get; set; } = new();

    /// <summary>org.gnome.system.proxy 下的值，只在 <see cref="Backends"/> 含 gnome 时有效。</summary>
    [JsonPropertyName("gnome")]
    public GnomeProxyState? Gnome { get; set; }

    /// <summary>KDE 的 kioslaverc [Proxy Settings] 里的值，只在含 kde 时有效。</summary>
    [JsonPropertyName("kde")]
    public Dictionary<string, string>? Kde { get; set; }

    /// <summary>是否由本程序创建了环境变量文件（<c>environment.d</c> 与 <c>proxy.env</c>）。</summary>
    [JsonPropertyName("environmentFileWritten")]
    public bool EnvironmentFileWritten { get; set; }
}

/// <summary>org.gnome.system.proxy 的一次完整读取。</summary>
public sealed class GnomeProxyState
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "none";

    [JsonPropertyName("httpHost")]
    public string HttpHost { get; set; } = "";

    [JsonPropertyName("httpPort")]
    public int HttpPort { get; set; }

    [JsonPropertyName("httpsHost")]
    public string HttpsHost { get; set; } = "";

    [JsonPropertyName("httpsPort")]
    public int HttpsPort { get; set; }

    [JsonPropertyName("socksHost")]
    public string SocksHost { get; set; } = "";

    [JsonPropertyName("socksPort")]
    public int SocksPort { get; set; }

    [JsonPropertyName("ignoreHosts")]
    public List<string> IgnoreHosts { get; set; } = new();

    [JsonPropertyName("autoconfigUrl")]
    public string AutoconfigUrl { get; set; } = "";

    [JsonPropertyName("useSameProxy")]
    public bool UseSameProxy { get; set; }
}

/// <summary>连接期间写在运行目录里的「本轮是我们改的代理」证据，与 Windows 端同形。</summary>
public sealed class LinuxAppliedMarker
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("appliedAt")]
    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>本轮改过哪些后端。恢复时按它决定回写哪几个，不靠「现在探测到什么」。</summary>
    [JsonPropertyName("backends")]
    public List<string> Backends { get; set; } = new();
}
