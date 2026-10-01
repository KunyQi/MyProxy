using MyProxy.Services;

namespace MyProxy.Linux.Tests;

[TestClass]
public sealed class DeploymentConfigurationTests
{
    [TestMethod]
    public void SharedDeploymentResource_IsEmbeddedInLinuxCore()
    {
        BindingTarget target = BindingTarget.FromBaseUrl(DeploymentConfiguration.ApiBaseUrl);
        Assert.IsTrue(target.IsHttps);
        Assert.AreEqual(443, BindingTarget.FromBaseUrl("https://api.example.invalid").Port);
        Assert.AreEqual(80, BindingTarget.FromBaseUrl("http://127.0.0.1").Port);
        Assert.AreEqual(8443, BindingTarget.FromBaseUrl("https://203.0.113.10:8443").Port);
    }

    [TestMethod]
    public void ExampleDeployment_FailsBeforeCreatingAClient()
    {
        using var endpoint = new ApiEndpoint(BindingTarget.FromBaseUrl("https://api.example.invalid"));
        MyProxyException exception = Assert.ThrowsException<MyProxyException>(() => endpoint.Client("claim", TimeSpan.FromSeconds(1)));
        Assert.AreEqual(ErrorCode.ServerNotConfigured, exception.ErrorCode);
    }
}
