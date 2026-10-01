using System.Globalization;

namespace MyProxy.Services;

/// <summary>
/// KDE（kioslaverc 的 <c>[Proxy Settings]</c> 组）代理设置的命令构造与解析。
///
/// <para>
/// 与 GNOME 那份是同一套路：命令构造与文本解析都是纯函数，读回来的值原样进快照。
/// </para>
///
/// <para>
/// 用 <c>kwriteconfig</c>/<c>kreadconfig</c> 而不是直接改 <c>~/.config/kioslaverc</c>：
/// 桌面在跑的时候它会自己覆写那个文件，直接编辑会被无声地丢掉。
/// </para>
/// </summary>
internal static class KdeProxyCommands
{
    public const string Group = "Proxy Settings";
    public const string File = "kioslaverc";

    /// <summary>
    /// 我们关心的键。只读这些：多读一个键，恢复时就会多写回去一个我们没管过的值。
    /// </summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "ProxyType", "httpProxy", "httpsProxy", "socksProxy", "NoProxyFor"
    };

    /// <summary>ProxyType=1 是「手动指定代理」（3 是 PAC，0 是不用代理）。</summary>
    /// <remarks>
    /// 与 GNOME 那份同一决定：<b>不写 <c>socksProxy</c></b>。本地入站是 HTTP 代理，
    /// 把它指给 SOCKS 只会让读该设置的程序失败；留着用户原来的值则那些程序走直连。
    /// 两条都不免费，选择「不碰用户的设置」，理由见
    /// <see cref="GnomeProxyCommands.EnableCommands"/> 的说明。
    /// </remarks>
    public static IReadOnlyList<string[]> EnableCommands(string host, int port)
    {
        string url = $"{host}:{port}";

        return new List<string[]>
        {
            new[] { "--file", File, "--group", Group, "--key", "httpProxy", url },
            new[] { "--file", File, "--group", Group, "--key", "httpsProxy", url },
            new[] { "--file", File, "--group", Group, "--key", "NoProxyFor", string.Join(',', GnomeProxyCommands.DefaultIgnoreHosts) },
            new[] { "--file", File, "--group", Group, "--key", "ProxyType", "1" }
        };
    }

    public static IReadOnlyList<string[]> RestoreCommands(IReadOnlyDictionary<string, string> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var commands = new List<string[]>();
        foreach (string key in Keys)
        {
            if (state.TryGetValue(key, out string? value))
            {
                commands.Add(new[] { "--file", File, "--group", Group, "--key", key, value });
            }
        }

        return commands;
    }

    /// <summary>
    /// 恢复计划，与 GNOME 那份同一套规矩：<b>哪个键现在仍然指向我们，才写回哪个键</b>。
    ///
    /// <para>
    /// <paramref name="target"/> 为 null 表示没有可信快照：把 ProxyType 改回 0（不用代理），
    /// 而且只在确实动过指向时才动它。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string[]> RestoreCommands(
        IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? target,
        string host,
        int port)
    {
        var commands = new List<string[]>();
        bool touchedAny = false;

        // socksProxy 不参与：我们从没写过它（见 EnableCommands）。
        foreach (string key in new[] { "httpProxy", "httpsProxy" })
        {
            if (!KeyPointsAt(current, key, host, port))
            {
                continue;
            }

            touchedAny = true;
            commands.Add(new[]
            {
                "--file", File, "--group", Group, "--key", key,
                target is not null && target.TryGetValue(key, out string? original) ? original : ""
            });
        }

        if (!touchedAny)
        {
            return commands;
        }

        if (target is null)
        {
            commands.Add(new[] { "--file", File, "--group", Group, "--key", "ProxyType", "0" });
            return commands;
        }

        // NoProxyFor 先写、ProxyType 最后写：后者是总开关。
        foreach (string key in new[] { "NoProxyFor", "ProxyType" })
        {
            if (target.TryGetValue(key, out string? value))
            {
                commands.Add(new[] { "--file", File, "--group", Group, "--key", key, value });
            }
        }

        return commands;
    }

    private static bool KeyPointsAt(
        IReadOnlyDictionary<string, string>? state,
        string key,
        string host,
        int port)
    {
        if (state is null || !state.TryGetValue(key, out string? value))
        {
            return false;
        }

        string suffix = $":{port}";
        return value.EndsWith(suffix, StringComparison.Ordinal)
            && value.StartsWith(host, StringComparison.Ordinal);
    }

    /// <summary>当前 http/https 段是否指向 <paramref name="host"/>:<paramref name="port"/>。</summary>
    public static bool PointsAt(IReadOnlyDictionary<string, string>? state, string host, int port)
    {
        if (state is null)
        {
            return false;
        }

        // socksProxy 不算：它不是我们写进去的。
        foreach (string key in new[] { "httpProxy", "httpsProxy" })
        {
            if (KeyPointsAt(state, key, host, port))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>缺省值：读不到的键不进快照（而不是写一个空串回去）。</summary>
    public static string? NormalizeReadValue(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string value = raw.Trim();
        return value.Length == 0 ? "" : value;
    }

    public static string FormatPort(int port) => port.ToString(CultureInfo.InvariantCulture);
}
