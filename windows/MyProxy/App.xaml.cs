using System.Diagnostics;
using System.Windows;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if !DEBUG
        // 一道减速带，不是安全边界（见 AntiTamper）。Release 专属：Debug/测试构建
        // 保留可调试性。放在最前面，赶在任何 I/O 之前。
        MyProxy.Services.AntiTamper.GuardOrExit();
#endif

        // 交换安装目录的那个进程必须赶在任何界面、任何单实例锁之前分流出去：
        // 它是从暂存目录里那份已验签的 MyProxy.exe 拉起来的，任务只有等旧进程
        // 退出、换目录、把新版本拉起来，然后自己退出。抢单实例锁会让它把正在
        // 退出的旧进程挡在外面。
        if (UpdateApplier.TryParseFileArgs(
                e.Args,
                out string installExe,
                out string stagedExe,
                out string backupExe,
                out int fileParentPid))
        {
            RunFileUpdateApplier(installExe, stagedExe, backupExe, fileParentPid);
            Shutdown();
            return;
        }

        if (UpdateApplier.TryParseArgs(
                e.Args,
                out string installDir,
                out string stagedDir,
                out string backupDir,
                out int parentPid))
        {
            RunUpdateApplier(installDir, stagedDir, backupDir, parentPid);
            Shutdown();
            return;
        }

        if (e.Args.Contains(UpdateApplier.ApplySwitch) || e.Args.Contains(UpdateApplier.ApplyFileSwitch))
        {
            // Malformed update arguments must not fall through to the regular UI.
            Shutdown();
            return;
        }

        var singleInstance = new SingleInstanceService();
        if (!singleInstance.TryAcquire())
        {
            singleInstance.SignalActivate();
            Shutdown();
            return;
        }

        Services = new AppServices(singleInstance);
        Services.Log.Info(nameof(App), $"MyProxy {AppInfo.Version} started");

        Services.CrashRecovery.Run();

        // 上一次没做完的安装必须在窗口出现之前处理完：交换被打断时安装目录
        // 可能是半个，先把界面摆出来只会让用户对着一个随时会崩的程序操作。
        Services.Updates.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();

        AppSettings settings = Services.Storage.LoadSettings();
        // 自启动现在靠 schtasks，而 schtasks 会因为任务计划服务被禁用、组策略
        // 拦截、或者进程实际没拿到管理员权限而失败——这条以前是一次 HKCU 写入，
        // 基本不会失败，所以启动路径从来没考虑过它抛异常。修不好自启动是小事，
        // 因为它而起不来是大事：这里只记日志，让应用照常启动。设置页的开关另有
        // 自己的 try/catch，会把失败当场告诉用户。
        try
        {
            // 无条件跑：自启动开着与否都可能留着旧实现写下的 Run 项，而新的
            // SetAutoStartEnabled 只认识计划任务。幂等，没有残留时什么都不做。
            Services.Startup.RemoveLegacyAutoStart();

            if (settings.AutoStart && !Services.Startup.IsAutoStartEnabled())
            {
                Services.Startup.SetAutoStartEnabled(true);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Warn(nameof(App), $"无法登记开机自启动：{ex.GetType().Name}");
        }

        Services.Connection.InitializeMode(settings.ProxyMode);

        // 先恢复上次心跳带回的 flags，再订阅变化：恢复本身不该触发一次写回。
        Services.Updates.UpdateFlags(Core.FeatureFlags.FromValues(settings.FeatureFlags));
        Services.Updates.FlagsChanged += flags => _ = PersistFlagsAsync(flags);
        // 解绑（401 或手动重新绑定）之后，内存里的 flags 也属于上一台设备。
        Services.Connection.BindingRequired += () => Services.Updates.UpdateFlags(Core.FeatureFlags.Empty);

        // 必须赶在 new MainWindow() 之前：视图里的 {StaticResource} 在构造时就解析完了，
        // 晚一步换字典等于让用户先看见出厂皮肤再闪一下。
        Services.Theme.Apply(settings.UiTheme);

        if (IsProbablyBound())
        {
            Services.Connection.MarkBound();
        }

        MainWindow window = new();
        MainWindow = window;

        singleInstance.ActivateRequested += () => window.Dispatcher.InvokeAsync(() => ShowAndActivate(window));
        Services.Tray.OpenRequested += () => window.Dispatcher.InvokeAsync(() => ShowAndActivate(window));
        Services.Tray.StopRequested += () => _ = Services.Connection.StopAsync(CancellationToken.None);
        Services.Tray.ExitRequested += () => _ = ExitAsync(window);
        Services.Connection.StateChanged += () => window.Dispatcher.InvokeAsync(() =>
            Services.Tray.UpdateState(Services.Connection.State, Services.Connection.Mode));
        // 心跳把 feature flags 带回来；指派变更最多在一个心跳周期内到达在线客户端，
        // 客户端无需再轮询第二个接口。
        Services.Connection.HeartbeatReceived += heartbeat =>
            Services.Updates.UpdateFlags(Core.FeatureFlags.FromJson(heartbeat.FeatureFlags));
        Services.Connection.BindingRequired += () => window.Dispatcher.InvokeAsync(() =>
            window.ShowBindView(
                Services.Connection.LastErrorCode == ErrorCode.TokenInvalid
                    ? Services.Connection.LastErrorMessage
                    : null));

        window.Show();
        Services.Tray.Show();
        Services.Tray.UpdateState(Services.Connection.State, Services.Connection.Mode);

        if (e.Args.Contains("--autostart"))
        {
            window.Hide();
            if (settings.AutoConnect && Services.Connection.State == AppState.Disconnected)
            {
                _ = Services.Connection.StartAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 交换进程的全部工作。它不建窗口、不读设置、不碰注册表——只做目录搬运，
    /// 因为此刻安装目录正处在「旧的已移走、新的还没进来」的中间态。
    /// </summary>
    private static void RunUpdateApplier(string installDir, string stagedDir, string backupDir, int parentPid)
    {
        if (!UpdateApplier.WaitForParentExit(parentPid, UpdateApplier.ParentExitTimeout))
        {
            // 旧进程没退干净就动手，等于在一个仍被占用的目录上做移动。
            // 什么都不做，让启动时的恢复逻辑按 Applying 去还原。
            return;
        }

        SwapOutcome outcome = UpdateApplier.Swap(installDir, stagedDir, backupDir);
        string executable = System.IO.Path.Combine(installDir, UpdateInstaller.ExecutableName);
        if (!System.IO.File.Exists(executable))
        {
            return;
        }

        // 交换成功与否都要把安装目录里的程序拉起来：成功时是新版本，
        // 回滚后是旧版本。两种情况都由新进程读日志、收尾并上报。
        WriteApplyOutcome(installDir, stagedDir, backupDir, outcome);
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = installDir
            });
            _ = started;
        }
        catch (Exception)
        {
            // 拉不起来也不能在这里做别的：下次用户手动启动时，日志里的
            // Applying/Applied 会把状态收拾干净。
        }
    }

    private static void RunFileUpdateApplier(string installExe, string stagedExe, string backupExe, int parentPid)
    {
        if (!UpdateApplier.WaitForParentExit(parentPid, UpdateApplier.ParentExitTimeout)) return;

        SwapOutcome outcome = UpdateApplier.SwapExecutable(installExe, stagedExe, backupExe);
        if (!System.IO.File.Exists(installExe)) return;

        WriteApplyOutcome(System.IO.Path.GetDirectoryName(installExe)!,
            System.IO.Path.GetDirectoryName(stagedExe)!, backupExe, outcome);
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = installExe,
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(installExe)!
            });
            _ = started;
        }
        catch (Exception) { }
    }

    /// <summary>
    /// 把交换结果写回日志。交换进程不持有 AppServices，所以直接落盘，
    /// 字段与 <see cref="UpdateJournal"/> 保持一致。
    /// </summary>
    private static void WriteApplyOutcome(string installDir, string stagedDir, string backupDir, SwapOutcome outcome)
    {
        try
        {
            string journalPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(stagedDir) ?? "") ?? "",
                UpdateInstaller.JournalFileName);
            if (!System.IO.File.Exists(journalPath))
            {
                return;
            }

            UpdateJournal? journal = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(
                System.IO.File.ReadAllText(journalPath));
            if (journal is null)
            {
                return;
            }

            UpdateJournal updated = journal with
            {
                Stage = outcome.Ok ? UpdateStage.Applied
                    : outcome.RolledBack ? UpdateStage.RolledBack
                    : UpdateStage.Applying,
                BackupPath = backupDir,
                Detail = outcome.Detail,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            string temporary = journalPath + ".tmp";
            System.IO.File.WriteAllText(
                temporary,
                System.Text.Json.JsonSerializer.Serialize(updated, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temporary, journalPath, overwrite: true);
            _ = installDir;
        }
        catch (Exception)
        {
            // 日志写不下去时保持 Applying：启动恢复会按「交换被打断」还原备份，
            // 这是两害相权里安全的那一边。
        }
    }

    /// <summary>
    /// 系统代理在注销或关机时必须恢复，否则 HKCU 会继续指向 127.0.0.1 的本地端口：
    /// 下次登录后所有走 WinINET 的程序都上不了网，直到用户再次打开 MyProxy、
    /// 由崩溃恢复去收拾。托盘「退出」之外没有别的离场路径会做这件事，
    /// 而会话结束时进程随后就被系统直接结束。
    /// </summary>
    private static readonly TimeSpan SessionEndStopBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 注销或关机（WM_QUERYENDSESSION）。停止连接，顺序沿用 StopAsync 的 fail-open：
    /// 先恢复代理，成功才停 xray。系统留给这个消息的时间只有几秒，所以有时间上限；
    /// 超时就不再等，交给下次启动的崩溃恢复。
    ///
    /// <para>
    /// 这里也是本进程的最后一段代码：只要 SessionEnding 没被取消，WPF 随即调用
    /// <c>Shutdown()</c>，<see cref="ExitAsync"/> 不会再跑。所以它的收尾在这里做——设置刷盘、
    /// 释放托盘图标、写完日志队列。代价：若关机随后被别的程序取消，MyProxy 已经退出，
    /// 需要用户重新打开——比留下一个指向死端口的系统代理轻得多。
    /// </para>
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // 更新交换进程与第二实例从不构造 Services。
        if (Services is null || e.Cancel)
        {
            return;
        }

        MainWindow? window = MainWindow as MainWindow;
        try
        {
            Services.Log.Info(nameof(App), $"Session ending ({e.ReasonSessionEnding}), stopping the connection");

            // 放到线程池上等：StopAsync 全程 ConfigureAwait(false)，状态事件经
            // Dispatcher.InvokeAsync 派回 UI 线程而不是同步 Invoke，阻塞这里不会死锁。
            // 设置刷盘只是等待已发起的保存任务，同样不需要 UI 线程；放在停止之后，
            // 预算先给最要紧的那件事。
            Task stop = Task.Run(async () =>
            {
                await Services.Connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
                if (window is not null)
                {
                    await window.FlushPendingSettingsAsync().ConfigureAwait(false);
                }
            });
            if (!stop.Wait(SessionEndStopBudget))
            {
                Services.Log.Warn(nameof(App), "会话结束前未能在时限内停止连接，交给下次启动的崩溃恢复");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error(nameof(App), "Session-end stop failed", ex);
        }
        finally
        {
            // 不释放的话，关机若被取消，托盘里会残留一个点了没反应的图标。
            try
            {
                Services.Tray.Dispose();
            }
            catch (Exception)
            {
                // 进程马上就要结束；这里失败不该挡住下面的日志收尾。
            }

            Services.Log.Dispose();
        }
    }

    private static async Task ExitAsync(MainWindow window)
    {
        if (window.IsExiting)
        {
            return;
        }

        window.PrepareExit();

        try
        {
            if (Services.Connection.State is AppState.Connected or AppState.Connecting or AppState.Disconnecting or AppState.Error)
            {
                await Services.Connection.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error(nameof(App), "Exit stop failed", ex);
        }

        await window.FlushPendingSettingsAsync();

        Services.Tray.Dispose();
        Services.SingleInstance.Dispose();
        if (Services.Xray.IsRunning)
        {
            // StopAsync intentionally keeps xray alive when restoring the
            // system proxy failed.  Disposing it here would turn that safe
            // fail-open state into a dead 127.0.0.1 proxy and would also
            // delete the pid needed by the next crash-recovery attempt.
            Services.Log.Warn(nameof(App), "xray 仍在运行，保留进程与 pid 供下次启动恢复系统代理");
        }
        else if (Services.Xray is IDisposable xray)
        {
            xray.Dispose();
        }

        // 控制器放在 Log 之前释放：它的收尾会写日志。
        // 这里只取消后台操作——xray 与系统代理的处置在上面，且必须保持那个
        // fail-open 顺序（先恢复代理、成功才停 xray），不能挪进 Dispose。
        if (Services.Connection is IDisposable connection)
        {
            connection.Dispose();
        }

        Services.Log.Dispose();
        window.Close();
        Current.Shutdown();
    }

    /// <summary>
    /// device.dat 读不出来时按「已绑定」处理：把瞬时 I/O 故障当成未绑定会让用户
    /// 重新配对，并覆盖掉仍然有效的凭据。真的没绑定时后续流程会给出 NotBound。
    /// </summary>
    internal static bool IsProbablyBound()
    {
        try
        {
            return Services.Storage.LoadDevice() is not null;
        }
        catch (SecureStorageReadException ex)
        {
            Services.Log.Error(nameof(App), "device.dat 无法读取，暂按已绑定处理", ex);
            return true;
        }
    }

    private static async Task PersistFlagsAsync(Core.FeatureFlags flags)
    {
        try
        {
            await Services.Storage.UpdateSettingsAsync(
                settings => settings.FeatureFlags = flags.ToDictionary(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 缓存写不下去只影响下次启动的第一次连接，不影响现在。
            Services.Log.Warn(nameof(App), $"Could not cache feature flags: {ex.GetType().Name}");
        }
    }

    private static void ShowAndActivate(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
