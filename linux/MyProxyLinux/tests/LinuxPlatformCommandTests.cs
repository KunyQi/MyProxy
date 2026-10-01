using System.Diagnostics;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 外部命令与自启单元：两处都直接决定「停止流程会不会卡住」「系统代理恢复失败时内核还在不在」。
/// </summary>
[TestClass]
public sealed class LinuxPlatformCommandTests
{
    [TestMethod]
    public void Run_ReturnsTheOutputOfACommandThatFinishes()
    {
        RequireLinux();

        (int ExitCode, string StandardOutput)? result =
            LinuxCommands.Run("sh", new[] { "-c", "echo hello; echo noise >&2" }, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Value.ExitCode);
        Assert.AreEqual("hello", result.Value.StandardOutput.Trim());
    }

    [TestMethod]
    public void Run_EnforcesTheTimeoutOnAHungCommand()
    {
        RequireLinux();

        // 一个卡在 D-Bus 上的 gsettings 就是这个形状：不退出、也不关 stdout。
        // 超时必须真的生效，否则停止流程里的代理恢复会被它永远挂住。
        var clock = Stopwatch.StartNew();
        (int, string)? result = LinuxCommands.Run("sh", new[] { "-c", "sleep 30" }, TimeSpan.FromMilliseconds(500));

        Assert.IsNull(result);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [TestMethod]
    public void Run_DoesNotDeadlockOnALargeStandardError()
    {
        RequireLinux();

        // 先读完 stdout 再读 stderr 的写法，会在子进程写满 stderr 管道时与它互相等死。
        (int ExitCode, string StandardOutput)? result = LinuxCommands.Run(
            "sh",
            new[] { "-c", "head -c 1000000 /dev/zero >&2; echo done" },
            TimeSpan.FromSeconds(10));

        Assert.IsNotNull(result);
        Assert.AreEqual("done", result.Value.StandardOutput.Trim());
    }

    [TestMethod]
    public void ServiceUnit_LeavesTheKernelAliveWhenTheDaemonExits()
    {
        var startup = new LinuxStartupService(log: null, executablePath: "/opt/test/myproxy");

        string unit = startup.BuildServiceUnit();

        // mixed / control-group 会在主进程退出后把 cgroup 里的 xray 一并 SIGKILL，
        // 把「系统代理恢复失败时保留内核」这条 fail-open 规矩架空。
        StringAssert.Contains(unit, "KillMode=process");
        Assert.IsFalse(unit.Contains("KillMode=mixed", StringComparison.Ordinal));
        StringAssert.Contains(unit, "ExecStart=/opt/test/myproxy run");
        StringAssert.Contains(unit, "Restart=on-failure");
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("这组测试要真起 Linux 进程。");
        }
    }
}
