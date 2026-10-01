namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 自检结果行的呈现级别。View 只按它选颜色，不重新判断业务含义。
///
/// <para>
/// 与 <c>windows/MyProxy/ViewModels/CheckSeverity.cs</c> 逐字同形：成员名、顺序与含义
/// 都不许改——三套皮肤的样式表里写着 <c>{Binding CheckSeverity}</c> 并直接与
/// <c>None</c> / <c>Running</c> / <c>Good</c> / <c>Bad</c> 比较，改名等于把着色规则改坏。
/// </para>
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
