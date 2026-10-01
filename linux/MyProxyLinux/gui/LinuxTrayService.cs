using Avalonia.Controls;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Gui;

/// <summary>
/// Linux 托盘：实现共享平面里的 <see cref="ITrayService"/>，与 Windows 端的
/// <c>TrayService</c> 同名同形——菜单项、文案、行为一一对应：
///
/// <code>
///   MyProxy           （标题，禁用）
///   未连接            （状态行，禁用，随状态更新）
///   打开 MyProxy
///   停止代理          （只有能停的时候才可点）
///   ────────────
///   退出              （只关界面；连接仍由守护进程持有）
/// </code>
///
/// <para>
/// 与 Windows 端的差别只有两处，都是平台事实而不是取舍：
/// <list type="number">
/// <item>Windows 用 <c>Shell_NotifyIcon</c> 的气泡提示做「仍在后台运行」的告知；
/// StatusNotifier 没有等价物，<see cref="ShowBackgroundHint"/> 因此退化为
/// 「把悬停文字换成那句话」——信息还在，只是出现的位置不同。</item>
/// <item>菜单文字在 StatusNotifier 的 DBusMenu 上是**导出一次**的，Avalonia 11 对已导出项
/// 的属性更新支持有限。所以状态行仍然按同一份 <see cref="TrayPresentation.StatusLine"/>
/// 计算并写进去，但不保证所有托盘实现都会重绘它；**悬停文字是可靠的**
/// （它走的是另一条属性路径），于是状态在两处都表达了一遍。</item>
/// </list>
/// </para>
/// </summary>
public sealed class LinuxTrayService : ITrayService
{
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _stopItem;
    private bool _disposed;
    private bool _backgroundHintShown;

    private LinuxTrayService(TrayIcon tray, NativeMenuItem statusItem, NativeMenuItem stopItem)
    {
        _tray = tray;
        _statusItem = statusItem;
        _stopItem = stopItem;
    }

    public event Action? OpenRequested;
    public event Action? StopRequested;
    public event Action? ExitRequested;

    /// <summary>建立托盘。桌面不支持 StatusNotifier 时抛异常——由调用方决定降级方式。</summary>
    public static LinuxTrayService Create()
    {
        // 菜单项与 Windows 端逐字相同（含那条分隔线）。
        var titleItem = new NativeMenuItem { Header = AppInfo.ProductName, IsEnabled = false };
        var statusItem = new NativeMenuItem { Header = "未连接", IsEnabled = false };
        var openItem = new NativeMenuItem { Header = "打开客户端" };
        var stopItem = new NativeMenuItem { Header = "停止代理" };
        var exitItem = new NativeMenuItem { Header = "退出" };

        var menu = new NativeMenu();
        menu.Items.Add(titleItem);
        menu.Items.Add(statusItem);
        menu.Items.Add(openItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = TrayIconFactory.IconFor(AppState.Unbound),
            ToolTipText = AppInfo.ProductName,
            Menu = menu,
            IsVisible = true
        };

        var service = new LinuxTrayService(tray, statusItem, stopItem);

        openItem.Click += (_, _) => service.OpenRequested?.Invoke();
        stopItem.Click += (_, _) => service.StopRequested?.Invoke();
        exitItem.Click += (_, _) => service.ExitRequested?.Invoke();

        // 左键点击图标 = 打开窗口。它与菜单是两条输入路径，Windows 端也是这么分的。
        tray.Clicked += (_, _) => service.OpenRequested?.Invoke();

        return service;
    }

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        // Avalonia 的 TrayIcon 在 IsVisible=true 时就已经挂上去了，
        // 没有 Windows 那边「先注册、失败了再报告」的两步过程。
        _tray.IsVisible = true;
    }

    public void ShowBackgroundHint()
    {
        if (_disposed || _backgroundHintShown)
        {
            return;
        }

        _backgroundHintShown = true;

        // Windows 弹气泡「仍在后台运行，可从系统托盘打开。」；
        // StatusNotifier 没有气泡，退化成把这句话放进悬停文字。
        _tray.ToolTipText = "客户端仍在后台运行，可从系统托盘打开。";
    }

    public void UpdateState(AppState state, ProxyMode mode)
        => UpdateState(state, mode, unavailableStatusText: null);

    public void UpdateState(AppState state, ProxyMode mode, string? unavailableStatusText)
    {
        if (_disposed)
        {
            return;
        }

        string statusLine = unavailableStatusText is null
            ? TrayPresentation.StatusLine(state, mode)
            : $"{unavailableStatusText} · {ProxyModeText.NameFor(mode)}";
        _statusItem.Header = statusLine;
        _stopItem.IsEnabled = unavailableStatusText is null && TrayPresentation.CanStop(state);

        _tray.Icon = TrayIconFactory.IconFor(state);

        // 悬停文字同时承载「应用名 + 状态」：托盘菜单文字更新是否有平台支持不确定，
        // 而这一条在所有实现上都生效，所以状态在两处都写。
        string statusToolTip = unavailableStatusText is null
            ? TrayIconFactory.ToolTipFor(state, mode)
            : $"{AppInfo.ProductName} {statusLine}";
        _tray.ToolTipText = _backgroundHintShown
            ? $"客户端仍在后台运行，可从系统托盘打开。\n{statusToolTip}"
            : statusToolTip;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
