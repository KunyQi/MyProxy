using System.IO;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// Linux 端 Update Plane 的编排：查询指派 → 验签 → 暂存 → 交换 → 自替换 → 试用 → 确认/回滚 → 上报。
///
/// <para>
/// 与 Windows 端 <c>UpdateCoordinator</c> 的分工完全相同，形状按平台事实调整：
/// 那边要区分「单文件便携版」与「目录版」两种产物，这边只有目录版；
/// 那边的交换必须由暂存目录里的另一个进程执行，这边在原地做（见
/// <see cref="LinuxUpdateApplier"/>）。
/// </para>
///
/// <para>
/// <b>标志位刷新</b>与 Windows 端一致：心跳带回的 feature flags 由守护进程交给本类
/// （<c>LinuxDaemon</c> 订阅 <c>HeartbeatReceived</c>），<c>ConnectionController</c>
/// 自己不认识 Update Plane。
/// </para>
///
/// <para>
/// <b>试用期</b>是 Linux 独有的形状。交换之后 execv 换上的、以及 systemd 之后每次拉起的，
/// 都是安装目录里的<b>新</b>二进制——旧版本再也不会自己跑起来，所以共享判定里
/// 「交换完成、跑着的却是旧版本 → 回滚」那条边在这里走不到。于是新版本启动时读到
/// <see cref="UpdateStage.Applied"/> 不直接定稿，而是记一次试用期启动、保留备份；
/// 守护进程稳定运行一段时间（或正常退出）后调 <see cref="ConfirmHealthyAsync"/> 才删备份、
/// 上报 <c>installed</c>。连续 <see cref="MaxProbationStarts"/> 次启动都没等到确认
/// （崩溃、被 systemd 反复拉起），第 <c>MaxProbationStarts + 1</c> 次启动就把备份换回去，
/// 上报 <c>failed</c>，并要求调用方 execv 进换回来的旧版本。
/// </para>
///
/// <para>
/// 这套机制挡不住「新版本在走到 <see cref="RecoverAsync"/> 之前就崩」——例如 .NET 宿主
/// 自己起不来。那种包在发布前的打包门禁（<c>scripts/verify_linux_artifact.py</c>）里就该
/// 被拦下；真在现场发生时只能手工恢复（备份仍在 <c>&lt;安装目录&gt;.backup</c>）。
/// </para>
/// </summary>
public sealed class LinuxUpdateCoordinator
{
    /// <summary>新版本换上之后，最多允许「启动了却没等到确认」几次。</summary>
    public const int MaxProbationStarts = 3;

    private readonly IUpdateService _update;
    private readonly ILinuxUpdateInstaller _installer;
    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly string _installDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateJournal? _awaitingConfirmation;

