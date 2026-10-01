using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>一次「本设备被指派了什么」的查询结果。</summary>
public sealed record AssignedUpdate(
    UpdatePlanResult Plan,
    FeatureFlags Flags,
    string ReleaseId = "");

public interface IUpdateService
{
    /// <summary>
    /// 公开的版本提示，不需要设备令牌。带回签名 manifest 时会一并验签。
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    /// <summary>
    /// 查询本设备被指派的 release，并在本地验签。需要设备令牌。
    /// 未绑定或控制面不可达时返回 null，调用方退回 <see cref="CheckAsync"/>。
    /// </summary>
    Task<AssignedUpdate?> FetchAssignedAsync(CancellationToken ct);

    /// <summary>
    /// 上报安装结果。这是服务端发现「某个 release 在现场大面积失败」的唯一途径。
    /// 失败不抛异常：上报不上去不该让一次成功的安装看起来像失败。
    /// </summary>
    Task ReportInstallAsync(string releaseId, string status, string detail, CancellationToken ct);
}
