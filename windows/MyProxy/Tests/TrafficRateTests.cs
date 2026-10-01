using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 速率换算与统计解析。两者都是纯逻辑，边界全部可确定性复现——
/// 而它们恰恰是「速率图安静地一直显示 0」这类故障最容易藏身的地方。
/// </summary>
[TestClass]
public sealed class TrafficRateTests
{
    [TestMethod]
    public void FirstSample_HasNoBaseline_SoItReportsZero()
    {
        var monitor = new TrafficRateMonitor();

        TrafficRate rate = monitor.Add(new TrafficCounters(1_000, 5_000), 1_000);

        Assert.AreEqual(0, rate.UplinkBytesPerSecond);
        Assert.AreEqual(0, rate.DownlinkBytesPerSecond);
    }

    [TestMethod]
    public void SecondSample_DividesDeltaByElapsedSeconds()
    {
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(1_000, 5_000), 1_000);

        TrafficRate rate = monitor.Add(new TrafficCounters(3_000, 15_000), 3_000);

        Assert.AreEqual(1_000, rate.UplinkBytesPerSecond, 0.001, "2000 字节 / 2 秒");
        Assert.AreEqual(5_000, rate.DownlinkBytesPerSecond, 0.001);
        Assert.AreEqual(rate, monitor.Current);
    }

    [TestMethod]
    public void CounterGoingBackwards_ReadsAsZero_NotAsANegativeSpike()
    {
        // xray 重启后计数器归零。差分是个大负数，照算会在图上留下一根向下的假尖峰。
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(900_000, 900_000), 1_000);

        TrafficRate rate = monitor.Add(new TrafficCounters(120, 340), 2_000);

        Assert.AreEqual(0, rate.UplinkBytesPerSecond);
        Assert.AreEqual(0, rate.DownlinkBytesPerSecond);
    }

    [TestMethod]
    public void AfterAReset_TheNextSampleResumesFromTheNewBaseline()
    {
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(900_000, 900_000), 1_000);
        monitor.Add(new TrafficCounters(120, 340), 2_000);

        // 基准已推进到归零后的新值，于是下一拍立刻恢复出真实速率。
        TrafficRate rate = monitor.Add(new TrafficCounters(1_120, 2_340), 3_000);

        Assert.AreEqual(1_000, rate.UplinkBytesPerSecond, 0.001);
        Assert.AreEqual(2_000, rate.DownlinkBytesPerSecond, 0.001);
    }

    [TestMethod]
    public void SamplesTooCloseTogether_DoNotProduceAnInflatedSpike()
    {
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(0, 0), 1_000);

        // 10ms 的间隔：照除会把 1KB 放大成 100KB/s。
        TrafficRate rate = monitor.Add(new TrafficCounters(1_024, 1_024), 1_010);

        Assert.AreEqual(0, rate.UplinkBytesPerSecond);
    }

    [TestMethod]
    public void ASkippedTickStillAdvancesTheBaseline()
    {
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(0, 0), 1_000);
        monitor.Add(new TrafficCounters(1_000, 1_000), 1_010);   // 间隔过短，跳过

        // 基准必须已经推进到 1_000：否则这一拍会把两段增量算成一段，凭空多一根尖峰。
        TrafficRate rate = monitor.Add(new TrafficCounters(2_000, 2_000), 2_010);

        Assert.AreEqual(1_000, rate.UplinkBytesPerSecond, 0.001);
    }

    [TestMethod]
    public void SamplesAreCappedAtCapacity_OldestFirst()
    {
        var monitor = new TrafficRateMonitor(capacity: 3);
        for (int i = 0; i <= 5; i++)
        {
            monitor.Add(new TrafficCounters(i * 1_000, i * 1_000), 1_000 * (i + 1));
        }

        Assert.AreEqual(3, monitor.Samples.Count);
        Assert.AreEqual(monitor.Current, monitor.Samples[^1], "最后一个样本就是当前速率");
    }

    [TestMethod]
    public void Reset_ClearsHistoryAndBaseline()
    {
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(0, 0), 1_000);
        monitor.Add(new TrafficCounters(5_000, 5_000), 2_000);

        monitor.Reset();

        Assert.AreEqual(0, monitor.Samples.Count);
        Assert.AreEqual(TrafficRate.Zero, monitor.Current);

        // 重置后第一拍重新是「无基准」，不得拿重置前的旧值算差分。
        Assert.AreEqual(TrafficRate.Zero, monitor.Add(new TrafficCounters(9_000, 9_000), 3_000));
    }

    [TestMethod]
    public void ResumingAfterASuspend_MustNotChargeTheWholeGapToOneTick()
    {
        // 挂起期间 xray 的计数器一直在涨。恢复时若留着旧基准，第一拍会把整段挂起
        // 时间的累计量除以一个很短的 dt，图上炸出一根假尖峰——所以恢复必须先 Reset。
        var monitor = new TrafficRateMonitor();
        monitor.Add(new TrafficCounters(0, 0), 1_000);
        monitor.Add(new TrafficCounters(1_000, 2_000), 2_000);

        monitor.Reset();   // ConnectionController.SetTrafficSamplingSuspended(false) 会做这件事

        // 挂起了 10 分钟，期间累计涨了 600MB
        TrafficRate first = monitor.Add(new TrafficCounters(600_000_000, 600_000_000), 602_000);
        Assert.AreEqual(TrafficRate.Zero, first, "恢复后的第一拍没有基准，必须报 0");

        TrafficRate second = monitor.Add(new TrafficCounters(600_001_000, 600_002_000), 603_000);
        Assert.AreEqual(1_000, second.UplinkBytesPerSecond, 0.001, "第二拍起才是真实速率");
        Assert.AreEqual(2_000, second.DownlinkBytesPerSecond, 0.001);
    }

}
