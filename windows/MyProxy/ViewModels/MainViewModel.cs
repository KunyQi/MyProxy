using System.Windows;
using System.Windows.Threading;
using MyProxy.Controls;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan ConnectionCheckInterval = TimeSpan.FromSeconds(120);

    private readonly IConnectionController _controller;
    private readonly ILogService _log;
    private readonly DispatcherTimer _connectionCheckTimer;
    private DispatcherOperation? _scheduledImmediateCheck;
    private ProxyMode _mode = ProxyMode.Rule;
    private bool _isLive = true;
    private bool _animationsEnabled = SystemParameters.ClientAreaAnimation;
    private bool _wasConnected;
    private bool _checkRequestInFlight;

    public MainViewModel(IConnectionController controller, ILogService log)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mode = controller.Mode;

        _controller.StateChanged += OnControllerStateChanged;
        _controller.ModeChanged += OnControllerModeChanged;
        _controller.CheckChanged += OnControllerCheckChanged;
        _controller.TrafficChanged += OnControllerTrafficChanged;

        _connectionCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ConnectionCheckInterval
        };
        _connectionCheckTimer.Tick += OnConnectionCheckTimerTick;

        PrimaryCommand = new RelayCommand(_log, ExecutePrimaryAsync, () => CanExecutePrimary);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        CheckCommand = new RelayCommand(_log, ExecuteCheckAsync, () => IsCheckEnabled);

        RefreshStateProperties();
    }

    public event Action? SettingsRequested;

    public AppState State => _controller.State;

    public string StatusText => State switch
    {
        AppState.Connected => "已连接",
        AppState.Connecting => "正在连接…",
        AppState.Disconnecting => "正在断开…",
        AppState.Error => "连接失败",
        AppState.Unbound => "未绑定",
        _ => "未连接"
    };

    public string StatusSubText => State switch
    {
        // 自检判定失败时让位给结论行：一边写「网络连接正常」一边写「连接失败」，
        // 用户只会更没底。
        // 服务器证书校验失败：连接还能用，但收不到配置更新与吊销，不能再说「正常」。
        AppState.Connected when _controller.ServerIdentityUnverified =>
            ErrorCodeMessages.ServerIdentityUnverifiedWhileConnected,
        AppState.Connected => CheckSeverity == CheckSeverity.Bad ? "" : "网络连接正常",
        AppState.Error => _controller.LastErrorMessage,
        _ => ""
    };

    public Visibility StatusSubTextVisibility
        => string.IsNullOrEmpty(StatusSubText) ? Visibility.Collapsed : Visibility.Visible;

    public string ButtonText => State switch
    {
        AppState.Connected => "停止",
        AppState.Connecting => "正在启动",
        AppState.Disconnecting => "正在停止",
        AppState.Error => "重试",
        _ => "启动"
    };

    public bool IsButtonEnabled => State switch
    {
        AppState.Disconnected or AppState.Error => true,
        AppState.Connected => !_controller.IsSwitching,
        _ => false
    };

    public bool IsModeSelectionEnabled => State == AppState.Connected && !_controller.IsSwitching;

    public bool IsSwitching => _controller.IsSwitching;

    public Visibility IsSwitchingTextVisible
        => _controller.IsSwitching ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectingSpinnerVisibility
        => State == AppState.Connecting && CanAnimate ? Visibility.Visible : Visibility.Collapsed;

    public ProxyMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsRuleMode));
                OnPropertyChanged(nameof(IsGlobalMode));
            }
        }
    }

    public bool IsRuleMode
    {
        get => Mode == ProxyMode.Rule;
        set
        {
            if (value && Mode != ProxyMode.Rule)
            {
                Mode = ProxyMode.Rule;
                _ = SwitchModeAsync(ProxyMode.Rule);
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
                Mode = ProxyMode.Global;
                _ = SwitchModeAsync(ProxyMode.Global);
            }
        }
    }

    /// <summary>
    /// 模式切换失败（且已回滚回旧模式）的原因。与「正在切换…」/ 延迟共用 MainView 的同一预留槽，
    /// 优先级：切换中 &gt; 切换失败 &gt; 延迟，三者由这里的可见性互斥保证。
    /// </summary>
    public string SwitchErrorMessage => _controller.LastSwitchErrorMessage ?? "";

    public Visibility SwitchErrorVisibility
        => State == AppState.Connected && !_controller.IsSwitching && SwitchErrorMessage.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public int? LatencyMs => _controller.LatencyMs;

    public string LatencyText => LatencyMs is int ms ? $"延迟 {ms} ms" : "";

    /// <summary>
    /// 连接时自动量到的那一次延迟。一旦用户主动检测过，实测结论更新更准，这一行让位
    /// （四者共用同一预留槽，优先级：切换中 &gt; 切换失败 &gt; 自检结论 &gt; 自动延迟）。
    /// </summary>
    public Visibility LatencyTextVisibility
        => State == AppState.Connected
           && !_controller.IsSwitching
           && LatencyMs is not null
           && SwitchErrorMessage.Length == 0
           && CheckResultText.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// 主页是否可见：退出、切页、最小化或隐藏时停掉装饰动画与流量采样。
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (!SetProperty(ref _isLive, value))
            {
                return;
            }

            _controller.SetTrafficSamplingSuspended(!value);
            OnPropertyChanged(nameof(CanAnimate));
            OnPropertyChanged(nameof(ConnectingSpinnerVisibility));
        }
    }

    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (SetProperty(ref _animationsEnabled, value))
            {
                OnPropertyChanged(nameof(CanAnimate));
                OnPropertyChanged(nameof(ConnectingSpinnerVisibility));
            }
        }
    }

    public bool CanAnimate => IsLive && AnimationsEnabled;

    public bool IsChecking => _checkRequestInFlight || _controller.IsChecking;

    /// <summary>自检只在连接稳定时有意义：切换中链路正在重建，断开时根本没有链路可测。</summary>
    public bool IsCheckEnabled
        => State == AppState.Connected && !_controller.IsSwitching && !IsChecking;

    public string CheckButtonText => IsChecking ? "正在检测…" : "检测连接";

    public string CheckResultText
    {
        get
        {
            if (IsChecking)
            {
                return "正在检测…";
            }

            ConnectionCheckResult? check = _controller.LastCheck;
            if (check is null || State != AppState.Connected)
            {
                return "";
            }

            if (!check.Reachable)
            {
                return "检测未通过，请重试";
            }

            // 出口对不上意味着 xray 活着、系统代理也指过去了，但请求其实走了直连。
            // 这是唯一一种「界面显示已连接、实际没有代理」的静默失效，必须说破。
            if (check.Egress == EgressVerdict.Bypassed)
            {
                return "连接失败，请重试";
            }

            // 「已验证」比「已连通」多的那一层意思，正是出口核对过了。
            // 两者只差一个字，语义强弱却对得上，比写全「已确认走服务器」短得多。
            string latency = check.LatencyMs is int ms ? $"{ms} ms" : "延迟未知";
            return check.Egress == EgressVerdict.Verified
                ? $"已验证 · {latency}"
                : $"已连通 · {latency}";
        }
    }

    public CheckSeverity CheckSeverity
    {
        get
        {
            if (IsChecking)
            {
                return CheckSeverity.Running;
            }

            ConnectionCheckResult? check = _controller.LastCheck;
            if (check is null || State != AppState.Connected)
            {
                return CheckSeverity.None;
            }

            return check.Reachable && check.Egress != EgressVerdict.Bypassed
                ? CheckSeverity.Good
                : CheckSeverity.Bad;
        }
    }

    public Visibility CheckResultVisibility
        => State == AppState.Connected
           && !_controller.IsSwitching
           && SwitchErrorMessage.Length == 0
           && CheckResultText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// 常显。未连接时按钮以禁用态占位，而不是隐藏——64px 的空 band 在绝对对称的版面里
    /// 会被读成「这里塌了一块」，而禁用态本身就说明了「连上以后可以点」。
    /// </summary>
    public Visibility CheckButtonVisibility
        => State == AppState.Unbound ? Visibility.Hidden : Visibility.Visible;

    public IReadOnlyList<TrafficRate> TrafficSamples => _controller.TrafficSamples;

    /// <summary>只有数值。方向由 MainView 里的矢量箭头表达，不再往文本里塞 ↑↓ 字符。</summary>
    public string UplinkText => TrafficSparkline.FormatRate(_controller.TrafficRate.UplinkBytesPerSecond);

    public string DownlinkText => TrafficSparkline.FormatRate(_controller.TrafficRate.DownlinkBytesPerSecond);

    /// <summary>
    /// 除未绑定外常显。断开时控制器已把监视器 Reset，于是这里自然读到 0 B/s 与空波形，
    /// 图上只剩一条基线——安静且诚实。
    ///
    /// 不整段隐藏，是因为隐藏会在状态区与模式开关之间留下一块 66px 的空当，
    /// 在这套居中对称的版面里会被读成「这里漏了什么」，而不是留白。
    /// </summary>
    public Visibility TrafficVisibility
        => State == AppState.Unbound ? Visibility.Hidden : Visibility.Visible;

    public RelayCommand PrimaryCommand { get; }

    public RelayCommand SettingsCommand { get; }

    public RelayCommand CheckCommand { get; }

    private bool CanExecutePrimary => IsButtonEnabled;

    private async Task ExecutePrimaryAsync()
    {
        try
        {
            if (_controller.State is AppState.Disconnected or AppState.Error)
            {
                await _controller.StartAsync(CancellationToken.None);
            }
            else if (_controller.State == AppState.Connected)
            {
                await _controller.StopAsync(CancellationToken.None);
            }
        }
        catch (MyProxyException ex)
        {
            // 控制器已处理状态与回滚；这里只兜底，避免命令异常外溢。
            _log.Warn(nameof(MainViewModel), $"Primary command failed: {ex.ErrorCode}");
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
        RefreshCheckProperties();

        try
        {
            await _controller.CheckConnectionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 控制器已把结论写回并通知 UI；这里只兜底，避免命令异常外溢。
            _log.Warn(nameof(MainViewModel), $"Connection check failed: {ex.Message}");
        }
        finally
        {
            _checkRequestInFlight = false;
            RefreshCheckProperties();
        }
    }

    private void OnConnectionCheckTimerTick(object? sender, EventArgs e)
    {
        if (IsCheckEnabled)
        {
            _ = ExecuteCheckAsync();
        }
    }

    /// <summary>
    /// GUI 连接自检：建立连接时立即做一次，连接保持期间每 120 秒再做一次。
    /// 按钮复用 ExecuteCheckAsync，因此手动触发与自动触发不会分叉出两套逻辑。
    /// </summary>
    private void SyncConnectionCheckSchedule()
    {
        bool connected = State == AppState.Connected;
        if (!connected)
        {
            CancelScheduledImmediateCheck();
            _connectionCheckTimer.Stop();
        }
        else if (!_connectionCheckTimer.IsEnabled)
        {
            _connectionCheckTimer.Start();
        }

        if (connected && !_wasConnected)
        {
            // 让状态变化先完成一轮 UI 刷新，再在 Dispatcher 上立即发起自检。
            // 除了避免在 StateChanged 回调中重入命令状态，也保证刚连上时界面
            // 先稳定显示「已连接」，随后再切到「正在检测…」。
            CancelScheduledImmediateCheck();
            _scheduledImmediateCheck = _connectionCheckTimer.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _scheduledImmediateCheck = null;
                    if (State == AppState.Connected && IsCheckEnabled)
                    {
                        _ = ExecuteCheckAsync();
                    }
                }));
        }

        _wasConnected = connected;
    }

    private void CancelScheduledImmediateCheck()
    {
        DispatcherOperation? operation = _scheduledImmediateCheck;
        _scheduledImmediateCheck = null;
        if (operation is not null && operation.Status == DispatcherOperationStatus.Pending)
        {
            operation.Abort();
        }
    }

    private async Task SwitchModeAsync(ProxyMode mode)
    {
        try
        {
            await _controller.SwitchModeAsync(mode, CancellationToken.None);
        }
        catch (MyProxyException ex)
        {
            // 控制器内部已回滚，并把失败原因写进 LastSwitchErrorMessage（由 SwitchErrorMessage 呈现）；
            // 这里只兜底，避免 fire-and-forget 的异常外溢。
            _log.Warn(nameof(MainViewModel), $"Mode switch to {mode} failed: {ex.ErrorCode}");
        }
        catch (Exception ex)
        {
            // 同上：失败提示由控制器统一给出，此处只需恢复 UI 与控制器的真实模式。
            // 但意料之外的异常必须留下痕迹，否则模式切换失败在出厂构建里无迹可寻。
            _log.Error(nameof(MainViewModel), $"Mode switch to {mode} failed unexpectedly", ex);
        }
        finally
        {
            RefreshModeFromController();
        }
    }

    private void OnControllerStateChanged()
    {
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.InvokeAsync(RefreshStateProperties);
            return;
        }

        RefreshStateProperties();
    }

    private void OnControllerTrafficChanged()
    {
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.InvokeAsync(RefreshTrafficProperties);
            return;
        }

        RefreshTrafficProperties();
    }

    private void RefreshTrafficProperties()
    {
        OnPropertyChanged(nameof(TrafficSamples));
        OnPropertyChanged(nameof(UplinkText));
        OnPropertyChanged(nameof(DownlinkText));
        OnPropertyChanged(nameof(TrafficVisibility));
    }

    private void OnControllerCheckChanged()
    {
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.InvokeAsync(RefreshCheckProperties);
            return;
        }

        RefreshCheckProperties();
    }

    private void RefreshCheckProperties()
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsCheckEnabled));
        OnPropertyChanged(nameof(CheckButtonText));
        OnPropertyChanged(nameof(CheckResultText));
        OnPropertyChanged(nameof(CheckSeverity));
        OnPropertyChanged(nameof(CheckResultVisibility));
        OnPropertyChanged(nameof(CheckButtonVisibility));
        OnPropertyChanged(nameof(LatencyTextVisibility));
        OnPropertyChanged(nameof(StatusSubText));
        OnPropertyChanged(nameof(StatusSubTextVisibility));
        CheckCommand.RaiseCanExecuteChanged();
    }

    private void OnControllerModeChanged()
    {
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.InvokeAsync(RefreshModeFromController);
            return;
        }

        RefreshModeFromController();
    }

    private void RefreshModeFromController()
    {
        Mode = _controller.Mode;
        RefreshStateProperties();
    }

    private void RefreshStateProperties()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusSubText));
        OnPropertyChanged(nameof(StatusSubTextVisibility));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(IsButtonEnabled));
        OnPropertyChanged(nameof(IsModeSelectionEnabled));
        OnPropertyChanged(nameof(IsSwitching));
        OnPropertyChanged(nameof(IsSwitchingTextVisible));
        OnPropertyChanged(nameof(ConnectingSpinnerVisibility));
        OnPropertyChanged(nameof(SwitchErrorMessage));
        OnPropertyChanged(nameof(SwitchErrorVisibility));
        OnPropertyChanged(nameof(LatencyMs));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(LatencyTextVisibility));
        PrimaryCommand.RaiseCanExecuteChanged();
        RefreshCheckProperties();
        RefreshTrafficProperties();
        SyncConnectionCheckSchedule();
    }
}
