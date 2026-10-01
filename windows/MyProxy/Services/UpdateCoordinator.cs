using System.Diagnostics;
using System.IO;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 把 Update Plane 的各段接起来：查询指派 → 验签 → 暂存 → 交换 → 恢复 → 上报。
///
/// <para>
/// 这里是唯一知道「安装目录在哪」的地方。分层照旧：<c>Core/</c> 里的
/// <see cref="UpdatePlanner"/> 与 <see cref="UpdateRecovery"/> 做纯判定，
/// 本类只负责把判定变成磁盘和进程上的动作。
/// </para>
/// </summary>
public sealed class UpdateCoordinator
{
    public const string BackupDirName = "backup";

    private readonly IUpdateService _update;
    private readonly IUpdateInstaller _installer;
    private readonly ILogService _log;
    private readonly IStorageService _storage;

    public UpdateCoordinator(
        IUpdateService update,
        IUpdateInstaller installer,
        IStorageService storage,
        ILogService log)
    {
        _update = update;
        _installer = installer;
        _storage = storage;
        _log = log;
    }

    /// <summary>当前生效的 feature flags，心跳每次刷新。</summary>
    public FeatureFlags Flags { get; private set; } = FeatureFlags.Empty;

    public event Action<FeatureFlags>? FlagsChanged;

    private string BackupDir => Path.Combine(_storage.DataRoot, UpdateInstaller.StagingDirName, BackupDirName);
    private string ExecutableBackup => Path.Combine(BackupDir, UpdateInstaller.ExecutableName);

    // The portable publish embeds Core/VERSION.txt; a directory publish has loose Core files.
    // Use a build property, not the contents of Downloads, to choose the update transaction.
    internal static bool IsPortableBuild => typeof(UpdateCoordinator).Assembly.GetManifestResourceNames()
        .Any(name => name.EndsWith(".Assets.Core.VERSION.txt", StringComparison.Ordinal));

