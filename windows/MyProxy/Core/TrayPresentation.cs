namespace MyProxy.Core;

/// <summary>
/// 托盘菜单要显示什么。纯逻辑、无 Win32、无 UI——托盘的其余部分全是
/// P/Invoke 与窗口消息，只有这一层能在测试里跑。
/// </summary>
public static class TrayPresentation
{
    /// <summary>菜单里那行只读状态，形如「已连接 · 智能分流」。</summary>
    public static string StatusLine(AppState state, ProxyMode mode)
    {
        string stateText = state switch
        {
            AppState.Connected => "已连接",
            AppState.Connecting => "连接中",
            AppState.Disconnecting => "断开中",
            AppState.Error => "连接失败",
            AppState.Unbound => "未绑定",
            _ => "未连接"
        };

        // 取自 ProxyModeText 而不是在这里再写一遍字面量：同一个模式在托盘、
        // 主界面和设置页出现三种叫法，是这类改名最常见的结局。
        return $"{stateText} · {ProxyModeText.NameFor(mode)}";
    }

    /// <summary>
    /// 「停止代理」是否可点。未绑定与已断开时没有可停的东西；
    /// 连接中 / 断开中 / 失败都保留可点，因为那几个状态都可能卡住，用户需要一个出口。
    /// </summary>
    public static bool CanStop(AppState state)
        => state is not (AppState.Disconnected or AppState.Unbound);
}
