using MyProxy.Services;

namespace MyProxy.Linux.Tests;

[TestClass]
public sealed class KdeProxyCommandsTests
{
    [TestMethod]
    public void EnableCommands_BypassOnlyLoopbackPrivateAndLinkLocalNetworksInBothFamilies()
    {
        IReadOnlyList<string[]> commands = KdeProxyCommands.EnableCommands("127.0.0.1", 10809);
        string[] command = commands.Single(c => c[5] == "NoProxyFor");

        // KDE parses comma-separated subnets with QHostAddress::parseSubnet; IPv6 CIDRs have no brackets.
        CollectionAssert.AreEqual(new[]
        {
            "localhost", "127.0.0.0/8", "::1",
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
            "fc00::/7", "fe80::/10"
        }, command[6].Split(','));
    }

    [TestMethod]
    public void RestoreCommands_PreserveTheUsersOriginalDualStackExceptions()
    {
        var current = new Dictionary<string, string>
        {
            ["httpProxy"] = "127.0.0.1:10809",
            ["httpsProxy"] = "127.0.0.1:10809",
            ["NoProxyFor"] = "localhost,fc00::/7,fe80::/10",
            ["ProxyType"] = "1"
        };
        const string originalNoProxy = "corp.example,fd12:3456::/48,192.168.10.0/24";
        var original = new Dictionary<string, string>
        {
            ["httpProxy"] = "proxy.corp.example:3128",
            ["httpsProxy"] = "proxy.corp.example:3128",
            ["NoProxyFor"] = originalNoProxy,
            ["ProxyType"] = "1"
        };

        IReadOnlyList<string[]> commands = KdeProxyCommands.RestoreCommands(current, original, "127.0.0.1", 10809);

        Assert.AreEqual(originalNoProxy, commands.Single(c => c[5] == "NoProxyFor")[6]);
    }
}
