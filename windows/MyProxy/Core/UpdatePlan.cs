namespace MyProxy.Core;

/// <summary>一次更新检查的判定结果。</summary>
public enum UpdateDecision
{
    /// <summary>没有指派，或指派的版本不高于本地版本。</summary>
    UpToDate,

    /// <summary>有更高的版本可装。</summary>
    Install,

    /// <summary>manifest 没通过验签或结构校验，拒绝安装。</summary>
    Rejected,

    /// <summary>
    /// 指派的版本要求的最低版本高于本地版本，本地必须先装中间版本。
    /// 直接跳装会得到一个升级路径没被测过的安装。
    /// </summary>
    UpgradePathRequired
}

/// <summary>更新判定的完整结论，含展示所需的一切。</summary>
public sealed record UpdatePlanResult(
    UpdateDecision Decision,
    ReleaseManifest? Manifest = null,
    ManifestRejection Rejection = ManifestRejection.None,
    string ReleaseId = "")
{
    public bool Mandatory => Manifest?.Mandatory ?? false;
}

/// <summary>
/// 「这份 manifest 该不该装」的纯判定。无 I/O、无网络，因而可以把每一条边
/// 都摆到测试里——这正是本项目把难缠的不变量抽成纯函数的一贯做法。
/// </summary>
public static class UpdatePlanner
{
    /// <summary>
    /// 判定是否安装。<paramref name="verification"/> 必须是已经验签过的结果：
    /// 本方法不碰签名，它只回答「验过之后要不要装」。
    /// </summary>
    public static UpdatePlanResult Decide(
        ManifestVerification verification,
        string localVersion,
        string releaseId = "")
    {
        if (!verification.Ok || verification.Manifest is null)
        {
            return new(UpdateDecision.Rejected, null, verification.Rejection, releaseId);
        }

        ReleaseManifest manifest = verification.Manifest;
        if (!ReleaseManifestVerifier.TryParseVersion(manifest.Version, out Version remote) ||
            !ReleaseManifestVerifier.TryParseVersion(localVersion, out Version local))
        {
            // 版本解析不了就没有版本结论，绝不能默认「已是最新」——这与
            // UpdateService 在 Failed 与 UpToDate 之间的既有区分是同一条规矩。
            return new(UpdateDecision.Rejected, manifest, ManifestRejection.BadVersion, releaseId);
        }

        if (remote <= local)
        {
            return new(UpdateDecision.UpToDate, manifest, ManifestRejection.None, releaseId);
        }

        if (manifest.MinimumVersion.Length > 0 &&
            ReleaseManifestVerifier.TryParseVersion(manifest.MinimumVersion, out Version minimum) &&
            local < minimum)
        {
            return new(UpdateDecision.UpgradePathRequired, manifest, ManifestRejection.None, releaseId);
        }

        return new(UpdateDecision.Install, manifest, ManifestRejection.None, releaseId);
    }
}
