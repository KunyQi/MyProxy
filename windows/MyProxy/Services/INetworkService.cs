using MyProxy.Models;

namespace MyProxy.Services;

public interface INetworkService
{
    Task<LatencyResult> TestThroughProxyAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// 「检测连接」的按需自检：多次采样取中位数，并核对流量是否真的从 <paramref name="expectedEgress"/> 转出。
    /// </summary>
    /// <param name="expectedEgress">
    /// 设备绑定的服务器地址。非 IP 字面量（域名）时不做出口判定，结果为
    /// <see cref="Models.EgressVerdict.Unknown"/>——宁可不判，也不拿一次本地 DNS 解析去当证据。
    /// </param>
    Task<ConnectionCheckResult> CheckConnectionAsync(
        string host, int port, string expectedEgress, CancellationToken ct);
}
