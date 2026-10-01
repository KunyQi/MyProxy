using MyProxy.Gui.ViewModels;

namespace MyProxy.Linux.Tests;

[TestClass]
public sealed class DaemonAvailabilityStateTests
{
    [TestMethod]
    public void PollTimeoutMakesThePreviousConnectedStateUnknownUntilAValidSnapshotArrives()
    {
        var availability = new DaemonAvailabilityState();

        availability.MarkUnavailable(PollStatusFailurePolicy.Timeout());

        Assert.IsTrue(availability.IsUnavailable);
        Assert.AreEqual(AppState.Error, availability.EffectiveState(AppState.Connected));
        Assert.AreEqual("连接状态未知", availability.StatusText);
        Assert.IsFalse(availability.ShouldShowInFlightActivity(true),
            "在途请求的互斥标记保留，但未知状态时不显示旧检测/切换活动。");
        StringAssert.Contains(availability.Hint, "轮询超时");

        availability.RestoreAfterValidStatus();

        Assert.IsFalse(availability.IsUnavailable);
        Assert.AreEqual(AppState.Connected, availability.EffectiveState(AppState.Connected));
        Assert.AreEqual("", availability.StatusText);
        Assert.AreEqual("", availability.Hint);
        Assert.IsTrue(availability.ShouldShowInFlightActivity(true),
            "有效状态恢复后，仍在途的请求重新显示活动状态。");
    }

    [TestMethod]
    public void GuiShutdownCancellationIsIgnoredButPollTimeoutIsUnavailable()
    {
        Assert.AreEqual(
            PollCancellationDisposition.IgnoreForGuiShutdown,
            PollStatusFailurePolicy.ClassifyCancellation(guiShutdownRequested: true));
        Assert.AreEqual(
            PollCancellationDisposition.MarkUnavailableAsTimeout,
            PollStatusFailurePolicy.ClassifyCancellation(guiShutdownRequested: false));
    }

    [TestMethod]
    public void ExplicitDaemonExitGetsUnavailableTextAndStartInstructions()
    {
        DaemonUnavailablePresentation presentation = PollStatusFailurePolicy.Response(
            ErrorCode.ApiUnreachable,
            "无法连接守护进程");

        Assert.AreEqual("后台不可用", presentation.StatusText);
        StringAssert.Contains(presentation.Hint, "systemctl --user start myproxy.service");
        StringAssert.Contains(presentation.Hint, "myproxy start");
    }
}
