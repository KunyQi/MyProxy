using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 统计通道的配置生成。这几条不是风格断言：少任何一条，速率图就会安静地一直显示 0。
/// </summary>
[TestClass]
public sealed class XrayStatsConfigTests
{
    private const int LocalPort = 10809;
    private const int ApiPort = 10810;
    private const int ProbePort = 10811;

    [TestMethod]
    public void WithoutStatsPort_ConfigIsByteIdenticalToPreStatsShape()
    {
        XrayRootConfig config = Generate(statsApiPort: null);

        Assert.IsNull(config.Stats);
        Assert.IsNull(config.Api);
        Assert.AreEqual(1, config.Inbounds.Count, "不开统计时不得多出 api inbound");
        Assert.IsFalse(config.Policy.System.StatsOutboundUplink);
        Assert.IsFalse(config.Policy.System.StatsOutboundDownlink);
        Assert.IsFalse(
            config.Routing.Rules.Exists(r => r.InboundTag is not null),
            "不开统计时不得多出 api 路由规则");
    }

    [TestMethod]
    public void ApiRuleMustComeFirst_OtherwisePrivateIpRuleSwallowsTheQuery()
    {
        XrayRootConfig config = Generate(ApiPort);

        RoutingRule first = config.Routing.Rules[0];
        CollectionAssert.AreEqual(new[] { "api" }, first.InboundTag, "api 规则必须是第一条");
        Assert.AreEqual("api", first.OutboundTag);

        // 这条就是会吃掉查询的那一条：路由首命中即停，它一旦排在 api 规则前面，
        // 发往 127.0.0.1 的统计查询会被整个 blocked 掉，速率图永远是 0。
        int privateRuleIndex = config.Routing.Rules.FindIndex(
            r => r.Ip is not null && r.Ip.Contains("geoip:private"));
        Assert.IsTrue(privateRuleIndex > 0, "geoip:private 规则必须排在 api 规则之后");
    }

    [TestMethod]
    public void StatsSectionsAreAllThree_NoneMayBeOmitted()
    {
        XrayRootConfig config = Generate(ApiPort);

        Assert.IsNotNull(config.Stats);
        Assert.IsNotNull(config.Api);
        CollectionAssert.AreEqual(new[] { "StatsService" }, config.Api!.Services);
        Assert.AreEqual("api", config.Api.Tag);

        // outbound 维度默认是关的；速率图要的正是这个维度。
        Assert.IsTrue(config.Policy.System.StatsOutboundUplink);
        Assert.IsTrue(config.Policy.System.StatsOutboundDownlink);

        InboundConfig api = config.Inbounds.Find(i => i.Tag == "api")!;
        Assert.IsNotNull(api);
        Assert.AreEqual("dokodemo-door", api.Protocol);
        Assert.AreEqual("127.0.0.1", api.Listen);
        Assert.AreEqual(ApiPort, api.Port);
        Assert.AreEqual("127.0.0.1", api.Settings.Address);
        Assert.IsNull(api.Sniffing, "查询通道不嗅探");
    }

    [TestMethod]
    public void WithoutProbePort_NoProbeInboundAndNoProbeRule()
    {
        XrayRootConfig config = Generate(ApiPort);

        Assert.IsFalse(config.Inbounds.Exists(i => i.Tag == XrayConfigGenerator.ProbeInboundTag));
        Assert.IsFalse(config.Routing.Rules.Exists(
            r => r.InboundTag is not null && r.InboundTag.Contains(XrayConfigGenerator.ProbeInboundTag)));
    }

    [DataTestMethod]
    [DataRow(ProxyMode.Rule)]
    [DataRow(ProxyMode.Global)]
    public void ProbeInbound_IsForcedIntoTheProxyAheadOfEveryDomainOrIpRule(ProxyMode mode)
    {
        // 探测必须先于直连规则，才能验证隧道而不是本机网络。
        XrayRootConfig config = XrayConfigGenerator.Generate(
            ValidProfile(), mode, LocalPort, @"C:\tmp\xray.log", statsApiPort: ApiPort, probePort: ProbePort);

        InboundConfig probe = config.Inbounds.Find(i => i.Tag == XrayConfigGenerator.ProbeInboundTag)!;
        Assert.IsNotNull(probe);
        Assert.AreEqual("http", probe.Protocol);
        Assert.AreEqual("127.0.0.1", probe.Listen);
        Assert.AreEqual(ProbePort, probe.Port);
        Assert.IsNull(probe.Settings.Address);
        Assert.IsNull(probe.Sniffing, "按入站路由，不需要嗅探");

        List<RoutingRule> probeRules = config.Routing.Rules
            .Where(r => r.InboundTag is not null && r.InboundTag.Contains(XrayConfigGenerator.ProbeInboundTag))
            .ToList();
        Assert.AreEqual(2, probeRules.Count);
        Assert.AreSame(config.Routing.Rules[0], probeRules[0], "探测规则必须排在最前面");
        Assert.AreSame(config.Routing.Rules[1], probeRules[1]);

        // 第一条：只放行探测主机走代理。
        Assert.AreEqual("proxy", probeRules[0].OutboundTag);
        CollectionAssert.AreEquivalent(
            XrayConfigGenerator.ProbeHosts.Select(host => "full:" + host).ToList(),
            probeRules[0].Domain);

        // 第二条：probe-in 的其余目标一律拦下，不然它就是一个绕开私网/BT 拦截的无鉴权代理。
        Assert.AreEqual("blocked", probeRules[1].OutboundTag);
        Assert.IsNull(probeRules[1].Domain);
        Assert.IsNull(probeRules[1].Ip);
    }

