using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class ConnectionControllerContractTests
{
    [TestMethod]
    public void CandidatePorts_AreValidTcpPorts()
    {
        Assert.IsTrue(ConnectionController.CandidatePorts.Count > 0);

        foreach (int port in ConnectionController.CandidatePorts)
        {
            Assert.IsTrue(port is >= IanaTcpPortMin and <= IanaTcpPortMax);
        }
    }

    private const int IanaTcpPortMin = 1;
    private const int IanaTcpPortMax = 65535;
}
