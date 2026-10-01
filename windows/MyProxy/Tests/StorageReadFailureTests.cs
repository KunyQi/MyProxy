using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 读取失败与「未绑定」必须分开：一次瞬时 IOException 被当成未绑定，会让用户
/// 回到配对页，SaveDeviceAsync 随后覆盖掉仍然有效的 device.dat，
/// 旧设备在服务端变成孤儿 active 占用名额。
/// </summary>
[TestClass]
public sealed class StorageReadFailureTests
{
    [TestMethod]
    public async Task LockedDeviceFile_Throws_InsteadOfLookingUnbound()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await storage.SaveDeviceAsync(NewDevice(), CancellationToken.None);
            Assert.IsNotNull(storage.LoadDevice());

            string path = Path.Combine(dataRoot, "device.dat");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsException<SecureStorageReadException>(() => storage.LoadDevice());
            }

            // 占用解除后仍是同一个绑定，凭据没有被任何一步覆盖。
            Assert.IsNotNull(storage.LoadDevice());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void MissingDeviceFile_IsStillUnbound()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            Assert.IsNull(storage.LoadDevice());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptDeviceFile_IsStillUnbound()
    {
        // 内容损坏是永久的，重试不会好转，等同于没有可用绑定。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            File.WriteAllBytes(Path.Combine(dataRoot, "device.dat"), new byte[] { 9, 8, 7, 6, 5 });
            Assert.IsNull(storage.LoadDevice());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static DeviceConfig NewDevice() => new()
    {
        DeviceId = "dev_test",
        DeviceToken = "tok_test",
        DeviceName = "TEST-PC",
        Platform = "windows",
        ClientVersion = "0.1.0",
        BoundAt = DateTimeOffset.UtcNow
    };

    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
