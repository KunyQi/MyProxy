namespace MyProxy.Core;

/// <summary>
/// 界面皮肤。三套皮肤共享同一套**结构**（版面、尺寸、字号、图标几何都来自同一份
/// 几何 Token），只在**材质**上分岔：面怎么受光、边怎么收口、按下去时物件发生什么。
///
/// 三者都必须定义完全相同的资源键集合——视图里一个 <c>{StaticResource}</c> 都不允许
/// 只在某一套皮肤下存在，否则换皮肤会抛 ResourceReferenceKeyNotFoundException。
/// 这条契约由 <c>Tests/ThemeParityTests</c> 逐键守住。
/// </summary>
public enum UiTheme
{
    /// <summary>出厂皮肤：浮空层 + 能量球，`Themes/DesignTokens.xaml`。老用户升级后看到的还是它。</summary>
    Classic = 0,

    /// <summary>瓷白：拒绝光泽与深度，层次全靠明度差与发丝线。动效只换颜色，不位移。</summary>
    Porcelain = 1,

    /// <summary>釉陶：上釉的胎体，凸起可按、凹陷可填。按下时物件真的陷进胎里。</summary>
    Ceramic = 2
}

/// <summary>皮肤的元数据。放在 Core 是因为它是纯查表，不碰 WPF 也不碰磁盘。</summary>
public static class UiThemes
{
    /// <summary>设置页分段控件里的顺序，也是枚举的声明顺序。</summary>
    public static readonly IReadOnlyList<UiTheme> All =
        new[] { UiTheme.Classic, UiTheme.Porcelain, UiTheme.Ceramic };

    /// <summary>
    /// 皮肤对应的资源字典文件名（不含路径）。真正拼 Pack URI 的活儿归
    /// <c>Services/ThemeService</c>——那一层才允许知道 WPF 的存在。
    /// </summary>
    public static string DictionaryFileName(UiTheme theme) => theme switch
    {
        UiTheme.Porcelain => "Porcelain.xaml",
        UiTheme.Ceramic => "Ceramic.xaml",
        _ => "DesignTokens.xaml"
    };

    /// <summary>界面上的名字。不出现「主题 / Theme / 皮肤」这类词，用户只看见材质本身。</summary>
    public static string DisplayName(UiTheme theme) => theme switch
    {
        UiTheme.Porcelain => "瓷白",
        UiTheme.Ceramic => "釉陶",
        _ => "经典"
    };

    /// <summary>越界的值一律落回出厂皮肤：配置文件是可以被手改坏的。</summary>
    public static UiTheme Normalize(UiTheme theme)
        => All.Contains(theme) ? theme : UiTheme.Classic;
}
