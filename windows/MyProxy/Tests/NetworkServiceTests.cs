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
/// 严格空 204 是「坏配置永远不能覆盖可用配置」的门禁：只有它通过，
/// PromoteCurrentConfigAsync 才会把配置以 Verified=true 写盘。
/// </summary>
[TestClass]
public sealed class NetworkServiceTests
{
    // 明文 URL：HttpClient 对 http:// 走代理时发绝对形式请求行，
    // 桩代理因此可以直接应答，不必 MITM TLS。
    private const string StubUrl = "http://myproxy.test.invalid/generate_204";

    [TestMethod]
    public async Task Exactly204_IsTreatedAsReachable()
    {
        using var stub = new StubProxy("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        LatencyResult result = await RunAsync(stub);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.LatencyMs);
    }

    [TestMethod]
    public async Task Captive200_IsRejected()
    {
        // 任意 2xx 都算通过时，用 200 应答的门户或劫持设备就能提升坏配置。
        using var stub = new StubProxy(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 2\r\n\r\nhi");
        LatencyResult result = await RunAsync(stub);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.LatencyMs);
        Assert.AreEqual(ErrorCode.ConnectTestFailed, result.Error);
    }

    [TestMethod]
    public async Task Redirect_IsNotFollowed()
    {
        using var stub = new StubProxy(
            "HTTP/1.1 302 Found\r\nLocation: http://myproxy.test.invalid/ok\r\nContent-Length: 0\r\n\r\n");
        LatencyResult result = await RunAsync(stub);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, stub.RequestCount, "AllowAutoRedirect 必须为 false，否则会再发一次请求");
    }

    [TestMethod]
    public async Task NonEmpty204_IsRejected()
    {
        using var stub = new StubProxy(
            "HTTP/1.1 204 No Content\r\nContent-Length: 7\r\n\r\npayload");
        LatencyResult result = await RunAsync(stub);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task UnavailableCandidate_FallsBackAndReusesTheHealthyEndpoint()
    {
        using var stub = new StubProxy(request => request.Contains("/unavailable ", StringComparison.Ordinal)
            ? "HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\n\r\n"
            : "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        var service = new NetworkService(testUrlsOverride: new[]
            { "http://myproxy.test.invalid/unavailable", StubUrl });
        Assert.IsTrue((await service.TestThroughProxyAsync("127.0.0.1", stub.Port, CancellationToken.None)).Success);
        Assert.AreEqual(2, stub.RequestCount);
        Assert.IsTrue((await service.TestThroughProxyAsync("127.0.0.1", stub.Port, CancellationToken.None)).Success);
        Assert.AreEqual(3, stub.RequestCount, "Reuse the successful candidate instead of waiting on a failed endpoint again.");
    }

    [TestMethod]
    public async Task AllCandidatesRejected_CannotVerifyTheTunnel()
    {
        using var stub = new StubProxy("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var service = new NetworkService(testUrlsOverride: new[] { StubUrl, "http://myproxy.test.invalid/other" });
        Assert.IsFalse((await service.TestThroughProxyAsync("127.0.0.1", stub.Port, CancellationToken.None)).Success);
        Assert.AreEqual(2, stub.RequestCount);
    }

    [TestMethod]
    public async Task ManualCheck_ReachesLastCandidateEvenWhenEarlierEndpointsTimeOut()
    {
        using var stub = new StubProxy(request => request.Contains("/good ", StringComparison.Ordinal)
            ? "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n" : "");
        var service = new NetworkService(testUrlsOverride: new[]
        {
            "http://myproxy.test.invalid/slow1", "http://myproxy.test.invalid/slow2",
            "http://myproxy.test.invalid/slow3", "http://myproxy.test.invalid/good"
        });
        ConnectionCheckResult result = await service.CheckConnectionAsync("127.0.0.1", stub.Port, "", CancellationToken.None);
        Assert.IsTrue(result.Reachable, "The whole budget must not be spent on the first two failed endpoints.");
        Assert.IsTrue(stub.RequestCount >= 4);
    }

    [TestMethod]
    public async Task CancelledProbe_DoesNotTryFallbacks()
    {
        using var stub = new StubProxy("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var service = new NetworkService(testUrlsOverride: new[] { StubUrl, "http://myproxy.test.invalid/other" });
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => service.TestThroughProxyAsync("127.0.0.1", stub.Port, cancel.Token));
        Assert.AreEqual(0, stub.RequestCount);
    }

    private static async Task<LatencyResult> RunAsync(StubProxy stub)
    {
        var service = new NetworkService(StubUrl);
        return await service.TestThroughProxyAsync("127.0.0.1", stub.Port, CancellationToken.None);
    }

    /// <summary>最小 HTTP 代理桩：读完请求头即回一份固定响应。</summary>
    private sealed class StubProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string> _response;
        private readonly CancellationTokenSource _cts = new();
        private int _requestCount;

        public StubProxy(string response) : this(_ => response) { }

        public StubProxy(Func<string, string> response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

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

                    Interlocked.Increment(ref _requestCount);
                    string reply = _response(request.ToString());
                    if (reply.Length == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
                        return;
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply).AsMemory(), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // 客户端提前断开。
                }
            }
        }
    }
}
