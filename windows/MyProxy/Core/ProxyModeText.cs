namespace MyProxy.Core;

/// <summary>
/// 两种连接模式对用户的叫法与说明。
///
/// <para>
/// <b>内部枚举仍叫 <see cref="ProxyMode.Rule"/> / <see cref="ProxyMode.Global"/>，
/// 存储值与协议字段一律不变</b>，所以升级不需要迁移数据，语义也没有变化。
/// 变的只有用户看见的那两个词。
/// </para>
///
/// <para>
/// 为什么不再叫「规则」：那个词听起来像存在一个用户可以增删改的规则集，
/// 而实际上分流策略是客户端内置、固定的。名字应该说清楚**流量怎么走**，
/// 而不是暗示一个并不存在的规则编辑器。
/// </para>
///
/// <para>
/// 文案集中在这里而不是散在各个 XAML 里，是为了让托盘、主界面和设置页说的是
/// 同一句话——同一个模式在三处有三种叫法，是这类改名最常见的结局。
/// </para>
/// </summary>
public static class ProxyModeText
{
    /// <summary>智能分流：国内域名/IP 直连，其余普通流量走代理。</summary>
    public const string RuleName = "智能分流";

    /// <summary>全局代理：普通流量统一走代理。</summary>
    public const string GlobalName = "全局代理";

    /// <summary>
    /// 一句话说明，给设置页与提示用。
    ///
    /// 两条都写明私网地址与 BitTorrent 继续阻断——那是两种模式共有的行为，
    /// 只在一处提会让人以为换个模式就不拦了。
    /// </summary>
    public const string RuleDescription = "国内网站直连，其余走代理。私网地址与 BT 流量始终阻断。";

    public const string GlobalDescription = "所有网站统一走代理。私网地址与 BT 流量始终阻断。";

    public static string NameFor(ProxyMode mode) => mode == ProxyMode.Rule ? RuleName : GlobalName;

    public static string DescriptionFor(ProxyMode mode) =>
        mode == ProxyMode.Rule ? RuleDescription : GlobalDescription;
}
