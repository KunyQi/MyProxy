using System.ComponentModel;
using MyProxy.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyProxy.ViewModels;

namespace MyProxy;

public partial class MainWindow : Window
{
    private readonly BindViewModel _bindViewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    public MainWindow()
    {
        InitializeComponent();

        _bindViewModel = new BindViewModel(App.Services.Binding, App.Services.Connection, App.Services.Log);
        _mainViewModel = new MainViewModel(App.Services.Connection, App.Services.Log);
        _settingsViewModel = new SettingsViewModel(
            App.Services.Storage,
            App.Services.Startup,
            App.Services.Update,
            App.Services.Connection,
            App.Services.Log,
            App.Services.Theme);

        _bindViewModel.BindCompleted += OnBindCompleted;
        _mainViewModel.SettingsRequested += OnSettingsRequested;
        _settingsViewModel.SettingsClosed += OnSettingsClosed;

        PageHost.Content = App.IsProbablyBound() ? _mainViewModel : _bindViewModel;
        FocusPageContent();

        // 窗口藏进托盘或最小化后，呼吸动画与每秒一次的流量采样都没有观众。
        // IsVisible 管 Hide()/Show()，WindowState 管最小化——WPF 的 IsVisible
        // 对最小化的窗口仍然是 true，两者必须一起判断。
        IsVisibleChanged += (_, _) => SyncLiveState();
        StateChanged += (_, _) => SyncLiveState();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SyncLiveState();

        App.Services.Theme.ThemeChanged += _ => Dispatcher.InvokeAsync(RebuildCurrentPage);
    }

    /// <summary>
    /// 换肤后把当前页重新实例化一次。
    ///
    /// 为什么必须重建：三页视图取样式全部走 <c>{StaticResource}</c>，它在**视图构造时**
    /// 就把笔刷解析完了。ThemeService 换掉合并字典只会改变「以后谁来查」的结果，
    /// 已经立在那里的按钮不会自己变色。把 Content 置空再放回去，DataTemplate 会重新
    /// 展开一份 View，新字典这才落到界面上。ViewModel 是同一个实例，状态不丢。
    ///
    /// 淡入用的是**新皮肤**的 Motion.Duration.Base：切过去的那一下就已经是新的动效语言。
    /// 动画与 <see cref="SetPage"/> 同样受 Windows 动画偏好与可见性约束，并在结束后释放
    /// 时钟——否则挂在 Opacity 上的时钟会锁死该属性，后续切页的淡入将无从生效。
    /// </summary>
    private void RebuildCurrentPage()
    {
        object? current = PageHost.Content;
        if (current is null)
        {
            return;
        }

        PageHost.Content = null;
        PageHost.Content = current;

        PageHost.BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation && IsVisible && !IsExiting)
        {
            DoubleAnimation fade = new(0d, 1d, (Duration)FindResource("Motion.Duration.Base"))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => PageHost.BeginAnimation(OpacityProperty, null);
            PageHost.BeginAnimation(OpacityProperty, fade);
        }

        // 焦点必须重新送进页内：旧页元素已被移除，WPF 会把焦点退回 Window，
        // 而设置页的 Esc 挂在 SettingsView 的 InputBindings 上——焦点不在页内就按不动。
        FocusPageContent();
    }

    /// <summary>把「界面是否真的在给人看」同步给主页 ViewModel。</summary>
    private void SyncLiveState()
        => _mainViewModel.IsLive = !IsExiting && IsVisible
            && WindowState != WindowState.Minimized
            && ReferenceEquals(PageHost.Content, _mainViewModel);

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            _mainViewModel.AnimationsEnabled = SystemParameters.ClientAreaAnimation;
            if (!_mainViewModel.AnimationsEnabled)
                PageHost.BeginAnimation(OpacityProperty, null);
        }
    }

    private void SetPage(object viewModel)
    {
        PageHost.Content = viewModel;
        SyncLiveState();

        PageHost.BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation && IsVisible && !IsExiting)
        {
            DoubleAnimation fade = new(0d, 1d, (Duration)FindResource("Motion.Duration.Base"))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => PageHost.BeginAnimation(OpacityProperty, null);
            PageHost.BeginAnimation(OpacityProperty, fade);
        }
        FocusPageContent();
    }

    private void FocusPageContent()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => PageHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
    }

    public bool IsExiting { get; private set; }

    public void PrepareExit()
    {
        IsExiting = true;
        SyncLiveState();
    }

    public Task FlushPendingSettingsAsync() => _settingsViewModel.FlushAsync();

    public void ShowBindView(string? errorMessage = null)
    {
        _bindViewModel.Reset(errorMessage);
        SetPage(_bindViewModel);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
            App.Services.Tray.Show();
            if (App.Services.Connection.State == AppState.Connected)
            {
                App.Services.Tray.ShowBackgroundHint();
            }

            return;
        }

        base.OnClosing(e);
    }

    private void OnBindCompleted()
    {
        SetPage(_mainViewModel);
    }

    private void OnSettingsRequested()
    {
        // 这个 ViewModel 是构造一次长期复用的，设备身份必须每次进页面重读：
        // 首次运行时它建好的那一刻还没绑定，绑完回到本页也不会自己更新。
        _settingsViewModel.RefreshDevice();
        SetPage(_settingsViewModel);
    }

    private void OnSettingsClosed()
    {
        SetPage(_mainViewModel);
    }
}
