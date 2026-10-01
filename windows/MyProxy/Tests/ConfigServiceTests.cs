using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class ConfigServiceTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ServerProfile CreateProfile(string uuid = "123e4567-e89b-12d3-a456-426614174000")
    {
        return new ServerProfile
        {
            Server = "203.0.113.10",
            Port = 443,
            Uuid = uuid,
            Security = "reality",
            PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ShortId = "0123456789abcdef",
            Sni = "www.microsoft.com",
            Fingerprint = "chrome",
            Flow = "xtls-rprx-vision",
            SpiderX = "/"
        };
    }

    private static async Task SaveDeviceAsync(StorageService storage, string token = "tok_test")
    {
        await storage.SaveDeviceAsync(new DeviceConfig
        {
            DeviceId = "dev_test",
            DeviceToken = token,
            DeviceName = "TEST-PC",
            Platform = "windows",
            ClientVersion = "0.1.0",
            BoundAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    private static async Task SaveCacheAsync(StorageService storage, long version, bool verified, ServerProfile? profile = null)
    {
        await storage.SaveConfigCacheAsync(new ConfigCacheEntry
        {
            ConfigVersion = version,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = verified,
            Profile = profile ?? CreateProfile("00000000-0000-0000-0000-000000000000")
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task UnconfiguredServer_DoesNotFallBackToAnExistingVerifiedProfile()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, 7, verified: true);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            using var endpoint = new ApiEndpoint(BindingTarget.FromBaseUrl("https://api.example.invalid"));
            var service = new ConfigService(storage, log, endpoint);
            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(() => service.GetConfigAsync(CancellationToken.None));
            Assert.AreEqual(ErrorCode.ServerNotConfigured, ex.ErrorCode);
        }
        finally { Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task OnlineNewVersion_ReturnsUnverifiedCandidate()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.OK,
            """{"configVersion":8,"config":{"server":"203.0.113.10","port":443,"uuid":"123e4567-e89b-12d3-a456-426614174000","security":"reality","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"0123456789abcdef","sni":"www.microsoft.com","fingerprint":"chrome","flow":"xtls-rprx-vision","spiderX":"/"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 7, verified: true);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
            ConfigCacheEntry candidate = await service.GetConfigAsync(CancellationToken.None);

            Assert.AreEqual(8, candidate.ConfigVersion);
            Assert.IsFalse(candidate.Verified);
            Assert.AreEqual("123e4567-e89b-12d3-a456-426614174000", candidate.Profile.Uuid);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlineSameVersion_WithVerifiedCache_ReturnsCache()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.OK,
            """{"configVersion":7,"config":{"server":"203.0.113.10","port":443,"uuid":"123e4567-e89b-12d3-a456-426614174000","security":"reality","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"0123456789abcdef","sni":"www.microsoft.com","fingerprint":"chrome","flow":"xtls-rprx-vision","spiderX":"/"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 7, verified: true);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
            ConfigCacheEntry candidate = await service.GetConfigAsync(CancellationToken.None);

            Assert.AreEqual(7, candidate.ConfigVersion);
            Assert.IsTrue(candidate.Verified);
            Assert.AreEqual("00000000-0000-0000-0000-000000000000", candidate.Profile.Uuid);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlineSameVersion_WithUnverifiedCache_ReturnsOnlineCandidate()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.OK,
            """{"configVersion":7,"config":{"server":"203.0.113.10","port":443,"uuid":"123e4567-e89b-12d3-a456-426614174000","security":"reality","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"0123456789abcdef","sni":"www.microsoft.com","fingerprint":"chrome","flow":"xtls-rprx-vision","spiderX":"/"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 7, verified: false);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
            ConfigCacheEntry candidate = await service.GetConfigAsync(CancellationToken.None);

            Assert.IsFalse(candidate.Verified);
            Assert.AreEqual("123e4567-e89b-12d3-a456-426614174000", candidate.Profile.Uuid);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlineFails_WithVerifiedCache_ReturnsCache()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 7, verified: true);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl("http://127.0.0.1:9")));
            ConfigCacheEntry candidate = await service.GetConfigAsync(CancellationToken.None);

            Assert.IsTrue(candidate.Verified);
            Assert.AreEqual(7, candidate.ConfigVersion);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlineFails_NoCache_ThrowsNoValidConfig()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl("http://127.0.0.1:9")));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => service.GetConfigAsync(CancellationToken.None));

            Assert.AreEqual(ErrorCode.NoValidConfig, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Online401_ThrowsTokenInvalid()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.Unauthorized,
            """{"error":{"code":"TokenInvalid","message":"设备已失效，请重新绑定"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 7, verified: true);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => service.GetConfigAsync(CancellationToken.None));

            Assert.AreEqual(ErrorCode.TokenInvalid, ex.ErrorCode);
        }
        finally
        {

            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ServerVersionLower_WithVerifiedCache_ReturnsCache()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.OK,
            """{"configVersion":6,"config":{"server":"203.0.113.10","port":443,"uuid":"123e4567-e89b-12d3-a456-426614174000","security":"reality","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"0123456789abcdef","sni":"www.microsoft.com","fingerprint":"chrome","flow":"xtls-rprx-vision","spiderX":"/"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            await SaveCacheAsync(storage, version: 8, verified: true);

            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));
            ConfigCacheEntry candidate = await service.GetConfigAsync(CancellationToken.None);

            Assert.AreEqual(8, candidate.ConfigVersion);
            Assert.IsTrue(candidate.Verified);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task PromoteCurrentConfig_SavesVerifiedEntry()
    {
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl("http://127.0.0.1:8090")));
            ServerProfile profile = CreateProfile();

            await service.PromoteCurrentConfigAsync(profile, 9, CancellationToken.None);

            ConfigCacheEntry? cache = storage.LoadConfigCache();
            Assert.IsNotNull(cache);
            Assert.AreEqual(9, cache!.ConfigVersion);
            Assert.IsTrue(cache.Verified);
            Assert.AreEqual(profile.Uuid, cache.Profile.Uuid);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat_UsesDeviceEndpointAndBearerAndReturnsVersion()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.OK,
            """{"ok":true,"configVersion":8,"serverTime":"2026-08-22T00:00:00Z"}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage, "tok_heartbeat");
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));

            HeartbeatResult result = await service.HeartbeatAsync(CancellationToken.None);
            string request = await server.RequestText.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(8, result.ConfigVersion);
            StringAssert.Contains(request, "POST /api/device/heartbeat HTTP/1.1");
            StringAssert.Contains(request, "Authorization: Bearer tok_heartbeat");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat401_ThrowsTokenInvalid()
    {
        string dataRoot = NewDataRoot();
        using var server = new TinyConfigServer(
            HttpStatusCode.Unauthorized,
            """{"error":{"code":"TokenInvalid","message":"设备已失效，请重新绑定"}}""");
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            await SaveDeviceAsync(storage);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            var service = new ConfigService(storage, log, new ApiEndpoint( BindingTarget.FromBaseUrl(server.BaseUrl)));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => service.HeartbeatAsync(CancellationToken.None));

            Assert.AreEqual(ErrorCode.TokenInvalid, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class TinyConfigServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _responseBytes;
        private readonly Task _loop;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<string> _requestText =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TinyConfigServer(TcpListener listener, byte[] responseBytes)
        {
            _listener = listener;
            _responseBytes = responseBytes;
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; private set; } = "";

        public Task<string> RequestText => _requestText.Task;

        public TinyConfigServer(HttpStatusCode statusCode, string body)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";

            string statusLine = $"HTTP/1.1 {(int)statusCode} {statusCode}\r\n";
            string headers =
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
                            NetworkStream stream = client.GetStream();
                            byte[] buffer = new byte[4096];
                            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            _requestText.TrySetResult(Encoding.UTF8.GetString(buffer, 0, read));
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
