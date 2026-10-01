using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class WindowsProxyServiceTests
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public async Task ThirdPartyProxyChange_IsNotOverwrittenOnRestore()
    {
        // 有有效备份时曾无条件写回快照，而紧邻分支与 TryRecoverFromCrash 都会
        // 先确认注册表值仍属于本程序。连接期间企业 VPN 写 AutoConfigURL、
        // 或用户自己改代理设置，会在 Stop 时被静默覆盖。
        //
        // 这个测试**不是 hermetic 的**：它真读写 HKCU Internet Settings。它在
        // GitHub windows-latest runner 上长期失败而本机常绿，所以每一步的注册表
        // 现场都记下来、附在断言消息里——非 hermetic 的测试失败时只说
        // 「Assert.IsTrue failed」，等于没说。
        //
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        var trace = new List<string> { RegistryTrace.Snap("A 进入时") };
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);

            service.CaptureCurrentSettings();
            trace.Add(RegistryTrace.Snap("B CaptureCurrentSettings 后"));

            await service.EnableAsync("127.0.0.1", 10809, CancellationToken.None);
            trace.Add(RegistryTrace.Snap("C EnableAsync 后"));

            // 第三方在连接期间接管了系统代理。
            var thirdParty = new ProxySettings
            {
                ProxyEnable = 0,
                ProxyServer = "",
                ProxyOverride = "",
                AutoConfigUrl = "http://vpn.example.invalid/proxy.pac",
                AutoDetect = true
            };
            ProxySettings.Restore(thirdParty);

            trace.Add(RegistryTrace.Snap("D 第三方接管后"));

            await service.RestoreAsync(CancellationToken.None);
            trace.Add(RegistryTrace.Snap("E RestoreAsync 后"));

            ProxySettings after = ProxySettings.Read();
            string where = string.Join(Environment.NewLine, trace);
            Assert.AreEqual(thirdParty.AutoConfigUrl, after.AutoConfigUrl, where);
            Assert.IsTrue(after.AutoDetect, where);
            Assert.AreEqual(0, after.ProxyEnable, where);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-applied.marker")), where);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")), where);
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CaptureWithStaleBackup_KeepsTheBackupAsTheInMemoryFallback()
    {
        // _snapshot 曾在「陈旧备份是唯一副本」时恰好为 null，
        // 此时若备份不可读，回退分支会硬清 PAC URL 与 WPAD 自动检测。
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var first = new WindowsProxyService(storage);
            first.CaptureCurrentSettings();
            string backupPath = Path.Combine(dataRoot, "proxy-backup.json");
            Assert.IsTrue(File.Exists(backupPath));
            string backupJson = File.ReadAllText(backupPath);

            // 新实例遇到遗留备份：权威原件是文件，不是当前注册表。
            var second = new WindowsProxyService(storage);
            second.CaptureCurrentSettings();
            Assert.AreEqual(backupJson, File.ReadAllText(backupPath));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task CaptureEnableRestore_RoundTrip()
    {
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);

            service.CaptureCurrentSettings();
            Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")));

            await service.EnableAsync("127.0.0.1", 10809, CancellationToken.None);

            ProxySettings applied = ProxySettings.Read();
            Assert.AreEqual(1, applied.ProxyEnable);
            Assert.AreEqual("127.0.0.1:10809", applied.ProxyServer);
            Assert.AreEqual("<local>", applied.ProxyOverride);
            Assert.AreEqual("", applied.AutoConfigUrl);
            Assert.AreEqual(false, applied.AutoDetect);
            Assert.IsTrue(service.IsManagedByMyProxy);
            Assert.IsTrue(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));

            await service.RestoreAsync(CancellationToken.None);

            ProxySettings restored = ProxySettings.Read();
            Assert.AreEqual(original.ProxyEnable, restored.ProxyEnable);
            Assert.AreEqual(original.ProxyServer, restored.ProxyServer);
            Assert.AreEqual(original.ProxyOverride, restored.ProxyOverride);
            Assert.AreEqual(original.AutoConfigUrl, restored.AutoConfigUrl);
            Assert.AreEqual(original.AutoDetect, restored.AutoDetect);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")));
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingBackup_RestoresTheInMemorySnapshot_NotAnEmptyProxy()
    {
        // 模拟运行期间备份丢失；恢复仍应使用内存里的有效快照，不能直接
        // 关掉代理，用户原来设着的代理就没了。非 hermetic：真读写 HKCU，结束时还原。
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            ProxySettings.Restore(new ProxySettings
            {
                ProxyEnable = 1,
                ProxyServer = "10.0.0.1:8080",
                ProxyOverride = "*.corp.example;<local>",
                AutoConfigUrl = "",
                AutoDetect = original.AutoDetect
            });

            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);
            service.CaptureCurrentSettings();
            await service.EnableAsync("127.0.0.1", 10809, CancellationToken.None);

            // 模拟备份没写成：只剩内存里的快照与 marker。
            File.Delete(Path.Combine(dataRoot, "proxy-backup.json"));
            await service.RestoreAsync(CancellationToken.None);

            ProxySettings restored = ProxySettings.Read();
            Assert.AreEqual(1, restored.ProxyEnable, "用户原来开着代理");
            Assert.AreEqual("10.0.0.1:8080", restored.ProxyServer);
            Assert.AreEqual("*.corp.example;<local>", restored.ProxyOverride);
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CaptureCurrentSettings_DoesNotOverwriteExistingBackup()
    {
        string dataRoot = NewDataRoot();
        try
        {
            Directory.CreateDirectory(dataRoot);
            string backupPath = Path.Combine(dataRoot, "proxy-backup.json");
            File.WriteAllText(backupPath, "{\"capturedAt\":\"2000-01-01T00:00:00Z\",\"proxyEnable\":0,\"proxyServer\":\"\",\"proxyOverride\":\"\",\"autoConfigURL\":\"\",\"autoDetect\":false}");

            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);
            service.CaptureCurrentSettings();

            string content = File.ReadAllText(backupPath);
            StringAssert.Contains(content, "2000-01-01");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RestoreAsync_NoBackup_DoesNotThrowOrChangeProxy()
    {
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);

            await service.RestoreAsync(CancellationToken.None);

            ProxySettings current = ProxySettings.Read();
            Assert.AreEqual(original.ProxyEnable, current.ProxyEnable);
            Assert.AreEqual(original.ProxyServer, current.ProxyServer);
            Assert.AreEqual(original.ProxyOverride, current.ProxyOverride);
            Assert.AreEqual(original.AutoConfigUrl, current.AutoConfigUrl);
            Assert.AreEqual(original.AutoDetect, current.AutoDetect);
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void IsManagedByMyProxy_RecognizesCandidatePorts()
    {
        ProxySettings original = ProxySettings.Read();
        try
        {
            var storage = new StorageService(dataRootOverride: NewDataRoot());
            var service = new WindowsProxyService(storage);

            ProxySettings.Restore(new ProxySettings
            {
                ProxyEnable = 1,
                ProxyServer = "127.0.0.1:20809",
                ProxyOverride = "<local>",
                AutoConfigUrl = "",
                AutoDetect = false
            });

            Assert.IsTrue(service.IsManagedByMyProxy);

            ProxySettings.Restore(new ProxySettings
            {
                ProxyEnable = 1,
                ProxyServer = "127.0.0.1:9999",
                ProxyOverride = "<local>",
                AutoConfigUrl = "",
                AutoDetect = false
            });

            Assert.IsFalse(service.IsManagedByMyProxy);
        }
        finally
        {
            ProxySettings.Restore(original);
        }
    }

    [TestMethod]
    public void TryRecoverFromCrash_MarkerAndProxyMatch_RestoresAndDeletesFiles()
    {
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);

            service.CaptureCurrentSettings();

            Directory.CreateDirectory(storage.RuntimeDir);
            File.WriteAllText(
                Path.Combine(storage.RuntimeDir, "proxy-applied.marker"),
                "{\"pid\":1234,\"port\":10809,\"appliedAt\":\"2026-08-16T00:00:00Z\"}");
            ProxySettings.Restore(new ProxySettings
            {
                ProxyEnable = 1,
                ProxyServer = "127.0.0.1:10809",
                ProxyOverride = "<local>",
                AutoConfigUrl = "",
                AutoDetect = false
            });

            bool recovered = service.TryRecoverFromCrash();

            Assert.IsTrue(recovered);
            ProxySettings restored = ProxySettings.Read();
            Assert.AreEqual(original.ProxyEnable, restored.ProxyEnable);
            Assert.AreEqual(original.ProxyServer, restored.ProxyServer);
            Assert.AreEqual(original.ProxyOverride, restored.ProxyOverride);
            Assert.AreEqual(original.AutoConfigUrl, restored.AutoConfigUrl);
            Assert.AreEqual(original.AutoDetect, restored.AutoDetect);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")));
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void TryRecoverFromCrash_MarkerExistsButProxyNotOurs_DeletesFiles()
    {
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);

            Directory.CreateDirectory(storage.RuntimeDir);
            File.WriteAllText(
                Path.Combine(storage.RuntimeDir, "proxy-applied.marker"),
                "{\"pid\":1234,\"port\":10809,\"appliedAt\":\"2026-08-16T00:00:00Z\"}");
            File.WriteAllText(
                Path.Combine(dataRoot, "proxy-backup.json"),
                "{\"capturedAt\":\"2026-08-16T00:00:00Z\",\"proxyEnable\":0,\"proxyServer\":\"\",\"proxyOverride\":\"\",\"autoConfigURL\":\"\",\"autoDetect\":false}");

            bool recovered = service.TryRecoverFromCrash();

            Assert.IsFalse(recovered);
            ProxySettings current = ProxySettings.Read();
            Assert.AreEqual(original.ProxyEnable, current.ProxyEnable);
            Assert.AreEqual(original.ProxyServer, current.ProxyServer);
            Assert.AreEqual(original.ProxyOverride, current.ProxyOverride);
            Assert.AreEqual(original.AutoConfigUrl, current.AutoConfigUrl);
            Assert.AreEqual(original.AutoDetect, current.AutoDetect);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")));
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void TryRecoverFromCrash_CorruptBackup_DisablesManagedProxyThenDeletesEvidence()
    {
        ProxySettings original = ProxySettings.Read();
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new WindowsProxyService(storage);
            Directory.CreateDirectory(storage.RuntimeDir);
            File.WriteAllText(
                Path.Combine(storage.RuntimeDir, "proxy-applied.marker"),
                "{\"pid\":1234,\"port\":10809,\"appliedAt\":\"2026-08-16T00:00:00Z\"}");
            File.WriteAllText(Path.Combine(dataRoot, "proxy-backup.json"), "{not-json");
            ProxySettings.Restore(new ProxySettings
            {
                ProxyEnable = 1,
                ProxyServer = "127.0.0.1:10809",
                ProxyOverride = "<local>",
                AutoConfigUrl = "",
                AutoDetect = false
            });

            bool recovered = service.TryRecoverFromCrash();

            ProxySettings current = ProxySettings.Read();
            Assert.IsTrue(recovered);
            Assert.AreEqual(0, current.ProxyEnable);
            Assert.AreEqual("", current.ProxyServer);
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "proxy-backup.json")));
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "proxy-applied.marker")));
        }
        finally
        {
            ProxySettings.Restore(original);
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}

