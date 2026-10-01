using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

[TestClass]
public sealed class ConnectivityConfigurationTests
{
    [TestMethod]
    public void DefaultProbeUsesTheConfiguredServerAndPort()
    {
        CollectionAssert.AreEqual(new[] { "https://[2001:db8::1]:8443/connectivity-check" },
            DeploymentConfiguration.ParseConnectivityCheckUrls("{\"api_base_url\":\"https://[2001:db8::1]:8443/\"}").ToArray());
    }

    [TestMethod]
    public void ExplicitCandidatesPreservePathsAndPorts()
    {
        string[] urls = { "https://check.example.invalid:8443/204", "https://203.0.113.8/ping" };
        CollectionAssert.AreEqual(urls, DeploymentConfiguration.ParseConnectivityCheckUrls(
            JsonSerializer.Serialize(new { api_base_url = "https://api.example.invalid", connectivity_check_urls = urls })).ToArray());
    }

    [DataTestMethod]
    [DataRow("http://check.example.invalid/204")]
    [DataRow("https://user:secret@check.example.invalid/204")]
    [DataRow("https://check.example.invalid/204?")]
    [DataRow("https://check.example.invalid/204#")]
    [DataRow("https://check.example.invalid:0/204")]
    [DataRow("https://127.1/204")]
    [DataRow("https://check.example.invalid/hello world")]
    [DataRow("https://check.example.invalid/\\204")]
    public void InvalidCandidatesAreRejected(string url)
    {
        Assert.ThrowsException<ArgumentException>(() => DeploymentConfiguration.ParseConnectivityCheckUrls(
            JsonSerializer.Serialize(new { api_base_url = "https://api.example.invalid", connectivity_check_urls = new[] { url } })));
    }

    [DataTestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("[123]")]
    [DataRow("[\"https://a.invalid\",\"https://b.invalid\",\"https://c.invalid\",\"https://d.invalid\",\"https://e.invalid\"]")]
    public void UnboundedOrMalformedCandidateListsAreRejected(string value)
    {
        Assert.ThrowsException<ArgumentException>(() => DeploymentConfiguration.ParseConnectivityCheckUrls(
            "{\"api_base_url\":\"https://api.example.invalid\",\"connectivity_check_urls\":" + value + "}"));
    }
}
