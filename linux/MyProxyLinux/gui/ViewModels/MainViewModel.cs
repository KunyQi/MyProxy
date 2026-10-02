using System.Diagnostics;
using Avalonia.Threading;
using MyProxy.Controls;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

// 命名空间与 gui/MainWindow.axaml.cs、gui/App.axaml 的 vm: 前缀一致
// （MyProxy.Gui.ViewModels），而不是 Windows 端的 MyProxy.ViewModels：
// 这个工程的 RootNamespace 是 MyProxy.Gui。
namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 主页的状态与动作。成员名、文案、可见性互斥优先级、命令可用性规则逐条照搬
/// <c>windows/MyProxy/ViewModels/MainViewModel.cs</c>；**唯一的结构性差别是数据从哪来**。
///
/// <para>
/// <b>本类不认识连接。</b>没有 <c>IConnectionController</c>、没有内核、没有系统代理、
/// 没有存储，只有一个 <see cref="ControlClient"/>：每个动作是一条控制命令，每一格状态
/// 来自每秒一次的 <c>status</c> 快照。这条边界不是洁癖——Linux 上连接的唯一持有者是
/// 守护进程（<c>myproxy run</c>），GUI 自己拉一份连接就必然出现系统代理、心跳与
/// LastKnownGood 各改一半的局面，而那正是这套控制通道存在的理由。
/// </para>
///
/// <para>
/// <b>哪些与 Windows 端逐字相同：</b>状态文案表（<see cref="StatusText"/> /
/// <see cref="StatusSubText"/> 与 <see cref="ButtonText"/>）、四槽共用预留行的可见性互斥链
/// （切换中 &gt; 切换失败 &gt; 自检结论 &gt; 延迟，只由 <see cref="LineSlot"/> 一处实现）、
/// <see cref="IsButtonEnabled"/> / <see cref="IsModeSelectionEnabled"/> /
/// <see cref="IsCheckEnabled"/> 的规则、自检结论行与严重级、速率文案的单位与位数、
/// <see cref="CanAnimate"/>（= <see cref="IsLive"/> &amp;&amp; <see cref="AnimationsEnabled"/>）、
/// 以及「连上后立刻自检一次、之后每 120 秒一次」的节拍。
/// </para>
///
/// <para>
/// <b>因为架构不同而必须不同的地方（都在成员注释里就地写明）：</b>
/// </para>
/// <list type="number">
/// <item><b>后台状态不可用</b>（Windows 端不存在这个状态）：<see cref="StatusText"/> 说明
/// 连接状态未知或后台不可用，<see cref="StatusSubText"/> 给出重试线索；已知的设备与模式
/// <b>不清空</b>——抹成默认值会让用户以为设置丢了。</item>
/// <item><b>分流模式被拒</b>：守护进程在 <c>Unbound/Connecting/Disconnecting/Error</c>
/// 下会明确拒绝切换（共享控制器对那几种状态是静默返回），拒绝的那句话由本类原样显示到
/// <see cref="SwitchErrorMessage"/>。Windows 端没有这条路径——它的模式选择在非
/// <c>Connected</c> 时是禁用的，用户根本点不到。</item>
/// <item><b><see cref="IsSwitching"/></b>：快照里没有这个字段，于是由「本机正在等一条
/// <c>mode</c> 命令的回应」推导——那是唯一能发起切换的入口。</item>
/// <item><b><see cref="TrafficSamples"/></b>：快照只带瞬时速率（<c>uplinkBytesPerSecond</c> /
/// <c>downlinkBytesPerSecond</c>），没有历史通道，所以环形缓冲由本类按每次轮询一拍重建，
/// 容量取共享的 <c>TrafficRateMonitor.DefaultCapacity</c>（60 拍 ≈ 最近一分钟，与
/// Windows 端 Sparkline 的横轴同长）。</item>
/// <item><b>流量采样挂不起来</b>：控制通道里没有 <c>SetTrafficSamplingSuspended</c> 的
/// 对应命令，守护进程照常采它的样。<see cref="IsLive"/> 只让<b>本类</b>停止把新样本推进
/// 波形，并在恢复可见时清空历史——对用户而言与 Windows 端一致（不可见时不累积、
/// 回来时从空波形重新长）。</item>
/// </list>
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>
    /// 轮询间隔。守护进程不在时也要照常走：界面既靠它发现「后台起来了」，
    /// 也靠它从「后台没了」里恢复过来。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 单次 <c>status</c> 的超时。ControlClient 默认给 60 秒（那是留给 <c>check</c>、
    /// <c>start</c> 这类长命令的），一次状态查询卡满 60 秒会让界面整整一分钟不刷新，
    /// 看起来像死了。
    /// </summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(8);

    /// <summary>连接保持期间的自检间隔，与 Windows 端逐字相同。</summary>
    private static readonly TimeSpan ConnectionCheckInterval = TimeSpan.FromSeconds(120);

    /// <summary>Sparkline 的横轴长度，取自共享的 <c>TrafficRateMonitor</c>。</summary>
    private const int TrafficSampleCapacity = TrafficRateMonitor.DefaultCapacity;

    private readonly ControlClient _client;

    // ---- 最近一次快照铺开的状态（每一格都来自 StatusSnapshot 的同名字段）------------

    /// <summary>收到过至少一次回应。没有回应之前，界面只能说「正在查询」。</summary>
    private bool _statusKnown;

    private AppState _state = AppState.Disconnected;

    /// <summary>界面正在显示的档位。切换过程中会先乐观地移过去，失败再滑回真实值。</summary>
    private ProxyMode _mode = ProxyMode.Rule;

    /// <summary>守护进程报告的真实档位。它是 <see cref="_mode"/> 的对账基准。</summary>
    private ProxyMode _daemonMode = ProxyMode.Rule;

    private UiTheme _theme = UiTheme.Classic;

    /// <summary>快照里读到过皮肤。<see cref="Theme"/> 的通知只在它为真之后才发。</summary>
    private bool _themeKnown;

    private int? _latencyMs;
    private string _lastErrorMessage = "";
    private string _daemonSwitchErrorMessage = "";
    private bool _serverIdentityUnverified;
    private bool _daemonIsChecking;
    private bool _hasCheck;
    private bool _checkReachable;
    private int? _checkLatencyMs;
    private EgressVerdict _checkEgress = EgressVerdict.Unknown;

    // 快照里另外三项（checkBestLatencyMs / checkSampleCount / checkError）**刻意不映射**：
    // Windows 端主页没有展示它们的位置（「最快 38 ms · 3 次采样」那种细节行是旧 Linux
    // 界面自己的发明，不在移植面上）。多存三个没人读的字段只会让人以为界面少了什么。
    private double _uplinkBytesPerSecond;
    private double _downlinkBytesPerSecond;

    // ---- Linux 独有的状态（Windows 端没有对应物）----------------------------------

    /// <summary>
    /// 最近一次状态查询没有有效快照时的状态说明。Windows 端控制器就在本进程里，
    /// 不存在「连接状态的持有者不可查询」这一说。
    /// </summary>
    private readonly DaemonAvailabilityState _daemonAvailability = new();

    /// <summary>
    /// 上一次分流模式切换被守护进程明确拒绝时它给的那句话。守护进程自己的
    /// <c>lastSwitchErrorMessage</c> 优先（那是真的切换失败并回滚过的原因），
    /// 这一格是它的补充：被拒绝时快照里什么都没有，不留这一句就等于点了没反应。
    /// </summary>
    private string _modeSwitchCommandError = "";

    private bool _isTrayAvailable;
    private string _trayDetailReason = "";

    // ---- 界面侧的交互态 -----------------------------------------------------------

    private bool _isLive = true;

    /// <summary>
    /// 是否允许动效。Windows 端的出厂值来自 <c>SystemParameters.ClientAreaAnimation</c>；
    /// Linux 上没有那个统一开关，**可注入的开关就是本属性**：外壳
    /// （<c>MainWindow.SyncLiveState</c>）按桌面偏好（环境变量
    /// <c>MYPROXY_DISABLE_ANIMATIONS</c>）算好之后写进来，本类只把它与
    /// <see cref="IsLive"/> 相乘——与 Windows 端的 <c>CanAnimate</c> 同一个定义。
    /// 测试要断言「转圈可见」时必须显式置 true，否则断言只在开了动效的机器上成立
    /// （Windows 端那条测试踩过同一个坑）。
    /// </summary>
    private bool _animationsEnabled = true;

    /// <summary>自检请求正在途中（本机发起的那一次）。与守护进程的 <c>isChecking</c> 取或。</summary>
    private bool _checkRequestInFlight;

    /// <summary>本机正在等一条 <c>mode</c> 命令的回应，见 <see cref="IsSwitching"/>。</summary>
    private bool _isModeSwitchInFlight;

    private bool _isPolling;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource _pollShutdownCts = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _connectionCheckTimer;

    /// <summary>上一拍看到的是不是「已连接」，用来发现「刚连上」这个边沿。</summary>
    private bool _wasConnected;

    /// <summary>已经排进 UI 队列的那次「连上后立刻自检」的代号，用来作废它。</summary>
    private int _immediateCheckGeneration;

    // ---- 流量波形（本类自己维护的环形缓冲，见 TrafficSamples 的注释）----------------

    private readonly Queue<TrafficRate> _samples = new(TrafficSampleCapacity);

    /// <summary>交给视图的那一份数组快照。只有样本真的变了才换新实例，见 RefreshProperties。</summary>
    private TrafficRate[] _samplesView = [];

    // ---- 通知去重 ----------------------------------------------------------------

    /// <summary>
    /// 上一次报给视图的属性值。<b>每秒一次的轮询里只有值真的变了才发通知</b>：
    /// 无脑 OnPropertyChanged 会让整页（包括用户正在输入的框）每秒重建一次绑定，
    /// 看起来像在闪。派生值太多、彼此交叉（四个槽位可见性就是典型），逐处置标志位
    /// 迟早会漏；这里统一按「值有没有变」判断，顺带保证不会出现
    /// 「文案说失败、可见性却是另一个槽位」这种自相矛盾的组合。
    /// </summary>
    private readonly Dictionary<string, object?> _notified = new(StringComparer.Ordinal);

    public MainViewModel(ControlClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        PrimaryCommand = new RelayCommand(ExecutePrimaryAsync, () => IsButtonEnabled);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        CheckCommand = new RelayCommand(ExecuteCheckAsync, () => IsCheckEnabled);

        // 与 Windows 端一样，构造完先铺一次：视图拿到 DataContext 时每一格都已经有值，
        // 不必等第一拍轮询。
        RefreshProperties();
    }

    /// <summary>主页点了「设置」。外壳据此切页（与 Windows 端同一个语义）。</summary>
    public event Action? SettingsRequested;

    // ---- 状态行 -------------------------------------------------------------------

    public AppState State => _daemonAvailability.EffectiveState(_state);

    /// <summary>
    /// 状态行。取值表与 Windows 端逐字相同，**只有一处例外**：后台没有提供有效状态时
    /// 显示不可用或未知，而不是拿上一次快照去猜——连接归后台持有，GUI 无法确认它仍然存在。
    /// </summary>
    public string StatusText => IsDaemonUnreachable
        ? _daemonAvailability.StatusText
        : State switch
        {
            AppState.Connected => "已连接",
            AppState.Connecting => "正在连接…",
            AppState.Disconnecting => "正在断开…",
            AppState.Error => "连接失败",
            AppState.Unbound => "未绑定",
            _ => "未连接"
        };

    /// <summary>
    /// 状态行下面的补充说明。已连接时的取值与 Windows 端逐字相同；Linux 多出两段
    /// 前置判断：还没拿到过任何回应（进程内没有控制器，第一拍回来之前确实不知道），
    /// 以及后台没有提供有效状态。
    /// </summary>
    public string StatusSubText
    {
        get
        {
            if (IsDaemonUnreachable)
            {
                return _daemonAvailability.Hint;
            }

            if (!_statusKnown)
            {
                // Linux 独有：Windows 端的控制器就在进程里，没有「还不知道」这一格。
                return "正在查询后台服务…";
            }

            return State switch
            {
                // 自检判定失败时让位给结论行：一边写「网络连接正常」一边写「连接失败」，
                // 用户只会更没底。
                // 服务器证书校验失败：连接还能用，但收不到配置更新与吊销，不能再说「正常」。
                AppState.Connected when _serverIdentityUnverified =>
                    ErrorCodeMessages.ServerIdentityUnverifiedWhileConnected,
                AppState.Connected => CheckSeverity == CheckSeverity.Bad ? "" : "网络连接正常",
                AppState.Error => _lastErrorMessage,
                _ => ""
            };
        }
    }

    /// <summary>
    /// 补充说明是否显示。Avalonia 的可见性是布尔的 <c>IsVisible</c>（WPF 的
    /// <c>Visibility</c> 没有对应物）：<c>Visible/Collapsed</c> 这一对正好一一对应，
    /// <c>Hidden</c> 不是——见 <see cref="CheckButtonVisibility"/> 的注释。
    /// </summary>
    public bool StatusSubTextVisibility => StatusSubText.Length > 0;

    /// <summary>
    /// 后台服务是否没有提供有效状态。Linux 独有的状态：Windows 端的控制器就在进程里，
    /// 不存在「无法查询连接状态的持有者」这一格。
    ///
    /// <para>
    /// 界面与托盘用它停止显示过期状态；能量球不进呼吸/脉冲（<c>still</c> 类），
    /// 状态行和托盘会明确报告未知/不可用。<b>不要</b>用它去禁用主按钮——见
    /// <see cref="IsButtonEnabled"/> 的注释。
    /// </para>
    /// </summary>
    public bool IsDaemonUnreachable => _daemonAvailability.IsUnavailable;

    /// <summary>
    /// 流量 band 的不透明度。**这是 WPF <c>Visibility.Hidden</c> 占位语义的等价物的一半**
    /// （另一半是 <c>LayoutStyles.axaml</c> 里固定 66 高的 <c>TrafficBandStyle</c>）：
    /// Avalonia 只有布尔的 <c>IsVisible</c>，等于 WPF 的 <c>Collapsed</c>，直接照搬绑定会让
    /// 这一格塌成 0 高，多出来的 66px 被六个等权 gap 平分，整页 band 的纵向坐标全变。
    /// 隐藏时改透明度，槽高由样式钉住，于是版面一个像素都不动。
    /// </summary>
    public double TrafficOpacity => TrafficVisibility ? 1d : 0d;

    /// <summary>自检按钮的不透明度。理由同 <see cref="TrafficOpacity"/>，槽高 40 由样式钉住。</summary>
    public double CheckButtonOpacity => CheckButtonVisibility ? 1d : 0d;

    public string ButtonText => State switch
    {
        AppState.Connected => "停止",
        AppState.Connecting => "正在启动",
        AppState.Disconnecting => "正在停止",
        AppState.Error => "重试",
        _ => "启动"
    };

    /// <summary>
    /// 主按钮可点性，规则照搬 Windows 端：只有「已断开」与「失败」能发起连接、「已连接」
    /// 能断开；<c>Connected</c> 时还要没有在切换。未绑定、连接中、断开中一律不可点。
    ///
    /// <para>
    /// 这里<b>不</b>因为「守护进程不在」而禁用：GUI 不自己拉起后台服务，却也不该替用户
    /// 决定他想不想点。点下去会得到一条明确的失败（ControlClient 的「MyProxy 没有在运行」），
    /// 比一个灰着的按钮更能说明发生了什么；而状态行本来就已经在说同样的话。
    /// </para>
    /// </summary>
    public bool IsButtonEnabled => State switch
    {
        AppState.Disconnected or AppState.Error => true,
        AppState.Connected => !IsSwitching,
        _ => false
    };

    /// <summary>模式开关可点性：Windows 端是「仅已连接」，且在切换中禁用。</summary>
    public bool IsModeSelectionEnabled => State == AppState.Connected && !IsSwitching;

    /// <summary>
    /// 正在切换分流模式。
    ///
    /// <para>
    /// <b>Linux 独有的一处推导</b>：<c>StatusSnapshot</c> 里没有 <c>isSwitching</c> 字段，
    /// 所以这里回答的是「本机正在等一条 <c>mode</c> 命令的回应」。它就是切换动作的
    /// 全部入口（界面把开关禁用到连命令都发不出去），而守护进程的那次切换要重启内核
    /// 并重探端口，命令回来时它必然已经结束——于是这个近似与 Windows 端的
    /// <c>_controller.IsSwitching</c> 在用户看得见的范围内等价。
    /// </para>
    /// </summary>
    public bool IsSwitching => _daemonAvailability.ShouldShowInFlightActivity(_isModeSwitchInFlight);

    public bool IsSwitchingTextVisible => LineSlotOwner == LineSlot.Switching;

    /// <summary>
    /// 连接中的转圈是否可见。与 Windows 端同义：只在连接中、且允许动效时转。
    /// 名字里保留 <c>Visibility</c> 是为了不改视图绑定（视图侧已按
    /// <c>IsVisible="{Binding ConnectingSpinnerVisibility}"</c> 写）。
    /// </summary>
    public bool ConnectingSpinnerVisibility => State == AppState.Connecting && CanAnimate;

    public ProxyMode Mode => _mode;

    /// <summary>
    /// 模式滑块的两半。<b>先乐观移动、再对账</b>：Windows 端也是先 <c>Mode = ...</c>
    /// 再发命令，失败时由控制器的真实模式把它拨回去。这里的对账基准是最近一次快照里的
    /// <c>mode</c>，所以被拒绝时滑块会立刻弹回守护进程仍在用的那一档。
    /// </summary>
    public bool IsRuleMode
    {
        get => Mode == ProxyMode.Rule;
        set
        {
            if (value && Mode != ProxyMode.Rule)
            {
                RequestModeSwitch(ProxyMode.Rule);
            }
        }
    }

    public bool IsGlobalMode
    {
        get => Mode == ProxyMode.Global;
        set
        {
            if (value && Mode != ProxyMode.Global)
            {
                RequestModeSwitch(ProxyMode.Global);
            }
        }
    }

    /// <summary>
    /// 模式切换失败的原因。与「正在切换…」/ 自检结论 / 延迟共用 MainView 的同一预留槽，
    /// 优先级由 <see cref="LineSlotOwner"/> 一处保证。
    ///
    /// <para>
    /// 取值优先级：守护进程快照里的 <c>lastSwitchErrorMessage</c>（真的切换失败并回滚过，
    /// 与 Windows 端同源）优先；它为空时才用 <see cref="_modeSwitchCommandError"/>——
    /// 即守护进程**明确拒绝**这条命令时给的那句话。后者是 Linux 独有的：
    /// 被拒绝时响应里没有快照，不把这句话留下就是「点了没反应」。
    /// </para>
    /// </summary>
    public string SwitchErrorMessage
        => _daemonSwitchErrorMessage.Length > 0 ? _daemonSwitchErrorMessage : _modeSwitchCommandError;

    public bool SwitchErrorVisibility => LineSlotOwner == LineSlot.SwitchError;

    public int? LatencyMs => _latencyMs;

    public string LatencyText => LatencyMs is int ms ? $"延迟 {ms} ms" : "";

    /// <summary>
    /// 连接时自动量到的那一次延迟。一旦用户主动检测过，实测结论更新更准，这一行让位
    /// （四者共用同一预留槽，优先级：切换中 &gt; 切换失败 &gt; 自检结论 &gt; 自动延迟）。
    /// </summary>
    public bool LatencyTextVisibility => LineSlotOwner == LineSlot.Latency;

    /// <summary>
    /// 主页是否真的在给人看：退出、切页、最小化或隐藏时为 false。
    ///
    /// <para>
    /// 与 Windows 端的差别只有一处、而且不可见：那边这一格还会调
    /// <c>_controller.SetTrafficSamplingSuspended</c>，让采样线程真的停下来；
    /// 控制通道里没有这条命令，守护进程照常采它的样。本类能做的是同一件事的
    /// 界面侧：不可见时不把新样本推进波形，恢复可见时清空历史（守护进程的计数器在
    /// 这段时间里一直在涨，留着旧波形会把整段挂起时间画成一根假尖峰——
    /// 这正是 Windows 端 <c>SetTrafficSamplingSuspended(false)</c> 要 Reset 的理由）。
    /// </para>
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;

            if (value)
            {
                ClearTrafficSamples();
            }

            // 通知统一由 RefreshProperties 的「值变了才发」发出（CanAnimate 与
            // ConnectingSpinnerVisibility 也挂在它后面），这里不自己再发一次，
            // 否则同一个属性会被连报两遍。
            RefreshProperties();
        }
    }

    /// <summary>是否允许动效。见 <see cref="_animationsEnabled"/> 的注释（可注入开关）。</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (_animationsEnabled == value)
            {
                return;
            }

            _animationsEnabled = value;
            RefreshProperties();
        }
    }

    /// <summary>循环动画的总闸，与 Windows 端同义。</summary>
    public bool CanAnimate => IsLive && AnimationsEnabled;

    public bool IsChecking => _daemonAvailability.ShouldShowInFlightActivity(
        _checkRequestInFlight || _daemonIsChecking);

    /// <summary>自检只在连接稳定时有意义：切换中链路正在重建，断开时根本没有链路可测。</summary>
    public bool IsCheckEnabled => State == AppState.Connected && !IsSwitching && !IsChecking;

    public string CheckButtonText => IsChecking ? "正在检测…" : "检测连接";

    public string CheckResultText
    {
        get
        {
            if (IsDaemonUnreachable || State != AppState.Connected)
            {
                return "";
            }

            if (IsChecking)
            {
                return "正在检测…";
            }

            if (!_hasCheck)
            {
                return "";
            }

            if (!_checkReachable)
            {
                return "检测未通过，请重试";
            }

            // 只有独立证据确认绕过时才报失败；入口与回显地址不同会保持 Unknown。
            if (_checkEgress == EgressVerdict.Bypassed)
            {
                return "连接失败，请重试";
            }

            // 「已验证」比「已连通」多的那一层意思，正是出口核对过了。
            // 两者只差一个字，语义强弱却对得上，比写全「已确认走服务器」短得多。
            string latency = _checkLatencyMs is int ms ? $"{ms} ms" : "延迟未知";
            return _checkEgress == EgressVerdict.Verified
                ? $"已验证 · {latency}"
                : $"已连通 · {latency}";
        }
    }

    public CheckSeverity CheckSeverity
    {
        get
        {
            if (IsDaemonUnreachable || State != AppState.Connected || (!_hasCheck && !IsChecking))
            {
                return CheckSeverity.None;
            }

            if (IsChecking)
            {
                return CheckSeverity.Running;
            }

            return _checkReachable && _checkEgress != EgressVerdict.Bypassed
                ? CheckSeverity.Good
                : CheckSeverity.Bad;
        }
    }

    public bool CheckResultVisibility => LineSlotOwner == LineSlot.CheckResult;

    /// <summary>
    /// 常显。未绑定时不画，但**那一格仍然占位**：Windows 端这里返回 <c>Hidden</c>
    /// （不画、但保留 40px 高度），64px 的自检 band 因此永远是 64px。
    /// Avalonia 只有布尔的 <c>IsVisible</c>，false 等于 WPF 的 <c>Collapsed</c>——
    /// 会连高度一起收掉，六条等权 <c>*</c> 间隙会各长 6.7px，整页跟着位移。
    /// 所以视图侧必须按 Windows 端一样把那一格写死（把 <c>IsVisible</c> 放在 band 内的
    /// 内容上，band 自身保持 <c>Size.CheckBand</c> 的高度），本类只回答「画不画」。
    /// </summary>
    public bool CheckButtonVisibility => State != AppState.Unbound;

    /// <summary>
    /// 最近若干拍的上下行速率，从旧到新，供 Sparkline 绘制。
    ///
    /// <para>
    /// <b>这个环形缓冲是 Linux 独有的。</b>快照只带瞬时的
    /// <c>uplinkBytesPerSecond</c> / <c>downlinkBytesPerSecond</c>，历史没有通道可拿，
    /// 所以本类每次轮询推进一拍，容量取 Windows 端 Sparkline 的横轴长度
    /// （<c>TrafficRateMonitor.DefaultCapacity</c> = 60）。样本不足时不补零，
    /// 由绘制方决定怎么对齐——与共享的 <c>TrafficRateMonitor</c> 同一条规矩。
    /// </para>
    /// </summary>
    public IReadOnlyList<TrafficRate> TrafficSamples => _samplesView;

    /// <summary>
    /// 只有数值。方向由 MainView 里的矢量箭头表达，不再往文本里塞 ↑↓ 字符。
    /// 单位与保留位数取自 <c>TrafficSparkline.FormatRate</c>——与 Windows 端同一个来源
    /// （那边也是让 ViewModel 调控件的静态格式化方法），不在这里抄第二份规则。
    /// </summary>
    public string UplinkText => TrafficSparkline.FormatRate(_uplinkBytesPerSecond);

    public string DownlinkText => TrafficSparkline.FormatRate(_downlinkBytesPerSecond);

    /// <summary>
    /// 除未绑定外常显。断开时波形与数字都归零（守护进程那边共享控制器已经 Reset 过
    /// 监视器），图上只剩一条基线——安静且诚实。
    ///
    /// <para>
    /// 不整段隐藏，是因为隐藏会在状态区与模式开关之间留下一块 66px 的空当，
    /// 在这套居中对称的版面里会被读成「这里漏了什么」，而不是留白。与
    /// <see cref="CheckButtonVisibility"/> 同一个坑：Windows 端的 <c>Hidden</c>
    /// 保留 66px，Avalonia 的 <c>IsVisible=false</c> 不保留，视图侧要把这一格写死。
    /// </para>
    /// </summary>
    public bool TrafficVisibility => State != AppState.Unbound;

    /// <summary>
    /// 守护进程当前持有的皮肤（快照的 <c>theme</c> 字段）。
    ///
    /// <para>
    /// <b>本类只如实报告，不碰资源字典</b>：换肤是全进程只有一处能做的事
    /// （<c>AvaloniaThemeService</c>），订阅者按这个属性的变化去施加。
    /// 第一个快照回来之前不发通知——那之前的值是出厂默认，报出去只会让外壳
    /// 拿一个还没读到的值去换肤。
    /// </para>
    /// </summary>
    public UiTheme Theme => _theme;

    // ---- 托盘（外壳要用；这两格是 Linux 端特有的，Windows 端托盘没有「挂不上」这条路）----

    /// <summary>托盘是否挂上了。false 时窗口关闭就是退出（没有地方能再把它叫回来）。</summary>
    public bool IsTrayAvailable => _isTrayAvailable;

    /// <summary>
    /// 页脚那行托盘说明。<b>它必须把「关掉窗口会发生什么」说清楚</b>：
    /// 托盘在时关闭只是收起来（连接照常在后台跑），托盘不在时关闭就是退出程序——
    /// 两种情况的后果完全不同，而用户看不见托盘在不在。
    /// </summary>
    public string TrayDetailText => _isTrayAvailable
        ? "关闭窗口不会断开连接，也不会退出程序；需要退出请用托盘菜单。"
        : $"当前桌面没有可用的托盘{_trayDetailReason}，关闭窗口就是退出程序；"
          + "需要断开连接请执行 systemctl --user stop myproxy.service。";

    /// <summary>这行说明有没有内容。两种情况下都有，保留它是为了绑定的形状统一。</summary>
    public bool HasTrayDetail => TrayDetailText.Length > 0;

    // ---- 命令 --------------------------------------------------------------------

    public RelayCommand PrimaryCommand { get; }

    public RelayCommand SettingsCommand { get; }

    public RelayCommand CheckCommand { get; }

    // ---- 生命周期 ----------------------------------------------------------------

    /// <summary>
    /// 开始轮询。先探一次再起定时器：用户双击图标到看见状态之间不该有一秒的空白。
    /// 重复调用是安全的（外壳在装配时调一次）。
    /// </summary>
    public void StartPolling()
    {
        if (_pollShutdownCts.IsCancellationRequested)
        {
            _pollShutdownCts.Dispose();
            _pollShutdownCts = new CancellationTokenSource();
        }

        PollNow();
        StartUiTimer(ref _pollTimer, PollInterval, PollNow);
    }

    /// <summary>
    /// 停止轮询并取消在途的那一拍。<b>顺序是刻意的</b>：先掐掉定时器，再取消请求——
    /// 反过来的话，刚起来的下一拍会在收尾过程中落到属性上。
    /// </summary>
    public void StopPolling()
    {
        StopUiTimer(ref _pollTimer);
        StopUiTimer(ref _connectionCheckTimer);
        CancelScheduledImmediateCheck();

        try
        {
            _pollShutdownCts.Cancel();
            _pollCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 那一拍刚好在收尾里把 CTS 释放掉了：它的结果已经不会被写回界面，
            // 这里没有别的要做。无头宿主里这个交错是可能的（收尾不一定在界面线程上）。
        }
    }

    /// <summary>
    /// 绑定完成后立刻拉一次状态，别等下一秒——绑完还停在绑定页上一秒，用户会以为没成功。
    /// </summary>
    public void RefreshAfterBinding() => PollNow();

    /// <summary>托盘可用性。外壳在挂托盘那一步调它（挂不上时把原因一并带过来）。</summary>
    public void SetTrayAvailable(bool available, string detail = "")
    {
        _isTrayAvailable = available;
        _trayDetailReason = available ? "" : detail;
        RefreshProperties();
    }

    // ---- 内部：轮询 -----------------------------------------------------------------

    private void PollNow()
    {
        if (_isPolling)
        {
            // 上一次还没回来（守护进程卡住、或者它在跑一条几十秒的长命令）。
            // 跳过这一拍而不是排队：排下去只会在它回来后同时涌出十几个请求。
            return;
        }

        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        _isPolling = true;

        CancellationToken shutdownToken = _pollShutdownCts.Token;
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(PollTimeout);
        _pollCts = timeout;

        try
        {
            // ControlClient.Connect 内部用 Task.Wait 等连接（上限 3 秒），在界面线程上直接
            // 调用会让窗口在那 3 秒里僵住；轮询每秒都跑，暴露面最大，所以这一条路挪到
            // 线程池上，await 之后再回到界面线程（ConfigureAwait(true)，与设置页、绑定页
            // 的写法一致）。
            ControlResponse response = await Task
                .Run(() => _client.SendAsync(ControlCommands.Status, "", timeout.Token), timeout.Token)
                .ConfigureAwait(true);

            if (shutdownToken.IsCancellationRequested)
            {
                // StopPolling 刚取消了这一拍：结果已经没有意义，不要写回界面。
                return;
            }

            ApplyResponse(response, pushSample: true);
        }
        catch (OperationCanceledException)
        {
            if (PollStatusFailurePolicy.ClassifyCancellation(shutdownToken.IsCancellationRequested)
                == PollCancellationDisposition.MarkUnavailableAsTimeout)
            {
                ApplyUnavailable(PollStatusFailurePolicy.Timeout());
            }
        }
        catch (Exception ex)
        {
            // 控制通道的常见异常会由 ControlClient 转成失败响应；意外异常同样意味着
            // 这一拍没有有效状态，不能继续展示过期的连接与验证信息。
            Trace.WriteLine($"状态轮询失败：{ex}");
            if (!shutdownToken.IsCancellationRequested)
            {
                ApplyUnavailable(PollStatusFailurePolicy.UnexpectedFailure());
            }
        }
        finally
        {
            if (ReferenceEquals(_pollCts, timeout))
            {
                _pollCts = null;
            }

            timeout.Dispose();
            _isPolling = false;
        }
    }

    /// <summary>
    /// 把一条响应落到界面上。没有有效状态时保留设备与模式，避免让用户以为设置丢了；
    /// 当前连接与验证信息则清掉，避免把旧快照当成现状。
    /// </summary>
    private void ApplyResponse(ControlResponse response, bool pushSample)
    {
        if (response.Ok && response.Status is not null)
        {
            ApplySnapshot(response.Status, pushSample);
            return;
        }

        ApplyUnavailable(PollStatusFailurePolicy.Response(response.ErrorCode, Describe(response)));
    }

    private void ApplyUnavailable(DaemonUnavailablePresentation presentation)
    {
        _statusKnown = true;
        _daemonAvailability.MarkUnavailable(presentation);

        // 这些值都来自上一次快照。后台失联后保留它们会把旧流量或旧验证结果说成当前事实。
        _latencyMs = null;
        _serverIdentityUnverified = false;
        _daemonIsChecking = false;
        _hasCheck = false;
        _checkReachable = false;
        _checkLatencyMs = null;
        _checkEgress = EgressVerdict.Unknown;
        _daemonSwitchErrorMessage = "";
        _modeSwitchCommandError = "";

        // 速率是守护进程报的瞬时值，它不在了就没有新的了。把上一拍的数字留在屏幕上
        // 会被读成「还在跑流量」——那是假数据，清掉（数字归零、波形清空）。
        ClearTrafficSamples();

        // 状态未知时不排自检：那会在守护进程回来后凭空多跑一次没有意义的探测。
        SyncConnectionCheckSchedule();
        RefreshProperties();
    }

    /// <summary>
    /// 把快照铺到属性上。
    ///
    /// <para>
    /// <paramref name="pushSample"/> 只有轮询那一拍才为真：命令响应里带的快照是为了让
    /// 动作的结果立刻可见（按钮文案、状态行），若也推进波形，一次点击就会在图上多出
    /// 一拍——Windows 端的波形是恒定的每秒一拍。
    /// </para>
    /// </summary>
    private void ApplySnapshot(StatusSnapshot snapshot, bool pushSample)
    {
        AppState previousState = _state;
        if (IsDaemonUnreachable)
        {
            // 失联期间在途的 mode 请求可能晚到失败响应；不要让它的临时拒绝文案
            // 在恢复快照后冒充当前故障。守护进程快照里的真实原因仍会铺回。
            _modeSwitchCommandError = "";
        }

        _daemonAvailability.RestoreAfterValidStatus();
        _statusKnown = true;

        _state = snapshot.State;

        // 界面档位的对账规则见 IsSwitching：切换在途中不许快照把滑块拖回去，
        // 否则用户会看到它先弹回旧档、再跳回新档。
        _daemonMode = snapshot.Mode;
        if (!_isModeSwitchInFlight)
        {
            _mode = snapshot.Mode;
        }

        // 配置是可以被手改坏的，越界的皮肤一律落回出厂皮肤（与守护进程同一份 Normalize）。
        _theme = UiThemes.Normalize(snapshot.Theme);
        _themeKnown = true;

        _latencyMs = snapshot.LatencyMs;
        _lastErrorMessage = snapshot.LastErrorMessage ?? "";
        _daemonSwitchErrorMessage = snapshot.LastSwitchErrorMessage ?? "";
        if (_daemonSwitchErrorMessage.Length > 0)
        {
            // 守护进程自己讲了原因，本类那句「被拒绝」的补充就过期了。
            _modeSwitchCommandError = "";
        }

        _serverIdentityUnverified = snapshot.ServerIdentityUnverified;
        _daemonIsChecking = snapshot.IsChecking;
        _hasCheck = snapshot.HasCheck;
        _checkReachable = snapshot.CheckReachable;
        _checkLatencyMs = snapshot.CheckLatencyMs;
        _checkEgress = snapshot.CheckEgress;
        _uplinkBytesPerSecond = snapshot.UplinkBytesPerSecond;
        _downlinkBytesPerSecond = snapshot.DownlinkBytesPerSecond;

        if (snapshot.State is AppState.Connected || snapshot.State is AppState.Disconnecting)
        {
            // 断开中仍然显示最后一拍的波形：Windows 端监视器是在 StopAsync 收尾时才
            // Reset 的，那之前用户看到的是「正在停止，流量还停在最后一个数上」。
            if (pushSample && snapshot.State == AppState.Connected && IsLive)
            {
                PushTrafficSample();
            }
        }
        else
        {
            // 其余状态在 Windows 端监视器都是空的（上一次停止时已 Reset），这里照做。
            ClearTrafficSamples();
        }

        if (previousState != _state)
        {
            // 状态一变，上一次「被拒绝」的说明就过期了：Windows 端
            // lastSwitchErrorMessage 也是随停止（即一次状态变化）清掉的。
            _modeSwitchCommandError = "";
        }

        SyncConnectionCheckSchedule();
        RefreshProperties();
    }

    // ---- 内部：命令 -----------------------------------------------------------------

    private async Task ExecutePrimaryAsync()
    {
        // 与 Windows 端同一张表：只有这两个状态有动作可发（其余状态按钮本来就不可点，
        // 走到这里是 IsDefault 的回车或代码直接调起）。
        string command = State switch
        {
            AppState.Disconnected or AppState.Error => ControlCommands.Start,
            AppState.Connected => ControlCommands.Stop,
            _ => ""
        };

        if (command.Length == 0)
        {
            return;
        }

        try
        {
            ControlResponse response = await SendAsync(command, "").ConfigureAwait(true);
            if (response.Status is not null)
            {
                ApplySnapshot(response.Status, pushSample: false);
            }
            else
            {
                // 命令失败（守护进程明确拒绝，或者它根本不在了）。这里不另造提示：
                // 真实原因要么已经在状态行上（守护进程不在），要么下一拍快照就会带来
                // （守护进程把原因写进了状态与 LastErrorMessage）。所以补一拍，
                // 让 Error 行在几十毫秒内出现，而不是等满一秒。
                Trace.WriteLine($"主命令 {command} 未生效：{Describe(response)}");
                PollNow();
            }
        }
        catch (Exception ex)
        {
            // ControlClient 把控制通道的异常都变成了失败响应，能走到这里的只剩
            // 「本类自己的代码出了问题」。界面不能因此崩掉。
            Trace.WriteLine($"主命令 {command} 失败：{ex}");
        }
    }

    private async Task ExecuteCheckAsync()
    {
        CancelScheduledImmediateCheck();
        if (_checkRequestInFlight)
        {
            return;
        }

        _checkRequestInFlight = true;
        RefreshProperties();

        try
        {
            ControlResponse response = await SendAsync(ControlCommands.Check, "").ConfigureAwait(true);
            if (response.Status is not null)
            {
                ApplySnapshot(response.Status, pushSample: false);
            }
            else
            {
                // 被拒绝（例如守护进程那边已经不是 Connected 了）。同样不另造提示，
                // 补一拍状态即可——IsCheckEnabled 已经把这条路径挡在了按钮之外，
                // 走到这里的是竞态。
                Trace.WriteLine($"检测连接被拒绝：{Describe(response)}");
                PollNow();
            }
        }
        catch (Exception ex)
        {
            // 控制器已把结论写回并通知 UI；这里只兜底，避免命令异常外溢。
            Trace.WriteLine($"检测连接失败：{ex}");
        }
        finally
        {
            _checkRequestInFlight = false;
            RefreshProperties();
        }
    }

    /// <summary>
    /// 滑块被拨动：先乐观移动，再发命令。<see cref="IsSwitching"/> 在途中时不再受理
    /// （视图那边同时是禁用态，这里是第二道闸——避免连点发出两条 <c>mode</c>）。
    /// </summary>
    private void RequestModeSwitch(ProxyMode mode)
    {
        if (_isModeSwitchInFlight)
        {
            return;
        }

        _mode = mode;
        _isModeSwitchInFlight = true;
        _modeSwitchCommandError = "";
        RefreshProperties();

        _ = SwitchModeAsync(mode);
    }

    private async Task SwitchModeAsync(ProxyMode mode)
    {
        // 参数取值与 CLI、守护进程的 TryParseMode 一致（smart = 智能分流，global = 全局）。
        string argument = mode == ProxyMode.Rule ? "smart" : "global";

        try
        {
            ControlResponse response = await SendAsync(ControlCommands.Mode, argument).ConfigureAwait(true);

            if (response.Status is not null)
            {
                ApplySnapshot(response.Status, pushSample: false);
            }

            if (response.Ok)
            {
                _modeSwitchCommandError = "";
            }
            else
            {
                // **必须与 Windows 端不同的一处**：守护进程对
                // Unbound/Connecting/Disconnecting/Error 下的切换是明确拒绝（共享控制器对
                // 那几种状态静默返回，命令行不允许「命令成功、状态没变」这种无声谎报），
                // 而拒绝响应里没有快照、LastSwitchErrorMessage 也不会被写。所以那句话必须
                // 本类自己留下并显示——否则用户拨了一下、滑块弹回、界面一个字都不说。
                _modeSwitchCommandError = Describe(response);

                // 被拒时没有快照可对账，补一拍让滑块回到守护进程的真实档位。
                PollNow();
            }
        }
        catch (Exception ex)
        {
            // 走到这里的只剩「本类自己的代码出了问题」：ControlClient 不抛业务异常。
            Trace.WriteLine($"切换分流模式失败：{ex}");
        }
        finally
        {
            // 先放开在途标记，再把显示档位对回守护进程的真实值：
            // 成功时它已经是新档（上面的快照铺过），失败时它仍是旧档。
            _isModeSwitchInFlight = false;
            _mode = _daemonMode;
            RefreshProperties();
        }
    }

    private Task<ControlResponse> SendAsync(string command, string argument)
        // CancellationToken.None：离场不该把已经发出去的命令掐掉（设置页、绑定页同此）。
        // 超时由 ControlClient 自己管：连接 3 秒、交换 60 秒。
        => _client.SendAsync(command, argument, CancellationToken.None);

    // ---- 内部：自检节拍 --------------------------------------------------------------

    private void OnConnectionCheckTimerTick()
    {
        if (IsCheckEnabled)
        {
            _ = ExecuteCheckAsync();
        }
    }

    /// <summary>
    /// GUI 连接自检：连上时立刻做一次，连接保持期间每 120 秒再做一次。
    /// 按钮复用 <see cref="ExecuteCheckAsync"/>，因此手动触发与自动触发不会分叉出两套逻辑。
    ///
    /// <para>
    /// <b>一处 Linux 独有的事实：</b>这个节拍由界面驱动，探测本身跑在守护进程里
    /// （<c>status</c> 只是把它读回来）。Windows 端的探测与界面同进程，所以那边
    /// 「没有人开界面 = 没有自动自检」；这里同样——自检不是连接的必需条件，
    /// 它只是给用户看的证据。
    /// </para>
    /// </summary>
    private void SyncConnectionCheckSchedule()
    {
        bool connected = !IsDaemonUnreachable && State == AppState.Connected;

        if (!connected)
        {
            CancelScheduledImmediateCheck();
            StopUiTimer(ref _connectionCheckTimer);
        }
        else
        {
            StartUiTimer(ref _connectionCheckTimer, ConnectionCheckInterval, OnConnectionCheckTimerTick);
        }

        if (connected && !_wasConnected)
        {
            // 让状态变化先完成一轮界面刷新，再发起自检：除了避免在状态回调里重入命令
            // 状态，也保证刚连上时界面先稳定显示「已连接」，随后再切到「正在检测…」。
            CancelScheduledImmediateCheck();
            int generation = ++_immediateCheckGeneration;
            PostToUi(() =>
            {
                if (generation != _immediateCheckGeneration)
                {
                    return;
                }

                if (State == AppState.Connected && IsCheckEnabled)
                {
                    _ = ExecuteCheckAsync();
                }
            });
        }

        _wasConnected = connected;
    }

    private void CancelScheduledImmediateCheck() => _immediateCheckGeneration++;

    // ---- 内部：界面线程与定时器 ------------------------------------------------------

    /// <summary>
    /// 有没有可用的界面线程。没有的话（单元测试宿主、无窗口的宿主、平台运行时子系统
    /// 还没起来）一律不动 <see cref="DispatcherTimer"/>：它绑在 <c>Dispatcher.UIThread</c>
    /// 上，从别的线程 <c>Start()</c> 会抛 <c>InvalidOperationException</c>，
    /// 而无头环境下「定时」这件事本来也没有意义——那些场景靠显式调用
    /// （<see cref="RefreshAfterBinding"/>、命令响应）驱动状态。
    ///
    /// <para>判断方式与 <c>gui/AvaloniaUiDispatcher.cs</c> 一致：只用公开 API。</para>
    /// </summary>
    private static bool HasUiThread()
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess();
        }
        catch (Exception)
        {
            // Dispatcher 正在关停时连 CheckAccess 都可能抛：等同于没有可用的界面线程。
            return false;
        }
    }

    /// <summary>
    /// 起一个绑在界面线程上的定时器（<see cref="DispatcherPriority.Background"/>，
    /// 与 Windows 端 <c>DispatcherTimer</c> 的优先级一致）。已经在跑就只保证它在跑。
    /// </summary>
    private static void StartUiTimer(ref DispatcherTimer? field, TimeSpan interval, Action tick)
    {
        if (!HasUiThread())
        {
            return;
        }

        if (field is null)
        {
            // 用「间隔 + 优先级 + 回调」这个重载：文档写明它绑的是 UI 线程的 Dispatcher，
            // 外壳 App.axaml.cs 的一秒激活定时器用的是同一个。**不要**写成
            // (DispatcherPriority, Dispatcher)——Avalonia 11.3 的公开面上没有那个重载
            // （只有 internal 的那一份），照 WPF 的习惯写会编译不过。
            field = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick());
        }

        if (!field.IsEnabled)
        {
            field.Start();
        }
    }

    private static void StopUiTimer(ref DispatcherTimer? field)
    {
        try
        {
            field?.Stop();
        }
        catch (Exception)
        {
            // 正在关停：没有定时器要停了。
        }
    }

    /// <summary>
    /// 把动作排到界面线程的 Background 优先级上（Windows 端用同一个优先级，
    /// 理由是「先让状态变化完成一轮刷新」）。没有界面线程时就地执行——那是无头宿主，
    /// 没有「下一帧」可言。
    /// </summary>
    private static void PostToUi(Action action)
    {
        try
        {
            if (HasUiThread())
            {
                Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
                return;
            }
        }
        catch (Exception)
        {
            // 排不进去（正在关停）就落到就地执行，与 AvaloniaUiDispatcher.Post 同一条降级规则。
        }

        action();
    }

    // ---- 内部：四个槽位共用的优先级链 ------------------------------------------------

    /// <summary>MainView 里那条预留行同一时刻只属于一个来源。</summary>
    private enum LineSlot
    {
        None,
        Switching,
        SwitchError,
        CheckResult,
        Latency
    }

    /// <summary>
    /// 预留行的唯一优先级实现：切换中 &gt; 切换失败 &gt; 自检结论 &gt; 延迟。
    ///
    /// <para>
    /// 四个 <c>*Visibility</c> 全部从它派生，**不许各算各的**——各算各的迟早会算出
    /// 「切换失败」与「检测未通过」同时可见，两行字挤在同一个格子里。
    /// 每一条的判断条件与 Windows 端逐字相同。
    /// </para>
    /// </summary>
    private LineSlot LineSlotOwner
    {
        get
        {
            if (IsSwitching)
            {
                return LineSlot.Switching;
            }

            if (State != AppState.Connected)
            {
                return LineSlot.None;
            }

            if (SwitchErrorMessage.Length > 0)
            {
                return LineSlot.SwitchError;
            }

            if (CheckResultText.Length > 0)
            {
                return LineSlot.CheckResult;
            }

            return LatencyMs is not null ? LineSlot.Latency : LineSlot.None;
        }
    }

    // ---- 内部：流量波形 --------------------------------------------------------------

    private void PushTrafficSample()
    {
        _samples.Enqueue(new TrafficRate(
            Math.Max(_uplinkBytesPerSecond, 0),
            Math.Max(_downlinkBytesPerSecond, 0)));

        while (_samples.Count > TrafficSampleCapacity)
        {
            _samples.Dequeue();
        }

        // 换一个新数组：视图（Sparkline）靠这个属性变化重画，而数组之间是引用比较。
        _samplesView = _samples.ToArray();
    }

    private void ClearTrafficSamples()
    {
        _uplinkBytesPerSecond = 0;
        _downlinkBytesPerSecond = 0;

        if (_samples.Count == 0 && _samplesView.Length == 0)
        {
            return;
        }

        _samples.Clear();
        _samplesView = [];
    }

    // ---- 内部：通知 -----------------------------------------------------------------

    private static string Describe(ControlResponse response)
        => string.IsNullOrEmpty(response.Message)
            ? ErrorCodeMessages.Get(response.ErrorCode)
            : response.Message;

    /// <summary>
    /// 把所有会变的格子重算一遍，<b>只有值真的变了才发通知</b>。
    /// 每秒一次的轮询走的就是这里，见 <see cref="_notified"/> 的注释。
    /// </summary>
    private void RefreshProperties()
    {
        // 状态区
        NotifyIfChanged(nameof(State), State);
        NotifyIfChanged(nameof(StatusText), StatusText);
        NotifyIfChanged(nameof(StatusSubText), StatusSubText);
        NotifyIfChanged(nameof(StatusSubTextVisibility), StatusSubTextVisibility);
        NotifyIfChanged(nameof(IsDaemonUnreachable), IsDaemonUnreachable);
        NotifyIfChanged(nameof(ButtonText), ButtonText);

        if (NotifyIfChanged(nameof(IsButtonEnabled), IsButtonEnabled))
        {
            PrimaryCommand.RaiseCanExecuteChanged();
        }

        NotifyIfChanged(nameof(IsModeSelectionEnabled), IsModeSelectionEnabled);
        NotifyIfChanged(nameof(Mode), Mode);
        NotifyIfChanged(nameof(IsRuleMode), IsRuleMode);
        NotifyIfChanged(nameof(IsGlobalMode), IsGlobalMode);

        // 预留行的四个槽位
        NotifyIfChanged(nameof(IsSwitching), IsSwitching);
        NotifyIfChanged(nameof(IsSwitchingTextVisible), IsSwitchingTextVisible);
        NotifyIfChanged(nameof(SwitchErrorMessage), SwitchErrorMessage);
        NotifyIfChanged(nameof(SwitchErrorVisibility), SwitchErrorVisibility);
        NotifyIfChanged(nameof(CheckResultText), CheckResultText);
        NotifyIfChanged(nameof(CheckResultVisibility), CheckResultVisibility);
        NotifyIfChanged(nameof(LatencyMs), LatencyMs);
        NotifyIfChanged(nameof(LatencyText), LatencyText);
        NotifyIfChanged(nameof(LatencyTextVisibility), LatencyTextVisibility);

        // 动效
        NotifyIfChanged(nameof(IsLive), IsLive);
        NotifyIfChanged(nameof(AnimationsEnabled), AnimationsEnabled);
        NotifyIfChanged(nameof(CanAnimate), CanAnimate);
        NotifyIfChanged(nameof(ConnectingSpinnerVisibility), ConnectingSpinnerVisibility);

        // 自检
        NotifyIfChanged(nameof(IsChecking), IsChecking);

        if (NotifyIfChanged(nameof(IsCheckEnabled), IsCheckEnabled))
        {
            CheckCommand.RaiseCanExecuteChanged();
        }

        NotifyIfChanged(nameof(CheckButtonText), CheckButtonText);
        NotifyIfChanged(nameof(CheckButtonVisibility), CheckButtonVisibility);
        NotifyIfChanged(nameof(CheckSeverity), CheckSeverity);

        // 流量
        NotifyIfChanged(nameof(TrafficSamples), TrafficSamples);
        NotifyIfChanged(nameof(UplinkText), UplinkText);
        NotifyIfChanged(nameof(DownlinkText), DownlinkText);
        NotifyIfChanged(nameof(TrafficVisibility), TrafficVisibility);
        NotifyIfChanged(nameof(TrafficOpacity), TrafficOpacity);
        NotifyIfChanged(nameof(CheckButtonOpacity), CheckButtonOpacity);

        // 皮肤：只在读到过快照之后才报（见 Theme 的注释）。
        if (_themeKnown)
        {
            NotifyIfChanged(nameof(Theme), Theme);
        }

        // 托盘
        NotifyIfChanged(nameof(IsTrayAvailable), IsTrayAvailable);
        NotifyIfChanged(nameof(TrayDetailText), TrayDetailText);
        NotifyIfChanged(nameof(HasTrayDetail), HasTrayDetail);
    }

    /// <summary>相等就不通知。返回是否真的发了通知（调用方据此决定要不要刷命令的可用性）。</summary>
    private bool NotifyIfChanged(string propertyName, object? value)
    {
        if (_notified.TryGetValue(propertyName, out object? previous) && Equals(previous, value))
        {
            return false;
        }

        _notified[propertyName] = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
