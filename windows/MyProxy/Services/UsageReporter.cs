using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MyProxy.Core;

namespace MyProxy.Services;

public interface IUsageReporter
{
    /// <summary>把一次 per-tag 采样折进当前小时桶，必要时上报上一个小时。</summary>
    Task ObserveAsync(IReadOnlyDictionary<string, TrafficCounters> byTag, CancellationToken ct);

    /// <summary>核心重启后调用：忘掉 tag 基线，保留已攒下的待上报量。</summary>
    void ResetBaseline();

    /// <summary>
    /// 不等跨小时，把手上待上报的这一桶现在就发出去。停止、意外退出时调用：以前待上报量
    /// 只在跨小时时才发，连半小时就停的那次、常驻托盘的设备关机前的最后不满一小时，
    /// 都整桶丢了。服务端对同一小时的类别是累加写入，提前发一部分是安全的。
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Observability Plane 的客户端侧：按服务类别上报聚合字节数。
///
/// <para>
/// <b>它能说的只有「这一小时 video 搬了 5MB」。</b> 没有域名、没有 URL、
/// 没有单条连接——归因完全来自 xray 出站 tag 的字节计数器，客户端从头到尾
/// 不需要记录「访问了哪里」。
/// </para>
///
/// <para>
/// 它也<b>无法改变「用了多少」</b>：服务端的总量来自对 x-ui 累计计数器的扫描，
/// 那是对数据面的测量。这里发上去的数字只影响类别这一维度。
/// </para>
/// </summary>
public sealed class UsageReporter : IUsageReporter
{
    private readonly UsageAccumulator _accumulator = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IApiEndpoint _endpoint;
    private readonly HttpClient? _injectedHttp;
    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly Func<DateTimeOffset> _clock;

    public UsageReporter(
        IApiEndpoint endpoint,
        IStorageService storage,
        ILogService log,
        HttpClient? http = null,
        Func<DateTimeOffset>? clock = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _injectedHttp = http;
        _storage = storage;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private string ApiBaseUrl => _endpoint.BaseUrl;

    private HttpClient Http
        => _injectedHttp ?? _endpoint.Client(nameof(UsageReporter), TimeSpan.FromSeconds(10));

    public async Task ObserveAsync(IReadOnlyDictionary<string, TrafficCounters> byTag, CancellationToken ct)
    {
        DateTimeOffset now = _clock();
        string bucket;
        IReadOnlyDictionary<string, long> categories;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先把上一小时的那桶取出来，再折这次采样：这次的增量属于现在这一小时。
            // 顺序反过来，跨小时后的第一笔（睡眠之后可能是几个小时的量）会被记进旧桶，
            // 再随旧桶一起被服务端拒掉。
            if (_accumulator.ShouldFlush(now))
            {
                (bucket, categories) = _accumulator.Flush(now);
            }
            else
            {
                (bucket, categories) = ("", new Dictionary<string, long>());
            }

            _accumulator.Observe(byTag, now);
        }
        finally
        {
            _gate.Release();
        }

        if (categories.Count == 0 || bucket.Length == 0)
        {
            return;
        }

        if (!UsageAccumulator.IsAcceptedByServer(bucket, now))
        {
            _log.Warn(nameof(UsageReporter), $"Dropping usage bucket {bucket}: older than the server accepts");
            return;
        }

        await PostAsync(bucket, categories, ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock();
        string bucket;
        IReadOnlyDictionary<string, long> categories;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_accumulator.HasPending)
            {
                return;
            }

            (bucket, categories) = _accumulator.Flush(now);
        }
        finally
        {
            _gate.Release();
        }

        if (!UsageAccumulator.IsAcceptedByServer(bucket, now))
        {
            _log.Warn(nameof(UsageReporter), $"Dropping usage bucket {bucket}: older than the server accepts");
            return;
        }

        await PostAsync(bucket, categories, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 与 <see cref="ObserveAsync"/> 共用同一把锁：控制器在重启内核前调用它，而采样
    /// 在另一条循环上，累加器的字典不能被两边同时改。
    /// </summary>
    public void ResetBaseline()
    {
        _gate.Wait();
        try
        {
            _accumulator.ResetBaseline();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PostAsync(
        string bucket,
        IReadOnlyDictionary<string, long> categories,
        CancellationToken ct)
    {
        string? token = _storage.LoadDevice()?.DeviceToken;
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/device/usage");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            string body = JsonSerializer.Serialize(new
            {
                bucketStart = bucket,
                categories
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // 最可能的原因是本构建的类别词表比服务端新。丢掉这一桶而不是
                // 重试：服务端只接受当前与上一个小时，重试到下一小时必然还是 400。
                _log.Warn(nameof(UsageReporter), "Usage report rejected; dropping the bucket");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn(nameof(UsageReporter), $"Usage report failed: {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 观测是尽力而为：上报不上去绝不能影响连接本身。
            _log.Warn(nameof(UsageReporter), $"Usage report failed: {ex.GetType().Name}");
        }
    }
}
