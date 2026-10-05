using MyProxy.Core;

namespace MyProxy.Models;

/// <summary>
/// 基于可信出口数据的判定；连通性与出口归属是不同的结论。
/// </summary>
/// <remarks>
/// <see cref="ServerProfile.Server"/> 是连接入口，并不保证也是出口。204 探测通过时可以报告
/// 连通；没有独立确认的出口数据时，不能用入口与回显 IP 的差异推断流量绕过隧道。
/// </remarks>
public enum EgressVerdict
{
    /// <summary>缺少可信出口数据、回显不可用或不匹配；这些情况都不足以证明绕过。</summary>
    Unknown,

    /// <summary>回显 IP 匹配独立确认的代理出口地址。</summary>
    Verified,

    /// <summary>有独立证据证明请求绕过隧道；单纯入口/回显 IP 不同不能得出此结论。</summary>
    Bypassed
}

/// <summary>
/// 「检测连接」一次按需自检的完整结论。与 <see cref="LatencyResult"/> 的区别是
/// 后者是连接流程内部的单次门禁（只回答「能不能提升 LKG」），本类型是给用户看的实测报告。
/// </summary>
public sealed class ConnectionCheckResult
{
    /// <summary>严格 204 探测是否通过（至少一次采样成功）。</summary>
    public bool Reachable { get; init; }

    /// <summary>多次采样的中位数，毫秒。<see cref="Reachable"/> 为 false 时为 null。</summary>
    public int? LatencyMs { get; init; }

    /// <summary>采样中最快的一次，毫秒。首次采样含 TCP+TLS+REALITY 握手，靠后续热连接拉出真实值。</summary>
    public int? BestLatencyMs { get; init; }

    /// <summary>实际完成的成功采样次数，用于判断中位数的可信度。</summary>
    public int SampleCount { get; init; }

    public EgressVerdict Egress { get; init; }

    /// <summary>失败原因；<see cref="Reachable"/> 为 true 时无意义。</summary>
    public ErrorCode Error { get; init; }
}
