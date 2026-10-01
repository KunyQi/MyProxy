using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class XrayTrafficStatsServiceTests
{
    private const string Up = "outbound>>>proxy>>>traffic>>>uplink";
    private const string Down = "outbound>>>proxy>>>traffic>>>downlink";
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OmittedZeroAndFullInt64Values_AreReadWithoutPrecisionLoss()
    {
        Assert.AreEqual(new TrafficCounters(0, 0), XrayTrafficStatsService.ParseProxied(Stats(Stat(Up), Stat(Down))));
        Assert.AreEqual(new TrafficCounters(127, long.MaxValue),
            XrayTrafficStatsService.ParseProxied(Stats(Stat(Up, 127), Stat(Down, long.MaxValue))));
    }

    [TestMethod]
    public void MissingAndDuplicateCounters_AreUnavailable()
    {
        Assert.IsNull(XrayTrafficStatsService.ParseProxied(Array.Empty<byte>()));
        Assert.IsNull(XrayTrafficStatsService.ParseProxied(Stats(Stat(Up))));
        Assert.IsNull(XrayTrafficStatsService.ParseProxied(Stats(Stat(Up), Stat(Down), Stat(Up))));
        Assert.AreEqual(new TrafficCounters(1, 2), XrayTrafficStatsService.ParseProxied(
            Stats(Stat("outbound>>>direct>>>traffic>>>uplink", 999), Stat(Down, 2), Stat(Up, 1))));
    }

    [TestMethod]
    public void CategoryOutbounds_CountTowardsTheRate_DirectAndBlockedDoNot()
    {
        // 打开类别归因后，视频、社交、通讯流量走 cat-* 出站；只读 proxy 的话看视频时速率接近 0。
        byte[] payload = Stats(
            Stat(Up, 10), Stat(Down, 20),
            Stat("outbound>>>cat-video>>>traffic>>>uplink", 5), Stat("outbound>>>cat-video>>>traffic>>>downlink", 700),
            Stat("outbound>>>cat-messaging>>>traffic>>>uplink", 1), Stat("outbound>>>cat-messaging>>>traffic>>>downlink", 2),
            Stat("outbound>>>direct>>>traffic>>>uplink", 9000), Stat("outbound>>>direct>>>traffic>>>downlink", 9000),
            Stat("outbound>>>blocked>>>traffic>>>uplink", 9000), Stat("outbound>>>blocked>>>traffic>>>downlink", 9000));

        Assert.AreEqual(new TrafficCounters(16, 722), XrayTrafficStatsService.ParseProxied(payload));
    }

    [TestMethod]
    public void UnknownFields_AreSkippedAtBothMessageLevels()
    {
        // Unknown varint, fixed64, length-delimited and fixed32 fields, before known fields.
        byte[] unknown = { 24, 150, 1, 33, 0, 0, 0, 0, 0, 0, 0, 0, 42, 2, 0, 0, 53, 0, 0, 0, 0 };
        byte[] payload = unknown.Concat(Stats(unknown.Concat(Stat(Up, 4)).ToArray(), Stat(Down, 5))).ToArray();
        Assert.AreEqual(new TrafficCounters(4, 5), XrayTrafficStatsService.ParseProxied(payload));
    }

    [TestMethod]
    public void MalformedMessages_AreRejectedWithoutThrowing()
    {
        byte[] valid = Stats(Stat(Up, 1000), Stat(Down, 2000));
        for (int length = 0; length < valid.Length; length++)
            Assert.IsNull(XrayTrafficStatsService.ParseProxied(valid.AsSpan(0, length)), $"truncation at {length}");

        byte[][] corrupt =
        {
            new byte[] { 0 }, new byte[] { 8, 0 }, new byte[] { 10, 255, 255, 255, 255, 15 },
            new byte[] { 10, 1, 11 }, new byte[] { 10, 2, 16, 128 },
            Stats(Stat(Up, ulong.MaxValue), Stat(Down)), // signed negative counter
            Stats(Stat(Up).Concat(new byte[] { 16, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }).ToArray(), Stat(Down)),
            Stats(Stat(Up).Concat(Stat(Up)).ToArray(), Stat(Down)),
            new byte[16 * 1024 + 1]
        };
        foreach (byte[] payload in corrupt) Assert.IsNull(XrayTrafficStatsService.ParseProxied(payload));
    }

    [TestMethod]
    public async Task Query_UsesReadonlyLoopbackHttp2AndChecksTrailers()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.AreEqual("http://127.0.0.1:12345/xray.app.stats.command.StatsService/QueryStats", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(HttpVersion.Version20, request.Version);
            Assert.AreEqual(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            byte[] requestBody = await request.Content!.ReadAsByteArrayAsync(ct);
            byte[] pattern = Encoding.ASCII.GetBytes("outbound>>>");
            byte[] expected = Frame(new byte[] { 10, (byte)pattern.Length }.Concat(pattern).ToArray());
            CollectionAssert.AreEqual(expected, requestBody, "reset must remain false / omitted");
            return Response(Frame(Stats(Stat(Up, 1234), Stat(Down, 98765432101))));
        }));
        Assert.AreEqual(new TrafficCounters(1234, 98765432101),
            await new XrayTrafficStatsService(client, new TestLog()).QueryAsync(12345, CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("status")]
    [DataRow("missing-status")]
    [DataRow("duplicate-status")]
    [DataRow("http")]
    [DataRow("version")]
    [DataRow("type")]
    [DataRow("compressed")]
    [DataRow("oversize")]
    [DataRow("truncated")]
    [DataRow("extra-frame")]
    public async Task InvalidResponse_IsUnavailable(string fault)
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            byte[] body = Frame(Stats(Stat(Up), Stat(Down)));
            if (fault == "compressed") body[0] = 1;
            if (fault == "oversize") BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), int.MaxValue);
            if (fault == "truncated") body = body[..^1];
            if (fault == "extra-frame") body = body.Concat(Frame(Array.Empty<byte>())).ToArray();
            HttpResponseMessage response = Response(body);
            if (fault == "http") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            if (fault == "version") response.Version = HttpVersion.Version11;
            if (fault == "type") response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (fault is "status" or "missing-status") response.TrailingHeaders.Remove("grpc-status");
            if (fault == "status") response.TrailingHeaders.Add("grpc-status", "13");
            if (fault == "duplicate-status") response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }));
        Assert.IsNull(await new XrayTrafficStatsService(client, new TestLog()).QueryAsync(12345, CancellationToken.None));
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesButTransportFailureDoesNot()
    {
        using var entered = new SemaphoreSlim(0);
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            entered.Release();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        var service = new XrayTrafficStatsService(client, new TestLog());
        using var cancellation = new CancellationTokenSource();
        Task<TrafficCounters?> pending = service.QueryAsync(12345, cancellation.Token);
        Assert.IsTrue(await entered.WaitAsync(TimeSpan.FromSeconds(1)));
        cancellation.Cancel();
        try { await pending; Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException) { }

        using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        Assert.IsNull(await new XrayTrafficStatsService(failed, new TestLog()).QueryAsync(12345, CancellationToken.None));
    }

    [TestMethod]
    public async Task StalledQuery_HasABoundedDeadline()
    {
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        Assert.IsNull(await new XrayTrafficStatsService(client, new TestLog())
            .QueryAsync(12345, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task BundledCore_LoopbackTrafficMatchesCliAndCountersAreNotReset()
    {
        string coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
        string corePath = Path.Combine(coreDir, "xray.exe");
        Assert.IsTrue(File.Exists(corePath), "Bundled core is required for transport integration");
        string directory = Path.Combine(Path.GetTempPath(), "MyProxy.StatsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        int apiPort = FreePort();
        int proxyPort;
        do { proxyPort = FreePort(); } while (proxyPort == apiPort);
        string configPath = Path.Combine(directory, "config.json");
        File.WriteAllText(configPath, $$$"""
        {"log":{"loglevel":"none"},"stats":{},
         "api":{"tag":"api","services":["StatsService"]},
         "policy":{"system":{"statsOutboundUplink":true,"statsOutboundDownlink":true}},
         "inbounds":[
          {"tag":"api","listen":"127.0.0.1","port":{{{apiPort}}},"protocol":"dokodemo-door","settings":{"address":"127.0.0.1"}},
          {"tag":"http","listen":"127.0.0.1","port":{{{proxyPort}}},"protocol":"http","settings":{}}],
         "outbounds":[{"tag":"proxy","protocol":"freedom","settings":{}}],
         "routing":{"rules":[{"type":"field","inboundTag":["api"],"outboundTag":"api"}]}}
        """, new UTF8Encoding(false));
        using var core = new Process { StartInfo = StartInfo(corePath, "run", "-c", configPath) };
        bool coreStarted = false;
        try
        {
            coreStarted = core.Start();
            Assert.IsTrue(coreStarted);
            var service = new XrayTrafficStatsService(new TestLog());
            TrafficCounters? initial;
            do
            {
                Assert.IsFalse(core.HasExited, "Local test core exited early");
                initial = await service.QueryAsync(apiPort, deadline.Token);
                if (initial is null) await Task.Delay(50, deadline.Token);
            } while (initial is null);
            Assert.AreEqual(new TrafficCounters(0, 0), initial);

            Task serve = ServeOnceAsync(upstream, deadline.Token);
            using (var proxyClient = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true
            }))
            {
                byte[] received = await proxyClient.GetByteArrayAsync($"http://127.0.0.1:{upstreamPort}/", deadline.Token);
                Assert.AreEqual(4096, received.Length);
            }
            await serve;
            TrafficCounters? actual = await service.QueryAsync(apiPort, deadline.Token);
            Assert.IsNotNull(actual);
            Assert.IsTrue(actual.Value.UplinkBytes > 0);
            Assert.IsTrue(actual.Value.DownlinkBytes >= 4096);
            Assert.AreEqual(actual, await service.QueryAsync(apiPort, deadline.Token), "read must not reset counters");

            using var cli = new Process { StartInfo = StartInfo(corePath, "api", "statsquery", $"--server=127.0.0.1:{apiPort}") };
            cli.StartInfo.RedirectStandardOutput = true;
            cli.StartInfo.RedirectStandardError = true;
            Assert.IsTrue(cli.Start());
            Task<string> json = cli.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors = cli.StandardError.ReadToEndAsync(deadline.Token);
            await cli.WaitForExitAsync(deadline.Token);
            await errors;
            Assert.AreEqual(0, cli.ExitCode);
            using JsonDocument parsed = JsonDocument.Parse(await json);
            foreach (JsonElement stat in parsed.RootElement.GetProperty("stat").EnumerateArray())
            {
                string? name = stat.GetProperty("name").GetString();
                if (name is not (Up or Down)) continue;
                JsonElement value = stat.GetProperty("value");
                long counterValue = value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!) : value.GetInt64();
                Assert.AreEqual(name == Up ? actual.Value.UplinkBytes : actual.Value.DownlinkBytes, counterValue);
            }

            const int count = 50;
            using var currentProcess = Process.GetCurrentProcess();
            TimeSpan cpuBefore = currentProcess.TotalProcessorTime;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            for (int sample = 0; sample < count; sample++)
                Assert.AreEqual(actual, await service.QueryAsync(apiPort, deadline.Token));
            watch.Stop();
            TestContext.WriteLine($"Reused HTTP/2: {count} queries; wall={watch.Elapsed.TotalMilliseconds:F2} ms; client CPU={(currentProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2} ms; managed allocated={GC.GetTotalAllocatedBytes(true) - allocatedBefore} B; child processes=0");
            if (Environment.GetEnvironmentVariable("MYPROXY_STATS_BENCHMARK") == "1")
            {
                cpuBefore = currentProcess.TotalProcessorTime;
                allocatedBefore = GC.GetTotalAllocatedBytes(true);
                TimeSpan childCpu = TimeSpan.Zero;
                watch.Restart();
                for (int sample = 0; sample < count; sample++)
                {
                    using var query = new Process { StartInfo = StartInfo(corePath, "api", "statsquery", $"--server=127.0.0.1:{apiPort}") };
                    query.StartInfo.RedirectStandardOutput = true;
                    query.StartInfo.RedirectStandardError = true;
                    Assert.IsTrue(query.Start());
                    Task<string> output = query.StandardOutput.ReadToEndAsync(deadline.Token);
                    Task<string> error = query.StandardError.ReadToEndAsync(deadline.Token);
                    await query.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual(0, query.ExitCode);
                    await Task.WhenAll(output, error);
                    childCpu += query.TotalProcessorTime;
                }
                watch.Stop();
                TestContext.WriteLine($"CLI: {count} queries; wall={watch.Elapsed.TotalMilliseconds:F2} ms; client CPU={(currentProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2} ms; child CPU={childCpu.TotalMilliseconds:F2} ms; client managed allocated={GC.GetTotalAllocatedBytes(true) - allocatedBefore} B (excludes Go child allocations); child processes={count}");
            }
        }
        finally
        {
            if (coreStarted)
            {
                if (!core.HasExited) core.Kill(entireProcessTree: true);
                await core.WaitForExitAsync(CancellationToken.None);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync(ct);
        using NetworkStream stream = accepted.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4096\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(new byte[4096], ct);
    }

    private static ProcessStartInfo StartInfo(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] Stat(string name, ulong value = 0)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        var bytes = new List<byte> { 10, (byte)nameBytes.Length };
        bytes.AddRange(nameBytes);
        if (value > 0)
        {
            bytes.Add(16);
            do { bytes.Add((byte)((value & 127) | (value > 127 ? 128UL : 0))); value >>= 7; } while (value != 0);
        }
        return bytes.ToArray();
    }

    private static byte[] Stats(params byte[][] stats) => stats.SelectMany(stat => new byte[] { 10, (byte)stat.Length }.Concat(stat)).ToArray();
    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame, 5);
        return frame;
    }
    private static HttpResponseMessage Response(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.TrailingHeaders.Add("grpc-status", "0");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TestLog : ILogService
    {
        public void RegisterSensitiveValue(string value) { }
        public void Info(string scope, string message) { }
        public void Warn(string scope, string message) { }
        public void Error(string scope, string message, Exception? ex = null) { }
    }
}
