using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyProxy.Core;
using MyProxy.Gui.ViewModels;
using MyProxy.Services;

namespace MyProxy.Gui;

/// <summary>
/// 窗口外壳：一个 400 宽的窗口 + 一个 <c>ContentControl</c>（PageHost），
/// 三页（绑定 / 主界面 / 设置）靠换 <c>Content</c> 切换，DataTemplate 负责展开视图。
///
/// <para>
/// 结构与 Windows 端 <c>MainWindow.xaml.cs</c> 一一对应：谁来切页、切页时做什么
/// （淡入只动 Opacity、焦点送进页内、点关闭是收进托盘而不是退出）都照搬。
/// 差别只有两处，各自写在对应位置并说明了理由：首页判断的依据，以及换肤不必再重建页面。
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly BindViewModel _bindViewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private bool _isExiting;

    /// <summary>
    /// 无参构造：**只服务 Avalonia 的 XAML 加载器**（以及 XAML 预览器）。
    ///
    /// <para>
    /// 没有它，XAML 编译器会给出 `AVLN3001: XAML resource "avares://myproxy-gui/MainWindow.axaml"
    /// won't be reachable via runtime loader, as no public constructor was found`——
    /// 而这条警告的后果比看上去严重：实测它会让程序集里的预编译 XAML **整份**不可用，
    /// 于是第一个 <c>AvaloniaXamlLoader.Load</c>（在 <c>App.Initialize</c> 里）就抛
    /// `XamlLoadException: No precompiled XAML found for MyProxy.Gui.App`——
    /// 报的是 App，坏的是 MainWindow，很容易查错方向。
    /// </para>
    /// </summary>
    public MainWindow()
        : this(new ControlClient(ControlClient.DefaultSocketPath()))
    {
    }

    public MainWindow(ControlClient client)
    {
        InitializeComponent();

        // 三个 ViewModel 与 Windows 端同构，只是数据来源换成了控制通道：
        // GUI 是守护进程的客户端，不持有连接、不碰系统代理。
        _bindViewModel = new BindViewModel(client);
        _mainViewModel = new MainViewModel(client);
        _settingsViewModel = new SettingsViewModel(client);

        _bindViewModel.BindCompleted += OnBindCompleted;
        _mainViewModel.SettingsRequested += OnSettingsRequested;
        _settingsViewModel.SettingsClosed += OnSettingsClosed;
        _mainViewModel.PropertyChanged += OnMainViewModelChanged;

        // 首页判断与 Windows 端同义（那边是 App.IsProbablyBound()：磁盘上有设备凭据就直接进主界面）。
        // Linux 上设备凭据归守护进程，GUI 只从状态快照里读「绑没绑」——冷启动第一帧还不知道，
        // 于是先画绑定页，第一次轮询回来说已绑定就立刻切过去。代价是最多一秒的绑定页闪现，
        // 换来的是 GUI 不必碰任何凭据文件。
        PageHost.Content = _bindViewModel;
        SyncLiveState();

        // 本机验收专用：MYPROXY_GUI_PAGE=bind|main|settings 直接落到指定页，
        // 便于在没有交互的环境（WSLg 截图、CI 冒烟）里逐页取证。它只改首页，
        // 之后的状态跟随与用户操作完全照常；正常用户不会设置这个变量。
        string? forced = Environment.GetEnvironmentVariable("MYPROXY_GUI_PAGE");
        if (!string.IsNullOrEmpty(forced))
        {
            PageHost.Content = forced.ToLowerInvariant() switch
            {
                "main" => _mainViewModel,
                "settings" => _settingsViewModel,
                _ => _bindViewModel
            };
        }

        // 用**显式两关键帧动画**而不是 Transitions：Transition<T> 是在属性变更的瞬间
        // 取「旧值 → 新值」插值，而切页是「先把 Content 换掉、同一帧里把不透明度归零」，
        // 两次赋值落在同一帧时过渡看到的起点已经是 1，会退化成 1→1（看不出淡入）。
        // MainView 的状态淡入用的是同一种写法，两处保持一致。
        //
        // 时长与缓动取自令牌（换肤会变），所以不能写进 XAML 的 Transitions 集合
        // （那里的 DynamicResource 在 Avalonia 11 里不保证解析）。

        // 窗口藏进托盘或最小化之后，呼吸动画与每秒一次的流量采样都没有观众——
        // 与 Windows 端同一套判断（那边要同时看 IsVisible 与 WindowState）。
        PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty || args.Property == WindowStateProperty)
            {
                SyncLiveState();
            }
        };

        // 本机验收钩子：设置了 MYPROXY_GUI_DUMP 才做事（见 DumpVisualTreeIfRequested）。
        // 排在 Loaded 之后：第一帧的布局那时已经算完，量到的才是用户看见的那一版。
        Loaded += (_, _) => DumpVisualTreeIfRequested();
    }

    /// <summary>托盘与装配层要用的宿主 ViewModel。</summary>
    public MainViewModel Model => _mainViewModel;

    /// <summary>绑定完成（装配层据此刷新托盘提示）。</summary>
    public event Action? BindCompleted;

    public bool IsExiting => _isExiting;

    public void PrepareExit()
    {
        _isExiting = true;
        SyncLiveState();
    }

    public Task FlushPendingSettingsAsync() => _settingsViewModel.FlushAsync();

    /// <summary>切回绑定页（首次运行，或用户「重新绑定」之后）。</summary>
    public void ShowBindView(string? errorMessage = null)
    {
        _bindViewModel.Reset(errorMessage);
        SetPage(_bindViewModel);
    }

    /// <summary>从主界面进设置页（主界面点「设置」与托盘菜单都会走这里）。</summary>
    public void ShowSettings() => OnSettingsRequested();

    private void OnMainViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.State))
        {
            return;
        }

        // 绑定页 ↔ 主界面的自动跟随：守护进程说「已绑定」而界面还停在绑定页时切过去；
        // 反过来（被解绑/重新绑定）时清空绑定页的错误状态留在原地。
        // 停在设置页时不动——切页由设置页自己的事件决定。
        // 本机验收时若用 MYPROXY_GUI_PAGE 指定了页面，这个自动跟随就不要再抢走它。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYPROXY_GUI_PAGE")))
        {
            return;
        }

        bool bound = _mainViewModel.State != AppState.Unbound;
        if (!bound && ReferenceEquals(PageHost.Content, _bindViewModel))
        {
            _bindViewModel.Reset();
            return;
        }

        if (bound && ReferenceEquals(PageHost.Content, _bindViewModel))
        {
            OnBindCompleted();
        }
    }

    /// <summary>
    /// 把「界面是否真的在给人看」同步给主页 ViewModel：不可见、已最小化、或当前不在主页，
    /// 都算没人看（此时不跑动画与流量采样）。判断条件与 Windows 端一致。
    /// </summary>
    private void SyncLiveState()
    {
        _mainViewModel.IsLive = !_isExiting
            && IsVisible
            && WindowState != WindowState.Minimized
            && ReferenceEquals(PageHost.Content, _mainViewModel);
        _mainViewModel.AnimationsEnabled = AnimationsAllowed();
    }

    /// <summary>
    /// 是否允许动效。Windows 端读 <c>SystemParameters.ClientAreaAnimation</c>；
    /// Linux 上没有那个统一开关，于是分两步：显式的环境变量优先（容器、CI 与
    /// 「我就是要关掉动画」的用户都用得上，也是本机验收时唯一可靠的开关），
    /// 否则按允许处理——这与多数 Linux 桌面程序的默认一致。
    /// 这个判断只影响动效，不影响任何状态、文案或网络行为。
    /// </summary>
    private static bool AnimationsAllowed()
    {
        try
        {
            string? flag = Environment.GetEnvironmentVariable("MYPROXY_DISABLE_ANIMATIONS");
            return string.IsNullOrEmpty(flag) || flag is "0" or "false" or "no";
        }
        catch (Exception)
        {
            return true;
        }
    }

    private TimeSpan DurationOf(string key, TimeSpan fallback)
        => this.TryFindResource(key, out object? value) && value is TimeSpan duration ? duration : fallback;

    /// <summary>
    /// 切页：换 Content → 同步活跃状态 → 淡入 → 把焦点送进新页。
    /// 淡入只在新页真的会被看见时发生（隐藏或最小化时切页不该留下一个半透明页面）。
    /// </summary>
    private void SetPage(object viewModel)
    {
        PageHost.Content = viewModel;
        SyncLiveState();

        bool animate = _mainViewModel.AnimationsEnabled
            && IsVisible
            && WindowState != WindowState.Minimized;

        if (animate)
        {
            _ = FadeInPageAsync();
        }
        else
        {
            // 不播时也要把 Opacity 留在基值上：清掉动画之后同样是基值 1。
            PageHost.Opacity = 1;
        }

        FocusPageContent();
    }

    /// <summary>切页淡入。失败只记一条——界面照旧，绝不能因为动画播不出来就不切页。</summary>
    private async Task FadeInPageAsync()
    {
        var fade = new Animation
        {
            Duration = DurationOf("Motion.Duration.Base", TimeSpan.FromMilliseconds(240)),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            },
        };

        try
        {
            await fade.RunAsync(PageHost);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"切页淡入失败：{ex}");
        }
        finally
        {
            // 动画结束后把值显式留在基值上（FillMode.None 会把属性交还给样式系统）。
            PageHost.Opacity = 1;
        }
    }

    private void FocusPageContent()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (PageHost.Content is not Control page)
                {
                    return;
                }

                foreach (Visual visual in page.GetVisualDescendants())
                {
                    if (visual is InputElement { Focusable: true } element && element.IsEffectivelyVisible)
                    {
                        element.Focus();
                        return;
                    }
                }

                page.Focus();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 本机验收专用：把窗口的视觉树（类型 + x:Name + 位置尺寸 + 缩放/不透明度）打印到
    /// <c>MYPROXY_GUI_DUMP</c> 指定的文件。<b>只在设置了该环境变量时生效</b>，
    /// 正常用户路径一行都不做。
    ///
    /// <para>
    /// 存在的理由：版面缺陷只有两种查法——量像素，或者量控件的 Bounds。像素只能看见
    /// 「哪里不对」，量 Bounds 才看得出「是谁算错的」（例如模板里某个 Border 的
    /// 显式 Width 没生效，还是外层容器就没被约束住）。
    /// </para>
    /// </summary>
    private void DumpVisualTreeIfRequested()
    {
        string? path = Environment.GetEnvironmentVariable("MYPROXY_GUI_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    string probe = string.Empty;
                    foreach (Visual candidate in this.GetVisualDescendants())
                    {
                        if (candidate is RadioButton radio)
                        {
                            var chain = new List<string>();
                            for (Visual? v = radio; v is not null && chain.Count < 8; v = v.GetVisualParent())
                            {
                                chain.Add(v.GetType().Name);
                            }

                            probe += $"probe RadioButton \"{radio.Content}\" theme={Describe(radio)} " +
                                     $"firstChild={radio.GetVisualChildren().FirstOrDefault()?.GetType().Name ?? "null"} " +
                                     $"chain={string.Join('<', chain)}\n";
                        }
                    }

                    var lines = new List<string>
                    {
                        $"window {Bounds.Width:0.##}x{Bounds.Height:0.##}",
                        $"clientSize {ClientSize.Width:0.##}x{ClientSize.Height:0.##}",
                        probe.TrimEnd(),
                    };
                    AppendVisual(lines, PageHost, 0);
                    File.WriteAllLines(path, lines);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"视觉树转储失败：{ex}");
                }
            },
            DispatcherPriority.Background);
    }

    private static string Describe(Control control)
        => control.Theme is { } theme
            ? $"{theme} target={theme.TargetType?.Name} setters={theme.Setters.Count}"
            : "(null)";

    private static void AppendVisual(List<string> lines, Visual visual, int depth)
    {
        string indent = new(' ', depth * 2);
        string name = visual is Control { Name: { Length: > 0 } n } ? $" name={n}" : string.Empty;
        string classes = visual is Control { Classes.Count: > 0 } c
            ? $" classes={string.Join('|', c.Classes)}"
            : string.Empty;
        string text = visual switch
        {
            TextBlock { Text: { Length: > 0 } t } => $" text=\"{t}\"",
            Button { Content: string s } => $" content=\"{s}\"",
            _ => string.Empty,
        };
        string scale = visual is Control { RenderTransform: { } transform } ? $" render={transform.Value}" : string.Empty;
        string opacity = visual is Control { Opacity: < 1 } dim ? $" opacity={dim.Opacity:0.##}" : string.Empty;
        string theme = visual is Control { Theme: { } controlTheme } ? $" theme#{controlTheme.TargetType?.Name}" : string.Empty;

        lines.Add(
            $"{indent}{visual.GetType().Name}{name}{classes} " +
            $"[{visual.Bounds.X:0.##},{visual.Bounds.Y:0.##} {visual.Bounds.Width:0.##}x{visual.Bounds.Height:0.##}]" +
            $"{text}{theme}{scale}{opacity}");

        foreach (Visual child in visual.GetVisualChildren())
        {
            AppendVisual(lines, child, depth + 1);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 点关闭不是退出：收进托盘，连接继续由守护进程持有（与 Windows 端一致）。
        // 真正的退出走托盘菜单，那时 IsExiting 已置位（PrepareExit）。
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnBindCompleted()
    {
        SetPage(_mainViewModel);
        BindCompleted?.Invoke();
    }

    private void OnSettingsRequested()
    {
        // 这个 ViewModel 构造一次长期复用，设备身份必须每次进页面重读：
        // 首次运行时它建好的那一刻还没绑定，绑完回到本页也不会自己更新。
        _settingsViewModel.RefreshDevice();
        SetPage(_settingsViewModel);
    }

    private void OnSettingsClosed()
    {
        // 「重新绑定」之后守护进程会回到未绑定：这时该回绑定页，而不是主界面。
        if (_mainViewModel.State == AppState.Unbound)
        {
            ShowBindView();
            return;
        }

        SetPage(_mainViewModel);
    }
}
