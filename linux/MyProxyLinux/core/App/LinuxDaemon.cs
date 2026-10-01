using System.Runtime.InteropServices;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy;

/// <summary>
/// 守护进程（<c>myproxy run</c>）：连接的唯一持有者。
///
/// <para>
/// 启动顺序与 Windows 端的 <c>App.OnStartup</c> 逐条对应，理由也一样：
/// </para>
///
/// <list type="number">
/// <item><b>单实例</b>：已经有别的实例在跑就请求它把界面唤出来，然后自己退出；</item>
/// <item><b>崩溃收尾</b>（<see cref="ICrashRecoveryService.Run"/>）：先恢复系统代理，
/// 成功之后才清理孤儿内核；</item>
/// <item><b>更新恢复</b>（<see cref="LinuxUpdateCoordinator.RecoverAsync"/>）：
/// 上一次交换被打断时，安装目录可能是半个，这时不能先去连网；</item>
/// <item>设置读入 → 模式初始化 → 缓存的 feature flags 先喂给更新器
/// （否则常驻进程第一次连接会用出厂默认值生成配置）；</item>
/// <item>已绑定则 <c>MarkBound</c> → 起控制口 → 需要时自动连接。</item>
/// </list>
///
/// <para>
/// 离场路径只有一条（不像 Windows 有托盘退出与注销关机两条）：收到
/// <c>SIGTERM</c>/<c>SIGINT</c> 时跑一次 <see cref="IConnectionController.StopAsync"/>，
/// 它内部的 fail-open 顺序是「先恢复系统代理，只有恢复成功才停内核」。
/// </para>
/// </summary>
public sealed class LinuxDaemon : IAsyncDisposable
{
    /// <summary>systemd 的 <c>Restart=on-failure</c> 会因为这个退出码把我们拉起来。</summary>
    public const int ExitTempFail = 75;

    /// <summary>
    /// 更新后的试用期：新版本的守护进程稳定跑满这么久，才删备份、上报 installed。
    /// 比 systemd 的 <c>RestartSec</c> 长得多，一个起来就崩的版本撑不到这里。
    /// </summary>
    private static readonly TimeSpan ProbationWindow = TimeSpan.FromSeconds(60);

    private readonly LinuxAppServices _services;
    private readonly IReadOnlyList<string> _restartArguments;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private LinuxSingleInstanceService? _singleInstance;
    private ControlServer? _server;
    private bool _stopped;
    private bool _restartRequested;
    private string _updateMessage = "";
    private string _updateVersion = "";
    private bool _updateAvailable;
    private bool _updateMandatory;
    private string _updateReleaseId = "";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public LinuxDaemon(LinuxAppServices services, IReadOnlyList<string>? restartArguments = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _restartArguments = restartArguments ?? new[] { "run" };
    }

    public static string SocketPath(string? overridePath = null)
        => overridePath ?? LinuxPaths.RuntimeDir + "/control.sock";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        ClearInheritedProxyEnvironment();

