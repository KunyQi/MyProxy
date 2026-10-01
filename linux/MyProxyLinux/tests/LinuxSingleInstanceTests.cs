using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 单实例：同一台机器上只允许一个进程持有连接。
///
/// <para>
/// 锁的实现是「以 <see cref="FileShare.None"/> 打开一个文件」——在 Unix 上
/// .NET 用 <c>flock(2)</c> 实现文件共享语义，进程退出（包括被 <c>SIGKILL</c>）
/// 时内核自动释放。这条用例钉住的是「第二次打开必须失败」：如果它成立，
/// 两个守护进程就不会同时去改系统代理。
/// </para>
/// </summary>
[TestClass]
public sealed class LinuxSingleInstanceTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-instance-" + Guid.NewGuid().ToString("N"));
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
    public void SecondAcquire_FailsWhileTheFirstHoldsTheLock()
    {
        var storage = new LinuxStorageService(_root);
        using var first = new LinuxSingleInstanceService(storage);
        var second = new LinuxSingleInstanceService(storage);

        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(second.TryAcquire());

        second.Dispose();
    }

    [TestMethod]
    public void Names_KeepTheDaemonAndTheTrayApart()
    {
        var storage = new LinuxStorageService(_root);
        using var daemon = new LinuxSingleInstanceService(storage, "instance");
        using var gui = new LinuxSingleInstanceService(storage, "gui");

        // 两者必须能各持一份：否则「先开托盘再起服务」会变成一次莫名其妙的失败。
        Assert.IsTrue(daemon.TryAcquire());
        Assert.IsTrue(gui.TryAcquire());
    }

    [TestMethod]
    public void ReleasedLock_CanBeTakenAgain()
    {
        var storage = new LinuxStorageService(_root);

        var first = new LinuxSingleInstanceService(storage);
        Assert.IsTrue(first.TryAcquire());
        first.Dispose();

        using var second = new LinuxSingleInstanceService(storage);
        Assert.IsTrue(second.TryAcquire());
    }

    [TestMethod]
    public void ActivateRequest_IsDeliveredOnceThenConsumed()
    {
        var storage = new LinuxStorageService(_root);
        using var holder = new LinuxSingleInstanceService(storage, "gui");
        Assert.IsTrue(holder.TryAcquire());

        int activations = 0;
        holder.ActivateRequested += () => activations++;

        // 第二个实例抢不到锁时做的事：写一个请求文件然后退出。
        using var second = new LinuxSingleInstanceService(storage, "gui");
        second.SignalActivate();
        Assert.IsFalse(second.TryAcquire());

        holder.PollActivateRequest();
        Assert.AreEqual(1, activations);

        // 请求是一次性的：文件被读走之后不会再触发一次。
        holder.PollActivateRequest();
        Assert.AreEqual(1, activations);
    }
}
