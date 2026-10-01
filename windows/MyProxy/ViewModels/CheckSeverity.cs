namespace MyProxy.ViewModels;

/// <summary>
/// 自检结果行的呈现级别。View 只按它选颜色，不重新判断业务含义。
/// </summary>
public enum CheckSeverity
{
    /// <summary>无结果可显示。</summary>
    None,

    /// <summary>正在检测。</summary>
    Running,

    /// <summary>通过。</summary>
    Good,

    /// <summary>未通过，或通过了但流量没走隧道。</summary>
    Bad
}