        _singleInstance = new LinuxSingleInstanceService(_services.Storage, _services.SingleInstanceName);
        if (!_singleInstance.TryAcquire())
        {
            _services.Log.Warn(nameof(LinuxDaemon), "已有实例在运行，本次启动退出");
            _singleInstance.SignalActivate();
            _singleInstance.Dispose();
            return 0;
        }

        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, context => OnStopSignal(context));
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT, context => OnStopSignal(context));

        try
        {
            _services.Log.Info(nameof(LinuxDaemon), $"MyProxy {AppInfo.Version} ({AppInfo.Platform}) 启动");

            _services.CrashRecovery.Run();
            if (await _services.Updates.RecoverAsync(ct).ConfigureAwait(false))
            {
                // 安装目录已经换回了旧版本，而本进程还是新版本的映像：立刻换过去，
                // 不要带着一个已被回滚的映像去连网。
                _services.Log.Warn(nameof(LinuxDaemon), "新版本没能稳定运行，已换回旧版本，正在重启进入它");
                return _services.Updates.RestartIntoInstalledVersion(_restartArguments) ? 0 : ExitTempFail;
            }

            // 两套自启机制只留一个：都写着会让登录时起两个进程（第二个撞在单实例锁上）。
            _services.Startup.RemoveLegacyAutoStart();

            AppSettings settings = _services.Storage.LoadSettings();
            if (settings.AutoStart)
            {
                if (!_services.Startup.IsAutoStartEnabled())
                {
                    _services.Startup.SetAutoStartEnabled(true);
                }
                else
                {
                    // 旧版本写下的单元文件不会自己更新：换了版本之后，按本版本的内容重写一遍
                    // （内容相同就什么都不做）。
                    _services.Startup.RefreshAutoStartEntry();
                }
            }

            _services.Connection.InitializeMode(settings.ProxyMode);
            _services.Updates.UpdateFlags(FeatureFlags.FromValues(settings.FeatureFlags));
            _services.Updates.FlagsChanged += flags => _ = PersistFlagsAsync(flags);
            // 心跳把 feature flags 带回来；指派变更最多在一个心跳周期内到达在线客户端
            // （与 Windows 端 App.xaml.cs 同一条接线）。控制器生成配置时读的就是 Updates.Flags。
            _services.Connection.HeartbeatReceived += heartbeat =>
                _services.Updates.UpdateFlags(FeatureFlags.FromJson(heartbeat.FeatureFlags));
            _services.Connection.BindingRequired += () => _services.Updates.UpdateFlags(FeatureFlags.Empty);
            _services.Connection.StateChanged += () => _services.Log.Info(
                nameof(LinuxDaemon), $"状态：{_services.Connection.State}");

            if (_services.Storage.LoadDevice() is not null)
            {
                _services.Connection.MarkBound();
            }

            _server = new ControlServer(SocketPath(), HandleCommandAsync, _services.Log);
            _server.Start();
            _services.Log.Info(nameof(LinuxDaemon), $"控制口就绪：{_server.SocketPath}");

            Task updateLoop = Task.Run(() => UpdateLoopAsync(_stop.Token));
            Task probation = Task.Run(() => ConfirmUpdateAfterProbationAsync(_stop.Token));

            if (settings.AutoConnect && _services.Connection.State == AppState.Disconnected)
            {
                _ = _services.Connection.StartAsync(CancellationToken.None);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常离场。
            }

            await StopAsync().ConfigureAwait(false);
            await SafeAwait(updateLoop).ConfigureAwait(false);
            await SafeAwait(probation).ConfigureAwait(false);

            if (!_restartRequested)
            {
                // 走到这里是一次正常离场（信号或 shutdown 命令）：新版本起来了、收尾也做完了，
                // 试用期到此为止。不在试用期时这是空操作。
                await SafeAwait(_services.Updates.ConfirmHealthyAsync(CancellationToken.None)).ConfigureAwait(false);
            }
            else
            {
                // 原地换上刚交换进来的新版本。连接已经按 fail-open 顺序停干净了，
                // 所以 execv 之后新进程看到的是一个一致的状态（没有残留标记、
                // 内核不在跑），它的启动流程会照常做控制面同步与自启判定。
                _services.Log.Info(nameof(LinuxDaemon), "更新已应用，正在换上新的版本");
                if (!_services.Updates.RestartIntoInstalledVersion(_restartArguments))
                {
                    // execv 没换成（路径被改、权限不对）。用这个退出码让 systemd 的
                    // Restart=on-failure 把新版本拉起来——它同样会走到更新恢复那一步。
                    _services.Log.Warn(nameof(LinuxDaemon), "execv 失败，改由退出码触发重启");
                    return ExitTempFail;
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            _server?.Dispose();
            _singleInstance?.Dispose();
        }
    }

    /// <summary>
    /// 离场：只走 <see cref="IConnectionController.StopAsync"/>。它内部的顺序是
    /// 「先恢复系统代理，恢复成功才停内核」；这里不再补任何动作，
    /// 免得把那条顺序绕过去。
    /// </summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _services.Log.Info(nameof(LinuxDaemon), "正在停止");

            try
            {
                await _services.Connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 停止失败也要继续把剩下的收尾做完：这里能做的就是记下来，
                // 下一次启动的崩溃恢复会接着处理（它认的就是残留的标记文件）。
                _services.Log.Error(nameof(LinuxDaemon), "停止连接失败", ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnStopSignal(PosixSignalContext context)
    {
        // Cancel = true 阻止运行时默认的「立刻退出」：退出必须经过 StopAsync，
        // 否则系统代理会被留在那个已经死掉的本地端口上。
        context.Cancel = true;
        _services.Log.Info(nameof(LinuxDaemon), $"收到 {context.Signal}，开始收尾");
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _stop.Cancel();
            }
        });
    }

    private async Task PersistFlagsAsync(FeatureFlags flags)
    {
        try
        {
            await _services.Storage
                .UpdateSettingsAsync(settings =>
                {
                    settings.FeatureFlags.Clear();
                    foreach ((string name, string value) in flags.ToDictionary())
                    {
                        settings.FeatureFlags[name] = value;
                    }
                }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Warn(nameof(LinuxDaemon), $"保存 feature flags 失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 刚换上的新版本在试用期里：守护进程稳定跑满 <see cref="ProbationWindow"/> 才算它
    /// 真的能跑，这时删备份、上报 installed（见 <see cref="LinuxUpdateCoordinator"/>）。
    /// 不在试用期时直接返回。
    /// </summary>
    private async Task ConfirmUpdateAfterProbationAsync(CancellationToken ct)
    {
        if (!_services.Updates.IsAwaitingConfirmation)
        {
            return;
        }

        try
        {
            await Task.Delay(ProbationWindow, ct).ConfigureAwait(false);
            await _services.Updates.ConfirmHealthyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 试用期没满就离场了：正常离场由 RunAsync 收尾时确认，崩溃则留给下次启动计数。
        }
        catch (Exception ex)
        {
            _services.Log.Warn(nameof(LinuxDaemon), $"确认更新失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 定期看一眼有没有被指派的更新。<b>只报告，不安装</b>：安装是一次显式动作
    /// （<c>myproxy update apply</c>），与 Windows 端把安装留在用户点按钮之后同一条规矩。
    /// </summary>
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(6);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                AppSettings settings = _services.Storage.LoadSettings();
                if (!settings.AutoUpdateCheck)
                {
                    continue;
                }

                await PollAssignedUpdateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _services.Log.Warn(nameof(LinuxDaemon), $"更新检查失败：{ex.GetType().Name}");
            }
        }
    }

    /// <summary>拉一次指派并把结果写进状态；返回可安装的计划，没有就返回 null。</summary>
    private async Task<UpdatePlanResult?> PollAssignedUpdateAsync(CancellationToken ct)
    {
        UpdatePlanResult? plan = await _services.Updates.PollAsync(ct).ConfigureAwait(false);
        if (plan?.Manifest is null)
        {
            _updateAvailable = false;
            _updateMessage = "";
            return null;
        }

        _updateAvailable = true;
        _updateVersion = plan.Manifest.Version;
        _updateMandatory = plan.Mandatory;
        _updateReleaseId = plan.ReleaseId;
        _updateMessage = $"有新版本 {plan.Manifest.Version}";
        return plan;
    }

    private async Task<ControlResponse> HandleCommandAsync(ControlRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case ControlCommands.Ping:
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Status:
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Start:
                if (_services.Storage.LoadDevice() is null)
                {
                    return ControlResponse.Failure(ErrorCode.NotBound, ErrorCodeMessages.Get(ErrorCode.NotBound));
                }

                await _services.Connection.StartAsync(ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Stop:
                await _services.Connection.StopAsync(ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Mode:
                if (!TryParseMode(request.Argument, out ProxyMode mode))
                {
                    return ControlResponse.Failure(ErrorCode.Unknown, "模式只能是 smart 或 global。");
                }

                // 共享控制器对 Unbound/Connecting/Disconnecting/Error 下的切换是**静默返回**
                // （见 ConnectionController.SwitchModeAsync 的第一段判断）——那是 Windows GUI
                // 的语义：它的模式选择在非 Connected 时是禁用的，用户根本点不到。命令行没有
                // 禁用态，所以必须在这里挡住，否则「命令成功、状态没变」会变成一次无声的谎报。
                // 这是原生 Linux 冒烟测试逮到的第一个真问题。
                if (_services.Connection.State is AppState.Unbound)
                {
                    return ControlResponse.Failure(
                        ErrorCode.NotBound,
                        ErrorCodeMessages.Get(ErrorCode.NotBound));
                }

                if (_services.Connection.State is AppState.Connecting or AppState.Disconnecting or AppState.Error)
                {
                    return ControlResponse.Failure(
                        ErrorCode.Unknown,
                        "连接正在变化或处于失败状态，请先停止（或重试连接）再切换分流模式。");
                }

                await _services.Connection.SwitchModeAsync(mode, ct).ConfigureAwait(false);

                // 再核一遍：控制器的静默返回分支将来若增加，这里也不会把空操作报成成功。
                if (_services.Connection.Mode != mode)
                {
                    return ControlResponse.Failure(
                        ErrorCode.Unknown,
                        "分流模式没有切换成功（当前状态不允许切换）。");
                }

                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Check:
                if (_services.Connection.State != AppState.Connected)
                {
                    return ControlResponse.Failure(ErrorCode.Unknown, "只有已连接时才能检测。");
                }

                await _services.Connection.CheckConnectionAsync(ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Bind:
                await BindAsync(request.Argument, ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Unbind:
                await _services.Connection.RebindAsync(ct).ConfigureAwait(false);
                _services.Updates.UpdateFlags(FeatureFlags.Empty);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.AutoStart:
                await SetToggleAsync(
                    request.Argument,
                    sideEffect: value => _services.Startup.SetAutoStartEnabled(value),
                    mutate: static (settings, value) => settings.AutoStart = value,
                    ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.AutoConnect:
                await SetToggleAsync(
                    request.Argument,
                    sideEffect: static _ => { },
                    mutate: static (settings, value) => settings.AutoConnect = value,
                    ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Theme:
                if (!TryParseTheme(request.Argument, out UiTheme theme))
                {
                    return ControlResponse.Failure(ErrorCode.Unknown, "皮肤只能是 classic 或 porcelain。");
                }

                await _services.Storage
                    .UpdateSettingsAsync(settings => settings.UiTheme = theme, ct)
                    .ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.UpdateCheck:
                await PollAssignedUpdateAsync(ct).ConfigureAwait(false);
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.UpdateApply:
            {
                UpdatePlanResult? plan = await PollAssignedUpdateAsync(ct).ConfigureAwait(false);
                if (plan is null)
                {
                    return ControlResponse.Failure(ErrorCode.Unknown, "没有可安装的更新。");
                }

                // 准备失败就到此为止：绝不能退而去装磁盘上上一次暂存剩下的那个版本
                // （它可能已经被服务端撤销）。ApplyAsync 也只认这一次准备好的 releaseId。
                if (!await _services.Updates.PrepareAsync(plan, ct).ConfigureAwait(false))
                {
                    _updateMessage = "更新准备失败（详见日志与服务端上报）";
                    return ControlResponse.Failure(
                        ErrorCode.Unknown,
                        "更新准备失败（下载或校验没有通过），安装目录未被改动。");
                }

                _updateMessage = "已下载并验签，等待应用";
                if (!await _services.Updates.ApplyAsync(plan.ReleaseId, ct).ConfigureAwait(false))
                {
                    return ControlResponse.Failure(ErrorCode.Unknown, "应用更新失败，安装目录未被改动。");
                }

                // 目录已经换过了，但**现在跑的还是旧映像**——新版本要等进程重启才生效。
                // 延迟一下再收尾：这条响应得先发出去，调用方才不会以为命令失败了。
                // 收尾走的是正常离场路径（StopAsync 的 fail-open 顺序），随后
                // RunAsync 用 execv 原地换上新的自己。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                    _restartRequested = true;
                    _stop.Cancel();
                });
                return ControlResponse.Success(BuildStatus());
            }

            case ControlCommands.Activate:
                // 把已有窗口唤到前台是**界面自己**的事：托盘程序持有名为 "gui" 的
                // 单实例锁，第二个实例写的是同一个激活请求文件，由它自己轮询。
                // 守护进程没有窗口，收到这条命令只如实回一份状态。
                return ControlResponse.Success(BuildStatus());

            case ControlCommands.Shutdown:
                // 与 update apply 同一个道理：**响应必须先发出去**。这个任务一旦先跑完
                // StopAsync，RunAsync 的 finally 就会关掉控制口，调用方看到的是
                // 「守护进程没有回应」——一次成功的关闭被报成了故障（原生 Linux 冒烟
                // 测试里就是这样丢过一次响应）。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                    await StopAsync().ConfigureAwait(false);
                    _stop.Cancel();
                });
                return ControlResponse.Success(BuildStatus());

            default:
                return ControlResponse.Failure(ErrorCode.Unknown, $"未知命令：{request.Command}");
        }
    }

    private async Task BindAsync(string pairingCode, CancellationToken ct)
    {
        BindResult result = await _services.Binding.BindAsync(pairingCode, ct).ConfigureAwait(false);
        _services.Connection.MarkBound();
        _services.Log.Info(nameof(LinuxDaemon), $"绑定成功，configVersion={result.ConfigVersion}");
    }

    /// <summary>
    /// 开关类命令的统一处理：先做副作用（写单元文件 / 删 .desktop），再落设置。
    /// 副作用失败会抛出去变成一条失败响应——设置里写着「开机自启」而系统里没有，
    /// 比一次可见的失败更糟。
    /// </summary>
    private async Task SetToggleAsync(
        string argument,
        Action<bool> sideEffect,
        Action<AppSettings, bool> mutate,
        CancellationToken ct)
    {
        bool value = ParseToggle(argument);
        sideEffect(value);

        await _services.Storage
            .UpdateSettingsAsync(settings => mutate(settings, value), ct)
            .ConfigureAwait(false);
    }

    private StatusSnapshot BuildStatus()
    {
        IConnectionController connection = _services.Connection;
        DeviceConfig? device = TryLoadDevice();
        ConfigCacheEntry? cache = _services.Storage.LoadConfigCache();
        AppSettings settings = _services.Storage.LoadSettings();
        ConnectionCheckResult? check = connection.LastCheck;
        TrafficRate rate = connection.TrafficRate;

        return new StatusSnapshot
        {
            Version = AppInfo.Version,
            Platform = AppInfo.Platform,
            DaemonPid = Environment.ProcessId,
            StartedAt = _startedAt,
            Bound = device is not null,
            DeviceId = device?.DeviceId ?? "",
            DeviceName = device?.DeviceName ?? "",
            BoundAt = device?.BoundAt,
            ConfigVersion = cache?.ConfigVersion ?? 0,
            State = connection.State,
            Mode = connection.Mode,
            LatencyMs = connection.LatencyMs,
            LastErrorCode = connection.LastErrorCode,
            LastErrorMessage = connection.LastErrorMessage,
            LastSwitchErrorMessage = connection.LastSwitchErrorMessage,
            ServerIdentityUnverified = connection.ServerIdentityUnverified,
            IsChecking = connection.IsChecking,
            HasCheck = check is not null,
            CheckReachable = check?.Reachable ?? false,
            CheckLatencyMs = check?.LatencyMs,
            CheckBestLatencyMs = check?.BestLatencyMs,
            CheckSampleCount = check?.SampleCount ?? 0,
            CheckEgress = check?.Egress ?? EgressVerdict.Unknown,
            CheckError = check?.Error ?? ErrorCode.Unknown,
            UplinkBytesPerSecond = rate.UplinkBytesPerSecond,
            DownlinkBytesPerSecond = rate.DownlinkBytesPerSecond,
            AutoStart = settings.AutoStart,
            AutoConnect = settings.AutoConnect,
            Theme = settings.UiTheme,
            UpdateAvailable = _updateAvailable,
            UpdateVersion = _updateVersion,
            UpdateMandatory = _updateMandatory,
            UpdateReleaseId = _updateReleaseId,
            UpdateMessage = _updateMessage,
            LogDirectory = LinuxPaths.LogDir
        };
    }

    private DeviceConfig? TryLoadDevice()
    {
        try
        {
            return _services.Storage.LoadDevice();
        }
        catch (SecureStorageReadException ex)
        {
            // 读不出来不等于没绑定，但状态查询不该因此失败：如实报成未绑定，
            // 并把原因写进日志（连接流程自己会按读失败处理）。
            _services.Log.Error(nameof(LinuxDaemon), "读取设备凭据失败", ex);
            return null;
        }
    }

    private static bool TryParseMode(string argument, out ProxyMode mode)
    {
        switch ((argument ?? "").Trim().ToLowerInvariant())
        {
            case "smart":
            case "rule":
                mode = ProxyMode.Rule;
                return true;
            case "global":
                mode = ProxyMode.Global;
                return true;
            default:
                mode = ProxyMode.Rule;
                return false;
        }
    }

    private static bool TryParseTheme(string argument, out UiTheme theme)
    {
        switch ((argument ?? "").Trim().ToLowerInvariant())
        {
            case "classic":
                theme = UiTheme.Classic;
                return true;
            case "porcelain":
                theme = UiTheme.Porcelain;
                return true;
            default:
                theme = UiTheme.Classic;
                return false;
        }
    }

    private static bool ParseToggle(string argument)
        => (argument ?? "").Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes" or "enable" or "enabled";

    /// <summary>
    /// 把继承来的代理环境变量从本进程里清掉。
    ///
    /// <para>
    /// 理由有两条，都不是洁癖：<b>(1)</b> 我们自己写的
    /// <c>environment.d/myproxy.conf</c> 会在下一次登录时被 systemd 读进用户会话，
    /// 于是一次崩溃遗留的文件就会让新进程一启动就带着
    /// <c>http_proxy=127.0.0.1:10809</c>——而那个端口上的内核此刻还没起来。
    /// <b>(2)</b> 内核（xray）根本不读环境变量里的代理，控制面请求却会读，
    /// 于是「用户的网络需要代理才能出去」这件事对我们的数据面本来就不成立，
    /// 让控制面去走那个代理只会制造一个假的连通性问题。
    /// </para>
    /// </summary>
    private static void ClearInheritedProxyEnvironment()
    {
        foreach (string name in new[]
                 {
                     "http_proxy", "https_proxy", "all_proxy", "no_proxy",
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"
                 })
        {
            try
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            catch (Exception)
            {
                // 清不掉就算了：这只影响我们自己的出站，不影响正确性。
            }
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台循环的异常已经在它内部记过日志。
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _server?.Dispose();
        _singleInstance?.Dispose();
        _stop.Dispose();
        _lifecycle.Dispose();
    }
}