internal sealed class ProxySettings
{
    public int ProxyEnable { get; init; }
    public string ProxyServer { get; init; } = "";
    public string ProxyOverride { get; init; } = "";
    public string AutoConfigUrl { get; init; } = "";
    public bool AutoDetect { get; init; }

    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxySettings Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return new ProxySettings
        {
            ProxyEnable = Convert.ToInt32(key?.GetValue("ProxyEnable", 0) ?? 0),
            ProxyServer = Convert.ToString(key?.GetValue("ProxyServer", "") ?? "") ?? "",
            ProxyOverride = Convert.ToString(key?.GetValue("ProxyOverride", "") ?? "") ?? "",
            AutoConfigUrl = Convert.ToString(key?.GetValue("AutoConfigURL", "") ?? "") ?? "",
            AutoDetect = ReadAutoDetect(key)
        };
    }

    /// <summary>
    /// 自动检测（WPAD）的**权威位置是 Connections\DefaultConnectionSettings 的标志字节**
    /// （bit 0x08），独立的 AutoDetect DWORD 只是一份旧镜像。
    ///
    /// 这不是理论：CI 的 windows-latest runner 上实测到 WinINET 会把独立值吸收进 blob
    /// 然后**删掉**它——RestoreAsync 里的 RefreshWinInet 之后，
    /// AutoDetect 从 '1' 变成「不存在」，同时 blob[8] 从 0x03 变成 0x0D
    /// （直连 + 自动配置脚本 + 自动检测），长度从 78 涨到 92（PAC URL 被存了进去）。
    /// 也就是说第三方的设置一个没丢，只是搬了家。本机不做这个吸收，所以只读独立值时
    /// 本机常绿、CI 长红。
    ///
    /// 两处都读、取并集：blob 还没吸收时以独立值为准，吸收之后以 blob 为准。
    /// </summary>
    private static bool ReadAutoDetect(RegistryKey? key)
    {
        if (Convert.ToInt32(key?.GetValue("AutoDetect", 0) ?? 0) != 0)
        {
            return true;
        }

        using RegistryKey? connections = key?.OpenSubKey("Connections", writable: false);
        return connections?.GetValue("DefaultConnectionSettings") is byte[] blob
            && blob.Length >= 9
            && (blob[8] & 0x08) != 0;
    }

    public static void Restore(ProxySettings value)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("ProxyEnable", value.ProxyEnable, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", value.ProxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", value.ProxyOverride, RegistryValueKind.String);
        key.SetValue("AutoConfigURL", value.AutoConfigUrl, RegistryValueKind.String);
        key.SetValue("AutoDetect", value.AutoDetect ? 1 : 0, RegistryValueKind.DWord);
    }
}

