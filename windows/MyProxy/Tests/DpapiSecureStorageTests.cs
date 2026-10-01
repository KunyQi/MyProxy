using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class DpapiSecureStorageTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public async Task SaveAndRead_RoundTrips()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new DpapiSecureStorage(dataRoot);
            string plain = "{\"deviceToken\":\"tok_roundtrip_secret\"}";

            await storage.SaveAsync("device", plain, CancellationToken.None);
            string? read = await storage.ReadAsync("device", CancellationToken.None);

            Assert.AreEqual(plain, read);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Save_DoesNotContainPlaintextToken()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new DpapiSecureStorage(dataRoot);
            string token = "tok_plaintext_should_not_exist";
            string plain = $"{{\"deviceToken\":\"{token}\"}}";

            await storage.SaveAsync("device", plain, CancellationToken.None);

            byte[] fileBytes = await File.ReadAllBytesAsync(Path.Combine(dataRoot, "device.dat"));
            string fileText = Convert.ToHexString(fileBytes);
            string tokenHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(token));

            Assert.IsFalse(fileText.Contains(tokenHex));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Delete_RemovesFile()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new DpapiSecureStorage(dataRoot);
            await storage.SaveAsync("device", "hello", CancellationToken.None);
            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "device.dat")));

            await storage.DeleteAsync("device", CancellationToken.None);

            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "device.dat")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task CorruptFile_ReturnsNull()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new DpapiSecureStorage(dataRoot);
            File.WriteAllBytes(Path.Combine(dataRoot, "device.dat"), new byte[] { 1, 2, 3, 4, 5 });

            string? read = await storage.ReadAsync("device", CancellationToken.None);

            Assert.IsNull(read);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
