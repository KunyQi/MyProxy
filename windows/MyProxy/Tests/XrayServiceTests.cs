using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class XrayServiceTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string WriteConfig(StorageService storage, int localPort)
    {
        var profile = new ServerProfile
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
        };

        XrayRootConfig config = XrayConfigGenerator.Generate(
            profile,
            ProxyMode.Global,
            localPort,
            Path.Combine(storage.LogDir, "xray.log"));

        string json = JsonSerializer.Serialize(config, XrayConfigGenerator.CreateJsonOptions());
        string configPath = Path.Combine(storage.RuntimeDir, "config.json");
        Directory.CreateDirectory(storage.RuntimeDir);
        File.WriteAllText(configPath, json, new System.Text.UTF8Encoding(false));
        return configPath;
    }

    [TestMethod]
    public async Task StartStop_WritesAndDeletesPidFile()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new XrayService(storage);
            string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
            int localPort = GetFreePort();
            string configPath = WriteConfig(storage, localPort);

            await service.StartAsync(configPath, coreDir, CancellationToken.None);

            try
            {
                Assert.IsTrue(service.IsRunning);
                string pidPath = Path.Combine(storage.RuntimeDir, "xray.pid");
                Assert.IsTrue(File.Exists(pidPath));
                int pid = int.Parse(File.ReadAllText(pidPath));
                Process process = Process.GetProcessById(pid);
                Assert.IsFalse(process.HasExited);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            Assert.IsFalse(service.IsRunning);
            Assert.IsFalse(File.Exists(Path.Combine(storage.RuntimeDir, "xray.pid")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Stop_LogsWhenTheCtrlBreakCannotBeDelivered()
    {
        // 子进程以 CREATE_NO_WINDOW 启动且父进程无控制台，Windows 不给它分配控制台，
        // 于是 AttachConsole 返回 ERROR_INVALID_HANDLE，优雅停止结构上无法生效
        // （详见 XrayService.TrySignalGracefulStop 的注释）。
        // 这条锁两件事：失败必须可见（原来 BOOL 返回值被丢弃，毫无痕迹）；信号送不出去时
        // 不再白等 3 秒超时（切模式、停止、关机收尾都受它拖累）。真正的优雅停止需要换
        // 停止信号，属于设计变更。
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new XrayService(storage, log);
            string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
            string configPath = WriteConfig(storage, GetFreePort());

            await service.StartAsync(configPath, coreDir, CancellationToken.None);
            var stopwatch = Stopwatch.StartNew();
            await service.StopAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.IsFalse(service.IsRunning);
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"信号送不出去时应直接强杀，不该再等 3 秒超时；实际 {stopwatch.ElapsedMilliseconds}ms");

            // 原来 GenerateConsoleCtrlEvent 的 BOOL 返回值被直接丢弃，失败完全无声。
            // 现在停止退化成超时强杀这件事必须留下可诊断的记录。
            string logs = ReadAllLogs(Path.Combine(dataRoot, "logs"));
            StringAssert.Contains(
                logs,
                "CTRL_BREAK 未能送达",
                "信号送达失败时必须记录，否则每次 Stop 白等 3 秒无迹可寻");
            StringAssert.Contains(logs, "AttachConsole failed");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static string ReadAllLogs(string logDir)
    {
        if (!Directory.Exists(logDir))
        {
            return "";
        }

        var text = new System.Text.StringBuilder();
        foreach (string file in Directory.GetFiles(logDir))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    [TestMethod]
    public async Task DuplicateStart_Throws()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new XrayService(storage);
            string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
            int localPort = GetFreePort();
            string configPath = WriteConfig(storage, localPort);

            await service.StartAsync(configPath, coreDir, CancellationToken.None);
            try
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => service.StartAsync(configPath, coreDir, CancellationToken.None));
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnexpectedExit_RaisesExitedEvent()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new XrayService(storage);
            var exitedTcs = new TaskCompletionSource<XrayExitEventArgs>();
            service.Exited += (_, args) => exitedTcs.TrySetResult(args);

            string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
            int localPort = GetFreePort();
            string configPath = WriteConfig(storage, localPort);

            await service.StartAsync(configPath, coreDir, CancellationToken.None);
            int pid = int.Parse(File.ReadAllText(Path.Combine(storage.RuntimeDir, "xray.pid")));

            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                XrayExitEventArgs args = await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(args.Expected);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RestartAsync_StopsAndStartsAgain()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            var service = new XrayService(storage);
            string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
            int localPort = GetFreePort();
            string configPath = WriteConfig(storage, localPort);

            await service.StartAsync(configPath, coreDir, CancellationToken.None);
            int firstPid = int.Parse(File.ReadAllText(Path.Combine(storage.RuntimeDir, "xray.pid")));

            try
            {
                await service.RestartAsync(configPath, coreDir, CancellationToken.None);
                Assert.IsTrue(service.IsRunning);
                int secondPid = int.Parse(File.ReadAllText(Path.Combine(storage.RuntimeDir, "xray.pid")));
                Assert.AreNotEqual(firstPid, secondPid);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
