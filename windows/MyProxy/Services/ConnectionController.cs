using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class ConnectionController : IConnectionController, IDisposable
{
    private const int PortReadyProbeCount = 50;
    private const int PortReadyProbeDelayMs = 200;
    private const string ProxyHost = "127.0.0.1";
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);

    public static readonly IReadOnlyList<int> CandidatePorts = Array.AsReadOnly(
        new[] { 10809, 20809, 30809, 40809, 50809, 60809 });

    /// <summary>
    /// 统计查询端口的候选，与 <see cref="CandidatePorts"/> 一一对应 +1。
    /// 两组分开挑，是为了让代理端口被占用时不至于连统计端口也跟着挪位。
    /// </summary>
    public static readonly IReadOnlyList<int> CandidateStatsPorts = Array.AsReadOnly(
        new[] { 10810, 20810, 30810, 40810, 50810, 60810 });

    /// <summary>
    /// 连通性探测入站的端口候选，与 <see cref="CandidatePorts"/> 一一对应 +2。
    /// 探测必须经专用入站发出，见 <see cref="XrayConfigGenerator"/> 的 ApplyProbeInbound。
    /// </summary>
    public static readonly IReadOnlyList<int> CandidateProbePorts = Array.AsReadOnly(
        new[] { 10811, 20811, 30811, 40811, 50811, 60811 });

    /// <summary>可见界面的流量采样周期。</summary>
    private static readonly TimeSpan TrafficSampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 类别归因的采样间隔。与速率图的 1 秒无关：服务端按小时收桶，一分钟一次足够，
    /// 而逐秒查询 per-tag 计数器只是在白白打本地 gRPC。
    /// </summary>
    internal static readonly TimeSpan DefaultUsageSampleInterval = TimeSpan.FromSeconds(60);

    /// <summary>停止前最后一次类别采样的时限：它在停止路径上，只等本地 gRPC。</summary>
    private static readonly TimeSpan FinalUsageSampleBudget = TimeSpan.FromSeconds(1);

    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly IConfigService _config;
    private readonly IXrayService _xray;
    private readonly ISystemProxyService _proxy;
    private readonly INetworkService _network;
    private readonly SemaphoreSlim _flowLock = new(1, 1);
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly object _heartbeatSync = new();
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _usageSampleInterval;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _heartbeatCts;
    private ProxyMode _mode = ProxyMode.Rule;
    private bool _isSwitching;
    private int? _latencyMs;
    private ErrorCode _lastErrorCode = ErrorCode.Unknown;
    private string _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
    private string? _lastSwitchErrorMessage;
    private int _currentPort;
    private ServerProfile _currentProfile = new();
    private long _currentConfigVersion;
    private int _handlingUnexpectedExit;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _checkSync = new();
    private CancellationTokenSource? _checkCts;
    private readonly ITrafficStatsService? _trafficStats;
    private readonly IUsageReporter? _usageReporter;
    private readonly IUiDispatcher? _uiDispatcher;
    private readonly Func<bool> _categoryAttributionEnabled;
    private readonly TrafficRateMonitor _trafficMonitor = new();
    private readonly object _trafficSync = new();
    private CancellationTokenSource? _trafficCts;
    private int _currentStatsPort;
    private int _currentProbePort;
    private volatile bool _trafficSuspended;
    private long _trafficEpoch;
    private int _disposed;
    private volatile bool _isChecking;

    /// <summary>
    /// 链路建立、停止或重建时递增。失效与自检结果写回共用 _checkSync，
    /// 防止旧请求跨越模式切换、配置更新或同端口重连后再次写回结论。
    /// </summary>
    private long _connectionEpoch;
    private volatile ConnectionCheckResult? _lastCheck;

    public ConnectionController(
        IStorageService storage,
        ILogService log,
        IConfigService config,
        IXrayService xray,
        ISystemProxyService proxy,
        INetworkService network,
        TimeSpan? heartbeatInterval = null,
        ITrafficStatsService? trafficStats = null,
        IUsageReporter? usageReporter = null,
        Func<bool>? categoryAttributionEnabled = null,
        TimeSpan? usageSampleInterval = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _xray = xray ?? throw new ArgumentNullException(nameof(xray));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        // 可选：不注入就不采集流量，速率图显示为空。它是可观测性，不是连接的前置条件。
        _trafficStats = trafficStats;
        _usageReporter = usageReporter;
        // 以委托而不是布尔值传入：flag 由心跳刷新，而控制器的生命周期比一次
        // 心跳长得多，存一个快照等于让开关永远停在启动那一刻的值。
        _categoryAttributionEnabled = categoryAttributionEnabled ?? (static () => false);
        // 可选的界面封送器：不注入即就地触发（命令行、systemd、测试宿主都走这条路）。
        // 它把「控制器不认识任何 UI 框架」落到实处，见 MyProxy.Core.IUiDispatcher。
        _uiDispatcher = uiDispatcher;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _usageSampleInterval = usageSampleInterval ?? DefaultUsageSampleInterval;
        if (_heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        _stateMachine.StateChanged += (_, _) => RaiseOnUiThread(() => StateChanged?.Invoke());
        _xray.Exited += OnXrayExited;
    }

    public AppState State => _stateMachine.Current;

    public ProxyMode Mode => _mode;

    public bool IsSwitching => _isSwitching;

    public int? LatencyMs => _latencyMs;

    public ErrorCode LastErrorCode => _lastErrorCode;

    public string LastErrorMessage => _lastErrorMessage;

    public string? LastSwitchErrorMessage => _lastSwitchErrorMessage;

    public bool IsChecking => _isChecking;

    /// <summary>
    /// 最近一次自检结论。停止、模式切换与配置热更新都会清空它：那些动作之后，
    /// 上一份延迟与出口判定描述的已经不是当前这条链路，留着就是在给用户看过期证据。
    /// </summary>
    public ConnectionCheckResult? LastCheck => _lastCheck;

    public TrafficRate TrafficRate => _trafficMonitor.Current;

    public IReadOnlyList<TrafficRate> TrafficSamples => _trafficMonitor.Samples;

    public event Action? StateChanged;

    private volatile bool _serverIdentityUnverified;

    /// <inheritdoc />
    public bool ServerIdentityUnverified => _serverIdentityUnverified;

    public event Action? ModeChanged;

    public event Action? BindingRequired;

    public event Action? CheckChanged;

    public event Action? TrafficChanged;

    /// <summary>
    /// 每次心跳成功后触发，带上服务端返回的 release 身份与 feature flags。
    /// 订阅者负责 Update Plane 的处理；控制器本身不认识它。
    /// </summary>
    public event Action<HeartbeatResult>? HeartbeatReceived;

    public void InitializeMode(ProxyMode mode)
    {
        _mode = mode;
    }

    public void MarkBound()
    {
        if (_stateMachine.Current == AppState.Unbound)
        {
            _stateMachine.Transition(ConnectionStateEvent.BindSuccess);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (_stateMachine.Current is not (AppState.Disconnected or AppState.Error))
        {
            return;
        }

        await _flowLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_stateMachine.Current is not (AppState.Disconnected or AppState.Error))
            {
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Dispose 可能发生在取到流程锁后、登记 CTS 前；登记后再检查可封住这个窗口。
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            CancellationToken linked = _cts.Token;

            _lastErrorCode = ErrorCode.Unknown;
            _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);

            if (_stateMachine.Current == AppState.Disconnected)
            {
                _stateMachine.Transition(ConnectionStateEvent.Start);
            }
            else
            {
                _stateMachine.Transition(ConnectionStateEvent.Retry);
            }

            try
            {
                DeviceConfig? device = _storage.LoadDevice();
                if (device is null)
                {
                    throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.NotBound), ErrorCode.NotBound);
                }

                string coreDir = CoreAssets.ResolveDirectory(_storage);

                ConfigCacheEntry candidate = await _config.GetConfigAsync(linked).ConfigureAwait(false);

                int port = SelectAvailablePort(linked);
                _currentPort = port;
                _currentStatsPort = SelectStatsPort(port, linked);
                _currentProbePort = SelectProbePort(port, _currentStatsPort, linked);
                _currentProfile = candidate.Profile;
                Interlocked.Exchange(ref _currentConfigVersion, candidate.ConfigVersion);

                string configPath = await GenerateConfigAsync(candidate.Profile, _mode, port, linked).ConfigureAwait(false);

                try
                {
                    _proxy.CaptureCurrentSettings();
                    _proxy.PersistSnapshotForCrashRecovery();
                }
                catch (Exception ex)
                {
                    _log.Warn(nameof(ConnectionController), $"Capture/Persist proxy snapshot failed: {ex.Message}");
                    throw new MyProxyException(
                        ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed),
                        ErrorCode.ProxyApplyFailed,
                        ex);
                }

                await _xray.StartAsync(configPath, coreDir, linked).ConfigureAwait(false);
                await WaitForInboundsReadyAsync(port, linked).ConfigureAwait(false);
                await _proxy.EnableAsync(ProxyHost, port, linked).ConfigureAwait(false);

                LatencyResult latency = await _network.TestThroughProxyAsync(ProxyHost, ProbePort, linked).ConfigureAwait(false);
                if (!latency.Success)
                {
                    throw new MyProxyException(ErrorCodeMessages.Get(latency.Error), latency.Error);
                }

                if (!candidate.Verified)
                {
                    await _config.PromoteCurrentConfigAsync(candidate.Profile, candidate.ConfigVersion, linked).ConfigureAwait(false);
                }

                linked.ThrowIfCancellationRequested();
                _latencyMs = latency.LatencyMs;
                ClearLastCheck();
                _stateMachine.Transition(ConnectionStateEvent.Success);
                StartHeartbeatLoop();
                StartTrafficLoop();
                _log.Info(nameof(ConnectionController), $"Connected. Port={port}, LatencyMs={_latencyMs}");
            }
            catch (MyProxyException ex) when (ex.ErrorCode == ErrorCode.TokenInvalid)
            {
                await HandleTokenInvalidAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RollbackAndSetErrorAsync(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _flowLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_stateMachine.Current is not (AppState.Connected or AppState.Error or AppState.Connecting or AppState.Disconnecting))
        {
            return;
        }

        CancelHeartbeatLoop();
        // 在停掉采样循环、停掉内核之前：最后采一次，把这一小时的类别量发出去。
        await FlushUsageBeforeStopAsync().ConfigureAwait(false);
        CancelTrafficLoop();
        ClearLastCheck();

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 当前流程已结束。
        }

        bool proxyRestored = false;
        await _flowLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current is not (AppState.Connected or AppState.Error or AppState.Connecting or AppState.Disconnecting))
            {
                return;
            }

            if (_stateMachine.Current == AppState.Disconnecting)
            {
                proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (_stateMachine.Current == AppState.Connecting)
            {
                _stateMachine.Transition(ConnectionStateEvent.StopRequest);
            }
            else
            {
                _stateMachine.Transition(ConnectionStateEvent.Stop);
            }

            proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _latencyMs = null;
            _isSwitching = false;
            _lastSwitchErrorMessage = null;
            ClearLastCheck();
            _trafficMonitor.Reset();
            RaiseTrafficChanged();

            if (_stateMachine.Current == AppState.Disconnecting)
            {
                if (proxyRestored)
                {
                    _stateMachine.Transition(ConnectionStateEvent.Done);
                }
                else
                {
                    SetProxyRestoreFailure();
                    _stateMachine.Transition(ConnectionStateEvent.StopFailure);
                }
            }

            _flowLock.Release();
        }
    }

    public async Task SwitchModeAsync(ProxyMode mode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (_stateMachine.Current is AppState.Connecting or AppState.Disconnecting or AppState.Error or AppState.Unbound)
        {
            return;
        }

        if (_stateMachine.Current == AppState.Disconnected)
        {
            await SetModeAsync(mode, ct).ConfigureAwait(false);
            return;
        }

        if (_stateMachine.Current != AppState.Connected || _isSwitching)
        {
            return;
        }

        await _flowLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_stateMachine.Current != AppState.Connected || _isSwitching)
            {
                return;
            }

            SetSwitching(true);
            ProxyMode oldMode = _mode;

            try
            {
                string configPath = await GenerateConfigAsync(_currentProfile, mode, _currentPort, ct).ConfigureAwait(false);
                await _xray.RestartAsync(configPath, CoreDir, ct).ConfigureAwait(false);
                await WaitForInboundsReadyAsync(_currentPort, ct).ConfigureAwait(false);

                LatencyResult latency = await _network.TestThroughProxyAsync(ProxyHost, ProbePort, ct).ConfigureAwait(false);
                if (!latency.Success)
                {
                    throw new MyProxyException(ErrorCodeMessages.Get(latency.Error), latency.Error);
                }

                _latencyMs = latency.LatencyMs;
                ClearLastCheck();
                await _storage.UpdateSettingsAsync(settings => settings.ProxyMode = mode, ct).ConfigureAwait(false);
                _mode = mode;
                RaiseModeChanged();
                _log.Info(nameof(ConnectionController), $"Mode switched to {mode}");
            }
            catch (Exception ex)
            {
                _log.Warn(nameof(ConnectionController), $"Switch failed, rolling back to {oldMode}: {ex.Message}");
                await TryRollbackModeSwitchAsync(oldMode, ct).ConfigureAwait(false);

                // 回滚成功 → State 仍是 Connected，失败原因不会经 LastErrorMessage 露出，
                // 需要单独向 UI 报告模式切换失败。
                if (_stateMachine.Current == AppState.Connected)
                {
                    _lastSwitchErrorMessage = ErrorCodeMessages.Get(
                        (ex as MyProxyException)?.ErrorCode ?? ErrorCode.Unknown);
                }
            }
            finally
            {
                SetSwitching(false);
            }
        }
        finally
        {
            _flowLock.Release();
        }
    }

    public async Task RebindAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        CancelHeartbeatLoop();

        if (_stateMachine.Current is AppState.Connecting or AppState.Connected or AppState.Error)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (_stateMachine.Current == AppState.Error &&
                _lastErrorCode == ErrorCode.ProxyApplyFailed)
            {
                return;
            }
        }

        await _flowLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _storage.ClearBindingAsync(ct).ConfigureAwait(false);

            if (_stateMachine.Current is AppState.Connected or AppState.Disconnected or AppState.Error)
            {
                _stateMachine.Transition(ConnectionStateEvent.Rebind);
            }

            _latencyMs = null;
            _currentProfile = new ServerProfile();
            Interlocked.Exchange(ref _currentConfigVersion, 0);
            _lastErrorCode = ErrorCode.Unknown;
            _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
            RaiseBindingRequired();
        }
        finally
        {
            _flowLock.Release();
        }
    }

    /// <summary>
    /// 取消后台工作；每项异步操作在自己的 finally 中释放 CTS。
    ///
    /// 不停止 xray、不恢复系统代理：那是 <c>StopAsync</c> / <c>App.ExitAsync</c> 的职责，
    /// 且 fail-open 顺序（先恢复代理、成功才停 xray）必须留在那里，不能挪进 Dispose。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _xray.Exited -= OnXrayExited;
        CancelHeartbeatLoop();
        CancelTrafficLoop();
        ClearLastCheck();

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 当前流程已结束。
        }

        // 不在仍有异步使用者时 Dispose 信号量，否则它们的 finally/Release 会抛异常。
        // 两者只使用 WaitAsync，从未访问 AvailableWaitHandle，没有待释放的系统句柄；
        // 留给 GC 回收即可。Start 的 CTS 同样由 StartAsync 的 finally 独占释放。
    }

    private string CoreDir => CoreAssets.ResolveDirectory(_storage);

    private int SelectAvailablePort(CancellationToken ct)
    {
        foreach (int port in CandidatePorts)
        {
            ct.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，继续尝试下一个。
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.PortUnavailable), ErrorCode.PortUnavailable);
    }

    public async Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (_stateMachine.Current != AppState.Connected || _isSwitching)
        {
            return Inconclusive();
        }

        // 不排队：重复点击应当拿回当前结论，而不是把一串自检堆在锁后面依次重放。
        if (!await _checkLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return _lastCheck ?? Inconclusive();
        }

        CancellationTokenSource? linked = null;
        try
        {
            int port;
            int probePort;
            string egress;
            long epoch;
            lock (_checkSync)
            {
                // 等待自检锁期间可能已开始重建链路，快照前再次检查。
                if (Volatile.Read(ref _disposed) != 0 || _stateMachine.Current != AppState.Connected || _isSwitching)
                {
                    return Inconclusive();
                }

                port = _currentPort;
                probePort = ProbePort;
                egress = _currentProfile.Server;
                epoch = _connectionEpoch;
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _checkCts = linked;
            }

            _isChecking = true;
            RaiseCheckChanged();

            ConnectionCheckResult result = await _network
                .CheckConnectionAsync(ProxyHost, probePort, egress, linked.Token)
                .ConfigureAwait(false);

            // 期间断开、换过端口、或整条连接被重建过：这份结论描述的不再是当前连接，
            // 丢弃比展示旧值安全。代数这一条是必需的——停止后立刻重连往往拿回同一个
            // 端口，只比对端口会让旧结论冒充成新连接的「已确认走服务器」。
            lock (_checkSync)
            {
                if (Volatile.Read(ref _disposed) != 0
                    || linked.IsCancellationRequested
                    || _stateMachine.Current != AppState.Connected
                    || _isSwitching
                    || _currentPort != port
                    || _connectionEpoch != epoch)
                {
                    return Inconclusive();
                }

                _lastCheck = result;
            }

            _log.Info(
                nameof(ConnectionController),
                $"Connection check: reachable={result.Reachable}, latencyMs={result.LatencyMs}, " +
                $"samples={result.SampleCount}, egress={result.Egress}");
            return result;
        }
        catch (OperationCanceledException)
        {
            // 停止连接会取消这一轮；此时 UI 已经不在 Connected，结果不会被展示。
            return Inconclusive();
        }
        finally
        {
            lock (_checkSync)
            {
                if (ReferenceEquals(_checkCts, linked))
                {
                    _checkCts = null;
                }
            }
            linked?.Dispose();
            _isChecking = false;
            _checkLock.Release();
            RaiseCheckChanged();
        }
    }

    /// <summary>无法给出结论时的统一返回：不写入 <see cref="_lastCheck"/>，也不改变任何已验证状态。</summary>
    private static ConnectionCheckResult Inconclusive() => new()
    {
        Reachable = false,
        Egress = EgressVerdict.Unknown,
        Error = ErrorCode.ConnectTestFailed
    };

    private void ClearLastCheck()
    {
        bool hadResult;
        CancellationTokenSource? pending;
        lock (_checkSync)
        {
            // 即使尚无结论，也必须让正在进行的请求失效。
            _connectionEpoch++;
            hadResult = _lastCheck is not null;
            _lastCheck = null;
            pending = _checkCts;
        }

        CancelPending(pending);

        if (hadResult)
        {
            RaiseCheckChanged();
        }
    }

    private static void CancelPending(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 该操作已经在自己的 finally 中完成清理。
        }
    }

    private void RaiseCheckChanged() => RaiseOnUiThread(() => CheckChanged?.Invoke());

    /// <summary>
    /// 挑一个不与代理端口冲突的统计端口。全被占用时退回 0 表示「这次不开统计」——
    /// 统计端口挑不到绝不能让连接失败。
    /// </summary>
    private int SelectStatsPort(int localPort, CancellationToken ct)
    {
        foreach (int port in CandidateStatsPorts)
        {
            ct.ThrowIfCancellationRequested();
            if (port == localPort)
            {
                continue;
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，继续尝试下一个。
            }
        }

        _log.Warn(nameof(ConnectionController), "No stats port available; traffic rate will be unavailable.");
        return 0;
    }

    /// <summary>
    /// 连通性探测要用的端口：探测入站的端口；挑不到探测端口时退回代理端口。
    /// </summary>
    private int ProbePort => _currentProbePort != 0 ? _currentProbePort : _currentPort;

    /// <summary>
    /// 挑一个探测入站端口。全被占用时退回 0：探测改走代理端口（先前的行为），并留下
    /// 一条警告——那时智能分流模式下的测通不再能证明隧道可用。候选有六个，实际几乎
    /// 不会发生；为它让整个连接失败，代价比收益大。
    /// </summary>
    private int SelectProbePort(int localPort, int statsPort, CancellationToken ct)
    {
        foreach (int port in CandidateProbePorts)
        {
            ct.ThrowIfCancellationRequested();
            if (port == localPort || port == statsPort)
            {
                continue;
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，继续尝试下一个。
            }
        }

        _log.Warn(nameof(ConnectionController), "No probe port available; the connectivity test falls back to the proxy port.");
        return 0;
    }

    /// <summary>代理端口与探测端口都能连上才算 xray 就绪：探测紧接着就要用后者。</summary>
    private async Task WaitForInboundsReadyAsync(int proxyPort, CancellationToken ct)
    {
        await WaitForPortReadyAsync(proxyPort, ct).ConfigureAwait(false);
        if (_currentProbePort != 0)
        {
            await WaitForPortReadyAsync(_currentProbePort, ct).ConfigureAwait(false);
        }
    }

    private void StartTrafficLoop()
    {
        if (_trafficStats is null || _currentStatsPort == 0)
        {
            return;
        }

        var source = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_trafficSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                source.Dispose();
                return;
            }
            previous = _trafficCts;
            _trafficCts = source;
        }
        CancelPending(previous);

        _ = RunTrafficLoopAsync(source, _currentStatsPort);
    }

    private void CancelTrafficLoop()
    {
        CancellationTokenSource? source;
        lock (_trafficSync)
        {
            source = _trafficCts;
            _trafficCts = null;
        }
        CancelPending(source);
    }

    private async Task RunTrafficLoopAsync(CancellationTokenSource source, int apiPort)
    {
        try
        {
            using var timer = new PeriodicTimer(TrafficSampleInterval);
            long nextUsageSampleAt = 0;
            while (await timer.WaitForNextTickAsync(source.Token).ConfigureAwait(false))
            {
                // 类别归因不看窗口是否可见：常驻托盘才是常态。它以前挂在下面那条
                // 「可见才采」的路径上，自启后一直在托盘的设备几乎不上报任何类别。
                if (Environment.TickCount64 >= nextUsageSampleAt &&
                    !_isSwitching && _stateMachine.Current == AppState.Connected)
                {
                    nextUsageSampleAt = Environment.TickCount64 + (long)_usageSampleInterval.TotalMilliseconds;
                    await SampleCategoriesAsync(apiPort, source).ConfigureAwait(false);
                }

                long epoch;
                lock (_trafficSync)
                {
                    if (_trafficSuspended || _isSwitching || _stateMachine.Current != AppState.Connected)
                    {
                        continue;
                    }
                    epoch = _trafficEpoch;
                }

                TrafficCounters? counters = await _trafficStats!
                    .QueryAsync(apiPort, source.Token)
                    .ConfigureAwait(false);
                if (counters is null)
                {
                    continue;
                }

                lock (_trafficSync)
                {
                    // Query 可能忽略取消或恰好完成：停止、重连、隐藏再显示后均不能写回旧值。
                    if (!ReferenceEquals(_trafficCts, source) || source.IsCancellationRequested ||
                        _trafficSuspended || _isSwitching || _stateMachine.Current != AppState.Connected ||
                        _trafficEpoch != epoch)
                    {
                        continue;
                    }
                    _trafficMonitor.Add(counters.Value, Environment.TickCount64);
                }
                RaiseTrafficChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            // 采集挂掉只该让速率图停住，不该影响连接本身。
            _log.Warn(nameof(ConnectionController), $"Traffic loop stopped: {ex.Message}");
        }
        finally
        {
            lock (_trafficSync)
            {
                if (ReferenceEquals(_trafficCts, source))
                {
                    _trafficCts = null;
                }
            }

            source.Dispose();
        }
    }

    public void SetTrafficSamplingSuspended(bool suspended)
    {
        lock (_trafficSync)
        {
            if (_trafficSuspended == suspended)
            {
                return;
            }

            _trafficSuspended = suspended;
            _trafficEpoch++;
            if (suspended)
            {
                return;
            }

            // 恢复时清掉历史与基准，第一拍不能采用隐藏前的查询结果。
            _trafficMonitor.Reset();
        }
        RaiseTrafficChanged();
    }

    private void RaiseTrafficChanged() => RaiseOnUiThread(() => TrafficChanged?.Invoke());

    private async Task WaitForPortReadyAsync(int port, CancellationToken ct)
    {
        for (int i = 0; i < PortReadyProbeCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            // xray 一启动就退出（配置被拒、端口被抢）时立刻失败。退出事件的处理器要等
            // _flowLock，在这条流程结束之前根本轮不到它；不在这里看一眼，就要把 50 次
            // 探测探满——Windows 上连一个没人监听的回环端口每次还要等约 2 秒，合起来
            // 「正在启动」要卡一两分钟才报错。
            if (!_xray.IsRunning)
            {
                _log.Warn(nameof(ConnectionController), "xray exited before its inbound came up");
                break;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await Task.Delay(PortReadyProbeDelayMs, ct).ConfigureAwait(false);
            }
        }

        throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.XrayStartFailed), ErrorCode.XrayStartFailed);
    }

    /// <summary>
    /// 采一次 per-tag 计数器喂给用量上报。整段是尽力而为：任何失败都只是少一次
    /// 归因采样，绝不能影响连接本身，所以它吞掉自己的异常并且从不改状态机。
    /// </summary>
    private async Task SampleCategoriesAsync(int apiPort, CancellationTokenSource source)
    {
        if (_usageReporter is null || !_categoryAttributionEnabled())
        {
            return;
        }

        try
        {
            IReadOnlyDictionary<string, TrafficCounters>? byTag = await _trafficStats!
                .QueryByTagAsync(apiPort, source.Token)
                .ConfigureAwait(false);
            if (byTag is { Count: > 0 })
            {
                await _usageReporter.ObserveAsync(byTag, source.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(ConnectionController), $"Usage sampling failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 停止前把这一小时攒下的类别量发出去（见 <see cref="IUsageReporter.FlushAsync"/>）。
    /// 只在这里等那次本地采样——内核还活着，毫秒级，上限 1 秒；上报放到后台，停止不等网络。
    /// </summary>
    private async Task FlushUsageBeforeStopAsync()
    {
        IUsageReporter? reporter = _usageReporter;
        if (reporter is null || _trafficStats is null || _currentStatsPort == 0 ||
            _stateMachine.Current != AppState.Connected || !_categoryAttributionEnabled())
        {
            return;
        }

        IReadOnlyDictionary<string, TrafficCounters>? finalSample = null;
        try
        {
            using var budget = new CancellationTokenSource(FinalUsageSampleBudget);
            finalSample = await _trafficStats
                .QueryByTagAsync(_currentStatsPort, budget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(ConnectionController), $"Final usage sample failed: {ex.GetType().Name}");
        }

        _ = FlushUsageAsync(reporter, finalSample);
    }

    private async Task FlushUsageAsync(
        IUsageReporter reporter,
        IReadOnlyDictionary<string, TrafficCounters>? finalSample)
    {
        try
        {
            if (finalSample is { Count: > 0 })
            {
                await reporter.ObserveAsync(finalSample, CancellationToken.None).ConfigureAwait(false);
            }

            await reporter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 观测是尽力而为，绝不能影响停止本身。
            _log.Warn(nameof(ConnectionController), $"Usage flush failed: {ex.GetType().Name}");
        }
    }

    private async Task<string> GenerateConfigAsync(ServerProfile profile, ProxyMode mode, int port, CancellationToken ct)
    {
        // 每次生成配置之后都是一次内核（重）启动，新进程的计数器从零开始：先忘掉
        // 归因的基线。已经攒下的待上报量保留（见 UsageAccumulator.ResetBaseline）。
        _usageReporter?.ResetBaseline();

        XrayRootConfig config = XrayConfigGenerator.Generate(
            profile,
            mode,
            port,
            Path.Combine(_storage.LogDir, "xray.log"),
            // 没有采集器、或候选端口全被占用（SelectStatsPort 返回 0）时都不开统计通道。
            // 0 必须在这里挡掉：Generate 会拒绝 1024 以下的端口，漏过去就是一次
            // ArgumentException 把整个 StartAsync 打掉——统计是可观测性，绝不能让连接失败。
            _trafficStats is null || _currentStatsPort == 0 ? null : _currentStatsPort,
            // 归因出站只在统计通道本来就开着、且服务端为本设备打开开关时才生成。
            // 关闭时配置与开启前逐字节一致。
            categoryAttribution: _trafficStats is not null
                && _currentStatsPort != 0
                && _categoryAttributionEnabled(),
            probePort: _currentProbePort == 0 ? null : _currentProbePort);

        string json = JsonSerializer.Serialize(config, XrayConfigGenerator.CreateJsonOptions());
        string path = Path.Combine(_storage.RuntimeDir, "config.json");
        Directory.CreateDirectory(_storage.RuntimeDir);
        string tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmpPath, path, overwrite: true);
        return path;
    }

    private async Task SetModeAsync(ProxyMode mode, CancellationToken ct)
    {
        await _storage.UpdateSettingsAsync(settings => settings.ProxyMode = mode, ct).ConfigureAwait(false);
        _mode = mode;
        RaiseModeChanged();
    }

    private async Task TryRollbackModeSwitchAsync(ProxyMode oldMode, CancellationToken ct)
    {
        try
        {
            string oldConfigPath = await GenerateConfigAsync(_currentProfile, oldMode, _currentPort, ct).ConfigureAwait(false);
            await _xray.RestartAsync(oldConfigPath, CoreDir, ct).ConfigureAwait(false);
            await WaitForInboundsReadyAsync(_currentPort, ct).ConfigureAwait(false);

            LatencyResult latency = await _network.TestThroughProxyAsync(ProxyHost, ProbePort, ct).ConfigureAwait(false);
            if (!latency.Success)
            {
                throw new MyProxyException(ErrorCodeMessages.Get(latency.Error), latency.Error);
            }

            _latencyMs = latency.LatencyMs;
            _mode = oldMode;
            RaiseModeChanged();
            _log.Info(nameof(ConnectionController), $"Mode rollback to {oldMode} succeeded");
        }
        catch (Exception rollbackEx)
        {
            _log.Error(nameof(ConnectionController), $"Mode rollback failed: {rollbackEx.Message}", rollbackEx);
            _mode = oldMode;
            await AbandonConnectionAfterFailedRollbackAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 切模式或配置热更新失败、回滚到旧配置也失败时的收尾：手上已经没有一份验证过能用的
    /// 内核配置，连接只能放弃。与意外退出（<see cref="HandleUnexpectedXrayExitAsync"/>）
    /// 同一组动作，顺序也一样——先恢复系统代理，成功才停 xray。
    ///
    /// 切模式那条路径以前只转 Error：注册表继续指向本地端口，xray 若已死浏览器全断，
    /// 而进了 Error 之后意外退出处理器也不会再管它；xray 若还活着，用户点「重试」时
    /// <c>XrayService.StartAsync</c> 因「已经在运行」抛出，第一次重试必然失败。
    /// </summary>
    private async Task AbandonConnectionAfterFailedRollbackAsync()
    {
        CancelHeartbeatLoop();
        CancelTrafficLoop();
        bool proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        _latencyMs = null;
        ClearLastCheck();
        _trafficMonitor.Reset();
        RaiseTrafficChanged();
        _lastErrorCode = ErrorCode.ConnectTestFailed;
        _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.ConnectTestFailed);
        if (!proxyRestored)
        {
            SetProxyRestoreFailure();
        }

        if (_stateMachine.Current == AppState.Connected)
        {
            _stateMachine.Transition(ConnectionStateEvent.SwitchFailure);
        }
    }

    private void StartHeartbeatLoop()
    {
        var source = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_heartbeatSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                source.Dispose();
                return;
            }
            previous = _heartbeatCts;
            _heartbeatCts = source;
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 旧循环已经自行结束。
        }

        _ = RunHeartbeatLoopAsync(source);
    }

    private void CancelHeartbeatLoop()
    {
        CancellationTokenSource? source;
        lock (_heartbeatSync)
        {
            source = _heartbeatCts;
            _heartbeatCts = null;
        }

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 循环已经自行结束。
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(source.Token).ConfigureAwait(false))
            {
                try
                {
                    HeartbeatResult heartbeat = await _config.HeartbeatAsync(source.Token).ConfigureAwait(false);
                    SetServerIdentityUnverified(false);
                    // 心跳带回来的 release 身份与 feature flags 由订阅者处理。
                    // 控制器本身不认识 Update Plane：让它去 new 一个 coordinator
                    // 会把「连接」和「更新」两件事焊死在一起。
                    HeartbeatReceived?.Invoke(heartbeat);
                    if (heartbeat.ConfigVersion > Interlocked.Read(ref _currentConfigVersion))
                    {
                        await ApplyConfigUpdateAsync(heartbeat.ConfigVersion, source.Token).ConfigureAwait(false);
                    }
                }
                catch (MyProxyException ex) when (ex.ErrorCode == ErrorCode.TokenInvalid)
                {
                    await HandleHeartbeatTokenInvalidAsync(source).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    return;
                }
                catch (MyProxyException ex) when (ex.ErrorCode == ErrorCode.ServerUntrusted)
                {
                    // 服务器证书校验失败：重试不会好，配置更新与吊销都到不了这台设备。数据面照常，
                    // 所以不打断连接，只让主页在「已连接」下说出原因，而不是一条看起来重试就会好的
                    // Warn 日志。
                    _log.Error(nameof(ConnectionController), "Heartbeat rejected the server certificate chain, validity or hostname");
                    SetServerIdentityUnverified(true);
                }
                catch (Exception ex)
                {
                    _log.Warn(nameof(ConnectionController), $"Heartbeat failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // 正常停止后台循环。
        }
        catch (Exception ex)
        {
            _log.Error(nameof(ConnectionController), "Heartbeat loop stopped unexpectedly", ex);
        }
        finally
        {
            lock (_heartbeatSync)
            {
                if (ReferenceEquals(_heartbeatCts, source))
                {
                    _heartbeatCts = null;
                }
            }

            source.Dispose();
        }
    }

    private async Task ApplyConfigUpdateAsync(long advertisedVersion, CancellationToken ct)
    {
        await _flowLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested ||
                _stateMachine.Current != AppState.Connected ||
                advertisedVersion <= Interlocked.Read(ref _currentConfigVersion))
            {
                return;
            }

            SetSwitching(true);
            ServerProfile oldProfile = _currentProfile;
            long oldVersion = Interlocked.Read(ref _currentConfigVersion);
            int? oldLatency = _latencyMs;
            bool runtimeChanged = false;

            try
            {
                ConfigCacheEntry candidate = await _config.GetConfigAsync(ct).ConfigureAwait(false);
                if (candidate.ConfigVersion <= oldVersion)
                {
                    return;
                }

                string configPath = await GenerateConfigAsync(
                    candidate.Profile,
                    _mode,
                    _currentPort,
                    ct).ConfigureAwait(false);

                runtimeChanged = true;
                await _xray.RestartAsync(configPath, CoreDir, ct).ConfigureAwait(false);
                await WaitForInboundsReadyAsync(_currentPort, ct).ConfigureAwait(false);

                LatencyResult latency = await _network
                    .TestThroughProxyAsync(ProxyHost, ProbePort, ct)
                    .ConfigureAwait(false);
                if (!latency.Success)
                {
                    throw new MyProxyException(ErrorCodeMessages.Get(latency.Error), latency.Error);
                }

                if (!candidate.Verified)
                {
                    await _config
                        .PromoteCurrentConfigAsync(candidate.Profile, candidate.ConfigVersion, ct)
                        .ConfigureAwait(false);
                }

                _currentProfile = candidate.Profile;
                Interlocked.Exchange(ref _currentConfigVersion, candidate.ConfigVersion);
                _latencyMs = latency.LatencyMs;
                ClearLastCheck();
                _log.Info(
                    nameof(ConnectionController),
                    $"Config updated. ConfigVersion={candidate.ConfigVersion}, LatencyMs={_latencyMs}");
            }
            catch (MyProxyException ex) when (ex.ErrorCode == ErrorCode.TokenInvalid)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    nameof(ConnectionController),
                    $"Config update failed. AdvertisedVersion={advertisedVersion}: {ex.Message}");

                if (runtimeChanged)
                {
                    await TryRollbackConfigUpdateAsync(oldProfile, oldVersion, oldLatency).ConfigureAwait(false);
                }
            }
            finally
            {
                SetSwitching(false);
            }
        }
        finally
        {
            _flowLock.Release();
        }
    }

    private async Task TryRollbackConfigUpdateAsync(
        ServerProfile oldProfile,
        long oldVersion,
        int? oldLatency)
    {
        try
        {
            string oldConfigPath = await GenerateConfigAsync(
                oldProfile,
                _mode,
                _currentPort,
                CancellationToken.None).ConfigureAwait(false);
            await _xray.RestartAsync(oldConfigPath, CoreDir, CancellationToken.None).ConfigureAwait(false);
            await WaitForInboundsReadyAsync(_currentPort, CancellationToken.None).ConfigureAwait(false);

            LatencyResult latency = await _network
                .TestThroughProxyAsync(ProxyHost, ProbePort, CancellationToken.None)
                .ConfigureAwait(false);
            if (!latency.Success)
            {
                throw new MyProxyException(ErrorCodeMessages.Get(latency.Error), latency.Error);
            }

            _currentProfile = oldProfile;
            Interlocked.Exchange(ref _currentConfigVersion, oldVersion);
            _latencyMs = latency.LatencyMs ?? oldLatency;
            _log.Info(nameof(ConnectionController), $"Config rollback succeeded. ConfigVersion={oldVersion}");
        }
        catch (Exception rollbackEx)
        {
            _log.Error(nameof(ConnectionController), "Config rollback failed", rollbackEx);
            await AbandonConnectionAfterFailedRollbackAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleHeartbeatTokenInvalidAsync(CancellationTokenSource source)
    {
        // Do not let a stale heartbeat clear a binding created by a newer
        // connection after StopAsync/StartAsync raced with this response.
        lock (_heartbeatSync)
        {
            if (!ReferenceEquals(_heartbeatCts, source))
            {
                return;
            }
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await _flowLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_heartbeatSync)
            {
                if (!ReferenceEquals(_heartbeatCts, source))
                {
                    return;
                }
            }

            if (_stateMachine.Current != AppState.Unbound)
            {
                await HandleTokenInvalidAsync(cancelHeartbeatLoop: false).ConfigureAwait(false);
            }
        }
        finally
        {
            _flowLock.Release();
        }
    }

    private async Task HandleTokenInvalidAsync(bool cancelHeartbeatLoop = true)
    {
        if (cancelHeartbeatLoop)
        {
            CancelHeartbeatLoop();
        }
        CancelTrafficLoop();
        ClearLastCheck();
        _trafficMonitor.Reset();
        RaiseTrafficChanged();
        bool proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        if (!proxyRestored)
        {
            SetProxyRestoreFailure();
            if (_stateMachine.Current == AppState.Connecting)
            {
                _stateMachine.Transition(ConnectionStateEvent.Failure);
            }
            else if (_stateMachine.Current == AppState.Connected)
            {
                _stateMachine.Transition(ConnectionStateEvent.SwitchFailure);
            }
            else
            {
                RaiseStateChanged();
            }

            return;
        }

        try
        {
            await _storage.ClearBindingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(nameof(ConnectionController), "ClearBindingAsync failed", ex);
            _latencyMs = null;
            _lastErrorCode = ErrorCode.TokenInvalid;
            _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.TokenInvalid);

            if (_stateMachine.Current == AppState.Connecting)
            {
                _stateMachine.Transition(ConnectionStateEvent.Failure);
            }
            else if (_stateMachine.Current == AppState.Connected)
            {
                _stateMachine.Transition(ConnectionStateEvent.SwitchFailure);
            }
            else
            {
                RaiseStateChanged();
            }

            return;
        }

        if (_stateMachine.Current is AppState.Connecting or AppState.Connected or AppState.Disconnected or AppState.Error)
        {
            _stateMachine.Transition(ConnectionStateEvent.Rebind);
        }

        _latencyMs = null;
        _lastErrorCode = ErrorCode.TokenInvalid;
        _lastErrorMessage = ErrorCodeMessages.Get(ErrorCode.TokenInvalid);
        RaiseBindingRequired();
    }

    private void OnXrayExited(object? sender, XrayExitEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || args.Expected || Interlocked.Exchange(ref _handlingUnexpectedExit, 1) != 0)
        {
            return;
        }

        _ = HandleUnexpectedXrayExitAsync();
    }

    private async Task HandleUnexpectedXrayExitAsync()
    {
        try
        {
            await _flowLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    _stateMachine.Current is not (AppState.Connecting or AppState.Connected) || _xray.IsRunning)
                {
                    return;
                }

                // 取消必须在 bail-out 之后：StartHeartbeatLoop 只在 StartAsync 的成功
                // 路径上调用一次，所以在此处提前取消会让「新 xray 立即退出、回滚成功、
                // 状态仍是 Connected」的场景永久失去心跳 —— 不再更新 configVersion，
                // 也不再能发现 401/撤销，直到用户手动 Stop→Start。
                CancelHeartbeatLoop();
                _cts?.Cancel();
                ErrorCode errorCode = _stateMachine.Current == AppState.Connected
                    ? ErrorCode.ConnectTestFailed
                    : ErrorCode.XrayStartFailed;

                CancelTrafficLoop();
                // 内核已死，采不到最后一笔；手上攒下的照样发出去。
                if (_usageReporter is { } reporter)
                {
                    _ = FlushUsageAsync(reporter, finalSample: null);
                }

                bool proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _latencyMs = null;
                // 与 StopAsync 的 finally 同一组收尾：xray 已死，留着上一分钟的波形和
                // 上一次的自检结论，界面会一边说「连接失败」一边显示流量还在跑。
                ClearLastCheck();
                _trafficMonitor.Reset();
                RaiseTrafficChanged();
                _lastErrorCode = errorCode;
                _lastErrorMessage = ErrorCodeMessages.Get(errorCode);
                if (!proxyRestored)
                {
                    // 代理未恢复比触发它的错误更严重：xray 已死而注册表仍指向
                    // 那个本地端口，用户上网直接不通，重试文案帮不上忙。
                    SetProxyRestoreFailure();
                }

                if (_stateMachine.Current == AppState.Connecting)
                {
                    _stateMachine.Transition(ConnectionStateEvent.Failure);
                }
                else
                {
                    _stateMachine.Transition(ConnectionStateEvent.SwitchFailure);
                }

                _log.Error(nameof(ConnectionController), $"Xray exited unexpectedly. ErrorCode={errorCode}");
            }
            finally
            {
                _flowLock.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Error(nameof(ConnectionController), "Unexpected xray exit handling failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _handlingUnexpectedExit, 0);
        }
    }

    private async Task<bool> RollbackAsync(CancellationToken ct)
    {
        bool proxyRestored = false;
        try
        {
            await _proxy.RestoreAsync(ct).ConfigureAwait(false);
            proxyRestored = true;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(ConnectionController), "Rollback proxy restore failed", ex);
        }

        if (proxyRestored)
        {
            try
            {
                await _xray.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(nameof(ConnectionController), "Rollback xray stop failed", ex);
            }
        }
        else
        {
            _log.Warn(
                nameof(ConnectionController),
                "系统代理尚未确认恢复，保留 xray 进程与 pid 以避免本地代理端口失效");
        }

        CleanupRuntimeFiles();
        return proxyRestored;
    }

    private void SetProxyRestoreFailure()
    {
        _latencyMs = null;
        _lastErrorCode = ErrorCode.ProxyApplyFailed;
        _lastErrorMessage = ErrorCodeMessages.ProxyRestoreFailed;
    }

    private async Task RollbackAndSetErrorAsync(Exception ex)
    {
        _log.Error(nameof(ConnectionController), "Start failed", ex);
        bool proxyRestored = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);

        MyProxyException? myEx = ex as MyProxyException;
        _lastErrorCode = myEx?.ErrorCode ?? ErrorCode.Unknown;
        _lastErrorMessage = myEx?.FriendlyMessage ?? ErrorCodeMessages.Get(ErrorCode.Unknown);
        _latencyMs = null;
        if (!proxyRestored)
        {
            SetProxyRestoreFailure();
        }

        if (_stateMachine.Current == AppState.Connecting)
        {
            _stateMachine.Transition(ConnectionStateEvent.Failure);
        }
        else if (_stateMachine.Current == AppState.Connected)
        {
            _stateMachine.Transition(ConnectionStateEvent.SwitchFailure);
        }
        else
        {
            RaiseStateChanged();
        }
    }

    private void CleanupRuntimeFiles()
    {
        try
        {
            string configPath = Path.Combine(_storage.RuntimeDir, "config.json");
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(ConnectionController), $"Delete config.json failed: {ex.Message}");
        }
    }

    private void SetSwitching(bool value)
    {
        if (value)
        {
            // 新一轮切换开始：清掉上一次失败留下的提示，它与「正在切换…」共用同一预留槽。
            _lastSwitchErrorMessage = null;
        }

        if (_isSwitching == value)
        {
            return;
        }

        _isSwitching = value;
        if (value)
        {
            lock (_trafficSync)
            {
                _trafficEpoch++;
                _trafficMonitor.Reset();
            }
            RaiseTrafficChanged();
            // 在重启前失效，失败后回滚也不能重新采用重建前的检测结果。
            ClearLastCheck();
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => RaiseOnUiThread(() => StateChanged?.Invoke());

    private void SetServerIdentityUnverified(bool unverified)
    {
        if (_serverIdentityUnverified == unverified)
        {
            return;
        }

        _serverIdentityUnverified = unverified;
        RaiseStateChanged();
    }

    private void RaiseModeChanged() => RaiseOnUiThread(() => ModeChanged?.Invoke());

    private void RaiseBindingRequired() => RaiseOnUiThread(() => BindingRequired?.Invoke());

    private void RaiseOnUiThread(Action action)
    {
        // 未注入封送器时就地触发。界面进程注入各平台的实现：Windows 是
        // WpfUiDispatcher，Linux 是 Avalonia 的；无界面进程（CLI / systemd）不注入。
        if (_uiDispatcher is null)
        {
            action();
            return;
        }

        _uiDispatcher.Post(action);
    }
}