    private bool IsExpectedBackup(UpdateJournal journal)
    {
        try
        {
            string expected = IsPortableBuild ? ExecutableBackup : BackupDir;
            string actual = journal.BackupPath.Length == 0 && !IsPortableBuild
                ? expected : journal.BackupPath;
            return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动时处理上一次没做完的安装。
    ///
    /// **必须在窗口出现之前跑完**：如果上一次交换被打断，安装目录可能是半个，
    /// 这时候先把界面摆出来只会让用户对着一个随时会崩的程序操作。
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        UpdateJournal? journal = _installer.ReadJournal();
        RecoveryAction action = UpdateRecovery.Decide(journal, AppInfo.Version);
        if (action == RecoveryAction.None || journal is null)
        {
            return;
        }

        if (!IsExpectedBackup(journal))
        {
            _log.Warn(nameof(UpdateCoordinator), "Update journal has an unexpected backup path");
            return;
        }

        _log.Info(nameof(UpdateCoordinator), $"Update recovery: {action} (stage={journal.Stage})");
        string detail = journal.Detail;

        switch (action)
        {
            case RecoveryAction.DiscardStaging:
                DiscardStaging(journal);
                _installer.ClearJournal();
                return;

            case RecoveryAction.RestoreBackup:
            {
                SwapOutcome outcome = IsPortableBuild
                    ? UpdateApplier.RestoreExecutable(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.ExecutableName), ExecutableBackup)
                    : UpdateApplier.Restore(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), BackupDir);
                detail = outcome.Ok ? "interrupted swap restored" : outcome.Detail;
                _log.Warn(nameof(UpdateCoordinator), $"Interrupted swap: {detail}");
                break;
            }

            case RecoveryAction.FinalizeInstall:
                if (IsPortableBuild) UpdateApplier.DiscardExecutableBackup(ExecutableBackup);
                else UpdateApplier.DiscardBackup(BackupDir);
                DiscardStaging(journal);
                detail = "";
                break;

            case RecoveryAction.RollbackFailedInstall:
            {
                SwapOutcome outcome = IsPortableBuild
                    ? UpdateApplier.RestoreExecutable(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.ExecutableName), ExecutableBackup)
                    : UpdateApplier.Restore(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), BackupDir);
                detail = outcome.Ok ? "new version did not start" : outcome.Detail;
                _log.Warn(nameof(UpdateCoordinator), $"Rolled back: {detail}");
                break;
            }
        }

        string status = UpdateRecovery.ReportStatusFor(action);
        if (status.Length > 0 && journal.ReleaseId.Length > 0)
        {
            await _update.ReportInstallAsync(journal.ReleaseId, status, detail, ct).ConfigureAwait(false);
        }

        _installer.ClearJournal();
    }

    /// <summary>
    /// 拉一次指派并刷新 flags。返回可安装的计划，没有就返回 null。
    /// </summary>
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

    /// <summary>心跳带回的 flags。</summary>
    public void UpdateFlags(FeatureFlags flags)
    {
        if (flags.Count == Flags.Count && flags.ToString() == Flags.ToString())
        {
            return;
        }

        Flags = flags;
        _log.Info(nameof(UpdateCoordinator), $"Feature flags: {flags}");
        FlagsChanged?.Invoke(flags);
    }

    /// <summary>
    /// 下载、校验并暂存。**校验不过就什么都不装**，并把失败上报，让管理员看到
    /// 某个 release 正在现场失败。
    /// </summary>
    public async Task<bool> PrepareAsync(UpdatePlanResult plan, CancellationToken ct)
    {
        if (plan.Decision != UpdateDecision.Install || plan.Manifest is null)
        {
            return false;
        }

        StageResult staged = await _installer.StageAsync(plan.Manifest, plan.ReleaseId, ct).ConfigureAwait(false);
        if (!staged.Ok)
        {
            await _update.ReportInstallAsync(plan.ReleaseId, "failed", staged.Detail, ct).ConfigureAwait(false);
            return false;
        }

        if (IsPortableBuild && !UpdateApplier.IsSingleExecutableStage(staged.StagedPath))
        {
            await _update.ReportInstallAsync(plan.ReleaseId, "failed", "portable update must contain only MyProxy.exe", ct)
                .ConfigureAwait(false);
            return false;
        }

        if (!IsPortableBuild && !UpdateApplier.CanSwapDirectory(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), staged.StagedPath))
        {
            await _update.ReportInstallAsync(plan.ReleaseId, "failed", "installation directory contains unowned files or links", ct)
                .ConfigureAwait(false);
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
                BackupPath = IsPortableBuild ? ExecutableBackup : BackupDir
            },
            ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 拉起交换进程。调用方随后必须退出本进程——交换要等它退出才会开始。
    /// </summary>
    public async Task<bool> LaunchApplyAsync(CancellationToken ct)
    {
        UpdateJournal? journal = _installer.ReadJournal();
        if (journal is null || journal.Stage != UpdateStage.Staged || journal.StagedPath.Length == 0)
        {
            return false;
        }

        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string executable = Path.Combine(journal.StagedPath, UpdateInstaller.ExecutableName);
        if (!File.Exists(executable))
        {
            return false;
        }

        if (!IsExpectedBackup(journal) ||
            (IsPortableBuild && !UpdateApplier.IsSingleExecutableStage(journal.StagedPath)) ||
            (!IsPortableBuild && !UpdateApplier.CanSwapDirectory(installDir, journal.StagedPath)))
        {
            _log.Warn(nameof(UpdateCoordinator), "Update preflight failed; installation was left untouched");
            return false;
        }

        // 先把日志推进到 Applying 再拉起进程：反过来的话，交换开始后、日志落盘前
        // 断电，下次启动会以为什么都没发生，而安装目录已经被移走了。
        await _installer.WriteJournalAsync(journal with { Stage = UpdateStage.Applying }, ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(IsPortableBuild ? UpdateApplier.ApplyFileSwitch : UpdateApplier.ApplySwitch);
        startInfo.ArgumentList.Add(IsPortableBuild ? Path.Combine(installDir, UpdateInstaller.ExecutableName) : installDir);
        startInfo.ArgumentList.Add(IsPortableBuild ? executable : journal.StagedPath);
        startInfo.ArgumentList.Add(journal.BackupPath);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(UpdateCoordinator), "Failed to launch the update applier", ex);
            await _installer.WriteJournalAsync(journal with { Stage = UpdateStage.Staged }, ct).ConfigureAwait(false);
            return false;
        }
    }

    private void DiscardStaging(UpdateJournal journal)
    {
        if (journal.StagedPath.Length == 0)
        {
            return;
        }

        try
        {
            // 删掉版本目录而不只是 staged/，否则解包剩下的空壳会一直堆着。
            string? versionDir = Path.GetDirectoryName(journal.StagedPath);
            if (versionDir is not null && Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
