namespace MyProxy.Core;

/// <summary>xray 的累计字节计数器，单调递增（进程重启则归零）。</summary>
public readonly record struct TrafficCounters(long UplinkBytes, long DownlinkBytes)
{
    public static readonly TrafficCounters Zero = new(0, 0);
}

/// <summary>一次采样算出的瞬时速率，字节/秒。</summary>
public readonly record struct TrafficRate(double UplinkBytesPerSecond, double DownlinkBytesPerSecond)
{
    public static readonly TrafficRate Zero = new(0, 0);
}

/// <summary>
/// 把累计计数器序列换算成速率，并保留最近若干个样本供 Sparkline 绘制。
///
/// 纯逻辑、无 I/O、无时钟：时间戳由调用方注入，于是所有边界（计数器归零、
/// 采样间隔抖动、xray 重启）都能在单测里确定性地复现。
/// </summary>
public sealed class TrafficRateMonitor
{
    /// <summary>Sparkline 的横轴长度。60 个样本 × 1s ≈ 最近一分钟。</summary>
    public const int DefaultCapacity = 60;

    /// <summary>
    /// 间隔小于它就不出速率：两次采样挨得太近时，除以一个接近 0 的 dt 会把
    /// 正常流量放大成一根假尖峰。
    /// </summary>
    private const double MinIntervalSeconds = 0.05;

    private readonly int _capacity;
    private readonly Queue<TrafficRate> _samples;
    private readonly object _sync = new();

    private TrafficCounters _baseline;
    private long _baselineTimestampMs;
    private bool _hasBaseline;
    private TrafficRate _current = TrafficRate.Zero;

    public TrafficRateMonitor(int capacity = DefaultCapacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _samples = new Queue<TrafficRate>(capacity);
    }

    /// <summary>
    /// 最近一拍的速率。读也要在锁内：<see cref="TrafficRate"/> 是含两个 double 的
    /// 16 字节结构，赋值不是原子的，UI 线程在采集线程写到一半时读会拿到
    /// 「上行来自新样本、下行还是旧样本」的组合。
    /// </summary>
    public TrafficRate Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>最近的速率样本，按时间从旧到新。样本不足时不补零，由绘制方决定怎么对齐。</summary>
    public IReadOnlyList<TrafficRate> Samples
    {
        get
        {
            lock (_sync)
            {
                return _samples.ToArray();
            }
        }
    }

    /// <summary>
    /// 记录一次采样。
    /// </summary>
    /// <param name="counters">本次读到的累计值。</param>
    /// <param name="timestampMs">单调时钟毫秒值（<c>Environment.TickCount64</c>），不是墙钟。</param>
    /// <returns>本次算出的速率；首次采样没有基准，返回 <see cref="TrafficRate.Zero"/>。</returns>
    public TrafficRate Add(TrafficCounters counters, long timestampMs)
    {
        lock (_sync)
        {
            TrafficRate rate = TrafficRate.Zero;

            if (_hasBaseline)
            {
                double seconds = (timestampMs - _baselineTimestampMs) / 1000.0;
                if (seconds >= MinIntervalSeconds)
                {
                    rate = new TrafficRate(
                        RatePerSecond(counters.UplinkBytes, _baseline.UplinkBytes, seconds),
                        RatePerSecond(counters.DownlinkBytes, _baseline.DownlinkBytes, seconds));
                }
            }

            // 基准无条件推进——包括间隔过短而未出速率的那一次。留着旧基准会让
            // 下一次采样把两段时间的增量算成一次，图上凭空多一根尖峰。
            _baseline = counters;
            _baselineTimestampMs = timestampMs;
            _hasBaseline = true;

            return Push(rate);
        }
    }

    /// <summary>断开连接时调用：清空基准与历史，下一次采样重新从零开始。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _hasBaseline = false;
            _baseline = TrafficCounters.Zero;
            _baselineTimestampMs = 0;
            _samples.Clear();
            _current = TrafficRate.Zero;
        }
    }

    /// <summary>
    /// 计数器变小意味着 xray 重启过（它的计数器只增不减），差分会是个大负数。
    /// 按 0 计，避免图上出现一根向下的假尖峰；基准由调用方推进到新值，下一拍自然恢复。
    /// </summary>
    private static double RatePerSecond(long current, long baseline, double seconds)
    {
        long delta = current - baseline;
        return delta <= 0 ? 0 : delta / seconds;
    }

    private TrafficRate Push(TrafficRate rate)
    {
        _samples.Enqueue(rate);
        while (_samples.Count > _capacity)
        {
            _samples.Dequeue();
        }

        _current = rate;
        return rate;
    }
}
