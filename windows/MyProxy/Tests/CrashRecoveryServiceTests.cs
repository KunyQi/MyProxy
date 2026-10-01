using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class CrashRecoveryServiceTests
{
    [TestMethod]
    public void ProxyRecoveryFailure_PreservesPidEvidence()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            string pidPath = Path.Combine(storage.RuntimeDir, "xray.pid");
            File.WriteAllText(pidPath, "invalid-pid");
            var recovery = new CrashRecoveryService(new ThrowingProxyService(), storage);

            bool recovered = recovery.Run();

            Assert.IsFalse(recovered);
            Assert.IsTrue(File.Exists(pidPath));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private sealed class ThrowingProxyService : ISystemProxyService
    {
        public bool IsManagedByMyProxy => true;

        public void CaptureCurrentSettings() => throw new NotSupportedException();

        public Task EnableAsync(string host, int port, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RestoreAsync(CancellationToken ct) => throw new NotSupportedException();

        public void PersistSnapshotForCrashRecovery() => throw new NotSupportedException();

        public bool TryRecoverFromCrash() => throw new InvalidOperationException("simulated recovery failure");
    }
}
