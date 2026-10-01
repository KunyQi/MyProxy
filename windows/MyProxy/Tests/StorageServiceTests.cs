using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class StorageServiceTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public async Task Settings_RoundTrip_Works()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var settings = new AppSettings
            {
                AutoStart = true,
                AutoConnect = true,
                AutoUpdateCheck = false,
                ProxyMode = ProxyMode.Global,
                UiTheme = UiTheme.Ceramic
            };

            await storage.SaveSettingsAsync(settings, CancellationToken.None);
            AppSettings loaded = storage.LoadSettings();

            Assert.AreEqual(settings.AutoStart, loaded.AutoStart);
            Assert.AreEqual(settings.AutoConnect, loaded.AutoConnect);
            Assert.AreEqual(settings.AutoUpdateCheck, loaded.AutoUpdateCheck);
            Assert.AreEqual(ProxyMode.Global, loaded.ProxyMode);
            Assert.AreEqual(UiTheme.Ceramic, loaded.UiTheme);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClientInstanceId_IsStableAcrossRetryRebindAndServiceRestart()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            string first = await storage.GetOrCreateClientInstanceIdAsync(CancellationToken.None);
            string retry = await storage.GetOrCreateClientInstanceIdAsync(CancellationToken.None);

            await storage.ClearBindingAsync(CancellationToken.None);
            var restartedStorage = new StorageService(dataRootOverride: dataRoot);
            string afterRebind = await restartedStorage.GetOrCreateClientInstanceIdAsync(CancellationToken.None);

            Assert.IsTrue(Guid.TryParseExact(first, "D", out _));
            Assert.AreEqual(first, retry);
            Assert.AreEqual(first, afterRebind);
            Assert.AreEqual(first, restartedStorage.LoadSettings().ClientInstanceId);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentSettingsUpdates_PreserveUnrelatedFields()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);

            await Task.WhenAll(
                storage.UpdateSettingsAsync(settings => settings.AutoConnect = true, CancellationToken.None),
                storage.UpdateSettingsAsync(settings => settings.ProxyMode = ProxyMode.Global, CancellationToken.None),
                storage.GetOrCreateClientInstanceIdAsync(CancellationToken.None));

            AppSettings loaded = storage.LoadSettings();
            Assert.IsTrue(loaded.AutoConnect);
            Assert.AreEqual(ProxyMode.Global, loaded.ProxyMode);
            Assert.IsTrue(Guid.TryParseExact(loaded.ClientInstanceId, "D", out _));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// 升级路径：0.1.0 写下的 settings.json 里没有 uiTheme 这一项。
    /// 读出来必须是 Classic —— 用户没动过外观，就不该因为装了新版本而换了一张脸。
    /// 顺带盯住枚举是按**字符串**存的：存成数字的话，以后往枚举中间插一档就会静默换皮肤。
    /// </summary>
    [TestMethod]
    public void Settings_WithoutUiTheme_FallsBackToClassic()
    {
        string dataRoot = NewDataRoot();
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(
                Path.Combine(dataRoot, "settings.json"),
                """{"autoStart":false,"autoConnect":false,"autoUpdateCheck":true,"proxyMode":"rule"}""");

            AppSettings loaded = new StorageService(dataRootOverride: dataRoot).LoadSettings();

            Assert.AreEqual(UiTheme.Classic, loaded.UiTheme);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>皮肤存的是字符串而不是序号。</summary>
    [TestMethod]
    public void Settings_UiTheme_IsPersistedAsString()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            storage.UpdateSettingsAsync(s => s.UiTheme = UiTheme.Porcelain, CancellationToken.None)
                   .GetAwaiter().GetResult();

            string json = File.ReadAllText(Path.Combine(dataRoot, "settings.json"));

            StringAssert.Contains(json, "\"Porcelain\"",
                $"uiTheme 没有按字符串存：{json}");
            Assert.AreEqual(UiTheme.Porcelain,
                new StorageService(dataRootOverride: dataRoot).LoadSettings().UiTheme);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Settings_Read_IsCaseInsensitive_AndEnumUsesString()
    {
        string dataRoot = NewDataRoot();
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(
                Path.Combine(dataRoot, "settings.json"),
                """{"autoStart":true,"autoConnect":true,"autoUpdateCheck":true,"proxyMode":"global","apiBaseUrl":"http://unit.test:8090"}""");

            AppSettings loaded = new StorageService(dataRootOverride: dataRoot).LoadSettings();

            Assert.AreEqual(ProxyMode.Global, loaded.ProxyMode);
            // apiBaseUrl 还留在这份旧 settings.json 里，但它已经不再是设置项了
            // （目标属于绑定，存在 device.dat 上）。多出来的键不能让读取失败。
            Assert.IsTrue(loaded.AutoStart);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConfigCache_RoundTrip_Works()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var entry = new ConfigCacheEntry
            {
                ConfigVersion = 7,
                FetchedAt = DateTimeOffset.UtcNow,
                Verified = true,
                Profile = new ServerProfile
                {
                    Server = "203.0.113.10",
                    Port = 443,
                    Uuid = "123e4567-e89b-12d3-a456-426614174000",
                    Security = "reality",
                    PublicKey = "pub-key",
                    ShortId = "short-id",
                    Sni = "www.microsoft.com",
                    Fingerprint = "chrome",
                    Flow = "xtls-rprx-vision",
                    SpiderX = "/"
                }
            };

            await storage.SaveConfigCacheAsync(entry, CancellationToken.None);
            ConfigCacheEntry? loaded = storage.LoadConfigCache();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(entry.ConfigVersion, loaded!.ConfigVersion);
            Assert.AreEqual(entry.Verified, loaded.Verified);
            Assert.AreEqual(entry.Profile.Uuid, loaded.Profile.Uuid);
            Assert.AreEqual(entry.Profile.SpiderX, loaded.Profile.SpiderX);
            Assert.AreEqual(entry.Profile.Fingerprint, loaded.Profile.Fingerprint);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptSettings_ReturnsDefaults_AndOverwrites()
    {
        string dataRoot = NewDataRoot();
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{not-json");

            AppSettings loaded = new StorageService(dataRootOverride: dataRoot).LoadSettings();

            Assert.IsFalse(loaded.AutoStart);
            Assert.IsFalse(loaded.AutoConnect);
            Assert.IsFalse(loaded.AutoUpdateCheck);
            Assert.AreEqual(ProxyMode.Rule, loaded.ProxyMode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptConfigCache_ReturnsNull()
    {
        string dataRoot = NewDataRoot();
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "config-cache.json"), "{not-json");

            ConfigCacheEntry? loaded = new StorageService(dataRootOverride: dataRoot).LoadConfigCache();

            Assert.IsNull(loaded);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveJson_IsAtomic_NoTmpLeftBehind()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await storage.SaveSettingsAsync(new AppSettings(), CancellationToken.None);
            await storage.SaveConfigCacheAsync(new ConfigCacheEntry(), CancellationToken.None);

            string[] tmpFiles = Directory.GetFiles(dataRoot, "*.tmp", SearchOption.TopDirectoryOnly);
            Assert.AreEqual(0, tmpFiles.Length);
            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "settings.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "config-cache.json")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Device_RoundTrip_And_ClearBinding_DeletesBothFiles()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var device = new DeviceConfig
            {
                DeviceId = "dev_001",
                DeviceToken = "tok_secret",
                DeviceName = "TEST-PC",
                Platform = "windows",
                ClientVersion = "0.1.0",
                BoundAt = DateTimeOffset.UtcNow
            };

            await storage.SaveDeviceAsync(device, CancellationToken.None);
            await storage.SaveConfigCacheAsync(new ConfigCacheEntry(), CancellationToken.None);

            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "device.dat")));
            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "config-cache.json")));

            DeviceConfig? loaded = storage.LoadDevice();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(device.DeviceToken, loaded!.DeviceToken);

            await storage.ClearBindingAsync(CancellationToken.None);

            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "device.dat")));
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "config-cache.json")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptDeviceFile_ReturnsNull()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            File.WriteAllBytes(Path.Combine(dataRoot, "device.dat"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

            DeviceConfig? loaded = storage.LoadDevice();

            Assert.IsNull(loaded);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
