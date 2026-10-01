using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// Windows 端的 <see cref="IUiDispatcher"/>：把回调交给 WPF 的 Dispatcher。
///
/// <para>
/// 没有 <c>Application.Current</c> 时就地执行——测试宿主、启动早期、以及
/// 界面尚未建立时的后台路径都属于这种情况，这也是控制器原来内联在此处的行为。
/// </para>
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread
        => System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }
}
