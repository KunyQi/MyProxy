using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConnectionControllerTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ConnectionController CreateController(
        StorageService storage,
        LogService log,
        FakeConfigService? config = null,
        FakeXrayService? xray = null,
        FakeWindowsProxyService? proxy = null,
        FakeNetworkService? network = null)
    {
        return new ConnectionController(
            storage,
            log,
            config ?? new FakeConfigService(),
            xray ?? new FakeXrayService(),
            proxy ?? new FakeWindowsProxyService(),
            network ?? new FakeNetworkService());
    }

    [TestMethod]
    public void MarkBound_TransitionsToDisconnected()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            ConnectionController controller = CreateController(storage, log);

            controller.MarkBound();

            Assert.AreEqual(AppState.Disconnected, controller.State);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void InitializeMode_SetsMode()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            ConnectionController controller = CreateController(storage, log);

            controller.InitializeMode(ProxyMode.Global);

            Assert.AreEqual(ProxyMode.Global, controller.Mode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SwitchModeAsync_WhenDisconnected_PersistsSettings()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            ConnectionController controller = CreateController(storage, log);
            controller.InitializeMode(ProxyMode.Rule);
            controller.MarkBound();
            bool modeChangedRaised = false;
            controller.ModeChanged += () => modeChangedRaised = true;

            await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);

            Assert.AreEqual(ProxyMode.Global, controller.Mode);
            Assert.AreEqual(ProxyMode.Global, storage.LoadSettings().ProxyMode);
            Assert.IsTrue(modeChangedRaised);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RebindAsync_FromDisconnected_ClearsBindingAndRaisesEvent()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);

            await storage.SaveDeviceAsync(new DeviceConfig
            {
                DeviceId = "dev_test",
                DeviceToken = "tok_test",
                DeviceName = "TEST-PC",
                Platform = "windows",
                ClientVersion = "0.1.0",
                BoundAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await storage.SaveConfigCacheAsync(new ConfigCacheEntry
            {
                ConfigVersion = 7,
                FetchedAt = DateTimeOffset.UtcNow,
                Verified = false,
                Profile = new ServerProfile()
            }, CancellationToken.None);

            ConnectionController controller = CreateController(storage, log);
            controller.MarkBound();
            bool bindingRequiredRaised = false;
            controller.BindingRequired += () => bindingRequiredRaised = true;

            await controller.RebindAsync(CancellationToken.None);

            Assert.AreEqual(AppState.Unbound, controller.State);
            Assert.IsTrue(bindingRequiredRaised);
            Assert.IsNull(storage.LoadDevice());
            Assert.IsNull(storage.LoadConfigCache());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class FakeConfigService : IConfigService
    {
        public Task<ConfigCacheEntry> GetConfigAsync(CancellationToken ct)
        {
            return Task.FromResult(new ConfigCacheEntry
            {
                ConfigVersion = 7,
                FetchedAt = DateTimeOffset.UtcNow,
                Verified = false,
                Profile = new ServerProfile
                {
                    Server = "203.0.113.10",
                    Port = 443,
                    Uuid = "123e4567-e89b-12d3-a456-426614174000",
                    Security = "reality",
                    PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    ShortId = "0123456789abcdef",
                    Sni = "www.microsoft.com",
                    Fingerprint = "chrome",
                    Flow = "xtls-rprx-vision",
                    SpiderX = "/"
                }
            });
        }

        public Task PromoteCurrentConfigAsync(ServerProfile profile, long configVersion, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct)
        {
            return Task.FromResult(new HeartbeatResult
            {
                Ok = true,
                ConfigVersion = 7,
                ServerTime = DateTimeOffset.UtcNow
            });
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenAllCandidatePortsOccupied_ReportsPortUnavailable()
    {
        string dataRoot = NewDataRoot();
        var listeners = new List<TcpListener>();
        try
        {
            foreach (int port in ConnectionController.CandidatePorts)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    listeners.Add(listener);
                }
                catch (SocketException)
                {
                    listener.Stop();
                    // 已被其他进程占用，同样满足测试前置条件。
                }
            }

            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            await storage.SaveDeviceAsync(new DeviceConfig
            {
                DeviceId = "dev_test",
                DeviceToken = "tok_test",
                DeviceName = "TEST-PC",
                Platform = "windows",
                ClientVersion = "0.1.0",
                BoundAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            ConnectionController controller = CreateController(storage, log);
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);

            Assert.AreEqual(AppState.Error, controller.State);
            Assert.AreEqual(ErrorCode.PortUnavailable, controller.LastErrorCode);
            Assert.AreEqual(ErrorCodeMessages.Get(ErrorCode.PortUnavailable), controller.LastErrorMessage);
        }
        finally
        {
            foreach (TcpListener listener in listeners)
            {
                listener.Stop();
            }

            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class FakeXrayService : IXrayService
    {
        public bool IsRunning { get; private set; }

        public event EventHandler<XrayExitEventArgs>? Exited;
        public event EventHandler<string>? OutputReceived { add { } remove { } }

        /// <summary>模拟 xray 进程退出，驱动 HandleUnexpectedXrayExitAsync。</summary>
        public void RaiseExited(bool expected, int exitCode = 1)
        {
            IsRunning = false;
            Exited?.Invoke(this, new XrayExitEventArgs(exitCode, expected));
        }

        public Task StartAsync(string configPath, string workingDir, CancellationToken ct)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task RestartAsync(string configPath, string workingDir, CancellationToken ct)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindowsProxyService : ISystemProxyService
    {
        public bool IsManagedByMyProxy => false;

        public int CaptureCount { get; private set; }
        public int EnableCount { get; private set; }
        public int RestoreCount { get; private set; }

        public void CaptureCurrentSettings() => CaptureCount++;

        public Task EnableAsync(string host, int port, CancellationToken ct)
        {
            EnableCount++;
            return Task.CompletedTask;
        }

        public Task RestoreAsync(CancellationToken ct)
        {
            RestoreCount++;
            return Task.CompletedTask;
        }

        public void PersistSnapshotForCrashRecovery()
        {
        }

        public bool TryRecoverFromCrash() => false;
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public Task<LatencyResult> TestThroughProxyAsync(string host, int port, CancellationToken ct)
        {
            return Task.FromResult(new LatencyResult
            {
                Success = true,
                LatencyMs = 38,
                Error = ErrorCode.ConnectTestFailed
            });
        }

        public Task<ConnectionCheckResult> CheckConnectionAsync(
            string host, int port, string expectedEgress, CancellationToken ct)
        {
            return Task.FromResult(new ConnectionCheckResult
            {
                Reachable = true,
                LatencyMs = 38,
                BestLatencyMs = 31,
                SampleCount = 5,
                Egress = EgressVerdict.Verified,
                Error = ErrorCode.ConnectTestFailed
            });
        }
    }
}
