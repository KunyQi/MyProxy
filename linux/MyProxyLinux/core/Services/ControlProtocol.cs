using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>
/// 控制通道的线上格式：一行一个 JSON 对象，请求与响应各一行。
///
/// <para>
/// 存在的理由：连接只能有<b>一个</b>持有者。Linux 上「谁在跑内核」这件事有两个
/// 可能的答案（用户双击的托盘程序，或者 systemd 拉起来的常驻进程），两条路各自
/// 持有连接会让系统代理、心跳与 LKG 全部打架。所以定成：
/// <b>守护进程是唯一持有者，CLI 与 GUI 都是它的客户端</b>。
/// </para>
///
/// <para>
/// 协议刻意做得小：命令是一个字符串加一个可选参数，响应是一个状态快照加
/// 一句话错误。GUI 每秒拉一次 <see cref="ControlCommands.Status"/> 就够了——
/// 一秒钟一次本机 UNIX 套接字往返，代价远小于维护一条推送通道的复杂度。
/// </para>
/// </summary>
public sealed class ControlRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("argument")]
    public string Argument { get; set; } = "";
}

public sealed class ControlResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ErrorCode ErrorCode { get; set; } = ErrorCode.Unknown;

    /// <summary>给用户看的一句话。成功时通常是空串。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("status")]
    public StatusSnapshot? Status { get; set; }

    public static ControlResponse Success(StatusSnapshot? status = null)
        => new() { Ok = true, ErrorCode = ErrorCode.Unknown, Status = status };

    public static ControlResponse Failure(ErrorCode code, string message)
        => new() { Ok = false, ErrorCode = code, Message = message };
}

/// <summary>
/// 一次状态快照：GUI 与 CLI 需要的全部信息。
///
/// <para>
/// 它是<b>只读投影</b>，不是权威状态：权威在守护进程里，快照只是某一刻的拷贝。
/// 加字段不会破坏兼容性——旧客户端忽略新字段，新客户端在旧守护进程上拿到默认值。
/// </para>
/// </summary>
public sealed class StatusSnapshot
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = AppInfo.Version;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = AppInfo.Platform;

    [JsonPropertyName("daemonPid")]
    public int DaemonPid { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("bound")]
    public bool Bound { get; set; }

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("boundAt")]
    public DateTimeOffset? BoundAt { get; set; }

    [JsonPropertyName("configVersion")]
    public long ConfigVersion { get; set; }

    [JsonPropertyName("state")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppState State { get; set; } = AppState.Unbound;

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProxyMode Mode { get; set; } = ProxyMode.Rule;

    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; set; }

    [JsonPropertyName("lastErrorCode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ErrorCode LastErrorCode { get; set; } = ErrorCode.Unknown;

    [JsonPropertyName("lastErrorMessage")]
    public string LastErrorMessage { get; set; } = "";

    [JsonPropertyName("lastSwitchErrorMessage")]
    public string? LastSwitchErrorMessage { get; set; }

    [JsonPropertyName("serverIdentityUnverified")]
    public bool ServerIdentityUnverified { get; set; }

    [JsonPropertyName("isChecking")]
    public bool IsChecking { get; set; }

    [JsonPropertyName("hasCheck")]
    public bool HasCheck { get; set; }

    [JsonPropertyName("checkReachable")]
    public bool CheckReachable { get; set; }

    [JsonPropertyName("checkLatencyMs")]
    public int? CheckLatencyMs { get; set; }

    [JsonPropertyName("checkBestLatencyMs")]
    public int? CheckBestLatencyMs { get; set; }

    [JsonPropertyName("checkSampleCount")]
    public int CheckSampleCount { get; set; }

    [JsonPropertyName("checkEgress")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EgressVerdict CheckEgress { get; set; } = EgressVerdict.Unknown;

    [JsonPropertyName("checkError")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ErrorCode CheckError { get; set; } = ErrorCode.Unknown;

    [JsonPropertyName("uplinkBytesPerSecond")]
    public double UplinkBytesPerSecond { get; set; }

    [JsonPropertyName("downlinkBytesPerSecond")]
    public double DownlinkBytesPerSecond { get; set; }

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    [JsonPropertyName("theme")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UiTheme Theme { get; set; } = UiTheme.Classic;

    /// <summary>服务端指派了比本机更高的版本，且验签通过。</summary>
    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("updateVersion")]
    public string UpdateVersion { get; set; } = "";

    [JsonPropertyName("updateMandatory")]
    public bool UpdateMandatory { get; set; }

    [JsonPropertyName("updateReleaseId")]
    public string UpdateReleaseId { get; set; } = "";

    /// <summary>最近一次更新检查/安装的一句话结论（失败原因或进度）。</summary>
    [JsonPropertyName("updateMessage")]
    public string UpdateMessage { get; set; } = "";

    /// <summary>
    /// 日志目录（绝对路径）。设置页的「打开日志」需要它——GUI 自己不该去猜路径：
    /// 数据根可以由 XDG 环境变量改写，猜出来的路径在别人机器上就是错的。
    /// </summary>
    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = "";
}

public static class ControlCommands
{
    public const string Ping = "ping";
    public const string Status = "status";
    public const string Bind = "bind";
    public const string Unbind = "unbind";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Mode = "mode";
    public const string Check = "check";
    public const string AutoStart = "autostart";
    public const string AutoConnect = "autoconnect";
    public const string Theme = "theme";
    public const string UpdateCheck = "update-check";
    public const string UpdateApply = "update-apply";
    public const string Activate = "activate";
    public const string Shutdown = "shutdown";
}

internal static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
