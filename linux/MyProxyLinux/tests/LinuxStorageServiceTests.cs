using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 设置与缓存的落盘行为。
///
/// <para>
/// 与 Windows 端 <c>StorageServiceTests</c> 盯的是同一组语义——文件损坏时的降级、
/// 解绑时连 feature flags 一起清、设备凭据读失败与未绑定的区分——因为两端共享的是
/// <see cref="IStorageService"/> 这份契约。
/// </para>
/// </summary>
[TestClass]
public sealed class LinuxStorageServiceTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-storage-" + Guid.NewGuid().ToString("N"));
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
    public void Settings_RoundTripThroughDisk()
    {
        var storage = new LinuxStorageService(_root);

        AppSettings defaults = storage.LoadSettings();
        Assert.IsFalse(defaults.AutoUpdateCheck);
        Assert.AreEqual(ProxyMode.Rule, defaults.ProxyMode);
        Assert.AreEqual(UiTheme.Classic, defaults.UiTheme);

        storage.SaveSettingsAsync(new AppSettings
        {
            AutoStart = true,
            AutoConnect = true,
            AutoUpdateCheck = true,
            ProxyMode = ProxyMode.Global
        }, CancellationToken.None).GetAwaiter().GetResult();

        AppSettings reloaded = new LinuxStorageService(_root).LoadSettings();
        Assert.IsTrue(reloaded.AutoStart);
        Assert.IsTrue(reloaded.AutoConnect);
        Assert.IsTrue(reloaded.AutoUpdateCheck);
        Assert.AreEqual(ProxyMode.Global, reloaded.ProxyMode);
    }

    [TestMethod]
    public void Settings_CorruptFile_FallsBackToDefaults()
    {
        var storage = new LinuxStorageService(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ this is not json");

        AppSettings settings = storage.LoadSettings();

        Assert.AreEqual(ProxyMode.Rule, settings.ProxyMode);
        // 覆盖成默认值是刻意的：一个读不懂的设置文件不该让程序再也起不来。
        Assert.IsTrue(File.ReadAllText(Path.Combine(_root, "settings.json")).Contains(
            "proxyMode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ClientInstanceId_IsStableAcrossCallsAndProcesses()
    {
        var storage = new LinuxStorageService(_root);

        string first = storage.GetOrCreateClientInstanceIdAsync(CancellationToken.None).GetAwaiter().GetResult();
        string second = storage.GetOrCreateClientInstanceIdAsync(CancellationToken.None).GetAwaiter().GetResult();
        string afterRestart = new LinuxStorageService(_root)
            .GetOrCreateClientInstanceIdAsync(CancellationToken.None).GetAwaiter().GetResult();

        // 这个值撑起 claim 的幂等：变了服务端就会多建一台设备。
        Assert.AreEqual(first, second);
        Assert.AreEqual(first, afterRestart);
        Assert.IsTrue(Guid.TryParseExact(first, "D", out _));
    }

    [TestMethod]
    public void ConfigCache_RoundTripsWithTheProfile()
    {
        var storage = new LinuxStorageService(_root);
        var entry = new ConfigCacheEntry
        {
            ConfigVersion = 7,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = true,
            Profile = new ServerProfile { Server = "203.0.113.9", Port = 443, Uuid = Guid.NewGuid().ToString() }
        };

        storage.SaveConfigCacheAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

        ConfigCacheEntry? reloaded = new LinuxStorageService(_root).LoadConfigCache();
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(7, reloaded!.ConfigVersion);
        Assert.IsTrue(reloaded.Verified);
        Assert.AreEqual("203.0.113.9", reloaded.Profile.Server);
    }

    [TestMethod]
    public void Device_SaveAndLoadKeepsTheToken()
    {
        var storage = new LinuxStorageService(_root);
        var device = new DeviceConfig
        {
            DeviceId = "dev-1",
            DeviceToken = "token-1",
            DeviceName = "linux-box",
            Platform = "linux",
            BoundAt = DateTimeOffset.UtcNow
        };

        storage.SaveDeviceAsync(device, CancellationToken.None).GetAwaiter().GetResult();

        DeviceConfig? reloaded = new LinuxStorageService(_root).LoadDevice();
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("dev-1", reloaded!.DeviceId);
        Assert.AreEqual("token-1", reloaded.DeviceToken);
        Assert.AreEqual("linux", reloaded.Platform);
    }

    [TestMethod]
    public void ClearBinding_AlsoDropsTheCachedFeatureFlags()
    {
        var storage = new LinuxStorageService(_root);
        storage.SaveDeviceAsync(
            new DeviceConfig { DeviceId = "dev-1", DeviceToken = "token-1" },
            CancellationToken.None).GetAwaiter().GetResult();
        storage.SaveConfigCacheAsync(
            new ConfigCacheEntry { ConfigVersion = 3, FetchedAt = DateTimeOffset.UtcNow, Verified = true },
            CancellationToken.None).GetAwaiter().GetResult();
        storage.UpdateSettingsAsync(
            settings => settings.FeatureFlags["usageCategories"] = "true",
            CancellationToken.None).GetAwaiter().GetResult();

        storage.ClearBindingAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsNull(storage.LoadDevice());
        Assert.IsNull(storage.LoadConfigCache());
        // 缓存的开关属于那台设备：换绑之后沿用它们，第一次连接就会按上一台设备的
        // 开关去生成数据面。
        Assert.AreEqual(0, storage.LoadSettings().FeatureFlags.Count);
    }

    [TestMethod]
    public void RuntimeDirectory_LivesUnderTheDataRootSoItSurvivesAReboot()
    {
        var storage = new LinuxStorageService(_root);

        // 崩溃恢复要读的标记文件不能放在 tmpfs 上：系统的代理设置跨重启存活，
        // 而 $XDG_RUNTIME_DIR 不存活。
        Assert.IsTrue(storage.RuntimeDir.StartsWith(storage.DataRoot, StringComparison.Ordinal));
        Assert.IsTrue(Directory.Exists(storage.RuntimeDir));
        Assert.IsTrue(Directory.Exists(storage.LogDir));
    }
}
