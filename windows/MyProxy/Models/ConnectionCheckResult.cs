using MyProxy.Core;

namespace MyProxy.Models;

/// <summary>
/// 出口归属的判定：本次探测的回显 IP 是否就是设备绑定的那台服务器。
/// </summary>
/// <remarks>
/// 这是「到底连上没有」唯一能给出证据的一项。204 探测只能证明「有东西按规范应答了」，
/// 证明不了流量从哪出去；只有把回显 IP 和 <see cref="ServerProfile.Server"/> 对上，
/// 才排除了「xray 活着、系统代理也指过去了，但请求其实走的本地直连」这一类静默失效。
/// </remarks>
public enum EgressVerdict
{
    /// <summary>未判定：回显探测失败，或绑定的服务器地址不是 IP 字面量（域名无法本地比对，宁可不判也不误判）。</summary>
    Unknown,

    /// <summary>回显 IP 等于绑定的服务器地址：流量确实由该服务器转出。</summary>
    Verified,

    /// <summary>回显 IP 不是绑定的服务器地址：本次请求没有走隧道。</summary>
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
