namespace MyProxy.Core;

/// <summary>
/// 把 xray 的<b>累计</b>per-tag 计数器折成每小时的<b>增量</b>，按服务类别归并。
///
/// <para>
/// 纯逻辑，无 I/O、无时钟：当前时间由调用方传进来。这让「跨小时边界」「核心
/// 重启导致计数器归零」「重连后 tag 消失又出现」这些只在真实运行里偶然发生的
/// 情况，全都能在 JVM/单测里被摆出来——这正是本项目把难缠的不变量抽成纯函数
/// 的一贯做法。
/// </para>
///
/// <para>
/// 计数器归零的处理与服务端 <c>observability.counter_delta</c> 必须一致：
/// 新值直接当增量。既不凭空造出流量（<c>current - previous</c> 会变成负数），
/// 也不丢掉重启后的流量（返回 0 会）。
/// </para>
/// </summary>
public sealed class UsageAccumulator
{
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pending = new(StringComparer.Ordinal);
    private string _bucket = "";

    /// <summary>当前累积所属的小时桶；还没有任何采样时为空串。</summary>
    public string Bucket => _bucket;

    public bool HasPending => _pending.Count > 0;

    /// <summary>把一次采样折进当前小时桶。</summary>
    public void Observe(IReadOnlyDictionary<string, TrafficCounters> byTag, DateTimeOffset now)
    {
        string bucket = HourBucket(now);
        // 手上没有待上报量时，桶跟着时钟走：否则安静了几个小时之后的第一笔流量会被
        // 记到几个小时前的那一桶里。有待上报量时由调用方先 Flush 再 Observe。
        if (_bucket.Length == 0 || (_pending.Count == 0 && _bucket != bucket))
        {
            _bucket = bucket;
        }

        foreach ((string tag, TrafficCounters counters) in byTag)
        {
            string category = ServiceCategories.CategoryForTag(tag);
            if (category.Length == 0)
            {
                // 未知 tag（direct / blocked / 以后新加的）不归因：把它们塞进
                // 某个类别会让数字看起来更完整，代价是它是错的。
                continue;
            }

            long total = Saturate(counters.UplinkBytes) + Saturate(counters.DownlinkBytes);
            long previous = _lastSeen.TryGetValue(tag, out long value) ? value : 0;
            long delta = total < previous ? total : total - previous;
            _lastSeen[tag] = total;

            if (delta > 0)
            {
                _pending[category] = _pending.TryGetValue(category, out long accumulated)
                    ? accumulated + delta
                    : delta;
            }
        }
    }

    /// <summary>
    /// 该不该现在上报：跨了小时边界，且手上确实有东西。
    ///
    /// 只在跨桶时上报，而不是每次采样都发，是因为服务端只接受当前小时或上一个
    /// 小时的桶——按小时攒一次既落在窗口内，也把请求数压到每小时一个。
    /// </summary>
    public bool ShouldFlush(DateTimeOffset now) =>
        _pending.Count > 0 && _bucket.Length > 0 && HourBucket(now) != _bucket;

    /// <summary>
    /// 取出待上报的一桶并清空。返回的 bucket 是这批数据<b>所属</b>的小时，
    /// 不是现在这一小时。
    /// </summary>
    public (string Bucket, IReadOnlyDictionary<string, long> Categories) Flush(DateTimeOffset now)
    {
        var snapshot = new Dictionary<string, long>(_pending, StringComparer.Ordinal);
        string bucket = _bucket;
        _pending.Clear();
        _bucket = HourBucket(now);
        return (bucket, snapshot);
    }

    /// <summary>
    /// 核心重启：忘掉所有 tag 的基线，但<b>保留</b>已经攒下的待上报量。
    /// 忘掉基线是因为新进程的计数器从零开始；保留待上报量是因为那些字节
    /// 确实搬过，丢掉它们等于让每次重连都吃掉一段历史。
    /// </summary>
    public void ResetBaseline()
    {
        _lastSeen.Clear();
    }

    /// <summary>
    /// 服务端只收当前小时与上一个小时的桶（<c>app.py</c> 的 usage 校验）。更早的桶发出去
    /// 必然 400——睡眠、休眠之后最常见。
    /// </summary>
    public static bool IsAcceptedByServer(string bucket, DateTimeOffset now) =>
        bucket == HourBucket(now) || bucket == HourBucket(now.AddHours(-1));

    public static string HourBucket(DateTimeOffset moment)
    {
        DateTimeOffset utc = moment.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }

    private static long Saturate(long value) => value < 0 ? 0 : value;
}
