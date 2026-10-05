using MyProxy.Models;

namespace MyProxy.Services;

public interface INetworkService
{
    Task<LatencyResult> TestThroughProxyAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// 「检测连接」的按需自检：多次采样取中位数，并在有可信出口数据时核对回显 IP。
    /// </summary>
    /// <param name="knownProxyEgressIp">
    /// 独立确认的代理出口 IP 字面量；没有该数据时传 null。服务器连接入口不能作为出口证据：
    /// 双栈、NAT 或服务器的其它地址都可能使入口与出口不同。回显匹配时可确认该出口，
    /// 不匹配时仍为 <see cref="Models.EgressVerdict.Unknown"/>，不能仅凭差异断言直连。
    /// </param>
    Task<ConnectionCheckResult> CheckConnectionAsync(
        string host, int port, string? knownProxyEgressIp, CancellationToken ct);
}
