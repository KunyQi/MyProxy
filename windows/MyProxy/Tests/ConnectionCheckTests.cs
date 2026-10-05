using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 「检测连接」的自检。它不参与 LKG 提升，但它是用户判断「到底连上没有」的唯一证据来源，
/// 所以出口判定宁可返回 Unknown，也绝不能把「没走隧道」误报成「已确认」。
/// </summary>
[TestClass]
public sealed class ConnectionCheckTests
{
    // 明文 URL：HttpClient 对 http:// 走代理时发绝对形式请求行，
    // 桩代理因此可以直接应答，不必 MITM TLS。
    private const string ProbeUrl = "http://myproxy.test.invalid/generate_204";
    private const string TraceUrl = "http://myproxy.test.invalid/cdn-cgi/trace";
    private const string ServerIp = "203.0.113.10";

    private const string Ok204 = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n";

    [TestMethod]
    public async Task AllSamplesSucceed_ReportsMedianAndSampleCount()
    {
        using var stub = new RoutingStubProxy(Ok204, Trace(ServerIp));
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.IsTrue(result.Reachable);
        Assert.AreEqual(5, result.SampleCount, "五次采样都应计入");
        Assert.IsNotNull(result.LatencyMs);
        Assert.IsNotNull(result.BestLatencyMs);
        Assert.IsTrue(result.BestLatencyMs <= result.LatencyMs, "最快值不可能大于中位数");
    }

    [DataTestMethod]
    [DataRow("203.0.113.10", "203.0.113.10")]
    [DataRow("2001:db8::10", "2001:0db8:0:0:0:0:0:10")]
    public async Task EchoedIpMatchesKnownProxyEgress_IsVerified(string knownEgress, string observedEgress)
    {
        using var stub = new RoutingStubProxy(Ok204, Trace(observedEgress));
        ConnectionCheckResult result = await RunAsync(stub, knownEgress);

        Assert.AreEqual(EgressVerdict.Verified, result.Egress);
    }

    [DataTestMethod]
    [DataRow("203.0.113.10", "203.0.113.7")]
    [DataRow("203.0.113.10", "2001:db8::7")]
    [DataRow("2001:db8::10", "203.0.113.7")]
    [DataRow("2001:db8::10", "2001:db8::7")]
    public async Task DifferentEchoedIp_DoesNotProveBypass(string knownEgress, string observedEgress)
    {
        // 同一节点可能有双栈或多个出口。地址不同不能独立证明请求走了直连。
        using var stub = new RoutingStubProxy(Ok204, Trace(observedEgress));
        ConnectionCheckResult result = await RunAsync(stub, knownEgress);

        Assert.IsTrue(result.Reachable, "延迟仍然量得到，不该因为出口不符就报不可达");
        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
        Assert.IsNotNull(result.LatencyMs);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("vps.example.com")]
    public async Task MissingOrNonIpEgressEvidence_StaysUnknown(string? knownEgress)
    {
        // 域名只能靠一次本地 DNS 解析去比对，而本地解析恰恰是被劫持时最先失真的东西。
        using var stub = new RoutingStubProxy(Ok204, Trace(ServerIp));
        ConnectionCheckResult result = await RunAsync(stub, knownEgress);

        Assert.IsTrue(result.Reachable);
        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
        Assert.AreEqual(5, stub.ProbeCount, "不做出口判定时不该再发回显请求");
        Assert.AreEqual(0, stub.TraceCount);
    }

    [TestMethod]
    public async Task TraceEndpointFails_DegradesToUnknownButKeepsLatency()
    {
        using var stub = new RoutingStubProxy(Ok204, traceResponse: null);
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.IsTrue(result.Reachable, "回显失败不该把整次自检拖成失败");
        Assert.IsNotNull(result.LatencyMs);
        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
    }

    [TestMethod]
    public async Task TraceBodyWithoutIpLine_StaysUnknown()
    {
        using var stub = new RoutingStubProxy(Ok204, Body("fl=abc\nh=example\nts=1\n"));
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
    }

    [TestMethod]
    public async Task TraceBodyWithGarbageIp_StaysUnknown()
    {
        // 回显内容是外部输入，解析不出 IPAddress 就只能是未判定，绝不能当成匹配。
        using var stub = new RoutingStubProxy(Ok204, Body($"ip=not-an-ip\n"));
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
    }

    [TestMethod]
    public async Task Captive200_CountsAsNoSample()
    {
        // 严格度必须与 LKG 门禁一致：用 200 应答的门户不能让自检报「连接正常」。
        using var stub = new RoutingStubProxy(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 2\r\n\r\nhi",
            Trace(ServerIp));
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.IsFalse(result.Reachable);
        Assert.AreEqual(0, result.SampleCount);
        Assert.IsNull(result.LatencyMs);
        Assert.AreEqual(ErrorCode.ConnectTestFailed, result.Error);
        Assert.AreEqual(EgressVerdict.Unknown, result.Egress);
        Assert.AreEqual(0, stub.TraceCount, "采样全失败时不该再去问出口");
    }

    [TestMethod]
    public async Task NonEmpty204_CountsAsNoSample()
    {
        using var stub = new RoutingStubProxy(
            "HTTP/1.1 204 No Content\r\nContent-Length: 7\r\n\r\npayload",
            Trace(ServerIp));
        ConnectionCheckResult result = await RunAsync(stub, ServerIp);

        Assert.IsFalse(result.Reachable);
    }

    private static async Task<ConnectionCheckResult> RunAsync(RoutingStubProxy stub, string? knownEgress)
    {
        var service = new NetworkService(ProbeUrl, TraceUrl);
        return await service.CheckConnectionAsync(
            "127.0.0.1", stub.Port, knownEgress, CancellationToken.None);
    }

    private static string Trace(string ip) => Body($"fl=1f2\nh=example\nip={ip}\nts=1700000000\n");

    private static string Body(string payload)
    {
        int length = Encoding.UTF8.GetByteCount(payload);
        return $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {length}\r\n\r\n{payload}";
    }

    /// <summary>
    /// 按请求行里的路径分发的 HTTP 代理桩：204 探测与出口回显要走不同应答，
    /// 单响应的桩没法区分两者。
    /// </summary>
    private sealed class RoutingStubProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _probeResponse;
        private readonly byte[]? _traceResponse;
        private readonly CancellationTokenSource _cts = new();
        private int _probeCount;
        private int _traceCount;

        /// <param name="traceResponse">null 表示回显端点不可用，桩回 404。</param>
        public RoutingStubProxy(string probeResponse, string? traceResponse)
        {
            _probeResponse = Encoding.ASCII.GetBytes(probeResponse);
            _traceResponse = traceResponse is null ? null : Encoding.UTF8.GetBytes(traceResponse);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public int TraceCount => Volatile.Read(ref _traceCount);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 监听器已关闭。
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    var request = new StringBuilder();
                    byte[] buffer = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(buffer.AsMemory(0, 1), _cts.Token) == 0)
                        {
                            return;
                        }

                        request.Append((char)buffer[0]);
                    }

                    byte[] response;
                    if (request.ToString().Contains("/cdn-cgi/trace", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _traceCount);
                        response = _traceResponse
                                   ?? Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                    }
                    else
                    {
                        Interlocked.Increment(ref _probeCount);
                        response = _probeResponse;
                    }

                    await stream.WriteAsync(response.AsMemory(), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // 客户端已断开。
                }
            }
        }
    }
}
