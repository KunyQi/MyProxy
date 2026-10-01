using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class NetworkService : INetworkService
{
    private const int TestTimeoutSeconds = 5;
    internal static string DefaultTestUrl => DeploymentConfiguration.ConnectivityCheckUrls[0];

    /// <summary>
    /// 回显自身出口 IP 的端点。必须是 HTTPS：明文回显可被中间设备改写成服务器地址，
    /// 那样「已确认走服务器」就成了攻击者说了算的一句话。
    /// </summary>
    internal const string DefaultTraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>采样次数。第一次含 TCP+TLS+REALITY 握手必然偏高，取中位数把它挤掉。</summary>
    private const int CheckSampleCount = 5;

    /// <summary>回显响应只有几百字节；设上限避免一个恶意端点把内存读爆。</summary>
    private const long TraceResponseCap = 64 * 1024;

    /// <summary>整轮自检的总预算。超时不算失败，已采到的样本照常出结论。</summary>
    private static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<string> _testUrls;
    private string? _preferredTestUrl;
    private readonly string _traceUrl;

    /// <param name="testUrlOverride">仅供测试指向本地桩；生产使用 deployment.json。</param>
    /// <param name="traceUrlOverride">仅供测试指向本地桩；生产走 <see cref="DefaultTraceUrl"/>。</param>
    public NetworkService(string? testUrlOverride = null, string? traceUrlOverride = null,
        IReadOnlyList<string>? testUrlsOverride = null)
    {
        _testUrls = testUrlsOverride ?? (string.IsNullOrWhiteSpace(testUrlOverride)
            ? DeploymentConfiguration.ConnectivityCheckUrls : new[] { testUrlOverride });
        if (_testUrls.Count is < 1 or > 4) throw new ArgumentException("One to four probe URLs are required.", nameof(testUrlsOverride));
        _traceUrl = string.IsNullOrWhiteSpace(traceUrlOverride) ? DefaultTraceUrl : traceUrlOverride;
    }

    public async Task<LatencyResult> TestThroughProxyAsync(string host, int port, CancellationToken ct)
    {
        using HttpClient http = CreateProxiedClient(host, port, TimeSpan.FromSeconds(TestTimeoutSeconds));

        // Four attempts at most, each bounded by the same five-second timeout.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds * 4));
        int? latency = await TrySampleAsync(http, budget.Token, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new LatencyResult
        {
            Success = latency.HasValue,
            LatencyMs = latency,
            Error = ErrorCode.ConnectTestFailed
        };
    }

    public async Task<ConnectionCheckResult> CheckConnectionAsync(
        string host, int port, string expectedEgress, CancellationToken ct)
    {
        // 整轮共用一个 HttpClient：首次采样付握手成本，其后复用连接量到的才是稳态 RTT。
        using HttpClient http = CreateProxiedClient(host, port, TimeSpan.FromSeconds(TestTimeoutSeconds));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CheckBudget);

        var samples = new List<int>(CheckSampleCount);
        for (int i = 0; i < CheckSampleCount; i++)
        {
            if (budget.IsCancellationRequested)
            {
                break;
            }

            int? sample = await TrySampleAsync(http, budget.Token, ct,
                TimeSpan.FromMilliseconds(CheckBudget.TotalMilliseconds / _testUrls.Count)).ConfigureAwait(false);
            if (sample is int value)
            {
                samples.Add(value);
            }
        }

        if (samples.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return new ConnectionCheckResult
            {
                Reachable = false,
                Egress = EgressVerdict.Unknown,
                Error = ErrorCode.ConnectTestFailed
            };
        }

        // 出口判定放在延迟之后：延迟已经拿到了，回显失败不该把整次自检拖成失败。
        EgressVerdict egress = await ResolveEgressAsync(http, expectedEgress, budget.Token, ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var sorted = new List<int>(samples);
        sorted.Sort();

        return new ConnectionCheckResult
        {
            Reachable = true,
            LatencyMs = sorted[sorted.Count / 2],
            BestLatencyMs = sorted[0],
            SampleCount = sorted.Count,
            Egress = egress,
            Error = ErrorCode.ConnectTestFailed
        };
    }

    /// <returns>本次采样的毫秒数；失败返回 null。</returns>
    /// <param name="budgetToken">整轮预算（含用户取消）。</param>
    /// <param name="userToken">仅用户取消。用户主动取消必须外抛，预算耗尽只是提前收工。</param>
    private async Task<int?> TrySampleAsync(
        HttpClient http, CancellationToken budgetToken, CancellationToken userToken,
        TimeSpan? candidateTimeout = null)
    {
        userToken.ThrowIfCancellationRequested();
        string? preferred = Volatile.Read(ref _preferredTestUrl);
        IEnumerable<string> candidates = preferred is null ? _testUrls
            : new[] { preferred }.Concat(_testUrls.Where(url => url != preferred));
        foreach (string url in candidates)
        {
            userToken.ThrowIfCancellationRequested();
            if (budgetToken.IsCancellationRequested) break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
            attempt.CancelAfter(candidateTimeout is { } time && time < TimeSpan.FromSeconds(TestTimeoutSeconds)
                ? time : TimeSpan.FromSeconds(TestTimeoutSeconds));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                if (await IsCanonical204Async(response, attempt.Token).ConfigureAwait(false))
                {
                    Volatile.Write(ref _preferredTestUrl, url);
                    return (int)stopwatch.ElapsedMilliseconds;
                }
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or WebException or OperationCanceledException or IOException) { }
        }
        userToken.ThrowIfCancellationRequested();
        return null;
    }

    private async Task<EgressVerdict> ResolveEgressAsync(
        HttpClient http, string expectedEgress, CancellationToken budgetToken, CancellationToken userToken)
    {
        // 域名走到这里只会得到一次本地 DNS 解析的结果，而本地解析恰恰是被劫持时最先失真的东西。
        if (!IPAddress.TryParse(expectedEgress, out IPAddress? expected))
        {
            return EgressVerdict.Unknown;
        }

        try
        {
            using HttpResponseMessage response =
                await http.GetAsync(_traceUrl, budgetToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return EgressVerdict.Unknown;
            }

            string body = await ReadCappedAsync(response, budgetToken).ConfigureAwait(false);
            return TryParseTraceIp(body, out IPAddress? observed) && observed is not null
                ? observed.Equals(expected) ? EgressVerdict.Verified : EgressVerdict.Bypassed
                : EgressVerdict.Unknown;
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WebException
                                      or OperationCanceledException or InvalidOperationException or IOException)
        {
            return EgressVerdict.Unknown;
        }
    }

    /// <summary>按 `key=value` 逐行找 `ip=`。响应体是外部输入，只允许解析成 <see cref="IPAddress"/>。</summary>
    private static bool TryParseTraceIp(string body, out IPAddress? address)
    {
        foreach (string line in body.Split('\n'))
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            if (!trimmed.StartsWith("ip=", StringComparison.Ordinal))
            {
                continue;
            }

            return IPAddress.TryParse(trimmed[3..], out address);
        }

        address = null;
        return false;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > TraceResponseCap)
        {
            throw new InvalidOperationException("trace response exceeds cap");
        }

        using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[TraceResponseCap];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static HttpClient CreateProxiedClient(string host, int port, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(new Uri($"http://{host}:{port}")),
            UseProxy = true,
            // 跟随重定向会让 302 → 200 的中间设备也测通，等于让坏配置覆盖 LKG。
            AllowAutoRedirect = false
        };

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// 必须精确是 204 且响应体为空。任意 2xx 都算通过时，用 200 应答的
    /// 门户或劫持设备就能让 PromoteCurrentConfigAsync 把坏配置写成 Verified。
    /// </summary>
    private static async Task<bool> IsCanonical204Async(HttpResponseMessage response, CancellationToken ct)
        => response.StatusCode == HttpStatusCode.NoContent
           && await IsBodyEmptyAsync(response, ct).ConfigureAwait(false);

    /// <summary>读取一个字节即可判定：204 按规范不得带响应体。</summary>
    private static async Task<bool> IsBodyEmptyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0)
        {
            return false;
        }

        using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] probe = new byte[1];
        return await body.ReadAsync(probe.AsMemory(0, 1), ct).ConfigureAwait(false) == 0;
    }
}
