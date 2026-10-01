using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public class UsageAccumulatorTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 20, 12, 5, 0, TimeSpan.Zero);

    private static Dictionary<string, TrafficCounters> Sample(params (string Tag, long Up, long Down)[] items)
    {
        var result = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
        foreach ((string tag, long up, long down) in items)
        {
            result[tag] = new TrafficCounters(up, down);
        }

        return result;
    }

    [TestMethod]
    public void HourBucket_TruncatesToTheWholeUtcHour()
    {
        Assert.AreEqual("2026-09-20T12:00:00Z", UsageAccumulator.HourBucket(Noon));
        Assert.AreEqual(
            "2026-09-20T02:00:00Z",
            UsageAccumulator.HourBucket(new DateTimeOffset(2026, 9, 20, 12, 30, 0, TimeSpan.FromHours(10))));
    }

    [TestMethod]
    public void CumulativeCounters_BecomeDeltas()
    {
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-video", 100, 200)), Noon);
        accumulator.Observe(Sample(("cat-video", 150, 260)), Noon);

        (string bucket, IReadOnlyDictionary<string, long> categories) =
            accumulator.Flush(Noon.AddHours(1));

        Assert.AreEqual("2026-09-20T12:00:00Z", bucket);
        // 100+200 的首次读数，加上 50+60 的增量。
        Assert.AreEqual(410, categories[ServiceCategories.Video]);
    }

    [TestMethod]
    public void CounterReset_CountsTheNewReadingNotANegative()
    {
        // 核心重启后计数器从零开始，看起来就像倒退。与服务端
        // observability.counter_delta 必须给出同一个答案。
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-video", 1000, 1000)), Noon);
        accumulator.Observe(Sample(("cat-video", 30, 40)), Noon);

        (_, IReadOnlyDictionary<string, long> categories) = accumulator.Flush(Noon.AddHours(1));

        Assert.AreEqual(2000 + 70, categories[ServiceCategories.Video]);
    }

    [TestMethod]
    public void UnknownTags_AreNotAttributedToAnything()
    {
        // 把 direct/blocked 塞进某个类别会让数字看起来更完整，代价是它是错的。
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("proxy", 500, 500), ("direct", 900, 900), ("blocked", 1, 1)), Noon);

        (_, IReadOnlyDictionary<string, long> categories) = accumulator.Flush(Noon.AddHours(1));

        Assert.AreEqual(0, categories.Count);
    }

    [TestMethod]
    public void EveryRoutedTagMapsToAKnownCategory()
    {
        // 服务端对词表外的类别是拒绝而不是折进 other，所以一个映射错的 tag
        // 会让整条上报被 400 掉。
        foreach ((string category, string tag, _) in ServiceCategories.Routed)
        {
            Assert.AreEqual(category, ServiceCategories.CategoryForTag(tag));
            Assert.IsTrue(ServiceCategories.IsKnown(category), category);
        }

        Assert.AreEqual("", ServiceCategories.CategoryForTag("proxy"));
        Assert.AreEqual("", ServiceCategories.CategoryForTag("direct"));
    }

    [TestMethod]
    public void FlushOnlyHappensAfterTheHourTurns()
    {
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-video", 10, 10)), Noon);

        Assert.IsFalse(accumulator.ShouldFlush(Noon.AddMinutes(30)));
        Assert.IsTrue(accumulator.ShouldFlush(Noon.AddHours(1)));
    }

    [TestMethod]
    public void NothingPending_NeverFlushes()
    {
        var accumulator = new UsageAccumulator();
        Assert.IsFalse(accumulator.ShouldFlush(Noon.AddHours(5)));

        accumulator.Observe(Sample(("direct", 10, 10)), Noon);
        Assert.IsFalse(accumulator.ShouldFlush(Noon.AddHours(1)));
    }

    [TestMethod]
    public void FlushMovesTheBucketForwardAndClearsThePending()
    {
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-social", 10, 10)), Noon);
        accumulator.Flush(Noon.AddHours(1));

        Assert.IsFalse(accumulator.HasPending);
        Assert.AreEqual("2026-09-20T13:00:00Z", accumulator.Bucket);
    }

    [TestMethod]
    public void ResetBaseline_KeepsPendingButForgetsTheCounters()
    {
        // 那些字节确实搬过；丢掉它们等于让每次重连都吃掉一段历史。
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-video", 500, 500)), Noon);
        accumulator.ResetBaseline();
        accumulator.Observe(Sample(("cat-video", 100, 100)), Noon);

        (_, IReadOnlyDictionary<string, long> categories) = accumulator.Flush(Noon.AddHours(1));

        Assert.AreEqual(1000 + 200, categories[ServiceCategories.Video]);
    }

    [TestMethod]
    public void CategoriesAccumulateSeparately()
    {
        var accumulator = new UsageAccumulator();
        accumulator.Observe(Sample(("cat-video", 100, 0), ("cat-social", 0, 50), ("cat-messaging", 5, 5)), Noon);

        (_, IReadOnlyDictionary<string, long> categories) = accumulator.Flush(Noon.AddHours(1));

        Assert.AreEqual(100, categories[ServiceCategories.Video]);
        Assert.AreEqual(50, categories[ServiceCategories.Social]);
        Assert.AreEqual(10, categories[ServiceCategories.Messaging]);
    }
}