    [TestMethod]
    public void EveryHostTheProbesContact_IsAllowedThroughTheProbeInbound()
    {
        // 改了探测地址却忘了同步放行名单，探测会被第二条规则拦下，连接永远「测不通」。
        foreach (string url in DeploymentConfiguration.ConnectivityCheckUrls.Append(NetworkService.DefaultTraceUrl))
        {
            CollectionAssert.Contains(XrayConfigGenerator.ProbeHosts.ToList(), new Uri(url).Host, url);
        }
    }

    [TestMethod]
    public void ProbePort_IsValidatedLikeTheOtherPorts()
    {
        foreach (int bad in new[] { 80, 70000, LocalPort, ApiPort })
        {
            Assert.ThrowsException<ArgumentException>(
                () => XrayConfigGenerator.Generate(
                    ValidProfile(), ProxyMode.Rule, LocalPort, @"C:\tmp\xray.log",
                    statsApiPort: ApiPort, probePort: bad),
                $"probePort={bad}");
        }
    }

    [TestMethod]
    public void StatsSerializesStatsAsEmptyObject()
    {
        string json = JsonSerializer.Serialize(Generate(ApiPort), XrayConfigGenerator.CreateJsonOptions());

        // xray 认的是空对象；写成 null 或省略都不会打开计数器。
        StringAssert.Contains(json, "\"stats\": {}");
        StringAssert.Contains(json, "\"StatsService\"");
    }

    [TestMethod]
    public void HttpInboundKeepsSniffingAndOmitsAddress()
    {
        string json = JsonSerializer.Serialize(Generate(ApiPort), XrayConfigGenerator.CreateJsonOptions());
        InboundConfig http = Generate(ApiPort).Inbounds.Find(i => i.Tag == "http-in")!;

        Assert.IsNotNull(http.Sniffing);
        Assert.IsNull(http.Settings.Address, "http inbound 不得带 dokodemo-door 的 address");
        Assert.IsFalse(json.Contains("\"address\": null"), "为 null 的字段必须被整个省略");
    }

    [TestMethod]
    public void ApiPortCollidingWithLocalPort_IsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => Generate(LocalPort));
    }

    [TestMethod]
    public void DumpsConfigForLiveXrayVerification()
    {
        // 目视/联调工具，默认 no-op：MYPROXY_CONFIG_DUMP 指到一个路径才落盘。
        string? dump = Environment.GetEnvironmentVariable("MYPROXY_CONFIG_DUMP");
        if (string.IsNullOrWhiteSpace(dump))
        {
            return;
        }

        // 与生产同形：统计入口与探测入站都在。
        XrayRootConfig config = XrayConfigGenerator.Generate(
            ValidProfile(), ProxyMode.Rule, LocalPort, @"C:\tmp\xray.log",
            statsApiPort: ApiPort, probePort: ProbePort);
        string json = JsonSerializer.Serialize(config, XrayConfigGenerator.CreateJsonOptions());
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, json);
    }

    private static XrayRootConfig Generate(int? statsApiPort) => XrayConfigGenerator.Generate(
        ValidProfile(),
        ProxyMode.Rule,
        LocalPort,
        @"C:\tmp\xray.log",
        statsApiPort);

    private static ServerProfile ValidProfile() => new()
    {
        Server = "203.0.113.10",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Security = "reality",
        PublicKey = "m9sVDn2Jh2BhL5ScrP3gaCoXloi5puFKz7as3TRmxFA",
        ShortId = "ab12cd34",
        Sni = "www.microsoft.com",
        Fingerprint = "chrome"
    };
}
