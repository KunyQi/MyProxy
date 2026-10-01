using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// gsettings 的取值解析与命令构造。
///
/// <para>
/// 这些是纯函数，却是整条系统代理链路上最容易写错的一段：<c>gsettings get</c>
/// 回的是 GVariant 文本（<c>'manual'</c>、<c>['a', 'b']</c>、<c>true</c>），
/// 不是 JSON。解析歪一点，快照里就会存下一个读不懂的值，而它会在恢复时被写回
/// 用户的桌面设置。
/// </para>
/// </summary>
[TestClass]
public sealed class GnomeProxyCommandsTests
{
    private static Dictionary<string, string> Values(params (string Schema, string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string schema, string key, string value) in entries)
        {
            values[GnomeProxyCommands.Key(schema, key)] = value;
        }

        return values;
    }

    [TestMethod]
    public void Parse_ReadsTheShapeGsettingsActuallyPrints()
    {
        var values = Values(
            (GnomeProxyCommands.Schema, "mode", "'manual'"),
            (GnomeProxyCommands.Schema, "autoconfig-url", "''"),
            (GnomeProxyCommands.Schema, "use-same-proxy", "false"),
            (GnomeProxyCommands.Schema, "ignore-hosts", "['localhost', '127.0.0.0/8']"),
            ($"{GnomeProxyCommands.Schema}.http", "host", "'10.0.0.1'"),
            ($"{GnomeProxyCommands.Schema}.http", "port", "3128"),
            ($"{GnomeProxyCommands.Schema}.https", "host", "'10.0.0.1'"),
            ($"{GnomeProxyCommands.Schema}.https", "port", "3128"),
            ($"{GnomeProxyCommands.Schema}.socks", "host", "'127.0.0.1'"),
            ($"{GnomeProxyCommands.Schema}.socks", "port", "1080"));

        GnomeProxyState state = GnomeProxyCommands.Parse(values);

        Assert.AreEqual("manual", state.Mode);
        Assert.AreEqual("", state.AutoconfigUrl);
        Assert.IsFalse(state.UseSameProxy);
        Assert.AreEqual(2, state.IgnoreHosts.Count);
        Assert.AreEqual("localhost", state.IgnoreHosts[0]);
        Assert.AreEqual("127.0.0.0/8", state.IgnoreHosts[1]);
        Assert.AreEqual("10.0.0.1", state.HttpHost);
        Assert.AreEqual(3128, state.HttpPort);
        Assert.AreEqual(1080, state.SocksPort);
    }

    [TestMethod]
    public void Parse_TreatsUnreadableValuesAsEmptyRatherThanGuessing()
    {
        var values = Values(
            (GnomeProxyCommands.Schema, "mode", "MANUAL-ish"),
            ($"{GnomeProxyCommands.Schema}.http", "port", "not-a-number"),
            (GnomeProxyCommands.Schema, "ignore-hosts", "@as []"));

        GnomeProxyState state = GnomeProxyCommands.Parse(values);

        // 未知模式一律当成「不代理」：把一个读不懂的模式写回去，
        // 比关掉代理更糟——用户会得到一个说不出所以然的网络故障。
        Assert.AreEqual("none", state.Mode);
        Assert.AreEqual(0, state.HttpPort);
        Assert.AreEqual(0, state.IgnoreHosts.Count);
    }

    [TestMethod]
    public void Parse_HandlesEmptyAndQuotedVariantsOfTheSameKey()
    {
        Assert.AreEqual("", GnomeProxyCommands.Parse(Values(
            ($"{GnomeProxyCommands.Schema}.http", "host", "@s ''"))).HttpHost);
        Assert.AreEqual("proxy.example", GnomeProxyCommands.Parse(Values(
            ($"{GnomeProxyCommands.Schema}.http", "host", "'proxy.example'"))).HttpHost);
    }

    [TestMethod]
    public void NormalizeMode_OnlyAcceptsTheThreeRealValues()
    {
        Assert.AreEqual("manual", GnomeProxyCommands.NormalizeMode("'manual'"));
        Assert.AreEqual("auto", GnomeProxyCommands.NormalizeMode("auto"));
        Assert.AreEqual("none", GnomeProxyCommands.NormalizeMode("'none'"));
        Assert.AreEqual("none", GnomeProxyCommands.NormalizeMode(""));
        Assert.AreEqual("none", GnomeProxyCommands.NormalizeMode("something-else"));
    }

    [TestMethod]
    public void PointsAt_IsTrueOnlyWhenSomeProxyPointsAtUs()
    {
        var ours = new GnomeProxyState { Mode = "manual", HttpHost = "127.0.0.1", HttpPort = 10809 };

        Assert.IsTrue(GnomeProxyCommands.PointsAt(ours, "127.0.0.1", 10809));
        Assert.IsFalse(GnomeProxyCommands.PointsAt(ours, "127.0.0.1", 20809));

        // 模式不是 manual 时端口对得上也不算「指向我们」：mode=none 下那些值不生效。
        ours.Mode = "none";
        Assert.IsFalse(GnomeProxyCommands.PointsAt(ours, "127.0.0.1", 10809));
    }

    [TestMethod]
    public void EnableCommands_TurnOnManualModeAndGiveUpAutoconfig()
    {
        IReadOnlyList<string[]> commands = GnomeProxyCommands.EnableCommands("127.0.0.1", 10809);

        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", GnomeProxyCommands.Schema, "mode", "'manual'" })));

        // PAC 必须让位：manual 与 autoconfig-url 同时存在时行为由客户端决定。
        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", GnomeProxyCommands.Schema, "autoconfig-url", "''" })));

        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", $"{GnomeProxyCommands.Schema}.http", "port", "10809" })));

        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", $"{GnomeProxyCommands.Schema}.https", "port", "10809" })));
    }

    [TestMethod]
    public void RestoreCommands_OnlyTouchTheSectionsThatStillPointAtUs()
    {
        var current = new GnomeProxyState
        {
            Mode = "manual",
            HttpHost = "127.0.0.1",
            HttpPort = 10809,
            // 连接期间第三方（企业 VPN）改掉了 https 段。
            HttpsHost = "vpn.corp.example",
            HttpsPort = 8443,
            SocksHost = "127.0.0.1",
            SocksPort = 10809
        };

        var target = new GnomeProxyState
        {
            Mode = "auto",
            AutoconfigUrl = "http://wpad/wpad.dat",
            HttpHost = "10.0.0.1",
            HttpPort = 3128,
            HttpsHost = "10.0.0.1",
            HttpsPort = 3128,
            IgnoreHosts = new List<string> { "localhost" }
        };

        IReadOnlyList<string[]> commands = GnomeProxyCommands.RestoreCommands(current, target, "127.0.0.1", 10809);

        // http 段还是我们的：写回原值。
        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", $"{GnomeProxyCommands.Schema}.http", "host", "'10.0.0.1'" })));
        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", $"{GnomeProxyCommands.Schema}.http", "port", "3128" })));

        // https 已经不是我们的了：一个字都不能动它。
        Assert.IsFalse(
            commands.Any(c => c.Length > 1 && c[1] == $"{GnomeProxyCommands.Schema}.https"),
            "第三方改过的段不该被我们覆盖");

        // socks 段即使指向我们也不碰（我们从没写过它）。
        Assert.IsFalse(
            commands.Any(c => c.Length > 1 && c[1] == $"{GnomeProxyCommands.Schema}.socks"),
            "socks 段从来不是我们写的");

        // mode 是总开关：先写别的再写它，中间不会出现「已启用但端口还是旧的」。
        string[] last = commands[^1];
        Assert.AreEqual("mode", last[2]);
        Assert.AreEqual("'auto'", last[3]);

        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", GnomeProxyCommands.Schema, "autoconfig-url", "'http://wpad/wpad.dat'" })));
    }

    [TestMethod]
    public void RestoreCommands_WithoutASnapshot_DisableOnlyWhatIsOurs()
    {
        var current = new GnomeProxyState
        {
            Mode = "manual",
            HttpHost = "127.0.0.1",
            HttpPort = 10809,
            HttpsHost = "vpn.corp.example",
            HttpsPort = 8443
        };

        IReadOnlyList<string[]> commands = GnomeProxyCommands.RestoreCommands(current, target: null, "127.0.0.1", 10809);

        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", $"{GnomeProxyCommands.Schema}.http", "host", "''" })));
        Assert.IsTrue(commands.Any(c => c.SequenceEqual(
            new[] { "set", GnomeProxyCommands.Schema, "mode", "'none'" })));
        Assert.IsFalse(commands.Any(c => c.Length > 1 && c[1] == $"{GnomeProxyCommands.Schema}.https"));
    }

    [TestMethod]
    public void RestoreCommands_WhenNothingIsOurs_DoNothingAtAll()
    {
        var current = new GnomeProxyState { Mode = "manual", HttpHost = "10.1.1.1", HttpPort = 3128 };

        Assert.AreEqual(
            0,
            GnomeProxyCommands.RestoreCommands(current, target: null, "127.0.0.1", 10809).Count);
    }

    [TestMethod]
    public void SocksIsNeverTouched()
    {
        // 本地入站是 HTTP 代理：把桌面的 SOCKS 指到它会让读该设置的程序（Firefox 等）
        // 直接失败，所以这条设置我们既不写、也不恢复、也不算作「指向我们」。
        IReadOnlyList<string[]> enable = GnomeProxyCommands.EnableCommands("127.0.0.1", 10809);
        Assert.IsFalse(
            enable.Any(c => c.Length > 1 && c[1] == $"{GnomeProxyCommands.Schema}.socks"),
            "连接时不该写 socks 段");

        var current = new GnomeProxyState
        {
            Mode = "manual",
            SocksHost = "127.0.0.1",
            SocksPort = 10809,
            HttpHost = "10.0.0.1",
            HttpPort = 3128
        };

        // socks 段指向我们、http 段不是：不算我们管着，恢复时一个字都不写。
        Assert.IsFalse(GnomeProxyCommands.PointsAt(current, "127.0.0.1", 10809));
        Assert.AreEqual(
            0,
            GnomeProxyCommands.RestoreCommands(current, target: null, "127.0.0.1", 10809)
                .Count(c => c.Length > 1 && c[1] == $"{GnomeProxyCommands.Schema}.socks"));
    }
}