    public LinuxUpdateCoordinator(
        IUpdateService update,
        ILinuxUpdateInstaller installer,
        IStorageService storage,
        ILogService log,
        string? installDirOverride = null)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // 测试不能去动真实的安装目录，所以位置可注入；正常运行时就是可执行文件所在目录。
        _installDir = (installDirOverride ?? LinuxPaths.InstallDir).TrimEnd(Path.DirectorySeparatorChar);
    }

    public FeatureFlags Flags { get; private set; } = FeatureFlags.Empty;

    public event Action<FeatureFlags>? FlagsChanged;

    /// <summary>刚换上的新版本还在试用期，等 <see cref="ConfirmHealthyAsync"/>。</summary>
    public bool IsAwaitingConfirmation => Volatile.Read(ref _awaitingConfirmation) is not null;

    private string InstallDir => _installDir;

    private string BackupDir => _installDir + ".backup";

    /// <summary>
    /// 启动时处理上一次没做完的安装。
    /// <b>必须在界面出现（或开始连接）之前跑完</b>：上一次交换被打断时，
    /// 安装目录可能只有一半，这时先去连网只会让用户对着一个随时会崩的程序操作。
    ///
    /// <para>
    /// 返回 <c>true</c> 表示安装目录已经被换回了旧版本，而本进程还是新版本的映像——
    /// 调用方必须立刻 execv 进安装目录里的那个版本（见 <see cref="RestartIntoInstalledVersion"/>），
    /// 不能带着一个已被回滚的映像继续跑。
    /// </para>
    /// </summary>
    public async Task<bool> RecoverAsync(CancellationToken ct)
    {
        UpdateJournal? journal = _installer.ReadJournal();
        RecoveryAction action = UpdateRecovery.Decide(journal, AppInfo.Version);
        if (action == RecoveryAction.None || journal is null)
        {
            // 没有进行中的安装，试用期记录也就没有意义了（上一次清账时没删掉的残留）。
            _installer.WriteProbationStarts(0);
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(journal.BackupPath.Length == 0 ? BackupDir : journal.BackupPath),
                Path.GetFullPath(BackupDir),
                StringComparison.Ordinal))
        {
            // 日志文件在磁盘上，谁都能改。备份路径不是我们预期的那个时，
            // 唯一安全的动作是什么都不做。
            _log.Warn(nameof(LinuxUpdateCoordinator), "Update journal has an unexpected backup path");
            return false;
        }

        _log.Info(nameof(LinuxUpdateCoordinator), $"Update recovery: {action} (stage={journal.Stage})");
        string detail = journal.Detail;
        bool restartRequired = false;

        switch (action)
        {
            case RecoveryAction.DiscardStaging:
                DiscardStaging(journal);
                _installer.ClearJournal();
                _installer.WriteProbationStarts(0);
                return false;

            case RecoveryAction.RestoreBackup:
            {
                (bool ok, string message) = LinuxUpdateApplier.Restore(InstallDir, BackupDir);
                detail = ok ? "interrupted swap restored" : message;
                _log.Warn(nameof(LinuxUpdateCoordinator), $"Interrupted swap: {detail}");
                // 能从安装目录启动到这里，说明第二次改名已经做完、跑着的是新版本的映像
                // （只差 Applied 没写下）。旧版本已经换回去了，得 execv 过去。
                restartRequired = ok;
                break;
            }

            case RecoveryAction.FinalizeInstall:
            {
                int starts = _installer.ReadProbationStarts() + 1;
                if (starts <= MaxProbationStarts)
                {
                    // 试用期：备份留着、日志留着，等守护进程稳定下来再定稿。
                    _installer.WriteProbationStarts(starts);
                    Volatile.Write(ref _awaitingConfirmation, journal);
                    _log.Info(
                        nameof(LinuxUpdateCoordinator),
                        $"Release {journal.ReleaseId} on probation (start {starts}/{MaxProbationStarts})");
                    return false;
                }

                // 连续几次启动都没撑到确认：新版本起不来。把旧版本换回去。
                (bool ok, string message) = LinuxUpdateApplier.Restore(InstallDir, BackupDir);
                detail = ok
                    ? $"new version did not stay up through {MaxProbationStarts} starts"
                    : message;
                _log.Warn(nameof(LinuxUpdateCoordinator), $"Rolling back: {detail}");
                if (journal.ReleaseId.Length > 0)
                {
                    await _update.ReportInstallAsync(journal.ReleaseId, "failed", detail, ct).ConfigureAwait(false);
                }

                DiscardStaging(journal);
                _installer.ClearJournal();
                _installer.WriteProbationStarts(0);
                return ok;
            }

            case RecoveryAction.RollbackFailedInstall:
            {
                (bool ok, string message) = LinuxUpdateApplier.Restore(InstallDir, BackupDir);
                detail = ok ? "new version did not start" : message;
                _log.Warn(nameof(LinuxUpdateCoordinator), $"Rolled back: {detail}");
                break;
            }
        }

        string status = UpdateRecovery.ReportStatusFor(action);
        if (status.Length > 0 && journal.ReleaseId.Length > 0)
        {
            await _update.ReportInstallAsync(journal.ReleaseId, status, detail, ct).ConfigureAwait(false);
        }

        _installer.ClearJournal();
        _installer.WriteProbationStarts(0);
        return restartRequired;
    }

    /// <summary>
    /// 试用期里的新版本已经自证能跑：删备份、丢暂存、上报 <c>installed</c>、清账。
    /// 不在试用期时什么都不做，可以重复调用。
    /// </summary>
    public async Task ConfirmHealthyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConfirmHealthyCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConfirmHealthyCoreAsync(CancellationToken ct)
    {
        UpdateJournal? pending = Interlocked.Exchange(ref _awaitingConfirmation, null);
        if (pending is null)
        {
            return;
        }

        UpdateJournal? current = _installer.ReadJournal();
        if (current is null
            || current.Stage != UpdateStage.Applied
            || !string.Equals(current.ReleaseId, pending.ReleaseId, StringComparison.Ordinal))
        {
            // 日志已经不是试用期开始时那一份了：那是别的安装的账，不去动它。
            return;
        }

        LinuxUpdateApplier.DiscardBackup(BackupDir);
        DiscardStaging(current);
        if (current.ReleaseId.Length > 0)
        {
            await _update.ReportInstallAsync(current.ReleaseId, "installed", "", ct).ConfigureAwait(false);
        }

        _installer.ClearJournal();
        _installer.WriteProbationStarts(0);
        _log.Info(nameof(LinuxUpdateCoordinator), $"Release {current.ReleaseId} confirmed");
    }

    /// <summary>拉一次指派并刷新 flags。返回可安装的计划，没有就返回 null。</summary>
    public async Task<UpdatePlanResult?> PollAsync(CancellationToken ct)
    {
        AssignedUpdate? assigned = await _update.FetchAssignedAsync(ct).ConfigureAwait(false);
        if (assigned is null)
        {
            return null;
        }

        UpdateFlags(assigned.Flags);
        return assigned.Plan.Decision == UpdateDecision.Install ? assigned.Plan : null;
    }

    public void UpdateFlags(FeatureFlags flags)
    {
        if (flags.Count == Flags.Count && flags.ToString() == Flags.ToString())
        {
            return;
        }

        Flags = flags;
        _log.Info(nameof(LinuxUpdateCoordinator), $"Feature flags: {flags}");
        FlagsChanged?.Invoke(flags);
    }

    /// <summary>下载、校验并暂存。校验不过就什么都不装，并把失败上报。</summary>
    public async Task<bool> PrepareAsync(UpdatePlanResult plan, CancellationToken ct)
    {
        if (plan.Decision != UpdateDecision.Install || plan.Manifest is null)
        {
            return false;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 试用期里又要装下一个版本：当前版本已经跑到能接受命令，算它过了试用期。
            // 否则下面写入的 Staged 日志会覆盖掉它那份 Applied，备份也会被交换删掉，
            // 而服务端永远收不到它的 installed。
            await ConfirmHealthyCoreAsync(ct).ConfigureAwait(false);

            LinuxStageResult staged = await _installer
                .StageAsync(plan.Manifest, plan.ReleaseId, ct)
                .ConfigureAwait(false);

            if (!staged.Ok)
            {
                await _update.ReportInstallAsync(plan.ReleaseId, "failed", staged.Detail, ct).ConfigureAwait(false);
                return false;
            }

            if (!LinuxUpdateApplier.IsPlausibleInstallTree(staged.StagedPath))
            {
                await _update.ReportInstallAsync(plan.ReleaseId, "failed", "staged tree is not a MyProxy installation", ct)
                    .ConfigureAwait(false);
                return false;
            }

            if (!LinuxUpdateApplier.CanSwapDirectory(InstallDir))
            {
                // 系统级安装（/usr、/opt、由包管理器管理的目录）不该被客户端自己改写。
                // 说清楚原因，而不是留下一个换到一半的目录。
                await _update.ReportInstallAsync(
                    plan.ReleaseId,
                    "failed",
                    "installation directory is not writable by this user",
                    ct).ConfigureAwait(false);
                _log.Warn(
                    nameof(LinuxUpdateCoordinator),
                    $"安装目录不可写，拒绝就地更新：{InstallDir}（请用发行版包管理器升级）");
                return false;
            }

            await _installer.WriteJournalAsync(
                new UpdateJournal
                {
                    Stage = UpdateStage.Staged,
                    ReleaseId = plan.ReleaseId,
                    TargetVersion = plan.Manifest.Version,
                    PreviousVersion = AppInfo.Version,
                    StagedPath = staged.StagedPath,
                    BackupPath = BackupDir
                },
                ct).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 应用暂存好的更新：交换目录 → 标记 Applied → 用 execv 原地换上新的自己。
    ///
    /// <para>
    /// <paramref name="releaseId"/> 是调用方刚刚准备好的那一个。日志里暂存的若是别的
    /// release（上一次准备好却没换成的、之后被撤销的那种），一律拒绝——「这次准备失败了，
    /// 那就装上次剩下的」会把一个服务端已经不再指派的版本装上去。
    /// </para>
    ///
    /// <para>
    /// 三个顺序都不能动。先把阶段推进到 <see cref="UpdateStage.Applying"/> 再交换：
    /// 反过来的话，交换开始后、日志落盘前断电，下次启动会以为什么都没发生，
    /// 而安装目录已经被移走了。交换成功后才写 Applied：新进程据此进入试用期
    /// （见类型注释）。
    /// </para>
    /// </summary>
    public async Task<bool> ApplyAsync(string releaseId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UpdateJournal? journal = _installer.ReadJournal();
            if (journal is null
                || journal.Stage != UpdateStage.Staged
                || journal.StagedPath.Length == 0
                || !string.Equals(journal.ReleaseId, releaseId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!LinuxUpdateApplier.IsPlausibleInstallTree(journal.StagedPath) ||
                !LinuxUpdateApplier.CanSwapDirectory(InstallDir))
            {
                _log.Warn(nameof(LinuxUpdateCoordinator), "Update preflight failed; installation was left untouched");
                return false;
            }

            await _installer
                .WriteJournalAsync(journal with { Stage = UpdateStage.Applying }, ct)
                .ConfigureAwait(false);

            (bool ok, string detail) = LinuxUpdateApplier.Swap(InstallDir, journal.StagedPath, BackupDir);
            if (!ok)
            {
                // 交换失败：退回 Staged，让下一次启动重新尝试（安装目录没有被改坏）。
                _log.Error(nameof(LinuxUpdateCoordinator), $"swap failed: {detail}", new IOException(detail));
                await _installer
                    .WriteJournalAsync(journal with { Stage = UpdateStage.Staged, Detail = detail }, ct)
                    .ConfigureAwait(false);
                return false;
            }

            await _installer
                .WriteJournalAsync(journal with { Stage = UpdateStage.Applied, Detail = "" }, ct)
                .ConfigureAwait(false);
            _installer.WriteProbationStarts(0);

            _log.Info(nameof(LinuxUpdateCoordinator), $"Applied release {journal.ReleaseId}; restarting into it");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 换上安装目录里现在的那个版本（交换之后是新版本，试用期回滚之后是旧版本）。
    /// 返回 false 表示 execv 没成功。
    /// </summary>
    public bool RestartIntoInstalledVersion(IReadOnlyList<string> arguments)
        => LinuxUpdateApplier.ExecSelf(InstallDir, arguments);

    private void DiscardStaging(UpdateJournal journal)
    {
        if (journal.StagedPath.Length == 0)
        {
            return;
        }

        try
        {
            // 删掉版本目录而不只是 payload/，否则解包剩下的空壳会一直堆着。
            // 这是一次递归删除，路径又来自磁盘上谁都能改的日志——只删暂存根下面的东西。
            string? versionDir = Path.GetDirectoryName(Path.GetFullPath(journal.StagedPath));
            string stagingRoot = Path.GetFullPath(_installer.StagingRoot).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (versionDir is null || !versionDir.StartsWith(stagingRoot, StringComparison.Ordinal))
            {
                _log.Warn(nameof(LinuxUpdateCoordinator), "Update journal points outside the staging root; left alone");
                return;
            }

            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(nameof(LinuxUpdateCoordinator), $"discarding staging failed: {ex.GetType().Name}");
        }
    }
}
