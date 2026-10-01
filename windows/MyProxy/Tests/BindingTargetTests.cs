using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class BindingTargetTests
{
    [DataTestMethod]
    [DataRow("https://api.example.invalid", 443)]
    [DataRow("https://203.0.113.10:443", 443)]
    [DataRow("https://203.0.113.10:8443/", 8443)]
    [DataRow("http://127.0.0.1", 80)]
    [DataRow("http://10.0.2.2:8090", 8090)]
    [DataRow("https://[::1]:8443", 8443)]
    public void Origins_UseStandardAndExplicitPorts(string url, int expected)
    {
        Assert.IsTrue(BindingTarget.TryFromBaseUrl(url, out BindingTarget target));
        Assert.AreEqual(expected, target.Port);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow(" https://api.example.invalid")]
    [DataRow("https://api.example.invalid ")]
    [DataRow("203.0.113.10")]
    [DataRow("ftp://203.0.113.10")]
    [DataRow("https://")]
    [DataRow("https://user:secret@api.example.invalid")]
    [DataRow("https://api.example.invalid/path")]
    [DataRow("https://api.example.invalid//")]
    [DataRow("https://api.example.invalid/./")]
    [DataRow("https://api.example.invalid?q=1")]
    [DataRow("https://api.example.invalid#fragment")]
    [DataRow("https://api.example.invalid:")]
    [DataRow("https://api.example.invalid:0")]
    [DataRow("https://api.example.invalid:65536")]
    [DataRow("https://api.example.invalid.")]
    [DataRow("https://127.1")]
    [DataRow("https://0177.0.0.1")]
    [DataRow("https://2130706433")]
    [DataRow("https://0x7f000001")]
    [DataRow("https://0x7f.0.0.1")]
    [DataRow("https://bad_host.example.invalid")]
    [DataRow("https://-host.example.invalid")]
    [DataRow("https://api.example.invalid\u0001")]
    [DataRow("https://api.example.invalid\u007f")]
    public void UnusableOrigins_AreRejected(string url)
    {
        Assert.IsFalse(BindingTarget.TryFromBaseUrl(url, out _));
    }

    [TestMethod]
    public void DeploymentResource_IsEmbeddedAndValid()
    {
        BindingTarget target = BindingTarget.FromBaseUrl(DeploymentConfiguration.ApiBaseUrl);
        Assert.IsTrue(target.IsHttps);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"api_base_url\":123}")]
    [DataRow("{\"api_base_url\":\"http://127.0.0.1:8090\"}")]
    [DataRow("{\"api_base_url\":\"https://api.example.invalid/path\"}")]
    public void DeploymentConfiguration_RejectsInvalidProductionValues(string json)
    {
        Assert.ThrowsException<ArgumentException>(() => DeploymentConfiguration.ParseApiBaseUrl(json));
    }

    [TestMethod]
    public void ExampleTarget_CanBeConstructedButCannotCreateNetworkClient()
    {
        using var endpoint = new ApiEndpoint(BindingTarget.FromBaseUrl("https://api.example.invalid"));
        MyProxyException ex = Assert.ThrowsException<MyProxyException>(() => endpoint.Client("claim", TimeSpan.FromSeconds(1)));
        Assert.AreEqual(ErrorCode.ServerNotConfigured, ex.ErrorCode);
        StringAssert.Contains(ex.FriendlyMessage, "尚未配置服务器");
        Assert.IsFalse(BindingTarget.FromBaseUrl("https://invalid").IsConfigured);
    }
}