[TestClass]
public class UsageBucketTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 5, 0, TimeSpan.Zero);

    private static Dictionary<string, TrafficCounters> Video(long bytes) =>
        new(StringComparer.Ordinal) { ["cat-video"] = new TrafficCounters(bytes, 0) };

    [TestMethod]
    public void WithNothingPending_TheBucketFollowsTheClock()
    {
        var accumulator = new UsageAccumulator();
        accumulator.Observe(new Dictionary<string, TrafficCounters>(), Noon);
        accumulator.Observe(Video(100), Noon.AddHours(3));

        Assert.AreEqual("2026-09-20T15:00:00Z", accumulator.Bucket);
    }

    [TestMethod]
    public void ServerAcceptsOnlyTheCurrentAndPreviousHour()
    {
        Assert.IsTrue(UsageAccumulator.IsAcceptedByServer("2026-09-20T12:00:00Z", Noon));
        Assert.IsTrue(UsageAccumulator.IsAcceptedByServer("2026-09-20T11:00:00Z", Noon));
        Assert.IsFalse(UsageAccumulator.IsAcceptedByServer("2026-09-20T10:00:00Z", Noon));
    }

    [TestMethod]
    public async Task ThePreviousHourIsSent_WithoutTheNewHoursTraffic()
    {
        // 跨小时后的这次采样属于新的一小时。以前先 Observe 再 Flush，它被算进了旧桶。
        using ReporterHarness h = await ReporterHarness.CreateAsync(Noon);
        await h.Reporter.ObserveAsync(Video(100), CancellationToken.None);
        h.Now = Noon.AddHours(1);
        await h.Reporter.ObserveAsync(Video(150), CancellationToken.None);
        h.Now = Noon.AddHours(2);
        await h.Reporter.ObserveAsync(Video(150), CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { ("2026-09-20T12:00:00Z", 100L), ("2026-09-20T13:00:00Z", 50L) },
            h.Handler.Posted.ToArray());
    }

    [TestMethod]
    public async Task ABucketTheServerNoLongerAccepts_IsNotSent_AndDoesNotSwallowNewTraffic()
    {
        // 睡眠之后：12 点那桶已在服务端窗口之外，发出去必然 400。以前连同醒来后的
        // 第一笔新流量一起塞进那一桶，一起被拒。
        using ReporterHarness h = await ReporterHarness.CreateAsync(Noon);
        await h.Reporter.ObserveAsync(Video(100), CancellationToken.None);
        h.Now = Noon.AddHours(2);
        await h.Reporter.ObserveAsync(Video(150), CancellationToken.None);
        Assert.AreEqual(0, h.Handler.Posted.Count, "12 点那桶服务端已经不收了");

        h.Now = Noon.AddHours(3);
        await h.Reporter.ObserveAsync(Video(150), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { ("2026-09-20T14:00:00Z", 50L) }, h.Handler.Posted.ToArray());
    }

    [TestMethod]
    public async Task FlushSendsThePartialHourNow_AndTheRestOfTheHourAddsToIt()
    {
        using ReporterHarness h = await ReporterHarness.CreateAsync(Noon);
        await h.Reporter.ObserveAsync(Video(100), CancellationToken.None);
        h.Now = Noon.AddMinutes(25);
        await h.Reporter.FlushAsync(CancellationToken.None);

        h.Now = Noon.AddMinutes(35);
        await h.Reporter.ObserveAsync(Video(150), CancellationToken.None);
        await h.Reporter.FlushAsync(CancellationToken.None);
        await h.Reporter.FlushAsync(CancellationToken.None); // nothing pending: nothing sent

        CollectionAssert.AreEqual(
            new[] { ("2026-09-20T12:00:00Z", 100L), ("2026-09-20T12:00:00Z", 50L) },
            h.Handler.Posted.ToArray());
    }

    private sealed class ReporterHarness : IDisposable
    {
        private readonly string _root;
        private readonly LogService _log;

        private ReporterHarness(string root, LogService log, RecordingHandler handler, DateTimeOffset now)
        {
            _root = root;
            _log = log;
            Handler = handler;
            Now = now;
        }

        public UsageReporter Reporter { get; private set; } = null!;
        public RecordingHandler Handler { get; }
        public DateTimeOffset Now { get; set; }

        public static async Task<ReporterHarness> CreateAsync(DateTimeOffset now)
        {
            string root = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storage = new StorageService(dataRootOverride: root);
            await storage.SaveDeviceAsync(new DeviceConfig
            {
                DeviceId = "dev_test",
                DeviceToken = "tok_test",
                DeviceName = "TEST-PC",
                Platform = "windows",
                ClientVersion = "0.1.0",
                BoundAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            var harness = new ReporterHarness(root, new LogService(Path.Combine(root, "logs")), new RecordingHandler(), now);
            harness.Reporter = new UsageReporter(
                new FixedEndpoint(), storage, harness._log, new HttpClient(harness.Handler), () => harness.Now);
            return harness;
        }

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedEndpoint : IApiEndpoint
    {
        public string BaseUrl => "http://usage.test";

        public HttpClient Client(string key, TimeSpan timeout) => throw new InvalidOperationException("injected");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Bucket, long Video)> Posted { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Posted.Add((
                body.RootElement.GetProperty("bucketStart").GetString()!,
                body.RootElement.GetProperty("categories").GetProperty("video").GetInt64()));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

[TestClass]
public class FeatureFlagCacheTests
{
    [TestMethod]
    public void CachedFlags_RoundTrip()
    {
        FeatureFlags flags = FeatureFlags.FromValues(new Dictionary<string, string>
        {
            [KnownFeatureFlags.UsageCategories] = "true",
            ["rollout"] = "25"
        });

        FeatureFlags restored = FeatureFlags.FromValues(flags.ToDictionary());

        Assert.IsTrue(restored.IsEnabled(KnownFeatureFlags.UsageCategories));
        Assert.AreEqual(25, restored.GetInt64("rollout"));
    }

    [TestMethod]
    public async Task ClearingTheBinding_ForgetsTheCachedFlags()
    {
        // 重新绑定成另一台设备：第一次连接不能按上一台设备的开关生成数据面。
        string root = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new StorageService(dataRootOverride: root);
            await storage.UpdateSettingsAsync(
                settings => settings.FeatureFlags[KnownFeatureFlags.UsageCategories] = "true",
                CancellationToken.None);

            await storage.ClearBindingAsync(CancellationToken.None);

            Assert.AreEqual(0, storage.LoadSettings().FeatureFlags.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TheCacheIsFilteredLikeTheServerCopy()
    {
        FeatureFlags restored = FeatureFlags.FromValues(new Dictionary<string, string>
        {
            [""] = "true",
            [new string('x', 65)] = "true",
            ["ok"] = "true"
        });

        Assert.AreEqual(1, restored.Count);
        Assert.AreSame(FeatureFlags.Empty, FeatureFlags.FromValues(null));
    }
}

[TestClass]
public class CategoryRoutingTests
{
    private static ServerProfile Profile() => new()
    {
        Server = "203.0.113.10",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Security = "reality",
        PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        ShortId = "0123456789abcdef",
        Sni = "www.microsoft.com",
        Fingerprint = "chrome",
        Flow = "xtls-rprx-vision",
        SpiderX = "/"
    };

    private static XrayRootConfig Generate(bool categories, ProxyMode mode = ProxyMode.Rule) =>
        XrayConfigGenerator.Generate(
            Profile(),
            mode,
            10809,
            Path.Combine(Path.GetTempPath(), "xray.log"),
            statsApiPort: 20809,
            categoryAttribution: categories);

    [TestMethod]
    public void Disabled_ProducesTheSameConfigAsBefore()
    {
        // 这条钉住「默认关闭时数据面形状不变」：多一个出站就多一份到同一台
        // 服务器的连接池，那是只应在管理员显式打开时才发生的改动。
        XrayRootConfig config = Generate(categories: false);

        CollectionAssert.AreEqual(
            new[] { "proxy", "direct", "blocked" },
            config.Outbounds.Select(outbound => outbound.Tag).ToArray());
        Assert.IsFalse(config.Routing.Rules.Any(rule => rule.OutboundTag.StartsWith("cat-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Enabled_AddsOneOutboundPerRoutedCategory()
    {
        XrayRootConfig config = Generate(categories: true);

        foreach ((_, string tag, _) in ServiceCategories.Routed)
        {
            Assert.IsTrue(config.Outbounds.Any(outbound => outbound.Tag == tag), tag);
        }

        Assert.AreEqual(3 + ServiceCategories.Routed.Count, config.Outbounds.Count);
    }

    [TestMethod]
    public void CategoryOutboundsPointAtTheSameServer()
    {
        // 归因绝不能改变流量的去向：几个出站除 tag 外必须完全一致。
        XrayRootConfig config = Generate(categories: true);

        // tag 那一行按正则抹平：输出是缩进过的，所以键和值之间有空格，
        // 直接拼字符串去替换会静默匹配不到，断言就变成了永远成立。
        string Serialize(OutboundConfig outbound) => System.Text.RegularExpressions.Regex.Replace(
            JsonSerializer.Serialize(outbound, XrayConfigGenerator.CreateJsonOptions()),
            @"""tag"":\s*""[^""]*""",
            @"""tag"": ""X""");

        string proxy = Serialize(config.Outbounds.Single(outbound => outbound.Tag == "proxy"));
        foreach ((_, string tag, _) in ServiceCategories.Routed)
        {
            Assert.AreEqual(proxy, Serialize(config.Outbounds.Single(outbound => outbound.Tag == tag)), tag);
        }
    }

    [TestMethod]
    public void CategoryRulesComeAfterDirectButBeforeBlocked()
    {
        // 排在 geosite:cn 之前会抢走「国内站点直连」这条模式语义；排在
        // geoip:private 之后就永远轮不到。
        XrayRootConfig config = Generate(categories: true, mode: ProxyMode.Rule);
        List<string> tags = config.Routing.Rules.Select(rule => rule.OutboundTag).ToList();

        int lastDirect = tags.LastIndexOf("direct");
        int firstCategory = tags.FindIndex(tag => tag.StartsWith("cat-", StringComparison.Ordinal));
        int firstBlocked = tags.IndexOf("blocked");

        Assert.IsTrue(lastDirect >= 0 && firstCategory > lastDirect, "categories must follow the direct rules");
        Assert.IsTrue(firstBlocked > firstCategory, "categories must precede the blocked rules");
    }

    [TestMethod]
    public void GlobalModeStillGetsCategoryRules()
    {
        XrayRootConfig config = Generate(categories: true, mode: ProxyMode.Global);
        Assert.IsTrue(config.Routing.Rules.Any(rule => rule.OutboundTag == "cat-video"));
        Assert.IsFalse(config.Routing.Rules.Any(rule => rule.OutboundTag == "direct"));
    }
}

[TestClass]
public class PerTagStatsParsingTests
{
    /// <summary>手搓一个 QueryStatsResponse，字段编号见 command.proto。</summary>
    private static byte[] Response(params (string Name, long Value)[] stats)
    {
        var body = new List<byte>();
        foreach ((string name, long value) in stats)
        {
            var stat = new List<byte>();
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(name);
            stat.Add(10); // Stat.name, field 1 / wire 2
            stat.AddRange(Varint((ulong)raw.Length));
            stat.AddRange(raw);
            if (value != 0)
            {
                stat.Add(16); // Stat.value, field 2 / wire 0
                stat.AddRange(Varint((ulong)value));
            }

            body.Add(10); // QueryStatsResponse.stat, field 1 / wire 2
            body.AddRange(Varint((ulong)stat.Count));
            body.AddRange(stat);
        }

        return body.ToArray();
    }

    private static IEnumerable<byte> Varint(ulong value)
    {
        while (value >= 0x80)
        {
            yield return (byte)(value | 0x80);
            value >>= 7;
        }

        yield return (byte)value;
    }

    [TestMethod]
    public void SplitsEveryTag()
    {
        IReadOnlyDictionary<string, TrafficCounters>? parsed = XrayTrafficStatsService.ParseByTag(Response(
            ("outbound>>>proxy>>>traffic>>>uplink", 10),
            ("outbound>>>proxy>>>traffic>>>downlink", 20),
            ("outbound>>>cat-video>>>traffic>>>uplink", 30),
            ("outbound>>>cat-video>>>traffic>>>downlink", 40)));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(2, parsed!.Count);
        Assert.AreEqual(10, parsed["proxy"].UplinkBytes);
        Assert.AreEqual(40, parsed["cat-video"].DownlinkBytes);
    }

    [TestMethod]
    public void ZeroCountersAreOmittedByProto3AndStillParse()
    {
        IReadOnlyDictionary<string, TrafficCounters>? parsed = XrayTrafficStatsService.ParseByTag(Response(
            ("outbound>>>cat-social>>>traffic>>>uplink", 0),
            ("outbound>>>cat-social>>>traffic>>>downlink", 0)));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0, parsed!["cat-social"].UplinkBytes);
    }

    [TestMethod]
    public void AHalfReportedTagIsDropped()
    {
        // 只有上行没有下行的 tag 会让增量算错一半，不如整条丢掉。
        IReadOnlyDictionary<string, TrafficCounters>? parsed = XrayTrafficStatsService.ParseByTag(Response(
            ("outbound>>>cat-video>>>traffic>>>uplink", 5)));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0, parsed!.Count);
    }

    [TestMethod]
    public void DuplicateCounterRejectsTheWholeResponse()
    {
        IReadOnlyDictionary<string, TrafficCounters>? parsed = XrayTrafficStatsService.ParseByTag(Response(
            ("outbound>>>cat-video>>>traffic>>>uplink", 5),
            ("outbound>>>cat-video>>>traffic>>>uplink", 6)));

        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void NonOutboundCountersAreIgnored()
    {
        IReadOnlyDictionary<string, TrafficCounters>? parsed = XrayTrafficStatsService.ParseByTag(Response(
            ("inbound>>>local>>>traffic>>>uplink", 5),
            ("inbound>>>local>>>traffic>>>downlink", 5),
            ("user>>>someone>>>traffic>>>uplink", 5)));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0, parsed!.Count);
    }

    [TestMethod]
    public void MalformedPayloadIsRejectedWithoutThrowing()
    {
        Assert.IsNull(XrayTrafficStatsService.ParseByTag(new byte[] { 10, 200 }));
        Assert.IsNotNull(XrayTrafficStatsService.ParseByTag(Array.Empty<byte>()));
    }
}
