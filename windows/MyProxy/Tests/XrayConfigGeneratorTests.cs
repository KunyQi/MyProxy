using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Tests;

[TestClass]
public sealed class XrayConfigGeneratorTests
{
    private const int TestLocalPort = 10809;

    private static ServerProfile CreateValidProfile()
    {
        return new ServerProfile
        {
            Server = "203.0.113.10",
            Port = 443,
            Uuid = "123e4567-e89b-12d3-a456-426614174000",
            Security = "reality",
            PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ShortId = "0123456789abcdef",
            Sni = "www.microsoft.com",
            Fingerprint = "chrome",
            Flow = "xtls-rprx-vision",
            SpiderX = "/"
        };
    }

    private static string Serialize(ProxyMode mode, int localPort = TestLocalPort)
    {
        XrayRootConfig root = XrayConfigGenerator.Generate(
            CreateValidProfile(),
            mode,
            localPort,
            @"C:\Users\test\AppData\Local\MyProxy\logs\xray.log");
        return JsonSerializer.Serialize(root, XrayConfigGenerator.CreateJsonOptions());
    }

    [TestMethod]
    public void Global_FirstOutboundIsProxy_WithReality()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global));
        JsonElement outbounds = document.RootElement.GetProperty("outbounds");

        Assert.AreEqual("proxy", outbounds[0].GetProperty("tag").GetString());
        Assert.AreEqual("vless", outbounds[0].GetProperty("protocol").GetString());
        Assert.AreEqual("reality", outbounds[0].GetProperty("streamSettings").GetProperty("security").GetString());
    }

    [TestMethod]
    public void Rule_BlocksPrivateAndBtBeforeDomesticAndMixedAddressDecisions()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Rule));
        JsonElement rules = document.RootElement.GetProperty("routing").GetProperty("rules");

        int cnDomainIndex = FindRuleIndex(rules, "geosite:cn", "direct");
        int cnIpIndex = FindRuleIndex(rules, "geoip:cn", "direct");
        int privateIndex = FindRuleIndex(rules, "geoip:private", "blocked");
        int btIndex = FindRuleIndex(rules, "bittorrent", "blocked");

        Assert.IsTrue(cnDomainIndex >= 0);
        Assert.IsTrue(cnIpIndex >= 0);
        int foreignIndex = FindRuleIndex(rules, "!geoip:cn", "proxy");
        Assert.IsTrue(privateIndex < cnDomainIndex);
        Assert.IsTrue(btIndex < cnDomainIndex);
        Assert.IsTrue(privateIndex < foreignIndex && foreignIndex < cnIpIndex);
        Assert.IsTrue(privateIndex < btIndex);
    }

    [TestMethod]
    public void Global_RulesDoNotContainCnDirect()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global));
        JsonElement rules = document.RootElement.GetProperty("routing").GetProperty("rules");

        Assert.AreEqual(-1, FindRuleIndex(rules, "geosite:cn", "direct"));
        Assert.AreEqual(-1, FindRuleIndex(rules, "geoip:cn", "direct"));
    }

    [TestMethod]
    public void Inbound_IsSingleHttpOnLocalHostWithPassedPort()
    {
        const int localPort = 20809;
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global, localPort));
        JsonElement inbounds = document.RootElement.GetProperty("inbounds");

        Assert.AreEqual(1, inbounds.GetArrayLength());
        Assert.AreEqual("http-in", inbounds[0].GetProperty("tag").GetString());
        Assert.AreEqual("127.0.0.1", inbounds[0].GetProperty("listen").GetString());
        Assert.AreEqual(localPort, inbounds[0].GetProperty("port").GetInt32());
        Assert.AreEqual("http", inbounds[0].GetProperty("protocol").GetString());
    }

    [TestMethod]
    public void ProfileFields_AreMappedToVnextAndRealitySettings()
    {
        ServerProfile profile = CreateValidProfile();
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(
                XrayConfigGenerator.Generate(profile, ProxyMode.Global, TestLocalPort, @"C:\tmp\xray.log"),
                XrayConfigGenerator.CreateJsonOptions()));

        JsonElement vnext = document.RootElement.GetProperty("outbounds")[0]
            .GetProperty("settings").GetProperty("vnext")[0];
        Assert.AreEqual(profile.Server, vnext.GetProperty("address").GetString());
        Assert.AreEqual(profile.Port, vnext.GetProperty("port").GetInt32());
        Assert.AreEqual(profile.Uuid, vnext.GetProperty("users")[0].GetProperty("id").GetString());
        Assert.AreEqual(profile.Flow, vnext.GetProperty("users")[0].GetProperty("flow").GetString());

        JsonElement reality = document.RootElement.GetProperty("outbounds")[0]
            .GetProperty("streamSettings").GetProperty("realitySettings");
        Assert.AreEqual(profile.Fingerprint, reality.GetProperty("fingerprint").GetString());
        Assert.AreEqual(profile.Sni, reality.GetProperty("serverName").GetString());
        Assert.AreEqual(profile.PublicKey, reality.GetProperty("publicKey").GetString());
        Assert.AreEqual(profile.ShortId, reality.GetProperty("shortId").GetString());
        Assert.AreEqual(profile.SpiderX, reality.GetProperty("spiderX").GetString());
    }

    [TestMethod]
    public void LogErrorPath_AndPolicyAndDns_MatchTemplate()
    {
        const string logPath = @"C:\Users\test\AppData\Local\MyProxy\logs\xray.log";
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global));

        Assert.AreEqual(logPath, document.RootElement.GetProperty("log").GetProperty("error").GetString());
        Assert.AreEqual("warning", document.RootElement.GetProperty("log").GetProperty("loglevel").GetString());
        Assert.AreEqual("none", document.RootElement.GetProperty("log").GetProperty("access").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("dns").GetProperty("disableCache").GetBoolean());
        Assert.AreEqual("UseIP", document.RootElement.GetProperty("dns").GetProperty("queryStrategy").GetString());

        JsonElement levels = document.RootElement.GetProperty("policy").GetProperty("levels").GetProperty("0");
        Assert.IsTrue(levels.GetProperty("statsUserDownlink").GetBoolean());
        Assert.IsTrue(levels.GetProperty("statsUserOnline").GetBoolean());
        Assert.IsTrue(levels.GetProperty("statsUserUplink").GetBoolean());
    }

    [TestMethod]
    public void SerializedJson_IsParsable_AndAllKeysAreCamelCase()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Rule));
        AssertAllKeysLowerCamel(document.RootElement);
    }

    [TestMethod]
    public void SameInput_ProducesByteIdenticalJson()
    {
        string first = Serialize(ProxyMode.Rule);
        string second = Serialize(ProxyMode.Rule);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void InvalidInputs_ThrowArgumentException()
    {
        ServerProfile valid = CreateValidProfile();

        AssertInvalidProfile(valid, p => p.Server = "");
        AssertInvalidProfile(valid, p => p.Uuid = "");
        AssertInvalidProfile(valid, p => p.Uuid = "not-a-guid");
        AssertInvalidProfile(valid, p => p.Sni = "");
        AssertInvalidProfile(valid, p => p.PublicKey = "");
        AssertInvalidProfile(valid, p => p.ShortId = "");
        AssertInvalidProfile(valid, p => p.Fingerprint = "");
        AssertInvalidProfile(valid, p => p.Flow = "");
        AssertInvalidProfile(valid, p => p.SpiderX = "");
        AssertInvalidProfile(valid, p => p.Port = 0);
        AssertInvalidProfile(valid, p => p.Port = 65536);
        AssertInvalidProfile(valid, p => p.Security = "vless");

        Assert.ThrowsException<ArgumentException>(() =>
            XrayConfigGenerator.Generate(valid, ProxyMode.Rule, 80, @"C:\tmp\xray.log"));
        Assert.ThrowsException<ArgumentException>(() =>
            XrayConfigGenerator.Generate(valid, ProxyMode.Rule, 70000, @"C:\tmp\xray.log"));
        Assert.ThrowsException<ArgumentException>(() =>
            XrayConfigGenerator.Generate(valid, ProxyMode.Rule, TestLocalPort, ""));
        Assert.ThrowsException<ArgumentException>(() =>
            XrayConfigGenerator.Generate(valid, ProxyMode.Rule, TestLocalPort, @"relative\xray.log"));
    }

    [TestMethod]
    public void GeneratedJson_DoesNotContainPrivateKey()
    {
        string json = Serialize(ProxyMode.Global);

        Assert.IsFalse(json.Contains("privateKey", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DnsServers_RaceEquivalentGroupsWithoutDuplicateOrDomesticFallback()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global));
        JsonElement dns = document.RootElement.GetProperty("dns");
        JsonElement servers = dns.GetProperty("servers");

        Assert.IsTrue(dns.GetProperty("enableParallelQuery").GetBoolean());
        Assert.IsFalse(dns.GetProperty("disableCache").GetBoolean());
        Assert.AreEqual("UseIP", dns.GetProperty("queryStrategy").GetString());
        Assert.AreEqual(4, servers.GetArrayLength());

        Assert.AreEqual(JsonValueKind.Object, servers[0].ValueKind);
        Assert.AreEqual("223.5.5.5", servers[0].GetProperty("address").GetString());
        Assert.AreEqual(JsonValueKind.Object, servers[1].ValueKind);
        Assert.AreEqual("119.29.29.29", servers[1].GetProperty("address").GetString());
        Assert.AreEqual(JsonValueKind.Object, servers[2].ValueKind);
        Assert.AreEqual("https://1.1.1.1/dns-query", servers[2].GetProperty("address").GetString());

        Assert.AreEqual(JsonValueKind.Object, servers[3].ValueKind);
        Assert.AreEqual("https://8.8.8.8/dns-query", servers[3].GetProperty("address").GetString());
        Assert.IsTrue(servers[0].GetProperty("skipFallback").GetBoolean());
        Assert.IsTrue(servers[1].GetProperty("skipFallback").GetBoolean());
        Assert.AreEqual(servers[0].GetProperty("domains").GetRawText(), servers[1].GetProperty("domains").GetRawText());
        Assert.AreEqual(servers[0].GetProperty("expectIPs").GetRawText(), servers[1].GetProperty("expectIPs").GetRawText());
        Assert.AreEqual(servers[2].GetProperty("domains").GetRawText(), servers[3].GetProperty("domains").GetRawText());
        Assert.IsFalse(servers[2].TryGetProperty("skipFallback", out _));
        Assert.IsFalse(servers[3].TryGetProperty("skipFallback", out _));
    }

    [DataTestMethod]
    [DataRow("domain:github.com")]
    [DataRow("domain:githubusercontent.com")]
    [DataRow("domain:googleapis.com")]
    [DataRow("domain:youtube.com")]
    [DataRow("domain:microsoftonline.com")]
    [DataRow("domain:cloud.microsoft")]
    [DataRow("domain:x.com")]
    [DataRow("domain:chatgpt.com")]
    [DataRow("domain:oaiusercontent.com")]
    [DataRow("full:challenges.cloudflare.com")]
    public void InternationalServices_MatchDomainsBeforeIpFallback(string domainRule)
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Rule));
        JsonElement rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        int index = FindRuleIndex(rules, domainRule, "proxy");
        Assert.IsTrue(index >= 0 && index < FindRuleIndex(rules, "!geoip:cn", "proxy"));
        Assert.IsFalse(rules[index].TryGetProperty("ip", out _), "known services must not require DNS for routing");
    }

    [DataTestMethod]
    [DataRow("domain:azure.cn")]
    [DataRow("domain:microsoftonline.cn")]
    [DataRow("domain:office365.cn")]
    [DataRow("full:cn.bing.com")]
    public void MicrosoftChina_RemainsDirectBeforeInternationalServices(string domainRule)
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Rule));
        JsonElement rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        int index = FindRuleIndex(rules, domainRule, "direct");
        Assert.IsTrue(index >= 0 && index < FindRuleIndex(rules, "domain:microsoft.com", "proxy"));
        using JsonDocument global = JsonDocument.Parse(Serialize(ProxyMode.Global));
        Assert.AreEqual(-1, FindRuleIndex(global.RootElement.GetProperty("routing").GetProperty("rules"), domainRule, "direct"));
    }

    [DataTestMethod]
    [DataRow(ProxyMode.Rule, "IPIfNonMatch")]
    [DataRow(ProxyMode.Global, "AsIs")]
    public void RoutingDomainStrategy_OnlyRuleModeResolvesUnmatchedDomains(ProxyMode mode, string expectedStrategy)
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(mode));

        JsonElement routing = document.RootElement.GetProperty("routing");
        Assert.AreEqual(expectedStrategy, routing.GetProperty("domainStrategy").GetString());
        Assert.AreEqual(mode == ProxyMode.Rule ? 1 : 0,
            routing.GetProperty("rules").EnumerateArray().Count(rule =>
                rule.TryGetProperty("ip", out JsonElement ips) &&
                ips.EnumerateArray().Any(ip => ip.GetString() == "geoip:cn") &&
                rule.GetProperty("outboundTag").GetString() == "direct"));
    }

    [TestMethod]
    public void PolicyMatchesTemplate()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(ProxyMode.Global));

        JsonElement policy = document.RootElement.GetProperty("policy");
                Assert.IsTrue(policy.GetProperty("levels").GetProperty("0").GetProperty("statsUserDownlink").GetBoolean());
        Assert.IsTrue(policy.GetProperty("levels").GetProperty("0").GetProperty("statsUserOnline").GetBoolean());
        Assert.IsTrue(policy.GetProperty("levels").GetProperty("0").GetProperty("statsUserUplink").GetBoolean());

        JsonElement system = policy.GetProperty("system");
        Assert.IsTrue(system.GetProperty("statsInboundDownlink").GetBoolean());
        Assert.IsTrue(system.GetProperty("statsInboundUplink").GetBoolean());
        Assert.IsFalse(system.GetProperty("statsOutboundDownlink").GetBoolean());
        Assert.IsFalse(system.GetProperty("statsOutboundUplink").GetBoolean());
    }

    private static int FindRuleIndex(JsonElement rules, string value, string outboundTag)
    {
        for (int i = 0; i < rules.GetArrayLength(); i++)
        {
            JsonElement rule = rules[i];
            if (rule.GetProperty("outboundTag").GetString() != outboundTag)
            {
                continue;
            }

            if (rule.TryGetProperty("domain", out JsonElement domain) &&
                domain.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in domain.EnumerateArray())
                {
                    if (item.GetString() == value)
                    {
                        return i;
                    }
                }
            }

            if (rule.TryGetProperty("ip", out JsonElement ip) &&
                ip.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in ip.EnumerateArray())
                {
                    if (item.GetString() == value)
                    {
                        return i;
                    }
                }
            }

            if (rule.TryGetProperty("protocol", out JsonElement protocol) &&
                protocol.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in protocol.EnumerateArray())
                {
                    if (item.GetString() == value)
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    private static void AssertAllKeysLowerCamel(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Assert.IsFalse(char.IsUpper(property.Name[0]), $"键名 {property.Name} 首字母大写");
                AssertAllKeysLowerCamel(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertAllKeysLowerCamel(item);
            }
        }
    }

    private static void AssertInvalidProfile(ServerProfile valid, Action<ServerProfile> mutate)
    {
        ServerProfile profile = CreateValidProfile();
        mutate(profile);

        Assert.ThrowsException<ArgumentException>(() =>
            XrayConfigGenerator.Generate(profile, ProxyMode.Rule, TestLocalPort, @"C:\tmp\xray.log"));
    }
}
