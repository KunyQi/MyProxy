using System.Net;
using System.Net.Sockets;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

[TestClass]
public sealed class EnvironmentIgnoreHostsTests
{
    [TestMethod]
    public void Ipv6EnvironmentPrefixes_AreByteAlignedWithoutChangingIpv4OrDesktopSettings()
    {
        IReadOnlyList<string> environment = GnomeProxyCommands.EnvironmentIgnoreHosts;
        CollectionAssert.AreEqual(
            GnomeProxyCommands.DefaultIgnoreHosts.Where(entry => !entry.Contains(':')).ToArray(),
            environment.Where(entry => !entry.Contains(':')).ToArray());
        Assert.AreEqual(environment.Count, environment.Distinct(StringComparer.Ordinal).Count());
        foreach ((_, int bits) in Ipv6Networks(environment))
        {
            Assert.AreEqual(0, bits % 8, "IPv6 环境变量前缀不能经过有兼容缺陷的部分字节分支");
        }
        CollectionAssert.Contains(GnomeProxyCommands.DefaultIgnoreHosts.ToArray(), "fc00::/7");
        CollectionAssert.Contains(GnomeProxyCommands.DefaultIgnoreHosts.ToArray(), "fe80::/10");
    }

    [TestMethod]
    public void Ipv6EnvironmentPrefixes_CoverExactlyUlaAndLinkLocalInEveryLeadingWord()
    {
        (byte[] Address, int Bits)[] networks = Ipv6Networks(GnomeProxyCommands.EnvironmentIgnoreHosts);
        var address = new byte[16];
        address[^1] = 0x42; // Avoid matching the separate ::1 loopback exception.
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            address[0] = (byte)(word >> 8);
            address[1] = (byte)word;
            bool expected = (word & 0xfe00) == 0xfc00 || (word & 0xffc0) == 0xfe80;
            Assert.AreEqual(expected, networks.Any(network => Matches(address, network)),
                $"首段 {word:x4} 的绕过范围不应扩大或遗漏");
        }
    }

    [DataTestMethod]
    [DataRow("::1", true)]
    [DataRow("fc00::", true)]
    [DataRow("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [DataRow("fe80::", true)]
    [DataRow("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [DataRow("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [DataRow("fe00::", false)]
    [DataRow("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [DataRow("fec0::1", false)]
    [DataRow("::", false)]
    [DataRow("2001:db8::1", false)]
    [DataRow("2606:4700:4700::1111", false)]
    [DataRow("ff02::1", false)]
    public void Ipv6EnvironmentPrefixes_BypassInternalBoundariesButKeepOtherAddressesProxied(
        string host, bool expected)
    {
        byte[] address = IPAddress.Parse(host).GetAddressBytes();
        Assert.AreEqual(expected, Ipv6Networks(GnomeProxyCommands.EnvironmentIgnoreHosts)
            .Any(network => Matches(address, network)));
    }

    private static (byte[] Address, int Bits)[] Ipv6Networks(IReadOnlyList<string> hosts)
        => hosts.Select(host => host.Split('/'))
            .Where(parts => IPAddress.TryParse(parts[0], out IPAddress? address) &&
                address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(parts => (IPAddress.Parse(parts[0]).GetAddressBytes(),
                parts.Length == 1 ? 128 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

    private static bool Matches(byte[] address, (byte[] Address, int Bits) network)
    {
        int fullBytes = network.Bits / 8;
        if (!address.AsSpan(0, fullBytes).SequenceEqual(network.Address.AsSpan(0, fullBytes))) return false;
        int remaining = network.Bits % 8;
        return remaining == 0 || ((address[fullBytes] ^ network.Address[fullBytes]) &
            (0xff << (8 - remaining))) == 0;
    }
}
