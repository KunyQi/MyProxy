using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class WindowsProxyRestoreRetryTests
{
    [TestMethod]
    public void CaptureCurrentSettings_RejectsUnreadableExistingBackup()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.BackupPath, "{");

        try
        {
            fixture.Service.CaptureCurrentSettings();
            Assert.Fail("Unreadable backup content must be rejected.");
        }
        catch (JsonException)
        {
        }
    }

    [TestMethod]
    public void PersistSnapshotForCrashRecovery_RejectsInvalidExistingBackup()
    {
        using var fixture = new Fixture();
        fixture.Service.CaptureCurrentSettings();
        File.WriteAllText(fixture.BackupPath, "{}");

        Assert.ThrowsException<InvalidDataException>(
            () => fixture.Service.PersistSnapshotForCrashRecovery());
    }

    [DataTestMethod]
    [DataRow(true, false, true, false)]
    [DataRow(true, false, false, true)]
    [DataRow(true, false, true, true)]
    [DataRow(false, false, true, true)]
    [DataRow(true, true, true, false)]
    [DataRow(true, true, false, true)]
    [DataRow(true, true, true, true)]
    [DataRow(false, true, true, true)]
    public async Task Restore_PreservesThirdPartyAutomaticSettings(
        bool keepBackup, bool recoverFromCrash, bool changePac, bool changeWpad)
    {
        using var fixture = new Fixture();
        const string originalPac = "https://original.example.invalid/proxy.pac";
        const string externalPac = "https://vpn.example.invalid/proxy.pac";
        fixture.Platform.Registry.AutoConfigUrl = originalPac;
        fixture.Platform.Registry.AutoDetect = !changeWpad;
        fixture.Platform.Registry.ProxyOverride = "original-bypass";
        await fixture.EnableAsync(keepBackup);

        // Leave MyProxy's manual proxy untouched; only the independent fields change.
        if (changePac) fixture.Platform.Registry.AutoConfigUrl = externalPac;
        if (changeWpad) fixture.Platform.Registry.AutoDetect = true;
        if (changePac && changeWpad) fixture.Platform.Registry.ProxyOverride = "vpn-bypass";
        if (recoverFromCrash)
        {
            Assert.IsTrue(fixture.Service.TryRecoverFromCrash());
        }
        else
        {
            await fixture.Service.RestoreAsync(CancellationToken.None);
        }

        ProxySnapshot restored = fixture.Platform.Registry;
        Assert.AreEqual(0, restored.ProxyEnable);
        Assert.AreEqual("", restored.ProxyServer);
        Assert.AreEqual(changePac ? externalPac : keepBackup ? originalPac : "", restored.AutoConfigUrl);
        Assert.AreEqual(changeWpad || keepBackup, restored.AutoDetect);
        Assert.AreEqual(changePac && changeWpad ? "vpn-bypass" : "original-bypass", restored.ProxyOverride);
        Assert.AreEqual(restored.AutoConfigUrl, fixture.Platform.Cached.AutoConfigUrl);
        Assert.AreEqual(restored.AutoDetect, fixture.Platform.Cached.AutoDetect);
        Assert.IsFalse(File.Exists(fixture.MarkerPath));
        Assert.IsFalse(File.Exists(fixture.BackupPath));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Restore_RetriesRefreshBeforeDeletingEvidence(bool keepBackup)
    {
        using var fixture = new Fixture();
        await fixture.EnableAsync(keepBackup);
        fixture.Platform.FailRefresh = true;

        await Assert.ThrowsExceptionAsync<Win32Exception>(
            () => fixture.Service.RestoreAsync(CancellationToken.None));
        Assert.AreEqual(0, fixture.Platform.Registry.ProxyEnable);
        Assert.AreEqual(1, fixture.Platform.Cached.ProxyEnable);
        fixture.AssertEvidence(keepBackup);

        fixture.Platform.FailRefresh = false;
        await fixture.Service.RestoreAsync(CancellationToken.None);

        Assert.AreEqual(3, fixture.Platform.RefreshCalls); // enable, failed restore, retry
        Assert.AreEqual(0, fixture.Platform.Cached.ProxyEnable);
        Assert.IsFalse(File.Exists(fixture.MarkerPath));
        Assert.IsFalse(File.Exists(fixture.BackupPath));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Restore_RepeatedRefreshFailurePreservesEvidence(bool keepBackup)
    {
        using var fixture = new Fixture();
        await fixture.EnableAsync(keepBackup);
        fixture.Platform.FailRefresh = true;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsExceptionAsync<Win32Exception>(
                () => fixture.Service.RestoreAsync(CancellationToken.None));
            fixture.AssertEvidence(keepBackup);
            Assert.AreEqual(0, fixture.Platform.Registry.ProxyEnable);
            Assert.AreEqual("127.0.0.1:10809", fixture.Platform.Cached.ProxyServer);
        }
        Assert.AreEqual(3, fixture.Platform.RefreshCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        public FakePlatform Platform { get; } = new();
        public WindowsProxyService Service { get; }
        public string MarkerPath { get; }
        public string BackupPath => Path.Combine(_root, "proxy-backup.json");

        public Fixture()
        {
            var storage = new StorageService(dataRootOverride: _root);
            MarkerPath = Path.Combine(storage.RuntimeDir, "proxy-applied.marker");
            Service = new WindowsProxyService(storage, Platform);
        }

        public async Task EnableAsync(bool keepBackup)
        {
            Service.CaptureCurrentSettings();
            await Service.EnableAsync("127.0.0.1", 10809, CancellationToken.None);
            if (!keepBackup) File.Delete(BackupPath);
        }

        public void AssertEvidence(bool keepBackup)
        {
            Assert.IsTrue(File.Exists(MarkerPath));
            Assert.AreEqual(keepBackup, File.Exists(BackupPath));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FakePlatform : IWindowsProxyPlatform
    {
        public ProxySnapshot Registry { get; private set; } = new() { CapturedAt = DateTimeOffset.UtcNow };
        public ProxySnapshot Cached { get; private set; } = new();
        public bool FailRefresh { get; set; }
        public int RefreshCalls { get; private set; }

        public ProxySnapshot ReadSnapshot() => Copy(Registry);
        public void WriteSnapshot(ProxySnapshot snapshot) => Registry = Copy(snapshot);

        public void RefreshWinInet()
        {
            RefreshCalls++;
            if (FailRefresh) throw new Win32Exception(5, "injected WinINET refresh failure");
            Cached = Copy(Registry);
        }

        private static ProxySnapshot Copy(ProxySnapshot source) => new()
        {
            CapturedAt = DateTimeOffset.UtcNow,
            ProxyEnable = source.ProxyEnable,
            ProxyServer = source.ProxyServer,
            ProxyOverride = source.ProxyOverride,
            AutoConfigUrl = source.AutoConfigUrl,
            AutoDetect = source.AutoDetect
        };
    }
}
