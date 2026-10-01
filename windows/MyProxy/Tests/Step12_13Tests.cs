using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Models;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class Step12_13Tests
{
    [TestMethod]
    public void StartupService_SetAndGet_RoundTrip()
    {
        // 自启动改用「带最高权限的计划任务」（见 StartupService），创建/删除任务需要
        // 管理员权限。CI 的 windows runner 以提权账户运行，会真正跑这条往返；开发机
        // 一致。
        if (!IsProcessElevated())
        {
            Assert.Inconclusive("创建计划任务需要管理员权限；非提权环境跳过。");
        }

        var service = new StartupService();
        try
        {
            service.SetAutoStartEnabled(true);
            Assert.IsTrue(service.IsAutoStartEnabled());

            service.SetAutoStartEnabled(false);
            Assert.IsFalse(service.IsAutoStartEnabled());

            // 旧版用命令行建的任务：路径对、设置是默认值（电池上不启动、72 小时后结束）。
            // 必须判为「未启用」，启动逻辑才会用新设置覆盖它。
            string exe = Environment.ProcessPath!;
            using (System.Diagnostics.Process legacy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"MyProxy Autostart\" /TR \"\\\"{exe}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!)
            {
                legacy.WaitForExit();
                Assert.AreEqual(0, legacy.ExitCode, "创建旧式任务失败");
            }

            Assert.IsFalse(service.IsAutoStartEnabled(), "旧版默认设置的任务必须被识别为需要重建");
            service.SetAutoStartEnabled(true);
            Assert.IsTrue(service.IsAutoStartEnabled());
        }
        finally
        {
            // 无论断言结果如何，别把测试任务留在机器上。
            service.SetAutoStartEnabled(false);
        }
    }

    [TestMethod]
    public void StartupService_RemoveLegacyAutoStart_DeletesTheOldRunValue()
    {
        // 旧实现把自启动写在 HKCU\...\Run。换成计划任务之后，SetAutoStartEnabled
        // 只删计划任务，不认识这个值——不专门清理的话，升级上来的用户会永远留着
        // 一条在登录时不会静默提权、因而必然失效的自启动项，而且从应用里关不掉。
        // 只写 HKCU，不需要提权，所以这条在开发机与 CI 上都真的跑。
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "MyProxy";

        using Microsoft.Win32.RegistryKey key =
            Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 HKCU Run 注册表项。");

        // 机器上本来就有的值要原样放回去：这条测试不是 hermetic 的。
        object? original = key.GetValue(valueName);
        try
        {
            key.SetValue(valueName, "\"C:\\legacy\\MyProxy.exe\" --autostart", Microsoft.Win32.RegistryValueKind.String);
            Assert.IsNotNull(key.GetValue(valueName), "前置条件：残留值应当写进去了。");

            new StartupService().RemoveLegacyAutoStart();
            Assert.IsNull(key.GetValue(valueName), "迁移应当把旧的 Run 值删掉。");

            // 幂等：没有残留时再跑一次不应抛。
            new StartupService().RemoveLegacyAutoStart();
        }
        finally
        {
            if (original is null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(valueName, original);
            }
        }
    }

    private static bool IsProcessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [TestMethod]
    public async Task UpdateService_UnsignedNewerVersion_IsNotOffered()
    {
        // Unsigned update metadata must never offer an install, including mandatory updates.
        using var server = new UpdateStubServer(
            """{"version":"0.2.0","downloadUrl":"https://example.invalid/MyProxy-0.2.0.zip","sha256":"abc","mandatory":true}""");

        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsNull(result.Update);
    }

    [TestMethod]
    public async Task UpdateService_SignedManifest_OffersTheSignedVersionAndLink_NotThePlainCopy()
    {
        byte[] secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["test-key"] = TestSigner.PublicKey(secret)
        };
        (string manifest, string signature) = ReleaseManifestTests.Sign(
            ReleaseManifestTests.ManifestJson(
                version: "1.2.3",
                url: "https://releases.example/MyProxy-1.2.3.zip",
                mandatory: false),
            secret);

        // 明文副本被改过：版本、链接、强制标记都与签名覆盖的那份不同。
        string body = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = "9.9.9",
            ["downloadUrl"] = "https://attacker.example/MyProxy.exe",
            ["sha256"] = "",
            ["mandatory"] = true,
            ["releaseId"] = "rel_test",
            ["manifest"] = manifest,
            ["signature"] = signature,
            ["signingKeyId"] = "test-key"
        });
        using var server = new UpdateStubServer(body);

        var service = new UpdateService(
            new ApiEndpoint(BindingTarget.FromBaseUrl(server.BaseUrl)),
            signingKeys: keys);
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.AreEqual("1.2.3", result.Update!.Version);
        Assert.AreEqual("https://releases.example/MyProxy-1.2.3.zip", result.Update.DownloadUrl);
        Assert.IsFalse(result.Update.Mandatory);
    }

    [TestMethod]
    public async Task UpdateService_RemoteVersionLowerOrEqual_ConfirmsUpToDate()
    {
        using var server = new UpdateStubServer(
            """{"version":"0.1.0","downloadUrl":"https://example.invalid/MyProxy-0.1.0.zip","sha256":"abc","mandatory":false}""");

        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
        Assert.IsNull(result.Update);
    }

    [TestMethod]
    public async Task UpdateService_InvalidVersion_ReportsFailure()
    {
        using var server = new UpdateStubServer(
            """{"version":"not-semver","downloadUrl":"https://example.invalid/x.zip","sha256":"abc","mandatory":false}""");

        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsNull(result.Update);
    }

    [TestMethod]
    public async Task UpdateService_Unreachable_ReportsFailure()
    {
        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl("http://127.0.0.1:9")));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsNull(result.Update);
    }

    [TestMethod]
    public async Task UpdateService_DoesNotFollowRedirects()
    {
        using var server = new UpdateStubServer(
            "",
            status: "307 Temporary Redirect",
            extraHeaders: "Location: /client/windows/latest.json\r\n");

        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsNull(result.Update);
        Assert.AreEqual(1, server.RequestCount);
    }

    [DataTestMethod]
    [DataRow("503 Service Unavailable", "")]
    [DataRow("200 OK", "")]
    [DataRow("200 OK", "not-json")]
    [DataRow("200 OK", "null")]
    [DataRow("200 OK", "{}")]
    public async Task UpdateService_FailedOrInvalidResponse_DoesNotConfirmCurrentVersion(string status, string body)
    {
        using var server = new UpdateStubServer(body, status);
        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsNull(result.Update);
    }

    [TestMethod]
    public async Task UpdateService_UserCancellation_IsPropagated()
    {
        using var server = new UpdateStubServer("{}");
        var service = new UpdateService(new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => service.CheckAsync(cancel.Token));
    }

    private sealed class UpdateStubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _responseBytes;
        private readonly Task _loop;
        private readonly CancellationTokenSource _cts = new();
        private int _requestCount;

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        public UpdateStubServer(
            string body,
            string status = "200 OK",
            string extraHeaders = "")
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";

            string statusLine = $"HTTP/1.1 {status}\r\n";
            string headers =
                extraHeaders +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n";
            _responseBytes = Encoding.UTF8.GetBytes(statusLine + headers + body);
            _loop = Task.Run(LoopAsync);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 忽略关闭异常。
            }

            _cts.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            Interlocked.Increment(ref _requestCount);
                            NetworkStream stream = client.GetStream();
                            byte[] buffer = new byte[4096];
                            _ = await stream.ReadAsync(buffer, 0, buffer.Length);
                            await stream.WriteAsync(_responseBytes, 0, _responseBytes.Length);
                            await stream.FlushAsync();
                        }
                        catch
                        {
                            // 忽略单个请求失败。
                        }
                    }
                });
            }
        }
    }
}
