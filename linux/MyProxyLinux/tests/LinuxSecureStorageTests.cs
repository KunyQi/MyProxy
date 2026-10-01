using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 凭据存储：设备令牌明文落在 0600 的文件里。
///
/// <para>
/// 这套测试盯三件事：<b>读写往返</b>、<b>权限</b>、以及<b>「读不出来」与「没有绑定」
/// 必须分开</b>。第三件最容易被忽略，代价却最大：一次瞬时 IO 错误若被当成「没绑定」，
/// 用户会被送回配对页，而重新绑定会覆盖掉仍然有效的设备记录，在服务端留下一个
/// 孤儿设备。
/// </para>
/// </summary>
[TestClass]
public sealed class LinuxSecureStorageTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [TestMethod]
    public void SaveReadDelete_RoundTrips()
    {
        var storage = new LinuxSecureStorage(_root);

        storage.SaveAsync("device", "{\"deviceToken\":\"secret\"}", CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.AreEqual(
            "{\"deviceToken\":\"secret\"}",
            storage.ReadAsync("device", CancellationToken.None).GetAwaiter().GetResult());

        storage.DeleteAsync("device", CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsNull(storage.ReadAsync("device", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Read_OfAMissingFile_IsNullNotAnError()
    {
        var storage = new LinuxSecureStorage(_root);

        // 没绑定是一等公民的正常状态，不是异常。
        Assert.IsNull(storage.ReadAsync("device", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Read_OfAnUnrecognisedFile_IsTreatedAsCorruptNotAsBound()
    {
        var storage = new LinuxSecureStorage(_root);
        LinuxFileSecurity.EnsurePrivateDirectory(Path.Combine(_root, "secrets"));
        File.WriteAllText(Path.Combine(_root, "secrets", "device.json"), "not-our-format");

        Assert.IsNull(storage.ReadAsync("device", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Names_AreRestrictedToASafeAlphabet()
    {
        var storage = new LinuxSecureStorage(_root);

        // 名字来自调用方，不能让它变成一次任意的文件写入。
        Assert.ThrowsException<ArgumentException>(() =>
            storage.ReadAsync("../escape", CancellationToken.None).GetAwaiter().GetResult());
        Assert.ThrowsException<ArgumentException>(() =>
            storage.ReadAsync("Device", CancellationToken.None).GetAwaiter().GetResult());
        Assert.ThrowsException<ArgumentException>(() =>
            storage.ReadAsync("a/b", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void SavedFile_IsOnlyReadableByItsOwner()
    {
        if (!OperatingSystem.IsLinux())
        {
            // 权限位是 Unix 概念；这条断言在 Windows 开发机上无法成立，
            // 由 CI 的 Linux lane 来跑。
            Assert.Inconclusive("文件权限断言只在 Linux 上有意义。");
        }

        var storage = new LinuxSecureStorage(_root);
        storage.SaveAsync("device", "payload", CancellationToken.None).GetAwaiter().GetResult();

        string path = Path.Combine(_root, "secrets", "device.json");
        Assert.IsTrue(LinuxFileSecurity.IsPrivate(path), "凭据文件必须是 0600。");
    }

    [TestMethod]
    public void Read_RefusesASymlinkedCredentialFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("创建符号链接需要权限，Windows 开发机上不保证可建。");
        }

        var storage = new LinuxSecureStorage(_root);
        LinuxFileSecurity.EnsurePrivateDirectory(Path.Combine(_root, "secrets"));

        string target = Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(target, "myproxy-secret-v1\nstolen");
        File.CreateSymbolicLink(Path.Combine(_root, "secrets", "device.json"), target);

        // 符号链接可以被指向任何地方：跟着它读，等于让一个别人能改的路径决定
        // 我们去读哪个文件。OpenSSH 对私钥也是这个态度。
        Assert.ThrowsException<SecureStorageReadException>(() =>
            storage.ReadAsync("device", CancellationToken.None).GetAwaiter().GetResult());
    }
}