/// <summary>
/// 非 hermetic 注册表测试的失败现场记录。
///
/// <see cref="WindowsProxyServiceTests.ThirdPartyProxyChange_IsNotOverwrittenOnRestore"/>
/// 在 GitHub windows-latest runner 上长期失败、本机常绿，而
/// 「Assert.IsTrue failed」这种消息对排查毫无帮助。这里把每一步的注册表现场
/// 摊开：值、值的类型（写错类型会让读侧的转换悄悄退化），以及 WinINET 真正
/// 用来存自动检测的那个 Connections\DefaultConnectionSettings 二进制块的
/// 标志字节——独立的 AutoDetect DWORD 只是它的一份旧镜像。
/// </summary>
internal static class RegistryTrace
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    internal static string Snap(string stage)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key is null)
            {
                return $"{stage,-26} (Internet Settings 键不存在)";
            }

            return $"{stage,-26} PE={Val(key, "ProxyEnable")} PS={Val(key, "ProxyServer")} "
                 + $"ACU={Val(key, "AutoConfigURL")} AD={Val(key, "AutoDetect")} {Blob(key)}";
        }
        catch (Exception ex)
        {
            return $"{stage,-26} (读注册表失败: {ex.GetType().Name} {ex.Message})";
        }
    }

    /// <summary>值 + 类型。类型也要打：DWord 写成 String 时读侧的 Convert 会悄悄给出 0。</summary>
    private static string Val(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null);
        if (value is null)
        {
            return "(缺)";
        }

        return $"'{value}'({key.GetValueKind(name)})";
    }

    /// <summary>
    /// DefaultConnectionSettings 的标志字节：0x01 直连 / 0x02 手动代理 /
    /// 0x04 自动配置脚本 / 0x08 自动检测(WPAD)。
    /// </summary>
    private static string Blob(RegistryKey key)
    {
        using RegistryKey? conn = key.OpenSubKey("Connections", writable: false);
        if (conn?.GetValue("DefaultConnectionSettings") is not byte[] blob || blob.Length < 9)
        {
            return "blob=(缺)";
        }

        return $"blob[8]=0x{blob[8]:X2}(len {blob.Length})";
    }
}
