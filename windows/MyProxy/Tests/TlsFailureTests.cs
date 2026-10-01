using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>Real loopback TLS handshakes verify platform chain, expiry and hostname checks and preserve transport error mapping.</summary>
[TestClass]
public sealed class TlsFailureTests
{
    public enum Peer
    {
        Tls,
        ResetMidHandshake,
        CloseMidHandshake,
        NotTls,
        Nobody
    }

    [TestMethod]
    public async Task TrustedChainAndMatchingHostname_CompletesTheRequest()
    {
        // The test CA is trusted only by this test client, never installed in the system store.
        using X509Certificate2 root = CreateRoot();
        using X509Certificate2 certificate = CreateCertificate(root);
        await using var peer = LoopbackPeer.Start(Peer.Tls, certificate);
        using HttpClient http = CreateClient(root);

        using HttpResponseMessage response = await http.GetAsync(peer.BaseUrl + "/");

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task UntrustedChain_IsRecognisedAsARejectedCertificate()
    {
        using X509Certificate2 certificate = CreateCertificate();
        await using var peer = LoopbackPeer.Start(Peer.Tls, certificate);
        using HttpClient http = CreateClient();

        HttpRequestException ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => http.GetAsync(peer.BaseUrl + "/"));

        Assert.IsTrue(TlsFailure.IsServerCertificateRejected(ex), Describe(ex));
    }

    [DataTestMethod]
    [DataRow(Peer.ResetMidHandshake)]
    [DataRow(Peer.CloseMidHandshake)]
    [DataRow(Peer.NotTls)]
    [DataRow(Peer.Nobody)]
    public async Task NetworkFailures_AreNotMistakenForARejectedCertificate(Peer kind)
    {
        using X509Certificate2 certificate = CreateCertificate();
        await using var peer = LoopbackPeer.Start(kind, certificate);
        using HttpClient http = CreateClient();

        HttpRequestException ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => http.GetAsync(peer.BaseUrl + "/"));

        Assert.IsFalse(TlsFailure.IsServerCertificateRejected(ex), Describe(ex));
    }

    [DataTestMethod]
    [DataRow(Peer.Tls, ErrorCode.ServerUntrusted)]
    [DataRow(Peer.ResetMidHandshake, ErrorCode.ApiUnreachable)]
    public async Task Binding_ReportsIdentityAndReachabilitySeparately(Peer kind, ErrorCode expected)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using X509Certificate2 certificate = CreateCertificate();
            await using var peer = LoopbackPeer.Start(kind, certificate);
            var storage = new StorageService(dataRootOverride: dataRoot);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            using var endpoint = new StandardTlsEndpoint(peer.BaseUrl);
            var binding = new BindingService(storage, log, endpoint);

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => binding.BindAsync("A7K9-M2QF", CancellationToken.None));

            Assert.AreEqual(expected, ex.ErrorCode);
            if (expected == ErrorCode.ServerUntrusted)
            {
                Assert.IsFalse(ex.FriendlyMessage.Contains("地址"), ex.FriendlyMessage);
            }
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConfigSync_WithoutAVerifiedCache_ReportsCertificateRejection()
    {
        // 以前兜底 catch 把它换成 NoValidConfig「暂无可用配置，请重新绑定」，把用户引去要新配对码——
        // 而绑定走同一条 TLS，同样会失败。
        string dataRoot = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using X509Certificate2 certificate = CreateCertificate();
            await using var peer = LoopbackPeer.Start(Peer.Tls, certificate);
            var storage = new StorageService(dataRootOverride: dataRoot);
            await storage.SaveDeviceAsync(new MyProxy.Models.DeviceConfig
            {
                DeviceId = "dev_test",
                DeviceToken = "tok_test",
                DeviceName = "TEST-PC",
                Platform = "windows",
                ClientVersion = "0.1.0",
                BoundAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            using var log = new LogService(Path.Combine(dataRoot, "logs"));
            using var endpoint = new StandardTlsEndpoint(peer.BaseUrl);
            var config = new ConfigService(storage, log, endpoint);

            MyProxyException ex = await Assert.ThrowsExceptionAsync<MyProxyException>(
                () => config.GetConfigAsync(CancellationToken.None));

            Assert.AreEqual(ErrorCode.ServerUntrusted, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static HttpClient CreateClient(X509Certificate2? root = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback =
                    HttpClientFactory.StandardCertificateCallback()
            }
        };
        if (root is not null)
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(root);
            handler.SslOptions.CertificateChainPolicy = policy;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // SChannel 做服务端时不认临时密钥，走一次 PFX 往返。
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 CreateRoot()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=MyProxy Test Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(2));
        return new X509Certificate2(root.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 CreateCertificate(X509Certificate2 root, bool expired = false, bool wrongHostname = false)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(wrongHostname ? "wrong.example.invalid" : "localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        using X509Certificate2 leaf = request.Create(root, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 signed = leaf.CopyWithPrivateKey(key);
        return new X509Certificate2(signed.Export(X509ContentType.Pfx));
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task TrustedButExpiredOrWrongHostname_IsRejected(bool expired, bool wrongHostname)
    {
        using X509Certificate2 root = CreateRoot();
        using X509Certificate2 certificate = CreateCertificate(root, expired, wrongHostname);
        await using var peer = LoopbackPeer.Start(Peer.Tls, certificate);
        using HttpClient http = CreateClient(root);
        HttpRequestException ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => http.GetAsync(peer.BaseUrl + "/"));
        Assert.IsTrue(TlsFailure.IsServerCertificateRejected(ex), Describe(ex));
    }

    private static string Describe(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            text.Append(" -> ").Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        return text.ToString();
    }

    private sealed class StandardTlsEndpoint : IApiEndpoint, IDisposable
    {
        private readonly HttpClient _client;

        public StandardTlsEndpoint(string baseUrl)
        {
            BaseUrl = baseUrl;
            _client = CreateClient();
        }

        public string BaseUrl { get; }

        public HttpClient Client(string key, TimeSpan timeout) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class LoopbackPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        private LoopbackPeer(TcpListener listener, Task serving, int port)
        {
            _listener = listener;
            _serving = serving;
            BaseUrl = $"https://localhost:{port}";
        }

        public string BaseUrl { get; }

        public static LoopbackPeer Start(Peer kind, X509Certificate2 certificate)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (kind == Peer.Nobody)
            {
                listener.Stop();
                return new LoopbackPeer(listener, Task.CompletedTask, port);
            }

            Task serving = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    NetworkStream stream = client.GetStream();
                    if (kind == Peer.Tls)
                    {
                        using var tls = new SslStream(stream);
                        await tls.AuthenticateAsServerAsync(certificate);
                        var request = new byte[4096];
                        _ = await tls.ReadAsync(request);
                        await tls.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        return;
                    }

                    // 读到 ClientHello 的开头再动手，确保是「握手中途」。
                    var hello = new byte[16];
                    _ = await stream.ReadAsync(hello);
                    if (kind == Peer.ResetMidHandshake)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                    }
                    else if (kind == Peer.NotTls)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"));
                        await Task.Delay(200);
                    }
                }
                catch (Exception)
                {
                    // 客户端否决证书时服务端这边握手失败，属预期。
                }
            });

            return new LoopbackPeer(listener, serving, port);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
