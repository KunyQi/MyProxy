using Avalonia.Threading;
using MyProxy.Core;

namespace MyProxy.Gui;

/// <summary>
/// Avalonia 版本的 <see cref="IUiDispatcher"/>。
///
/// <para>
/// 现在这个 GUI <b>用不到它</b>：托盘程序不持有连接控制器，界面线程上跑的只有绑定与
/// 每秒一次的轮询，没有需要跨线程回送的事件源。写上它是因为它是 Linux 端最后一块与
/// Windows 端同形的平台件——将来若把守护进程内嵌进 GUI（单进程模式），
/// <c>ConnectionController</c> 的状态、模式、速率与自检结果都会从后台线程回来，
/// 那时需要的就是这个类型，而不是在控制器里长出第二套「有没有界面」的判断。
/// </para>
///
/// <para>
/// <b>只有公开 API。</b><see cref="Dispatcher"/> 上「是否正在关停」那个属性在 11.3 里是
/// internal，能用的判断只有 <see cref="Dispatcher.CheckAccess"/>——它同时也回答得了
/// 「有没有界面线程」：Avalonia 没有任何 dispatcher 实现时会退化成一个空实现，
/// 那种情况下它恒为 false，把动作排进队列就再也没人执行了。所以这里的两级降级是：
/// 拿不到界面线程 → <b>就地执行</b>，与 Windows 端「<c>Application.Current</c> 不存在时
/// 就地执行」同一条规则；拿得到、但不在界面线程上 → 排进 UI 队列。
/// </para>
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public bool IsOnUiThread => HasUiThread() && Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        bool queued = false;
        try
        {
            if (HasUiThread())
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    action();
                    return;
                }

                Dispatcher.UIThread.Post(action);
                queued = true;
            }
        }
        catch (Exception)
        {
            // Dispatcher 正在关停时 CheckAccess 也可能抛。抛了就等于没有可用的界面线程，
            // 落到下面的就地执行。
            queued = false;
        }

        if (!queued)
        {
            action();
        }
    }

    /// <summary>
    /// 有没有真正的界面线程：平台运行时子系统没起来时（构造顺序、无窗口的宿主），
    /// <c>Dispatcher.UIThread</c> 是可用的，但背后的实现是空的，<c>CheckAccess</c> 恒为 false。
    /// </summary>
    private static bool HasUiThread()
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess() || Dispatcher.UIThread.SupportsRunLoops;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
