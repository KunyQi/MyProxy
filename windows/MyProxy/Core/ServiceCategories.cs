namespace MyProxy.Core;

/// <summary>
/// Observability Plane 的服务类别：一张封闭词表，以及它与 xray 出站 tag 的映射。
///
/// <para>
/// <b>这里没有、也不会有单个域名或 URL 会被上报。</b> 归因的做法是：为几个粗
/// 类别各建一条<b>出站 tag 相同、目标服务器也相同</b>的出站，让路由按
/// geosite 列表把流量分到不同 tag 上，然后只读每个 tag 的<b>字节计数器</b>。
/// 客户端因此从头到尾不需要记录、更不需要发送「访问了哪里」——它发出去的
/// 只有「video 这一小时 5MB」。
/// </para>
///
/// <para>
/// 词表必须与 <c>server/myproxy_server/observability.py</c> 的
/// <c>SERVICE_CATEGORIES</c> 以及 Android 端逐字对应。服务端对词表外的类别
/// 是<b>拒绝</b>而不是折进 other，所以这里多写一个名字会让整条上报被拒。
/// </para>
/// </summary>
public static class ServiceCategories
{
    public const string Video = "video";
    public const string Social = "social";
    public const string Messaging = "messaging";
    public const string Web = "web";
    public const string Download = "download";
    public const string Other = "other";

    /// <summary>词表全集，顺序与服务端一致。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Video, Social, Messaging, Web, Download, Other
    };

    /// <summary>
    /// 词表里给「其余网页流量」留的名字。注意默认的 <c>proxy</c> 出站<b>不</b>归到它名下
    /// （<see cref="CategoryForTag"/> 对它返回空串，测试钉着）：没被任何类别规则命中的代理
    /// 流量里有网页也有下载、游戏，统统叫 web 是猜测；它的量已经在服务端的总量里。
    /// </summary>
    public const string FallbackCategory = Web;

    /// <summary>
    /// 需要独立出站 tag 的类别及其 geosite 列表。
    ///
    /// 只列了三个粗类别：每多一个 tag 就多一份到同一台服务器的连接池，而归因
    /// 的价值在「大致是什么」，不在分得多细。<c>download</c> 与 <c>other</c>
    /// 在 Windows 上没有可靠的 geosite 依据，留给词表里其他端使用。
    /// </summary>
    public static readonly IReadOnlyList<(string Category, string Tag, string[] Geosites)> Routed =
        new[]
        {
            (Video, "cat-video", new[] { "geosite:youtube", "geosite:netflix", "geosite:disney", "geosite:bilibili" }),
            (Social, "cat-social", new[] { "geosite:tiktok", "geosite:facebook", "geosite:twitter", "geosite:instagram" }),
            (Messaging, "cat-messaging", new[] { "geosite:telegram", "geosite:whatsapp", "geosite:signal" })
        };

    /// <summary>出站 tag → 类别。未知 tag 返回空串，调用方据此丢弃。</summary>
    public static string CategoryForTag(string tag)
    {
        foreach ((string category, string routedTag, _) in Routed)
        {
            if (string.Equals(tag, routedTag, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return string.Empty;
    }

    public static bool IsKnown(string category) => All.Contains(category, StringComparer.Ordinal);
}
