namespace MyProxy.Core;

/// <summary>
/// 把回调封送到界面线程。
///
/// <para>
/// 存在的理由是让 <c>Services/ConnectionController</c> 不必认识任何 UI 框架：控制器要在
/// 状态、模式、速率、自检结果变化时通知界面，而 Windows 端是 WPF、Linux 端是 Avalonia、
/// 命令行与 systemd 下根本没有界面线程。两端各自实现本接口，无界面进程不注入，
/// 默认<b>就地执行</b>——这与 Windows 端「<c>Application.Current</c> 不存在时就地执行」
/// 的原行为一致（测试宿主、启动早期都属于这种情况）。
/// </para>
///
/// <para>
/// 放在 <c>Core/</c> 是因为它只有形状没有 I/O：它不认识窗口、控件或消息循环。
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前线程是否就是界面线程。无界面进程恒为 true。</summary>
    bool IsOnUiThread { get; }

    /// <summary>把动作送到界面线程；已在界面线程上时就地执行。</summary>
    void Post(Action action);
}
