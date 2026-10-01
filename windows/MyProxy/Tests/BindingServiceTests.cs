using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BindingServiceTests
{
    private static string NewDataRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public async Task ErrorMapping_ServerCodes_MapToInternalErrorCodes()
    {
        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"PairingInvalid\",\"message\":\"bad\"}}",
            HttpStatusCode.BadRequest,
            ErrorCode.PairingInvalid,
            "配对码无效，请检查后重试");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"PairingExpired\",\"message\":\"expired\"}}",
            HttpStatusCode.BadRequest,
            ErrorCode.PairingExpired,
            "配对码已过期，请获取新的配对码");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"TokenInvalid\",\"message\":\"bad token\"}}",
            HttpStatusCode.Unauthorized,
            ErrorCode.TokenInvalid,
            "设备已失效，请重新绑定");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"DeviceNotFound\",\"message\":\"gone\"}}",
            HttpStatusCode.Unauthorized,
            ErrorCode.TokenInvalid,
            "设备已失效，请重新绑定");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"ServerError\",\"message\":\"boom\"}}",
            HttpStatusCode.InternalServerError,
            ErrorCode.ApiUnreachable,
            "暂时无法连接服务器，请检查网络");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"RateLimited\",\"message\":\"slow down\"}}",
            HttpStatusCode.TooManyRequests,
            ErrorCode.ApiUnreachable,
            "暂时无法连接服务器，请检查网络");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"bad request\"}}",
            HttpStatusCode.BadRequest,
            ErrorCode.PairingInvalid,
            "配对码无效，请检查后重试");

        await AssertErrorMappingAsync(
            "{\"error\":{\"code\":\"SomethingElse\",\"message\":\"??\"}}",
            HttpStatusCode.BadRequest,
            ErrorCode.Unknown,
            "发生未知错误，请查看日志");

        await AssertErrorMappingAsync(
            "not-json",
            HttpStatusCode.InternalServerError,
            ErrorCode.ApiUnreachable,
            "暂时无法连接服务器，请检查网络");
    }

    [TestMethod]
    public async Task NetworkError_MapsToApiUnreachable()
    {
        string dataRoot = NewDataRoot();
        LogService log = null!;
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);

            var target = BindingTarget.FromBaseUrl("http://127.0.0.1:9");
            var binding = new BindingService(storage, log, new ApiEndpoint( target));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => binding.BindAsync("MOCK-0001", CancellationToken.None));

            Assert.AreEqual(ErrorCode.ApiUnreachable, ex.ErrorCode);
            Assert.AreEqual("暂时无法连接服务器，请检查网络", ex.FriendlyMessage);
        }
        finally
        {
            log.Dispose();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Claim_SendsStablePersistedClientInstanceId()
    {
        const string responseBody =
            """{"deviceId":"dev_test","deviceToken":"tok_test","configVersion":7,"config":{"server":"203.0.113.10","port":443,"uuid":"123e4567-e89b-12d3-a456-426614174000","security":"reality","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"0123456789abcdef","sni":"www.microsoft.com","fingerprint":"chrome","flow":"xtls-rprx-vision","spiderX":"/"}}""";
        string dataRoot = NewDataRoot();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            using var server = await TinyHttpServer.StartAsync(200, "OK", responseBody);
            var target = BindingTarget.FromBaseUrl(server.BaseUrl);
            var binding = new BindingService(storage, log, new ApiEndpoint( target));
            await binding.BindAsync("A7K9-M2QF", CancellationToken.None);

            string requestBody = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            using JsonDocument request = JsonDocument.Parse(requestBody);
            string? sentId = request.RootElement.GetProperty("clientInstanceId").GetString();
            string persistedId = storage.LoadSettings().ClientInstanceId;

            Assert.IsTrue(Guid.TryParseExact(sentId, "D", out _));
            Assert.AreEqual(persistedId, sentId);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task MockIntegration_MOCK0001_SavesDeviceAndUnverifiedCache()
    {
        string dataRoot = NewDataRoot();
        using var mock = await MockApi.StartAsync();
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);

            var target = BindingTarget.FromBaseUrl("http://127.0.0.1:8090");
            var binding = new BindingService(storage, log, new ApiEndpoint( target));
            BindResult result = await binding.BindAsync("mock-0001", CancellationToken.None);

            Assert.AreEqual("dev_mock_0001", result.Device.DeviceId);
            Assert.AreEqual("tok_mock_0001", result.Device.DeviceToken);
            Assert.AreEqual(7, result.ConfigVersion);

            DeviceConfig? savedDevice = storage.LoadDevice();
            Assert.IsNotNull(savedDevice);
            Assert.AreEqual(result.Device.DeviceToken, savedDevice!.DeviceToken);

            ConfigCacheEntry? savedCache = storage.LoadConfigCache();
            Assert.IsNotNull(savedCache);
            Assert.AreEqual(7, savedCache!.ConfigVersion);
            Assert.IsFalse(savedCache.Verified);
            Assert.AreEqual("00000000-0000-0000-0000-000000000000", savedCache.Profile.Uuid);

            log.Dispose();
            string logContent = File.ReadAllText(Path.Combine(dataRoot, "logs", "app.log"));
            Assert.IsFalse(logContent.Contains("mock-0001", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(logContent.Contains("tok_mock_0001"));
        }
        finally
        {
            mock.StopIfStarted();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task MockIntegration_MOCK0002_ReturnsPairingExpired()
    {
        string dataRoot = NewDataRoot();
        using var mock = await MockApi.StartAsync();
        LogService log = null!;
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var target = BindingTarget.FromBaseUrl("http://127.0.0.1:8090");
            var binding = new BindingService(storage, log, new ApiEndpoint( target));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => binding.BindAsync("MOCK-0002", CancellationToken.None));

            Assert.AreEqual(ErrorCode.PairingExpired, ex.ErrorCode);
        }
        finally
        {
            log.Dispose();
            mock.StopIfStarted();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task MockIntegration_OtherCode_ReturnsPairingInvalid()
    {
        string dataRoot = NewDataRoot();
        using var mock = await MockApi.StartAsync();
        LogService log = null!;
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            log = new LogService(Path.Combine(dataRoot, "logs"));
            storage.AttachLogger(log);
            var target = BindingTarget.FromBaseUrl("http://127.0.0.1:8090");
            var binding = new BindingService(storage, log, new ApiEndpoint( target));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => binding.BindAsync("MOCK-9999", CancellationToken.None));

            Assert.AreEqual(ErrorCode.PairingInvalid, ex.ErrorCode);
        }
        finally
        {
            log.Dispose();
            mock.StopIfStarted();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static async Task AssertErrorMappingAsync(
        string responseBody,
        HttpStatusCode statusCode,
        ErrorCode expectedErrorCode,
        string expectedFriendlyMessage)
    {
        string dataRoot = NewDataRoot();
        var storage = new StorageService(dataRootOverride: dataRoot);
        LogService log = new(Path.Combine(dataRoot, "logs"));
        storage.AttachLogger(log);

        try
        {
            using var server = await TinyHttpServer.StartAsync(
                (int)statusCode,
                statusCode.ToString(),
                responseBody);
            var target = BindingTarget.FromBaseUrl(server.BaseUrl);
            var binding = new BindingService(storage, log, new ApiEndpoint( target));

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => binding.BindAsync("A7K9-M2QF", CancellationToken.None));

            Assert.AreEqual(expectedErrorCode, ex.ErrorCode);
            Assert.AreEqual(expectedFriendlyMessage, ex.FriendlyMessage);
        }
        finally
        {
            log.Dispose();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly byte[] _responseBytes;
        private readonly TaskCompletionSource<string> _requestBody =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TinyHttpServer(TcpListener listener, byte[] responseBytes)
        {
            _listener = listener;
            _responseBytes = responseBytes;
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; private set; } = "";

        public Task<string> RequestBody => _requestBody.Task;

        public static async Task<TinyHttpServer> StartAsync(int statusCode, string reasonPhrase, string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            string statusLine = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n";
            string headers =
                "Content-Type: application/json\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n";
            byte[] responseBytes = Encoding.UTF8.GetBytes(statusLine + headers + body);

            var server = new TinyHttpServer(listener, responseBytes)
            {
                BaseUrl = $"http://127.0.0.1:{port}"
            };

            // 等待监听真正就绪。
            await Task.CompletedTask;
            return server;
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
                // 测试结束时忽略关闭异常。
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
                            using var reader = new StreamReader(
                                stream,
                                Encoding.UTF8,
                                detectEncodingFromByteOrderMarks: false,
                                bufferSize: 1024,
                                leaveOpen: true);
                            int contentLength = 0;
                            while (await reader.ReadLineAsync() is string line && line.Length > 0)
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                {
                                    _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                                }
                            }

                            char[] bodyBuffer = new char[contentLength];
                            int read = 0;
                            while (read < bodyBuffer.Length)
                            {
                                int count = await reader.ReadAsync(bodyBuffer, read, bodyBuffer.Length - read);
                                if (count == 0)
                                {
                                    break;
                                }

                                read += count;
                            }

                            _requestBody.TrySetResult(new string(bodyBuffer, 0, read));
                            await stream.WriteAsync(_responseBytes, 0, _responseBytes.Length);
                            await stream.FlushAsync();
                        }
                        catch
                        {
                            // 忽略单个请求的读写失败。
                        }
                    }
                });
            }
        }
    }

    private sealed class MockApi : IDisposable
    {
        private Process? _process;
        private readonly bool _startedByUs;

        private MockApi(Process? process, bool startedByUs)
        {
            _process = process;
            _startedByUs = startedByUs;
        }

        public static async Task<MockApi> StartAsync()
        {
            if (await IsPortOpenAsync(8090))
            {
                return new MockApi(null, startedByUs: false);
            }

            string scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "mock_api.py");
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["MOCK_CONFIG_VERSION"] = "7";
            startInfo.Environment["MOCK_LATEST_VERSION"] = "0.2.0";

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 mock_api.py。");

            try
            {
                for (int i = 0; i < 100; i++)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException("mock_api.py 启动后立即退出。");
                    }

                    if (await IsPortOpenAsync(8090))
                    {
                        return new MockApi(process, startedByUs: true);
                    }

                    await Task.Delay(100);
                }

                throw new TimeoutException("等待 mock_api.py 监听 127.0.0.1:8090 超时。");
            }
            catch
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略清理失败。
                }

                process.Dispose();
                throw;
            }
        }

        public void StopIfStarted()
        {
            if (!_startedByUs)
            {
                return;
            }

            try
            {
                _process?.Kill(entireProcessTree: true);
                _process?.WaitForExit(3000);
            }
            catch
            {
                // 忽略清理失败。
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        public void Dispose()
        {
            StopIfStarted();
        }

        private static async Task<bool> IsPortOpenAsync(int port)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
