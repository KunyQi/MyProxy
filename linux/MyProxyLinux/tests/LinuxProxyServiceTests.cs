using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 系统代理的所有权与恢复顺序。
///
/// <para>
/// 这套测试盯的是**唯一会让人断网的那条路径**：停止时恢复用户的代理设置。
/// 失败的形态不是「功能少了」，而是「系统代理还指着那个已经死掉的本地端口」，
/// 所以每一条边都要能在这儿被走到——包括第三方在连接期间改了设置、
/// 恢复本身失败、崩溃只留下标记文件这几种。
/// </para>
/// </summary>
[TestClass]
public sealed class LinuxProxyServiceTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-linux-proxy-" + Guid.NewGuid().ToString("N"));
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

    private (LinuxProxyService Proxy, FakeLinuxProxyPlatform Platform, LinuxStorageService Storage) Build(
        FakeLinuxProxyPlatform? platform = null)
    {
        var fake = platform ?? new FakeLinuxProxyPlatform();
        var storage = new LinuxStorageService(_root);
        return (new LinuxProxyService(storage, fake, log: null, configHomeOverride: _root), fake, storage);
    }

    [TestMethod]
    public void CaptureEnableRestore_PutsTheOriginalSettingsBack()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        // 用户原本有一个公司代理与一段 PAC。
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "host", "'proxy.corp.example'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "port", "3128");
        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'manual'");
        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "autoconfig-url", "'http://wpad/wpad.dat'");

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual("'127.0.0.1'", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "host"));
        Assert.AreEqual("10809", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "port"));
        Assert.AreEqual("'manual'", platform.GetGnome(GnomeProxyCommands.Schema, "mode"));
        Assert.IsTrue(File.Exists(Path.Combine(storage.DataRoot, "proxy-backup.json")));
        Assert.IsTrue(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));

        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual("'proxy.corp.example'", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "host"));
        Assert.AreEqual("3128", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "port"));
        Assert.AreEqual(
            "'http://wpad/wpad.dat'",
            platform.GetGnome(GnomeProxyCommands.Schema, "autoconfig-url"));

        // 证据只在恢复成功之后才允许消失。
        Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(storage.DataRoot, "proxy-backup.json")));
    }

    [TestMethod]
    public void Restore_LeavesAThirdPartyChangeAloneButPutsTheRestBack()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, _) = Build();

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        // 连接期间企业 VPN（或用户自己）改掉了 http 段。
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "host", "'vpn.corp.example'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "port", "8080");

        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 那不是我们写的值，Stop 时静默覆盖它是错的（Windows 端同一条规矩）。
        Assert.AreEqual("'vpn.corp.example'", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "host"));
        Assert.AreEqual("8080", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "port"));

        // 但没人动过的 https 段必须恢复原样：只因为 http 被别人改了就整块放弃恢复，
        // 会给我们没碰过的那一段留下一个指向死端口的地址。
        Assert.AreEqual("''", platform.GetGnome($"{GnomeProxyCommands.Schema}.https", "host"));
        Assert.AreEqual("0", platform.GetGnome($"{GnomeProxyCommands.Schema}.https", "port"));
        Assert.AreEqual("'none'", platform.GetGnome(GnomeProxyCommands.Schema, "mode"));
    }

    [TestMethod]
    public void Restore_WhenTheSettingsWriteFails_KeepsTheEvidenceForTheNextAttempt()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        platform.GnomeWritesFail = true;

        Assert.ThrowsException<MyProxyException>(() =>
            proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult());

        // 恢复没成功 = 系统代理可能还指着我们。这时删掉标记，下一次启动就再也不知道
        // 该怎么收拾，用户的网络会一直停在一个死端口上。
        Assert.IsTrue(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        Assert.IsTrue(File.Exists(Path.Combine(storage.DataRoot, "proxy-backup.json")));
    }

    [TestMethod]
    public void Restore_WhenEnvironmentFileDeletionFails_KeepsEvidenceAndCanRetry()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();
        string dropIn = Path.Combine(_root, "environment.d", "myproxy.conf");
        string shell = Path.Combine(_root, "myproxy", "proxy.env");

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();
        platform.EnvironmentFileDeleteFailsAt = dropIn;

        Assert.ThrowsException<IOException>(
            () => proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.IsTrue(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        Assert.IsTrue(File.Exists(Path.Combine(storage.DataRoot, "proxy-backup.json")));
        Assert.IsTrue(platform.EnvironmentFileExists(dropIn));
        Assert.IsTrue(platform.EnvironmentFileExists(shell));

        platform.EnvironmentFileDeleteFailsAt = null;
        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsFalse(platform.EnvironmentFileExists(dropIn));
        Assert.IsFalse(platform.EnvironmentFileExists(shell));
        Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(storage.DataRoot, "proxy-backup.json")));
    }

    [TestMethod]
    public void DeleteEnvironmentFile_WhenPathIsDirectory_PropagatesFailure()
    {
        string directoryPath = Path.Combine(_root, "proxy.env");
        Directory.CreateDirectory(directoryPath);
        var platform = new LinuxProxyPlatform();
        bool threw = false;

        try
        {
            platform.DeleteEnvironmentFile(directoryPath);
        }
        catch (IOException)
        {
            threw = true;
        }
        catch (UnauthorizedAccessException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "A failed persisted environment file deletion must reach recovery logic.");
        Assert.IsTrue(Directory.Exists(directoryPath));
    }

    [TestMethod]
    public void PersistSnapshotForCrashRecovery_RejectsInvalidExistingBackup()
    {
        (LinuxProxyService proxy, _, LinuxStorageService storage) = Build();
        proxy.CaptureCurrentSettings();
        File.WriteAllText(Path.Combine(storage.DataRoot, "proxy-backup.json"), "{}");

        Assert.ThrowsException<InvalidDataException>(() => proxy.PersistSnapshotForCrashRecovery());
    }

    [TestMethod]
    public void Restore_WithoutABackupFile_UsesTheInMemorySnapshot()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.socks", "host", "'socks.corp.example'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.socks", "port", "1080");
        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'manual'");

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        // 备份文件丢了（磁盘满、被清理），只剩标记与本进程启动时抓下的快照。
        File.Delete(Path.Combine(storage.DataRoot, "proxy-backup.json"));

        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 没有快照可恢复时降级为「关掉代理」，那会把用户原来的公司代理清空——
        // 内存里那份快照存在的意义就是不让这件事发生（Windows 端踩过同一个坑）。
        Assert.AreEqual("'socks.corp.example'", platform.GetGnome($"{GnomeProxyCommands.Schema}.socks", "host"));
        Assert.AreEqual("1080", platform.GetGnome($"{GnomeProxyCommands.Schema}.socks", "port"));
    }

    [TestMethod]
    public void TryRecoverFromCrash_WithoutABackupFile_DisablesOnlyOurOwnSections()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.socks", "host", "'socks.corp.example'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.socks", "port", "1080");
        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'manual'");

        proxy.CaptureCurrentSettings();
        proxy.PersistSnapshotForCrashRecovery();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        File.Delete(Path.Combine(storage.DataRoot, "proxy-backup.json"));

        bool recovered = proxy.TryRecoverFromCrash();

        Assert.IsTrue(recovered);
        // 新进程里的崩溃恢复没有内存快照可用（这正是它与 RestoreAsync 的差别），
        // 于是走安全回退：把我们写进去的总开关关掉，用户原来那些值本进程无从得知，
        // 但至少代理不再指向一个没人听的端口。
        Assert.AreEqual("'none'", platform.GetGnome(GnomeProxyCommands.Schema, "mode"));
    }

    [TestMethod]
    public void TryRecoverFromCrash_WhenTheProxyIsNoLongerOurs_OnlyClearsTheEvidence()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'none'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "host", "'elsewhere.example'");

        bool recovered = proxy.TryRecoverFromCrash();

        Assert.IsFalse(recovered);
        Assert.AreEqual("'elsewhere.example'", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "host"));
        Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));

        // 桌面设置不归我们了，但那两个环境变量文件永远是我们的：留下它们，
        // 下次登录整个会话都会带着指向死端口的 http_proxy。
        Assert.IsFalse(platform.EnvironmentFileExists(Path.Combine(_root, "environment.d", "myproxy.conf")));
        Assert.IsFalse(platform.EnvironmentFileExists(Path.Combine(_root, "myproxy", "proxy.env")));
    }

    [TestMethod]
    public void Restore_WhenTheDesktopProxyIsNoLongerOurs_StillRemovesTheEnvironmentFiles()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        string dropIn = Path.Combine(_root, "environment.d", "myproxy.conf");
        string shell = Path.Combine(_root, "myproxy", "proxy.env");

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsTrue(platform.EnvironmentFileExists(dropIn));

        // 连接期间用户在 GNOME 设置里把网络代理关了，又把 http 段改成了别处。
        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'none'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "host", "'elsewhere.example'");

        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 别人的桌面设置一个字不动……
        Assert.AreEqual("'none'", platform.GetGnome(GnomeProxyCommands.Schema, "mode"));
        Assert.AreEqual("'elsewhere.example'", platform.GetGnome($"{GnomeProxyCommands.Schema}.http", "host"));
        // ……但我们自己的环境变量文件必须删掉，证据也一并清掉。
        Assert.IsFalse(platform.EnvironmentFileExists(dropIn));
        Assert.IsFalse(platform.EnvironmentFileExists(shell));
        Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
    }

    [TestMethod]
    public void TryRecoverFromCrash_WithoutAMarker_RemovesLeftoverEnvironmentFiles()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, LinuxStorageService storage) = Build();

        string dropIn = Path.Combine(_root, "environment.d", "myproxy.conf");
        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        // 标记已经没了（上一次恢复删到一半），环境变量文件却还在。
        File.Delete(Path.Combine(storage.RuntimeDir, "proxy-applied.marker"));

        Assert.IsFalse(proxy.TryRecoverFromCrash());
        Assert.IsFalse(platform.EnvironmentFileExists(dropIn));
    }

    [TestMethod]
    public void IsManagedByMyProxy_OnlyMatchesOurOwnCandidatePorts()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, _) = Build();

        Assert.IsFalse(proxy.IsManagedByMyProxy);

        platform.SetGnomeExternally(GnomeProxyCommands.Schema, "mode", "'manual'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "host", "'127.0.0.1'");
        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "port", "10809");
        Assert.IsTrue(proxy.IsManagedByMyProxy);

        platform.SetGnomeExternally($"{GnomeProxyCommands.Schema}.http", "port", "9999");
        Assert.IsFalse(proxy.IsManagedByMyProxy);
    }

    [TestMethod]
    public void EnvironmentFiles_AreWrittenOnConnectAndRemovedOnStop()
    {
        (LinuxProxyService proxy, FakeLinuxProxyPlatform platform, _) = Build();

        string dropIn = Path.Combine(_root, "environment.d", "myproxy.conf");
        string shell = Path.Combine(_root, "myproxy", "proxy.env");

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsTrue(platform.EnvironmentFileExists(dropIn));
        Assert.IsTrue(platform.EnvironmentFileExists(shell));

        string contents = platform.ReadEnvironmentFile(dropIn) ?? "";
        Assert.IsTrue(contents.Contains("http_proxy=http://127.0.0.1:10809", StringComparison.Ordinal));

        // 本地入站是 http 协议的代理：把 all_proxy 或 socks5:// 指向它不是一个有效的
        // SOCKS 代理，客户端会直接失败而不是回退。只看赋值行——注释里会提到这些名字。
        string[] assignments = contents
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains('=') && !line.StartsWith('#'))
            .ToArray();

        Assert.IsFalse(
            assignments.Any(line => line.StartsWith("all_proxy=", StringComparison.OrdinalIgnoreCase)),
            "不该设置 all_proxy：本地入站不是 SOCKS 代理");
        Assert.IsFalse(
            assignments.Any(line => line.Contains("socks5://", StringComparison.Ordinal)),
            "不该出现 socks5:// 指向本地 http 入站");

        proxy.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsFalse(platform.EnvironmentFileExists(dropIn));
        Assert.IsFalse(platform.EnvironmentFileExists(shell));
    }

    [TestMethod]
    public void Enable_OnAMachineWithNoDesktopBackend_StillWritesTheEnvironmentFiles()
    {
        // 无桌面会话的服务器：没有任何 gsettings / kioslaverc 可写，
        // 命令行工具只能靠环境变量文件。
        var platform = new FakeLinuxProxyPlatform { GnomePresent = false, KdePresent = false };
        (LinuxProxyService proxy, FakeLinuxProxyPlatform fake, _) = Build(platform);

        proxy.CaptureCurrentSettings();
        proxy.EnableAsync("127.0.0.1", 10809, CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(0, fake.GnomeWriteCount);
        Assert.IsTrue(fake.EnvironmentFileExists(
            Path.Combine(_root, "environment.d", "myproxy.conf")));
    }
}
