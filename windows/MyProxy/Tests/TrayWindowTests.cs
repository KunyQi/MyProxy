using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class TrayWindowTests
{
    [TestMethod]
    public void HiddenTrayWindow_ReceivesTopLevelBroadcasts() => IconGeometryTests.RunSta(() =>
    {
        using var tray = new TrayService();
        Assert.IsFalse(IsWindowVisible(tray.WindowHandle), "消息窗口不能出现在桌面上");
        Assert.AreEqual(IntPtr.Zero, GetParent(tray.WindowHandle), "message-only 窗口收不到任务栏广播");

        // 独立消息不会触发其他托盘应用的 TaskbarCreated 处理器。
        uint message = RegisterWindowMessageW($"MyProxy.Tests.Broadcast.{Guid.NewGuid():N}");
        Assert.AreNotEqual(0u, message);
        bool received = false;
        HwndSource source = HwndSource.FromHwnd(tray.WindowHandle);
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == message) received = true;
            return IntPtr.Zero;
        }
        source.AddHook(Hook);
        try
        {
            Assert.IsTrue(PostMessageW(new IntPtr(0xffff), message, IntPtr.Zero, IntPtr.Zero));
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!received && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(1);
            }
            Assert.IsTrue(received, "托盘窗口必须能接收发送给所有顶层窗口的广播");
        }
        finally { source.RemoveHook(Hook); }
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
