namespace MyProxy.Models;

public enum UpdateCheckStatus
{
    Failed,
    UpToDate,
    UpdateAvailable
}

/// <summary>只有成功解析并比较版本后才能确认 UpToDate；请求失败没有版本结论。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null);
