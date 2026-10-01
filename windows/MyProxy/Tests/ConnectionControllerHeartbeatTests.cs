using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConnectionControllerHeartbeatTests
{
    [TestMethod]
    public async Task Heartbeat401_ClearsBindingAndTransitionsToUnbound()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.TokenInvalid),
                    ErrorCode.TokenInvalid));
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();
            var bindingRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.BindingRequired += () => bindingRequired.TrySetResult();

            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);

            await bindingRequired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => controller.State == AppState.Unbound, TimeSpan.FromSeconds(3));

            Assert.IsNull(storage.LoadDevice());
            Assert.AreEqual(ErrorCode.TokenInvalid, controller.LastErrorCode);
            Assert.AreEqual("设备已失效，请重新绑定", controller.LastErrorMessage);
            Assert.IsTrue(proxy.RestoreCount >= 1);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task StartAsync_WhenProxySnapshotCaptureOrPersistFails_DoesNotStartXray(
        bool failCapture,
        bool failPersist)
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService
            {
                FailCapture = failCapture,
                FailPersist = failPersist
            };
            using var controller = CreateController(
                storage,
                log,
                new FakeConfigService(_ => CreateEntry(7, "node.example.com", verified: true),
                    _ => new HeartbeatResult { Ok = true, ConfigVersion = 7 }),
                xray,
                proxy,
                new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);

            Assert.AreEqual(AppState.Error, controller.State);
            Assert.AreEqual(ErrorCode.ProxyApplyFailed, controller.LastErrorCode);
            Assert.AreEqual(0, xray.StartCount, "Xray must not start without a verified recovery backup.");
            Assert.AreEqual(0, proxy.EnableCount, "System proxy must not be enabled without a verified recovery backup.");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task StaleHeartbeat401_DoesNotClearBindingAfterReconnect()
    {
        string dataRoot = NewDataRoot();
        var heartbeatStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new BlockingHeartbeatConfigService(heartbeatStarted, releaseHeartbeat);
            using var xray = new ListeningFakeXrayService();
            var controller = CreateController(
                storage,
                log,
                config,
                xray,
                new FakeWindowsProxyService(),
                new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            await heartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await controller.StopAsync(CancellationToken.None);
            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);

            releaseHeartbeat.TrySetResult();
            await Task.Delay(100);

            Assert.AreEqual(AppState.Connected, controller.State);
            Assert.IsNotNull(storage.LoadDevice());
            await controller.StopAsync(CancellationToken.None);
        }
        finally
        {
            releaseHeartbeat.TrySetResult();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat401_WhenProxyRestoreFails_PreservesBindingAndRunningXray()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.TokenInvalid),
                    ErrorCode.TokenInvalid));
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService(failRestore: true);
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();
            var bindingRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.BindingRequired += () => bindingRequired.TrySetResult();

            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);

            await WaitUntilAsync(() => controller.State == AppState.Error, TimeSpan.FromSeconds(3));

            Assert.IsFalse(bindingRequired.Task.IsCompleted);
            Assert.IsNotNull(storage.LoadDevice(), "代理未恢复时不得清除仍需重试恢复的绑定");
            Assert.AreEqual(ErrorCode.ProxyApplyFailed, controller.LastErrorCode);
            Assert.IsTrue(xray.IsRunning, "代理恢复失败时不得杀掉仍承载系统代理端口的 xray");
            Assert.AreEqual(0, xray.StopCount);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Stop_WhenProxyRestoreFails_TransitionsToErrorAndPreservesXray()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7 });
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService(failRestore: true);
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            await controller.StopAsync(CancellationToken.None);

            Assert.AreEqual(AppState.Error, controller.State);
            Assert.AreEqual(ErrorCode.ProxyApplyFailed, controller.LastErrorCode);
            Assert.IsTrue(xray.IsRunning);
            Assert.AreEqual(0, xray.StopCount);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task NewConfigVersion_RestartsTestsAndPromotesCandidate()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                call => call == 1
                    ? CreateEntry(7, "old.example.com", verified: true)
                    : CreateEntry(8, "new.example.com", verified: false),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 8 });
            using var xray = new ListeningFakeXrayService();
            var controller = CreateController(
                storage,
                log,
                config,
                xray,
                new FakeWindowsProxyService(),
                new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => config.PromotedVersions.Contains(8) && xray.RestartedConfigs.Count >= 1,
                TimeSpan.FromSeconds(3));

            Assert.AreEqual(AppState.Connected, controller.State);
            Assert.AreEqual(1, xray.RestartedConfigs.Count);
            StringAssert.Contains(xray.RestartedConfigs.Single(), "new.example.com");
            CollectionAssert.AreEqual(new long[] { 8 }, config.PromotedVersions.ToArray());

            await controller.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConfigUpdateTestFails_RestartsOldProfileAndDoesNotPromote()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                call => call == 1
                    ? CreateEntry(7, "old.example.com", verified: true)
                    : CreateEntry(8, "new.example.com", verified: false),
                call => new HeartbeatResult { Ok = true, ConfigVersion = call == 1 ? 8 : 7 });
            using var xray = new ListeningFakeXrayService();
            var network = new FakeNetworkService(
                Success(31),
                Failure(),
                Success(44));
            var controller = CreateController(
                storage,
                log,
                config,
                xray,
                new FakeWindowsProxyService(),
                network);
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => xray.RestartedConfigs.Count >= 2 && controller.LatencyMs == 44,
                TimeSpan.FromSeconds(3));

            string[] restarted = xray.RestartedConfigs.ToArray();
            Assert.AreEqual(AppState.Connected, controller.State);
            Assert.AreEqual(2, restarted.Length);
            StringAssert.Contains(restarted[0], "new.example.com");
            StringAssert.Contains(restarted[1], "old.example.com");
            Assert.AreEqual(0, config.PromotedVersions.Count);
            Assert.AreEqual(44, controller.LatencyMs);

            await controller.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnexpectedExit_WhenRollbackAlreadyRestartedXray_KeepsHeartbeatAlive()
    {
        // StartHeartbeatLoop 全仓库只在 StartAsync 的成功路径上调用一次。
        // CancelHeartbeatLoop 曾在取锁与 bail-out 判定之前无条件执行，于是
        // 「xray 退出事件 + 回滚已重启」的组合会让心跳永久消失：不再更新
        // configVersion，也不再能发现 401 / 撤销。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);

            var heartbeats = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int heartbeatCount = 0;
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ =>
                {
                    if (Interlocked.Increment(ref heartbeatCount) >= 2)
                    {
                        heartbeats.TrySetResult();
                    }

                    return new HeartbeatResult { ConfigVersion = 7 };
                });

            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);
            await heartbeats.Task.WaitAsync(TimeSpan.FromSeconds(5));

            xray.RaiseExited(expected: false, alreadyRestarted: true);

            int before = Volatile.Read(ref heartbeatCount);
            await WaitUntilAsync(
                () => Volatile.Read(ref heartbeatCount) > before + 1,
                TimeSpan.FromSeconds(5));

            Assert.IsTrue(
                Volatile.Read(ref heartbeatCount) > before + 1,
                "bail-out 之后心跳必须继续运行");
            Assert.AreEqual(AppState.Connected, controller.State);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnexpectedExit_WithXrayDown_RollsBackAndStopsHeartbeat()
    {
        // fix 的反面：xray 真的没了时，仍必须回滚系统代理、转 Error、停掉心跳。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);

            int heartbeatCount = 0;
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ =>
                {
                    Interlocked.Increment(ref heartbeatCount);
                    return new HeartbeatResult { ConfigVersion = 7 };
                });

            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);

            xray.RaiseExited(expected: false);

            await WaitUntilAsync(() => controller.State == AppState.Error, TimeSpan.FromSeconds(5));
            Assert.AreEqual(AppState.Error, controller.State);
            Assert.IsTrue(proxy.RestoreCount >= 1);

            int settled = Volatile.Read(ref heartbeatCount);
            await Task.Delay(200);
            Assert.AreEqual(settled, Volatile.Read(ref heartbeatCount), "心跳应已停止");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnexpectedExit_WithFailedProxyRestore_SurfacesRestoreFailure()
    {
        // 三处曾丢弃 RollbackAsync 的返回值，从不调用 SetProxyRestoreFailure()，
        // 使 ErrorCode.ProxyRestoreFailed 在这些路径上不可达。这一条尤其严重：
        // xray 已死而注册表仍指向死掉的本地端口，用户看到的却是可重试文案。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = 7 });

            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            Assert.AreEqual(AppState.Connected, controller.State);

            proxy.FailRestore = true;
            xray.RaiseExited(expected: false);

            await WaitUntilAsync(() => controller.State == AppState.Error, TimeSpan.FromSeconds(5));
            Assert.AreEqual(
                ErrorCodeMessages.ProxyRestoreFailed,
                controller.LastErrorMessage);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenAllStatsPortsOccupied_StillConnects()
    {
        // 统计是可观测性，不是连接的前置条件。SelectStatsPort 在候选全被占用时返回 0，
        // 而 XrayConfigGenerator 拒绝 1024 以下的端口——这个 0 必须在生成配置前被挡掉，
        // 否则一次 ArgumentException 会把整个 StartAsync 打掉，用户彻底连不上。
        string dataRoot = NewDataRoot();
        var listeners = new List<TcpListener>();
        try
        {
            foreach (int port in ConnectionController.CandidateStatsPorts)
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
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(1, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 1, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();

            // 必须注入采集器：没有它 statsApiPort 走的是另一条 null 分支，测不到这个 bug。
            var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), new FakeNetworkService(),
                trafficStats: new FakeTrafficStatsService());
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);

            Assert.AreEqual(AppState.Connected, controller.State,
                "统计端口挑不到绝不能让连接失败");

            await controller.StopAsync(CancellationToken.None);
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

    [TestMethod]
    public async Task EveryProbe_GoesThroughTheProbeInbound_NotTheUserProxyPort()
    {
        // 探测若走用户的 http 入站，检测目标可能在智能分流
        // 模式下会被「国内网站直连」放行：测通的是直连，一份隧道不通的配置也会被提升为
        // LKG。启动、切模式、自检三条路径都必须经探测入站发出。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            var network = new FakeNetworkService();
            using var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), network);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State);
                await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);
                await controller.CheckConnectionAsync(CancellationToken.None);

                using JsonDocument document = JsonDocument.Parse(xray.LastConfig!);
                JsonElement root = document.RootElement;
                int proxyPort = root.GetProperty("inbounds")[0].GetProperty("port").GetInt32();
                JsonElement probeInbound = root.GetProperty("inbounds").EnumerateArray()
                    .Single(i => i.GetProperty("tag").GetString() == XrayConfigGenerator.ProbeInboundTag);
                int probePort = probeInbound.GetProperty("port").GetInt32();
                Assert.AreNotEqual(proxyPort, probePort);

                JsonElement firstRule = root.GetProperty("routing").GetProperty("rules")[0];
                Assert.AreEqual(XrayConfigGenerator.ProbeInboundTag, firstRule.GetProperty("inboundTag")[0].GetString());
                Assert.AreEqual("proxy", firstRule.GetProperty("outboundTag").GetString());

                Assert.IsTrue(network.ProbePorts.Count >= 2, "启动与切模式各至少探测一次");
                CollectionAssert.AreEqual(
                    Enumerable.Repeat(probePort, network.ProbePorts.Count).ToArray(),
                    network.ProbePorts.ToArray(),
                    "每一次探测都必须经探测入站");
                CollectionAssert.AreEqual(new[] { probePort }, network.CheckPorts.ToArray(), "自检同样经探测入站");
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task StartAsync_WhenAllProbePortsOccupied_StillConnectsThroughTheProxyPort()
    {
        // 探测入站挑不到端口时退回先前的行为（经代理端口探测），而不是让连接失败。
        string dataRoot = NewDataRoot();
        var listeners = new List<TcpListener>();
        try
        {
            foreach (int port in ConnectionController.CandidateProbePorts)
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
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(1, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 1, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            var network = new FakeNetworkService();
            using var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), network);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State, "探测端口挑不到绝不能让连接失败");

                using JsonDocument document = JsonDocument.Parse(xray.LastConfig!);
                JsonElement inbounds = document.RootElement.GetProperty("inbounds");
                Assert.IsFalse(
                    inbounds.EnumerateArray().Any(i => i.GetProperty("tag").GetString() == XrayConfigGenerator.ProbeInboundTag),
                    "没有端口就不生成探测入站");
                int proxyPort = inbounds[0].GetProperty("port").GetInt32();
                CollectionAssert.AreEqual(new[] { proxyPort }, network.ProbePorts.ToArray());
            }
            finally { await controller.StopAsync(CancellationToken.None); }
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

    [TestMethod]
    public async Task ModeSwitchRollbackFailure_RestoresProxy_AndTheFirstRetryConnects()
    {
        // 切模式与回滚的测试都失败：以前只转 Error，系统代理继续指向本地端口、xray 留着，
        // 于是第一次「重试」必然撞上「xray 已经在运行」。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            // 启动成功 → 切模式失败 → 回滚失败 → 重试成功
            var network = new FakeNetworkService(Success(30), Failure(), Failure(), Success(30));
            using var controller = CreateController(storage, log, config, xray, proxy, network);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State);

                await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);

                Assert.AreEqual(AppState.Error, controller.State);
                Assert.AreEqual(ErrorCode.ConnectTestFailed, controller.LastErrorCode);
                Assert.IsTrue(proxy.RestoreCount >= 1, "回滚也失败时必须恢复系统代理");
                Assert.IsFalse(xray.IsRunning, "代理恢复成功后 xray 必须停掉");
                Assert.IsNull(controller.LatencyMs);

                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State, "第一次重试就应当连上");
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task StartAsync_WhenXrayExitsImmediately_FailsWithoutProbingEveryAttempt()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService { ExitOnStart = true };
            var proxy = new FakeWindowsProxyService();
            using var controller = CreateController(storage, log, config, xray, proxy, new FakeNetworkService());
            controller.MarkBound();

            // 以前要把 50 次端口探测探满（每次连拒绝的回环端口还要约 2 秒）。
            await controller.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(AppState.Error, controller.State);
            Assert.AreEqual(ErrorCode.XrayStartFailed, controller.LastErrorCode);
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [DataTestMethod]
    [DataRow("mode")]
    [DataRow("rollback")]
    [DataRow("config")]
    [DataRow("reconnect")]
    public async Task PendingCheck_AfterLinkChanges_CannotPublishOldVerdict(string change)
    {
        string dataRoot = NewDataRoot();
        var releaseCheck = new TaskCompletionSource<ConnectionCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            int advertisedVersion = 7;
            var config = new FakeConfigService(
                call => change == "config" && call > 1
                    ? CreateEntry(8, "new.example.com", verified: false)
                    : CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = Volatile.Read(ref advertisedVersion) });
            using var xray = new ListeningFakeXrayService();
            var network = change == "rollback"
                ? new FakeNetworkService(Success(20), Failure(), Success(21))
                : new FakeNetworkService();
            network.PendingCheck = releaseCheck.Task;
            network.IgnoreCheckCancellation = true;
            using var controller = CreateController(storage, log, config, xray, new FakeWindowsProxyService(), network);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State);
                Task<ConnectionCheckResult> check = controller.CheckConnectionAsync(CancellationToken.None);
                await network.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

                if (change == "config")
                {
                    Volatile.Write(ref advertisedVersion, 8);
                    await WaitUntilAsync(() => config.PromotedVersions.Contains(8) && !controller.IsSwitching,
                        TimeSpan.FromSeconds(3));
                }
                else if (change == "reconnect")
                {
                    await controller.StopAsync(CancellationToken.None);
                    await controller.StartAsync(CancellationToken.None);
                }
                else
                {
                    await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);
                    Assert.AreEqual(change == "rollback" ? ProxyMode.Rule : ProxyMode.Global, controller.Mode);
                }

                releaseCheck.SetResult(new ConnectionCheckResult { Reachable = true, Egress = EgressVerdict.Verified });
                ConnectionCheckResult result = await check.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.IsFalse(result.Reachable, "旧请求应返回无法判定");
                Assert.IsNull(controller.LastCheck, "链路重建后不得写回旧自检结论");
                Assert.AreEqual(AppState.Connected, controller.State);
            }
            finally
            {
                releaseCheck.TrySetCanceled();
                await controller.StopAsync(CancellationToken.None);
            }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task CompletedCheck_IsInvalidatedBeforeCoreRestart()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = 7 });
            using var xray = new ListeningFakeXrayService();
            using var controller = CreateController(storage, log, config, xray,
                new FakeWindowsProxyService(), new FakeNetworkService());
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                await controller.CheckConnectionAsync(CancellationToken.None);
                Assert.IsNotNull(controller.LastCheck);
                bool clearedAtRestart = false;
                xray.BeforeRestart = () => clearedAtRestart = controller.LastCheck is null;
                await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);
                Assert.IsTrue(clearedAtRestart, "切换开始就应移除结论，不能等测通后才清空");
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [DataTestMethod]
    [DataRow("stop")]
    [DataRow("mode")]
    [DataRow("dispose")]
    public async Task PendingCheck_IsCancelledWhenItsConnectionEnds(string change)
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = 7 });
            using var xray = new ListeningFakeXrayService();
            var network = new FakeNetworkService
            {
                PendingCheck = new TaskCompletionSource<ConnectionCheckResult>().Task
            };
            using var controller = CreateController(storage, log, config, xray,
                new FakeWindowsProxyService(), network);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Task<ConnectionCheckResult> pending = controller.CheckConnectionAsync(CancellationToken.None);
                await network.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

                if (change == "stop") await controller.StopAsync(CancellationToken.None);
                else if (change == "mode") await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);
                else controller.Dispose();

                ConnectionCheckResult result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.IsFalse(result.Reachable);
                Assert.IsFalse(controller.IsChecking, "停止或重建连接应主动取消旧自检并释放自检入口");
                Assert.IsNull(controller.LastCheck);

                if (change != "dispose")
                {
                    if (change == "stop") await controller.StartAsync(CancellationToken.None);
                    network.PendingCheck = null;
                    Assert.IsTrue((await controller.CheckConnectionAsync(CancellationToken.None)).Reachable,
                        "旧请求取消后，新连接应当可以立刻重新检测");
                }
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task Dispose_DuringStart_AllowsPendingFlowToUnwind()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = 7 });
            using var xray = new ListeningFakeXrayService();
            var proxy = new FakeWindowsProxyService();
            var network = new FakeNetworkService
            {
                PendingProbe = new TaskCompletionSource<LatencyResult>().Task
            };
            using var controller = CreateController(storage, log, config, xray, proxy, network);
            controller.MarkBound();
            Task start = controller.StartAsync(CancellationToken.None);
            await network.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            controller.Dispose();
            await start.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.IsFalse(xray.IsRunning);
            Assert.IsTrue(proxy.RestoreCount > 0);
            Assert.AreEqual(AppState.Error, controller.State);
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => controller.StartAsync(CancellationToken.None));
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [DataTestMethod]
    [DataRow("reconnect")]
    [DataRow("resume")]
    [DataRow("mode")]
    public async Task PendingTraffic_AfterConnectionOrVisibilityChanges_IsDiscarded(string change)
    {
        string dataRoot = NewDataRoot();
        var stats = new BlockingTrafficStatsService();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var config = new FakeConfigService(
                _ => CreateEntry(7, "old.example.com", verified: true),
                _ => new HeartbeatResult { ConfigVersion = 7 });
            using var xray = new ListeningFakeXrayService();
            using var controller = CreateController(storage, log, config, xray,
                new FakeWindowsProxyService(), new FakeNetworkService(), stats);
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);
                await stats.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (change == "reconnect")
                {
                    await controller.StopAsync(CancellationToken.None);
                    await controller.StartAsync(CancellationToken.None);
                }
                else if (change == "resume")
                {
                    controller.SetTrafficSamplingSuspended(true);
                    controller.SetTrafficSamplingSuspended(false);
                }
                else await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);

                stats.Release.SetResult(new TrafficCounters(120_000, 240_000));
                await stats.NextQuery.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Assert.AreEqual(0, controller.TrafficSamples.Count,
                    "旧查询即使忽略取消也不能成为新连接或恢复显示后的速率基准");
                Assert.AreEqual(TrafficRate.Zero, controller.TrafficRate);
            }
            finally
            {
                stats.Release.TrySetResult(null);
                await controller.StopAsync(CancellationToken.None);
            }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    private sealed class BlockingTrafficStatsService : ITrafficStatsService
    {
        private int _queries;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextQuery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TrafficCounters?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TrafficCounters?> QueryAsync(int apiPort, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _queries) == 1)
            {
                Started.TrySetResult();
                return Release.Task; // 有意忽略取消，模拟恰好晚到的响应。
            }
            NextQuery.TrySetResult();
            return Task.FromResult<TrafficCounters?>(null);
        }

        // 归因采样在这些用例里一律关闭，返回 null 表示「这次没采到」。
        public Task<IReadOnlyDictionary<string, TrafficCounters>?> QueryByTagAsync(int apiPort, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, TrafficCounters>?>(null);
    }

    private sealed class FakeTrafficStatsService : ITrafficStatsService
    {
        public Task<TrafficCounters?> QueryAsync(int apiPort, CancellationToken ct)
            => Task.FromResult<TrafficCounters?>(new TrafficCounters(0, 0));

        // 归因采样在这些用例里一律关闭，返回 null 表示「这次没采到」。
        public Task<IReadOnlyDictionary<string, TrafficCounters>?> QueryByTagAsync(int apiPort, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, TrafficCounters>?>(null);
    }

    [TestMethod]
    public async Task CategorySampling_RunsWhileTheWindowIsHidden_AndCoreRestartsResetTheBaseline()
    {
        // 常驻托盘才是常态：归因采样以前挂在「窗口可见才采」的速率图循环上，
        // 自启后一直在托盘的设备几乎不上报任何类别；ResetBaseline 也从来没人调用。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            var reporter = new CountingUsageReporter();
            using var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), new FakeNetworkService(),
                trafficStats: new TaggedTrafficStatsService(),
                usageReporter: reporter,
                categoryAttributionEnabled: () => true,
                usageSampleInterval: TimeSpan.FromMilliseconds(10));
            controller.MarkBound();
            controller.SetTrafficSamplingSuspended(true);
            try
            {
                await controller.StartAsync(CancellationToken.None);
                Assert.AreEqual(AppState.Connected, controller.State);
                int resetsAfterStart = reporter.Resets;
                Assert.IsTrue(resetsAfterStart >= 1, "启动内核前必须忘掉旧基线");

                await WaitUntilAsync(() => reporter.Observations >= 1, TimeSpan.FromSeconds(5));

                await controller.SwitchModeAsync(ProxyMode.Global, CancellationToken.None);
                Assert.IsTrue(reporter.Resets > resetsAfterStart, "切模式重启内核也要重置基线");
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task Stop_SamplesOnceMoreAndFlushesThisHoursCategoryUsage()
    {
        // 待上报量以前只在跨小时时才发：连半小时就停的那次，归因整桶丢失。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            var reporter = new CountingUsageReporter();
            using var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), new FakeNetworkService(),
                trafficStats: new TaggedTrafficStatsService(),
                usageReporter: reporter,
                categoryAttributionEnabled: () => true,
                usageSampleInterval: TimeSpan.FromHours(1));
            controller.MarkBound();

            await controller.StartAsync(CancellationToken.None);
            int observedBeforeStop = reporter.Observations;
            await controller.StopAsync(CancellationToken.None);

            await WaitUntilAsync(() => reporter.Flushes >= 1, TimeSpan.FromSeconds(3));
            Assert.IsTrue(reporter.Observations > observedBeforeStop, "停止前要再采最后一笔");
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    private sealed class TaggedTrafficStatsService : ITrafficStatsService
    {
        private long _bytes;

        public Task<TrafficCounters?> QueryAsync(int apiPort, CancellationToken ct)
            => Task.FromResult<TrafficCounters?>(new TrafficCounters(0, 0));

        public Task<IReadOnlyDictionary<string, TrafficCounters>?> QueryByTagAsync(int apiPort, CancellationToken ct)
        {
            long bytes = Interlocked.Add(ref _bytes, 1000);
            IReadOnlyDictionary<string, TrafficCounters> byTag = new Dictionary<string, TrafficCounters>
            {
                ["cat-video"] = new TrafficCounters(bytes, bytes)
            };
            return Task.FromResult<IReadOnlyDictionary<string, TrafficCounters>?>(byTag);
        }
    }

    private sealed class CountingUsageReporter : IUsageReporter
    {
        private int _observations;
        private int _resets;

        public int Observations => Volatile.Read(ref _observations);
        public int Resets => Volatile.Read(ref _resets);

        public Task ObserveAsync(IReadOnlyDictionary<string, TrafficCounters> byTag, CancellationToken ct)
        {
            Interlocked.Increment(ref _observations);
            return Task.CompletedTask;
        }

        public void ResetBaseline() => Interlocked.Increment(ref _resets);

        private int _flushes;

        public int Flushes => Volatile.Read(ref _flushes);

        public Task FlushAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _flushes);
            return Task.CompletedTask;
        }
    }

    private static ConnectionController CreateController(
        StorageService storage,
        LogService log,
        IConfigService config,
        IXrayService xray,
        ISystemProxyService proxy,
        INetworkService network,
        ITrafficStatsService? trafficStats = null,
        IUsageReporter? usageReporter = null,
        Func<bool>? categoryAttributionEnabled = null,
        TimeSpan? usageSampleInterval = null)
    {
        return new ConnectionController(
            storage,
            log,
            config,
            xray,
            proxy,
            network,
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            trafficStats: trafficStats,
            usageReporter: usageReporter,
            categoryAttributionEnabled: categoryAttributionEnabled,
            usageSampleInterval: usageSampleInterval);
    }

    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public async Task HeartbeatCertificateRejection_IsSurfacedWithoutDroppingTheConnection()
    {
        // 服务器证书验证失败：以前心跳只记一条 Warn，用户永远看不到原因。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            int beats = 0;
            var config = new FakeConfigService(
                _ => CreateEntry(7, "node.example.com", verified: true),
                _ => Interlocked.Increment(ref beats) <= 3
                    ? throw new MyProxyException(
                        ErrorCodeMessages.Get(ErrorCode.ServerUntrusted), ErrorCode.ServerUntrusted)
                    : new HeartbeatResult { Ok = true, ConfigVersion = 7, ServerTime = DateTimeOffset.UtcNow });
            using var xray = new ListeningFakeXrayService();
            using var controller = CreateController(
                storage, log, config, xray, new FakeWindowsProxyService(), new FakeNetworkService());
            controller.MarkBound();
            try
            {
                await controller.StartAsync(CancellationToken.None);

                await WaitUntilAsync(() => controller.ServerIdentityUnverified, TimeSpan.FromSeconds(3));
                Assert.AreEqual(AppState.Connected, controller.State, "数据面照常，不能因此断开");

                // 心跳一恢复就清除。
                await WaitUntilAsync(() => !controller.ServerIdentityUnverified, TimeSpan.FromSeconds(3));
            }
            finally { await controller.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    private static async Task SaveDeviceAsync(StorageService storage)
    {
        await storage.SaveDeviceAsync(new DeviceConfig
        {
            DeviceId = "dev_test",
            DeviceToken = "tok_test",
            DeviceName = "TEST-PC",
            Platform = "windows",
            ClientVersion = "0.1.0",
            BoundAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    private static ConfigCacheEntry CreateEntry(long version, string server, bool verified)
    {
        return new ConfigCacheEntry
        {
            ConfigVersion = version,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = verified,
            Profile = new ServerProfile
            {
                Server = server,
                Port = 443,
                Uuid = version == 7
                    ? "123e4567-e89b-12d3-a456-426614174000"
                    : "123e4567-e89b-12d3-a456-426614174001",
                Security = "reality",
                PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ShortId = "0123456789abcdef",
                Sni = "www.microsoft.com",
                Fingerprint = "chrome",
                Flow = "xtls-rprx-vision",
                SpiderX = "/"
            }
        };
    }

    private static LatencyResult Success(int latencyMs) => new()
    {
        Success = true,
        LatencyMs = latencyMs,
        Error = ErrorCode.ConnectTestFailed
    };

    private static LatencyResult Failure() => new()
    {
        Success = false,
        Error = ErrorCode.ConnectTestFailed
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("等待后台状态变化超时。");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeConfigService : IConfigService
    {
        private readonly Func<int, ConfigCacheEntry> _configFactory;
        private readonly Func<int, HeartbeatResult> _heartbeatFactory;
        private int _configCalls;
        private int _heartbeatCalls;

        public FakeConfigService(
            Func<int, ConfigCacheEntry> configFactory,
            Func<int, HeartbeatResult> heartbeatFactory)
        {
            _configFactory = configFactory;
            _heartbeatFactory = heartbeatFactory;
        }

        public ConcurrentQueue<long> PromotedVersions { get; } = new();

        public Task<ConfigCacheEntry> GetConfigAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_configFactory(Interlocked.Increment(ref _configCalls)));
        }

        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_heartbeatFactory(Interlocked.Increment(ref _heartbeatCalls)));
        }

        public Task PromoteCurrentConfigAsync(ServerProfile profile, long configVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PromotedVersions.Enqueue(configVersion);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingHeartbeatConfigService : IConfigService
    {
        private readonly TaskCompletionSource _heartbeatStarted;
        private readonly TaskCompletionSource _releaseHeartbeat;
        private int _heartbeatCalls;

        public BlockingHeartbeatConfigService(
            TaskCompletionSource heartbeatStarted,
            TaskCompletionSource releaseHeartbeat)
        {
            _heartbeatStarted = heartbeatStarted;
            _releaseHeartbeat = releaseHeartbeat;
        }

        public Task<ConfigCacheEntry> GetConfigAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(CreateEntry(7, "old.example.com", verified: true));
        }

        public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _heartbeatCalls) == 1)
            {
                _heartbeatStarted.TrySetResult();
                await _releaseHeartbeat.Task.ConfigureAwait(false);
                throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.TokenInvalid),
                    ErrorCode.TokenInvalid);
            }

            return new HeartbeatResult { Ok = true, ConfigVersion = 7 };
        }

        public Task PromoteCurrentConfigAsync(ServerProfile profile, long configVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ListeningFakeXrayService : IXrayService, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<TcpListener> _listeners = new();

        public bool IsRunning { get; private set; }

        /// <summary>最近一次启动（或重启）时读到的配置原文。</summary>
        public string? LastConfig { get; private set; }

        public int StopCount { get; private set; }
        public int StartCount { get; private set; }

        public ConcurrentQueue<string> RestartedConfigs { get; } = new();

        public Action? BeforeRestart { get; set; }

        public event EventHandler<XrayExitEventArgs>? Exited;
        public event EventHandler<string>? OutputReceived { add { } remove { } }

        /// <summary>模拟 xray 进程退出，驱动 HandleUnexpectedXrayExitAsync。</summary>
        /// <param name="alreadyRestarted">
        /// true 表示回滚已把 xray 重新拉起，处理器取到锁时 IsRunning 仍为真 ——
        /// 正是 bail-out 分支要覆盖的场景。
        /// </param>
        public void RaiseExited(bool expected, bool alreadyRestarted = false, int exitCode = 1)
        {
            if (!alreadyRestarted)
            {
                StopListener();
            }

            Exited?.Invoke(this, new XrayExitEventArgs(exitCode, expected));
        }

        /// <summary>模拟 xray 一启动就退出（配置被拒、端口被抢）：不监听，也不在运行。</summary>
        public bool ExitOnStart { get; set; }

        public Task StartAsync(string configPath, string workingDir, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            StartCount++;

            // 与真实 XrayService 一致：已经在运行时拒绝第二次启动。
            if (IsRunning)
            {
                throw new InvalidOperationException("xray 已经在运行");
            }

            if (ExitOnStart)
            {
                return Task.CompletedTask;
            }

            StartListener(configPath);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            StopCount++;
            StopListener();
            return Task.CompletedTask;
        }

        public Task RestartAsync(string configPath, string workingDir, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeRestart?.Invoke();
            string config = File.ReadAllText(configPath);
            RestartedConfigs.Enqueue(config);
            StopListener();
            StartListener(configPath);
            return Task.CompletedTask;
        }

        public void Dispose() => StopListener();

        // 与真 xray 一样监听配置里的每一个入站：控制器在探测前要等代理端口与探测端口都就绪。
        private void StartListener(string configPath)
        {
            string config = File.ReadAllText(configPath);
            using JsonDocument document = JsonDocument.Parse(config);
            lock (_sync)
            {
                foreach (JsonElement inbound in document.RootElement.GetProperty("inbounds").EnumerateArray())
                {
                    var listener = new TcpListener(IPAddress.Loopback, inbound.GetProperty("port").GetInt32());
                    listener.Start();
                    _listeners.Add(listener);
                }

                LastConfig = config;
                IsRunning = true;
            }
        }

        private void StopListener()
        {
            lock (_sync)
            {
                foreach (TcpListener listener in _listeners)
                {
                    listener.Stop();
                }

                _listeners.Clear();
                IsRunning = false;
            }
        }
    }

    private sealed class FakeWindowsProxyService : ISystemProxyService
    {
        private int _restoreCount;

        public FakeWindowsProxyService(bool failRestore = false)
        {
            FailRestore = failRestore;
        }

        /// <summary>连接建立之后再让恢复失败，用于覆盖回滚失败路径。</summary>
        public bool FailRestore { get; set; }
        public bool FailCapture { get; set; }
        public bool FailPersist { get; set; }
        public int EnableCount { get; private set; }

        public bool IsManagedByMyProxy => false;
        public int RestoreCount => Volatile.Read(ref _restoreCount);

        public void CaptureCurrentSettings()
        {
            if (FailCapture)
            {
                throw new IOException("simulated snapshot capture failure");
            }
        }

        public Task EnableAsync(string host, int port, CancellationToken ct)
        {
            EnableCount++;
            return Task.CompletedTask;
        }

        public Task RestoreAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _restoreCount);
            if (FailRestore)
            {
                throw new InvalidOperationException("simulated proxy restore failure");
            }
            return Task.CompletedTask;
        }

        public void PersistSnapshotForCrashRecovery()
        {
            if (FailPersist)
            {
                throw new IOException("simulated snapshot persistence failure");
            }
        }

        public bool TryRecoverFromCrash() => false;
    }

    private sealed class FakeNetworkService : INetworkService
    {
        private readonly Queue<LatencyResult> _results;
        private readonly object _sync = new();
        public Task<ConnectionCheckResult>? PendingCheck { get; set; }
        public bool IgnoreCheckCancellation { get; set; }
        public Task<LatencyResult>? PendingProbe { get; set; }
        public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>每次连通性探测与自检实际发往的本地端口。</summary>
        public ConcurrentQueue<int> ProbePorts { get; } = new();
        public ConcurrentQueue<int> CheckPorts { get; } = new();

        public FakeNetworkService(params LatencyResult[] results)
        {
            _results = new Queue<LatencyResult>(results.Length == 0 ? new[] { Success(30) } : results);
        }

        public Task<LatencyResult> TestThroughProxyAsync(string host, int port, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ProbePorts.Enqueue(port);
            ProbeStarted.TrySetResult();
            if (PendingProbe is not null) return PendingProbe.WaitAsync(ct);
            lock (_sync)
            {
                LatencyResult result = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
                return Task.FromResult(result);
            }
        }

        public Task<ConnectionCheckResult> CheckConnectionAsync(
            string host, int port, string expectedEgress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CheckPorts.Enqueue(port);
            CheckStarted.TrySetResult();
            if (PendingCheck is not null) return IgnoreCheckCancellation ? PendingCheck : PendingCheck.WaitAsync(ct);
            return Task.FromResult(new ConnectionCheckResult
            {
                Reachable = true,
                LatencyMs = 30,
                BestLatencyMs = 24,
                SampleCount = 5,
                Egress = EgressVerdict.Verified,
                Error = ErrorCode.ConnectTestFailed
            });
        }
    }
}
