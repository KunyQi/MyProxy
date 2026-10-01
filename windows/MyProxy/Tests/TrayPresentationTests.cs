using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

/// <summary>
/// 托盘菜单的文案与可用性。托盘其余部分是 Shell_NotifyIcon 与窗口消息，
/// 只有这一层能自动验证——所以它必须留在 Core 里，不能写回 P/Invoke 那个文件。
/// </summary>
[TestClass]
public sealed class TrayPresentationTests
{
    [DataTestMethod]
    [DataRow(AppState.Connected, ProxyMode.Rule, "已连接 · 智能分流")]
    [DataRow(AppState.Connected, ProxyMode.Global, "已连接 · 全局代理")]
    [DataRow(AppState.Connecting, ProxyMode.Rule, "连接中 · 智能分流")]
    [DataRow(AppState.Disconnecting, ProxyMode.Global, "断开中 · 全局代理")]
    [DataRow(AppState.Error, ProxyMode.Rule, "连接失败 · 智能分流")]
    [DataRow(AppState.Unbound, ProxyMode.Rule, "未绑定 · 智能分流")]
    [DataRow(AppState.Disconnected, ProxyMode.Global, "未连接 · 全局代理")]
    public void StatusLine_CoversEveryState(AppState state, ProxyMode mode, string expected)
        => Assert.AreEqual(expected, TrayPresentation.StatusLine(state, mode));

    [TestMethod]
    public void StopIsDisabled_OnlyWhenThereIsNothingToStop()
    {
        Assert.IsFalse(TrayPresentation.CanStop(AppState.Disconnected));
        Assert.IsFalse(TrayPresentation.CanStop(AppState.Unbound));
    }

    [TestMethod]
    public void StopStaysEnabled_WhereverTheFlowCouldBeStuck()
    {
        // 这三个状态都可能卡住，用户需要一个出口——尤其 Error：
        // xray 可能还活着而系统代理已指向它，必须能停。
        Assert.IsTrue(TrayPresentation.CanStop(AppState.Connecting));
        Assert.IsTrue(TrayPresentation.CanStop(AppState.Disconnecting));
        Assert.IsTrue(TrayPresentation.CanStop(AppState.Error));
        Assert.IsTrue(TrayPresentation.CanStop(AppState.Connected));
    }
}
