using System.Text.Json;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 控制通道：线上格式与一次真实往返。
///
/// <para>
/// 它是 CLI / GUI 与守护进程之间唯一的接口，所以两件事都要钉住：快照的 JSON 形状
/// （加字段不能破坏旧客户端）与「只有同一个用户能连上」这条边界。
/// </para>
/// </summary>
[TestClass]
public sealed class ControlChannelTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [TestMethod]
    public void StatusSnapshot_SerialisesEnumsAsNames()
    {
        var snapshot = new StatusSnapshot
        {
            State = AppState.Connected,
            Mode = ProxyMode.Global,
            Theme = UiTheme.Porcelain,
            LastErrorCode = ErrorCode.ServerUntrusted,
            CheckEgress = EgressVerdict.Bypassed
        };

        string json = JsonSerializer.Serialize(snapshot, ControlJson.Options);

        // 名字而不是数字：数字会让「谁改动了枚举顺序」变成一次静默的兼容性破坏。
        Assert.IsTrue(json.Contains("\"Connected\"", StringComparison.Ordinal), json);
        Assert.IsTrue(json.Contains("\"Global\"", StringComparison.Ordinal), json);
        Assert.IsTrue(json.Contains("\"ServerUntrusted\"", StringComparison.Ordinal), json);
        Assert.IsTrue(json.Contains("\"Bypassed\"", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void UnknownFieldsInASnapshot_AreIgnored()
    {
        // 新守护进程 + 旧客户端：旧的一方必须能继续读。
        const string json = """{"state":"Connected","mode":"Rule","somethingNew":42}""";

        StatusSnapshot? snapshot = JsonSerializer.Deserialize<StatusSnapshot>(json, ControlJson.Options);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AppState.Connected, snapshot!.State);
        Assert.AreEqual(ProxyMode.Rule, snapshot.Mode);
    }

    [TestMethod]
    public void ControlClient_WithoutADaemon_ReportsItInsteadOfThrowing()
    {
        var client = new ControlClient(Path.Combine(_root, "control.sock"));

        ControlResponse response = client
            .SendAsync(ControlCommands.Status, "", CancellationToken.None)
            .GetAwaiter().GetResult();

        // 「没在跑」是这套协议里最常见的正常状态，CLI 与托盘都要能平静处理它。
        Assert.IsFalse(response.Ok);
        Assert.AreEqual(ErrorCode.ApiUnreachable, response.ErrorCode);
        Assert.IsFalse(client.IsRunning());
    }

    [TestMethod]
    public void ControlServer_RoundTripsACommandOverTheSocket()
    {
        if (!OperatingSystem.IsLinux())
        {
            // 服务端用 SO_PEERCRED 核对对端 uid。这个选项在 Windows 上不存在，
            // 连接会被（正确地）拒绝——所以这条用例由 CI 的 Linux lane 来跑。
            Assert.Inconclusive("SO_PEERCRED 只在 Linux 上存在。");
        }

        string socketPath = Path.Combine(_root, "control.sock");
        using var server = new ControlServer(
            socketPath,
            (request, _) => Task.FromResult(request.Command switch
            {
                ControlCommands.Ping => ControlResponse.Success(new StatusSnapshot { State = AppState.Disconnected }),
                _ => ControlResponse.Failure(ErrorCode.Unknown, "未知命令")
            }));

        server.Start();

        var client = new ControlClient(socketPath);
        Assert.IsTrue(client.IsRunning());

        ControlResponse ping = client
            .SendAsync(ControlCommands.Ping, "", CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.IsTrue(ping.Ok);
        Assert.AreEqual(AppState.Disconnected, ping.Status!.State);

        ControlResponse unknown = client
            .SendAsync("nonsense", "", CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.IsFalse(unknown.Ok);
        Assert.AreEqual("未知命令", unknown.Message);
    }

    [TestMethod]
    public void CommandNames_AreStable()
    {
        // 这些字符串是 CLI、GUI 与守护进程之间的约定；改名等于换协议。
        Assert.AreEqual("status", ControlCommands.Status);
        Assert.AreEqual("bind", ControlCommands.Bind);
        Assert.AreEqual("unbind", ControlCommands.Unbind);
        Assert.AreEqual("start", ControlCommands.Start);
        Assert.AreEqual("stop", ControlCommands.Stop);
        Assert.AreEqual("mode", ControlCommands.Mode);
        Assert.AreEqual("check", ControlCommands.Check);
        Assert.AreEqual("autostart", ControlCommands.AutoStart);
        Assert.AreEqual("autoconnect", ControlCommands.AutoConnect);
        Assert.AreEqual("update-check", ControlCommands.UpdateCheck);
        Assert.AreEqual("update-apply", ControlCommands.UpdateApply);
        Assert.AreEqual("shutdown", ControlCommands.Shutdown);
    }
}
