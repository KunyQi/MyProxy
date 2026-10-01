using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MyProxy.Core;
using MyProxy.Gui.ViewModels;
using MyProxy.Services;

namespace MyProxy.Gui;

/// <summary>
/// 应用装配。
///
/// <para>
/// 与 Windows 端 <c>App.xaml.cs</c> 的分工一致：
/// 资源字典里挂默认皮肤入口，<c>MainWindow</c> 持有三个 ViewModel，托盘是独立的服务。
/// 差别只有一处，而且是架构性的：Linux 端**连接由守护进程持有**，所以这里没有
/// <c>LinuxAppServices</c>（那会连带构造 xray、系统代理、更新器等只该由守护进程拥有的东西），
/// 只有一个 <see cref="ControlClient"/>——GUI 是守护进程的客户端。
/// </para>
/// </summary>
public sealed partial class App : Application
{
    private LinuxSingleInstanceService? _singleInstance;
    private LinuxTrayService? _tray;
    private DispatcherTimer? _activateTimer;
    private MainWindow? _window;
    private bool _released;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // 单实例：托盘程序不该有第二份。名字刻意与守护进程的锁不同名
        // （守护进程用默认的 "instance"）——两者必须能同时存在，
        // 抢同一把锁会把「GUI 与守护进程并存」这件事直接做废。
        var storage = new LinuxStorageService();
        var singleInstance = new LinuxSingleInstanceService(storage, "gui");
        if (!singleInstance.TryAcquire())
        {
            // 已经有一个 GUI 在跑：把「把窗口摆到前台」写进请求文件就退出。
            singleInstance.SignalActivate();
            singleInstance.Dispose();
            desktop.Shutdown();
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _singleInstance = singleInstance;

        // GUI 只认识控制口：默认就是 $XDG_DATA_HOME/myproxy/runtime/control.sock。
        var client = new ControlClient(ControlClient.DefaultSocketPath());

        _window = new MainWindow(client);
        desktop.MainWindow = _window;
        _window.Model.PropertyChanged += OnModelPropertyChanged;

        // 收到「第二个实例想启动」的请求时把窗口摆到前台。请求来自一个文件，
        // 所以必须有人去读它——这个一秒一次的定时器就是那个读者。
        _singleInstance.ActivateRequested += ShowAndActivate;
        _activateTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => _singleInstance?.PollActivateRequest());
        _activateTimer.Start();

        _window.Show();
        _window.Activate();

        SetUpTray(client);
        _window.Model.StartPolling();
        RefreshTray();

        // 收尾钩子是**生命周期对象上的事件**，不是 Application 的虚方法：
        // Avalonia 11 的 Application 没有 OnShutdownRequested。两个都挂上并加一次性守卫，
        // 免得某条路径不触发时把托盘与单实例锁留在身后。
        desktop.ShutdownRequested += (_, _) => ReleaseResources();
        if (desktop is IControlledApplicationLifetime controlled)
        {
            controlled.Exit += (_, _) => ReleaseResources();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(ControlClient client)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _tray = LinuxTrayService.Create();

            // 三个事件与 Windows 端的 ITrayService 同义：
            // 「打开」只把窗口摆到前台；「停止代理」才是真的断连接；
            // 「退出」只关界面——连接与系统代理仍由守护进程持有。
            _tray.OpenRequested += ShowAndActivate;
            _tray.StopRequested += () => _ = client.SendAsync(ControlCommands.Stop);
            _tray.ExitRequested += () =>
            {
                _window.PrepareExit();
                _window.Close();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    lifetime.Shutdown();
                }
            };
        }
        catch (Exception ex)
        {
            // 托盘挂不上不是错误：没有 StatusNotifier 的桌面（裸 X、部分平铺窗管）就是没有。
            // 界面仍然可用，只是没有常驻图标——不该因为桌面缺一个扩展就让程序起不来。
            _tray = null;
            _ = ex;
        }
    }

    /// <summary>
    /// 让托盘跟上状态：图标颜色、菜单里的状态行、悬停文字。
    /// 与 Windows 端一样，状态一变就更新（轮询每秒跑一次，只有真的变了才回调）。
    /// </summary>
    private void RefreshTray()
    {
        if (_tray is null || _window is null)
        {
            return;
        }

        string? unavailableStatusText = _window.Model.IsDaemonUnreachable
            ? _window.Model.StatusText
            : null;
        _tray.UpdateState(_window.Model.State, _window.Model.Mode, unavailableStatusText);
    }

    private void ShowAndActivate()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>
    /// 进程收尾。顺序是刻意的：先停轮询再放单实例锁——反过来的话，轮询里那次还没回来的
    /// <c>status</c> 请求会在锁已释放之后继续跑，下一次启动可能与它撞上。
    ///
    /// <para>
    /// <b>这里不碰连接</b>：不发 <c>stop</c>、不停内核、不恢复系统代理。那些归守护进程，
    /// 这正是「GUI 退出不影响网络」这条不变量的实现方式。
    /// </para>
    /// </summary>
    private void ReleaseResources()
    {
        if (_released)
        {
            return;
        }

        _released = true;

        if (_window is not null)
        {
            _window.Model.PropertyChanged -= OnModelPropertyChanged;
            _window.Model.StopPolling();
        }

        if (_activateTimer is not null)
        {
            _activateTimer.Stop();
            _activateTimer = null;
        }

        _tray?.Dispose();
        _tray = null;

        TrayIconFactory.ReleaseCache();

        if (_singleInstance is not null)
        {
            _singleInstance.ActivateRequested -= ShowAndActivate;
            _singleInstance.Dispose();
            _singleInstance = null;
        }
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State)
            or nameof(MainViewModel.Mode)
            or nameof(MainViewModel.IsDaemonUnreachable)
            or nameof(MainViewModel.StatusText))
        {
            RefreshTray();
            return;
        }

        // 皮肤变了就把界面换过去。**谁改字典只有这一处**（见 AvaloniaThemeService）：
        // ViewModel 只如实报告守护进程里的设置，不碰资源字典。
        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            AvaloniaThemeService.Apply(_window?.Model.Theme ?? UiTheme.Classic);
        }
    }
}
