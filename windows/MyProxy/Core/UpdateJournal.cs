using System.Text.Json.Serialization;

namespace MyProxy.Core;

/// <summary>安装事务的阶段。顺序即是磁盘上真实发生过的事。</summary>
public enum UpdateStage
{
    /// <summary>没有进行中的安装。</summary>
    Idle,

    /// <summary>包已下载、已验签、已解包到暂存目录，但还没动过安装目录。</summary>
    Staged,

    /// <summary>正在交换目录。这一阶段中断意味着安装目录可能是半个。</summary>
    Applying,

    /// <summary>已交换完成并拉起新版本，等待新版本自证能跑起来。</summary>
    Applied,

    /// <summary>已回滚到交换前的版本。</summary>
    RolledBack
}

/// <summary>
/// 安装事务日志。它存在的唯一理由是：交换目录的那一刻如果断电，
/// 下次启动必须能从磁盘上读出「当时进行到哪一步」，否则就只剩一个半装好的
/// 安装目录和没人知道该怎么办的状态。
/// </summary>
public sealed record UpdateJournal
{
    [JsonPropertyName("stage")]
    public UpdateStage Stage { get; init; } = UpdateStage.Idle;

    [JsonPropertyName("releaseId")]
    public string ReleaseId { get; init; } = "";

    [JsonPropertyName("targetVersion")]
    public string TargetVersion { get; init; } = "";

    /// <summary>交换前正在运行的版本，也就是回滚要回到的那一个。</summary>
    [JsonPropertyName("previousVersion")]
    public string PreviousVersion { get; init; } = "";

    [JsonPropertyName("stagedPath")]
    public string StagedPath { get; init; } = "";

    [JsonPropertyName("backupPath")]
    public string BackupPath { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>失败原因，写给日志与上报，不展示给用户。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";
}

/// <summary>启动时该对上一次未完成的安装做什么。</summary>
public enum RecoveryAction
{
    /// <summary>无事可做。</summary>
    None,

    /// <summary>丢弃暂存目录：还没动过安装目录，没有任何东西需要恢复。</summary>
    DiscardStaging,

    /// <summary>交换被打断，必须把备份恢复回安装目录。</summary>
    RestoreBackup,

    /// <summary>新版本跑起来了：删掉备份并向服务端上报 installed。</summary>
    FinalizeInstall,

    /// <summary>
    /// 交换完成了，但现在跑着的还是旧版本——新版本没能起来。回滚并上报 failed。
    /// </summary>
    RollbackFailedInstall,

    /// <summary>上一次已经回滚过，向服务端上报 rolledback 后清账。</summary>
    ReportRollback
}

/// <summary>
/// 从日志与「现在实际跑着哪个版本」推出恢复动作。纯函数，因而每一条边都能
/// 被测试覆盖——而这些边恰恰是只在断电、崩溃或杀进程时才走到的那些。
/// </summary>
public static class UpdateRecovery
{
    public static RecoveryAction Decide(UpdateJournal? journal, string runningVersion)
    {
        if (journal is null)
        {
            return RecoveryAction.None;
        }

        switch (journal.Stage)
        {
            case UpdateStage.Idle:
                return RecoveryAction.None;

            case UpdateStage.Staged:
                // 只下载解包过，安装目录没被碰过。
                return RecoveryAction.DiscardStaging;

            case UpdateStage.Applying:
                // 交换过程中断。此时安装目录可能已经被移走，只有恢复备份是安全的。
                return RecoveryAction.RestoreBackup;

            case UpdateStage.Applied:
                if (VersionsMatch(runningVersion, journal.TargetVersion))
                {
                    return RecoveryAction.FinalizeInstall;
                }

                // 交换完成，跑起来的却不是目标版本：新版本起不来，或者它自己
                // 崩回了旧的。无论哪种，都不能把这次安装当成功。
                return RecoveryAction.RollbackFailedInstall;

            case UpdateStage.RolledBack:
                return RecoveryAction.ReportRollback;

            default:
                return RecoveryAction.None;
        }
    }

    /// <summary>
    /// 版本比较按语义版本的前三段，而不是字符串相等：同一个版本写成
    /// <c>1.2.3</c> 与 <c>1.2.3+build.7</c> 是同一个版本，字符串比较会把一次
    /// 成功的安装误判成失败并触发一次没必要的回滚。
    /// </summary>
    private static bool VersionsMatch(string left, string right)
    {
        if (!ReleaseManifestVerifier.TryParseVersion(left, out Version a) ||
            !ReleaseManifestVerifier.TryParseVersion(right, out Version b))
        {
            return false;
        }

        return a == b;
    }

    /// <summary>恢复动作对应要向服务端上报的状态；空串表示不上报。</summary>
    public static string ReportStatusFor(RecoveryAction action) => action switch
    {
        RecoveryAction.FinalizeInstall => "installed",
        RecoveryAction.RollbackFailedInstall => "failed",
        RecoveryAction.ReportRollback => "rolledback",
        RecoveryAction.RestoreBackup => "failed",
        _ => ""
    };
}
