using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MyProxy.Core;

namespace MyProxy.Services;

public interface ITrafficStatsService
{
    /// <summary>读累计流量。采样失败返回 null；调用方取消仍传播。</summary>
    Task<TrafficCounters?> QueryAsync(int apiPort, CancellationToken ct);

    /// <summary>
    /// 按出站 tag 读累计流量，用于服务类别归因（Observability Plane）。
    /// 采样失败返回 null；没有任何 tag 时返回空字典。
    /// </summary>
    Task<IReadOnlyDictionary<string, TrafficCounters>?> QueryByTagAsync(int apiPort, CancellationToken ct);
}

/// <summary>
/// 复用 HTTP/2 回环连接读取 xray StatsService，不启动采样子进程。
/// 这里只实现一个只读 unary 方法及其三个 protobuf 字段，不引入 gRPC 运行时。
/// 协议：https://github.com/XTLS/Xray-core/blob/v26.9.9/app/stats/command/command.proto
/// </summary>
public sealed class XrayTrafficStatsService : ITrafficStatsService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private const int MaxMessageBytes = 16 * 1024;
    private const string MethodPath = "/xray.app.stats.command.StatsService/QueryStats";

    /// <summary>默认的代理出站。速率图的读数以它为准：它缺席说明读数不可用。</summary>
    private const string ProxyTag = "proxy";

    // 此客户端只访问字面量 127.0.0.1 的本地核心，与远程 Device API 分离。
    // 禁代理、重定向和 cookie；系统代理指回 xray 时也不会把采样发进数据面。
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = QueryTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 1
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _client;
    private readonly ILogService _log;

    public XrayTrafficStatsService(ILogService log) : this(SharedClient, log) { }

    internal XrayTrafficStatsService(HttpClient client, ILogService log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// 「经代理出去的量」：默认的 proxy 出站，<b>加上</b>类别归因打开时分走的 cat-* 出站。
    /// 以前只读 proxy 两个计数器：一旦为设备打开归因，YouTube、Telegram 这类流量被路由到
    /// 各自的类别出站，看视频时速率图接近 0。一次取回全部出站，按 tag 加总。
    /// </summary>
    public async Task<TrafficCounters?> QueryAsync(int apiPort, CancellationToken ct)
    {
        byte[]? payload = await FetchAsync(apiPort, AllOutboundsRequestBody, ct).ConfigureAwait(false);
        return payload is null ? null : ParseProxied(payload);
    }

    /// <summary>
    /// proxy 与各类别出站的合计。proxy 缺席（或响应损坏）返回 null：不虚构速率。
    /// direct / blocked / api 不算：它们不经过隧道。
    /// </summary>
    internal static TrafficCounters? ParseProxied(ReadOnlySpan<byte> payload)
    {
        IReadOnlyDictionary<string, TrafficCounters>? byTag = ParseByTag(payload);
        if (byTag is null || !byTag.TryGetValue(ProxyTag, out TrafficCounters proxy))
        {
            return null;
        }

        long uplink = proxy.UplinkBytes;
        long downlink = proxy.DownlinkBytes;
        foreach ((_, string tag, _) in ServiceCategories.Routed)
        {
            if (byTag.TryGetValue(tag, out TrafficCounters routed))
            {
                uplink = SaturatingAdd(uplink, routed.UplinkBytes);
                downlink = SaturatingAdd(downlink, routed.DownlinkBytes);
            }
        }

        return new TrafficCounters(uplink, downlink);
    }

    private static long SaturatingAdd(long a, long b) => long.MaxValue - a < b ? long.MaxValue : a + b;

    /// <summary>
    /// 发一次 unary 调用并取回唯一那一帧的 payload。两个查询共用同一条
    /// 连接与同一套帧校验，区别只有请求里的 pattern。
    /// </summary>
    private async Task<byte[]?> FetchAsync(int apiPort, byte[] requestBody, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (apiPort is < 1 or > 65535) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{apiPort}{MethodPath}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(requestBody)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
        request.Headers.TryAddWithoutValidation("grpc-timeout", "2000m");

        try
        {
            using HttpResponseMessage response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode != HttpStatusCode.OK || response.Version != HttpVersion.Version20
                || mediaType is not ("application/grpc" or "application/grpc+proto")) return null;

            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
            // 不接受压缩帧，且在分配前限制大小。
            if (header[0] != 0 || length > MaxMessageBytes) return null;

            byte[] payload = new byte[(int)length];
            await stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            // unary 必须恰好一帧。读到 EOF 后 .NET 才保证 trailers 已到达。
            if (await stream.ReadAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false) != 0
                || !HasSuccessStatus(response)) return null;

            return payload;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            _log.Warn(nameof(XrayTrafficStatsService), $"Stats query failed: {ex.Message}");
            return null;
        }
    }

    private static bool HasSuccessStatus(HttpResponseMessage response)
    {
        // 有响应消息的成功调用，状态必须来自 trailers，不能把 HTTP 200 当作 RPC 成功。
        return response.TrailingHeaders.TryGetValues("grpc-status", out IEnumerable<string>? values)
            && values.SequenceEqual(new[] { "0" });
    }

    /// <summary>
    /// 所有出站的计数器：一次调用就能把每个 tag 的上下行都取回来。速率图与类别归因共用它。
    /// </summary>
    private static readonly byte[] AllOutboundsRequestBody = CreateRequest("outbound>>>"u8);

    public async Task<IReadOnlyDictionary<string, TrafficCounters>?> QueryByTagAsync(
        int apiPort, CancellationToken ct)
    {
        byte[]? payload = await FetchAsync(apiPort, AllOutboundsRequestBody, ct).ConfigureAwait(false);
        return payload is null ? null : ParseByTag(payload);
    }

    /// <summary>
    /// 把每条 stat 的名字拆成 (tag, counter)。速率图（经 <see cref="ParseProxied"/>）与类别
    /// 归因都走这里；跑真实 xray 的回归（BundledCore_LoopbackTrafficMatchesCli...）盯着它。
    /// 同一个计数器出现两次、或任一 stat 损坏，整个响应作废。
    /// </summary>
    internal static IReadOnlyDictionary<string, TrafficCounters>? ParseByTag(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessageBytes) return null;
        var uplink = new Dictionary<string, long>(StringComparer.Ordinal);
        var downlink = new Dictionary<string, long>(StringComparer.Ordinal);

        while (!payload.IsEmpty)
        {
            if (!ReadTag(ref payload, out int field, out int wire)) return null;
            if (field != 1)
            {
                if (!Skip(ref payload, wire)) return null;
                continue;
            }

            if (wire != 2 || !ReadBytes(ref payload, out ReadOnlySpan<byte> stat)) return null;
            if (!ReadNamedStat(stat, out string name, out long value)) return null;
            if (name.Length == 0) continue;

            if (!TrySplitOutboundName(name, out string tag, out bool isUplink)) continue;
            Dictionary<string, long> target = isUplink ? uplink : downlink;
            // 同一个计数器出现两次说明响应是伪造或损坏的，整体丢弃。
            if (!target.TryAdd(tag, value)) return null;
        }

        var result = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
        foreach (string tag in uplink.Keys)
        {
            if (downlink.TryGetValue(tag, out long down))
            {
                result[tag] = new TrafficCounters(uplink[tag], down);
            }
        }

        return result;
    }

    /// <summary>拆 <c>outbound&gt;&gt;&gt;TAG&gt;&gt;&gt;traffic&gt;&gt;&gt;uplink|downlink</c>。</summary>
    private static bool TrySplitOutboundName(string name, out string tag, out bool isUplink)
    {
        tag = "";
        isUplink = false;
        const string prefix = "outbound>>>";
        const string middle = ">>>traffic>>>";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;

        int middleIndex = name.IndexOf(middle, prefix.Length, StringComparison.Ordinal);
        if (middleIndex <= prefix.Length) return false;

        string suffix = name[(middleIndex + middle.Length)..];
        if (suffix == "uplink") isUplink = true;
        else if (suffix != "downlink") return false;

        tag = name[prefix.Length..middleIndex];
        return tag.Length > 0;
    }

    private static bool ReadNamedStat(ReadOnlySpan<byte> stat, out string name, out long value)
    {
        name = "";
        value = 0; // proto3 omits value when a counter is zero.
        bool sawName = false;
        bool sawValue = false;
        while (!stat.IsEmpty)
        {
            if (!ReadTag(ref stat, out int field, out int wire)) return false;
            if (field == 1)
            {
                if (sawName || wire != 2 || !ReadBytes(ref stat, out ReadOnlySpan<byte> raw)) return false;
                sawName = true;
                if (raw.Length > 256) return false;
                name = System.Text.Encoding.UTF8.GetString(raw);
            }
            else if (field == 2)
            {
                if (sawValue || wire != 0 || !ReadVarint(ref stat, out ulong number) || number > long.MaxValue) return false;
                sawValue = true;
                value = (long)number;
            }
            else if (!Skip(ref stat, wire)) return false;
        }

        return true;
    }

    private static byte[] CreateRequest(ReadOnlySpan<byte> pattern)
    {
        byte[] body = new byte[7 + pattern.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), (uint)(2 + pattern.Length));
        body[5] = 10; // QueryStatsRequest.pattern, field 1 / wire 2.
        body[6] = (byte)pattern.Length;
        pattern.CopyTo(body.AsSpan(7));
        // reset (field 2) omitted: proto3 default false; never change cumulative counters.
        return body;
    }

    private static bool ReadTag(ref ReadOnlySpan<byte> data, out int field, out int wire)
    {
        bool valid = ReadVarint(ref data, out ulong tag) && tag is >= 8 and <= uint.MaxValue;
        field = (int)(tag >> 3);
        wire = (int)(tag & 7);
        return valid;
    }

    private static bool ReadVarint(ref ReadOnlySpan<byte> data, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 70 && !data.IsEmpty; shift += 7)
        {
            byte current = data[0];
            data = data[1..];
            if (shift == 63 && current > 1) return false;
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    private static bool ReadBytes(ref ReadOnlySpan<byte> data, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!ReadVarint(ref data, out ulong length) || length > (ulong)data.Length) return false;
        value = data[..(int)length];
        data = data[(int)length..];
        return true;
    }

    private static bool Skip(ref ReadOnlySpan<byte> data, int wire)
    {
        if (wire == 0) return ReadVarint(ref data, out _);
        if (wire == 2) return ReadBytes(ref data, out _);
        int size = wire == 1 ? 8 : wire == 5 ? 4 : -1;
        if (size < 0 || data.Length < size) return false;
        data = data[size..];
        return true;
    }
}
