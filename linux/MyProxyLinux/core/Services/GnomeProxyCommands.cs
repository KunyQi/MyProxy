using System.Globalization;
using System.Text;
using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>
/// GNOME（org.gnome.system.proxy）代理设置的命令构造与解析。
///
/// <para>
/// 抽成纯函数有两个理由：它是这套逻辑里最容易写错的部分（gsettings 的
/// <c>get</c> 输出是 GVariant 文本，不是 JSON），而纯函数能在没有 GNOME 的机器上
/// 被逐条测到——本仓库对 Android 的 VPN 清理策略用的就是这个办法。
/// </para>
/// </summary>
internal static class GnomeProxyCommands
{
    public const string Schema = "org.gnome.system.proxy";

    /// <summary>
    /// 直连名单。默认与 Windows 端的 <c>&lt;local&gt;</c> 等价：本机与私网不走代理。
    /// <b>不含</b> <c>127.0.0.1</c> 之外的任何业务域名——代理是给用户上网用的，
    /// 不是给我们挑流量用的。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultIgnoreHosts = new[]
    {
        "localhost", "127.0.0.0/8", "::1",
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"
    };

    /// <summary>
    /// 打开手动模式并把 http/https 指到本地入站。
    ///
    /// <para>
    /// <b>刻意不动 <c>org.gnome.system.proxy.socks</c>。</b>本地入站是 HTTP 协议的代理
    /// （与 Windows 端同一个 <c>XrayConfigGenerator</c>，只有 <c>http</c> 入站），把桌面
    /// 的 SOCKS 指到它不是「也走了隧道」，而是让所有读该设置的程序（Firefox 等）用 SOCKS
    /// 协议去连一个 HTTP 代理——它们会直接失败。而「留着用户原来的 SOCKS 不变」的代价是
    /// 那些程序绕过隧道走直连。两种都不是免费的，但后者只影响<b>自己配了 SOCKS 的人</b>，
    /// 前者会让一个正常配置的用户突然上不了网；所以这里选择不碰它，并在抓快照时
    /// 如实提示（见 <see cref="LinuxProxyService"/>）。
    /// 真正的修法是数据面提供一个 SOCKS 入站，那会改动共享的配置生成器与 Windows 端的
    /// 形状，不在本次移植范围内。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string[]> EnableCommands(string host, int port)
    {
        var commands = new List<string[]>
        {
            new[] { "set", Schema, "mode", Quote("manual") },
            // use-same-proxy 关掉：http 与 https 各自显式写一遍，读回来时才有确定的形状。
            new[] { "set", Schema, "use-same-proxy", "false" },
            new[] { "set", $"{Schema}.http", "host", Quote(host) },
            new[] { "set", $"{Schema}.http", "port", port.ToString(CultureInfo.InvariantCulture) },
            new[] { "set", $"{Schema}.https", "host", Quote(host) },
            new[] { "set", $"{Schema}.https", "port", port.ToString(CultureInfo.InvariantCulture) },
            // 自动配置必须让位：mode=manual 与 autoconfig-url 同时存在时，
            // 行为由客户端决定，而我们要的是「一定走本地端口」。
            new[] { "set", Schema, "autoconfig-url", "''" },
            new[] { "set", Schema, "ignore-hosts", FormatStringArray(DefaultIgnoreHosts) }
        };

        return commands;
    }

    /// <summary>
    /// 恢复计划。<b>逐段判断所有权</b>：哪一段（http / https / socks）现在仍然指向
    /// 我们，才写回哪一段。
    ///
    /// <para>
    /// 这与 Windows 端的 <c>RestoreSnapshotOrDisableFallback</c> 是同一条规矩，
    /// 只是那边只有一个 <c>ProxyServer</c> 值、这边有三段。连接期间企业 VPN 或用户
    /// 自己改了 http 段而没动 https 段是常见情形；按「整个后端」判断，那次恢复就会
    /// 把用户刚写进去的值一起覆盖掉——这条正是被测试逮住的。
    /// </para>
    ///
    /// <para>
    /// <paramref name="target"/> 为 null 表示没有可信快照：这时降级为「把我们写进去的
    /// 那几段清空、总开关改回 none」，而不是「什么都不做」——我们的端口马上就要没人听了。
    /// 辅助项（自动配置、直连名单）只在确实动过我们那几段时才写回。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string[]> RestoreCommands(
        GnomeProxyState? current,
        GnomeProxyState? target,
        string host,
        int port)
    {
        var commands = new List<string[]>();
        bool touchedAny = false;

        touchedAny |= PlanSection("http", current?.HttpHost, current?.HttpPort ?? 0, target?.HttpHost, target?.HttpPort ?? 0);
        touchedAny |= PlanSection("https", current?.HttpsHost, current?.HttpsPort ?? 0, target?.HttpsHost, target?.HttpsPort ?? 0);
        // socks 段不参与：我们从没写过它（见 EnableCommands 的说明），所以也没有
        // 「按所有权恢复」这件事——碰它只会覆盖用户自己的设置。

        if (!touchedAny)
        {
            // 一段都不是我们的了：那是别人的设置，一个字都不该动。
            return commands;
        }

        if (target is null)
        {
            // 没有可信快照：把总开关关掉，剩下的交给用户。
            commands.Add(new[] { "set", Schema, "mode", Quote("none") });
            return commands;
        }

        commands.Add(new[] { "set", Schema, "autoconfig-url", Quote(target.AutoconfigUrl) });
        commands.Add(new[] { "set", Schema, "use-same-proxy", target.UseSameProxy ? "true" : "false" });
        commands.Add(new[] { "set", Schema, "ignore-hosts", FormatStringArray(target.IgnoreHosts) });
        // mode 最后写：它是总开关，先写别的再写它，中间不会出现「已启用但端口还是旧的」。
        // schema 里它是字符串类型，规范形状是带引号的。
        commands.Add(new[] { "set", Schema, "mode", Quote(target.Mode) });
        return commands;

        bool PlanSection(string section, string? currentHost, int currentPort, string? targetHost, int targetPort)
        {
            if (!PointsAtSection(currentHost, currentPort, host, port))
            {
                return false;
            }

            commands.Add(new[] { "set", $"{Schema}.{section}", "host", Quote(targetHost ?? "") });
            commands.Add(new[]
            {
                "set", $"{Schema}.{section}", "port",
                targetPort.ToString(CultureInfo.InvariantCulture)
            });
            return true;
        }
    }

    /// <summary>某一段是否指向 <paramref name="host"/>:<paramref name="port"/>。</summary>
    public static bool PointsAtSection(string? sectionHost, int sectionPort, string host, int port)
        => sectionPort == port && string.Equals(sectionHost, host, StringComparison.Ordinal);

    /// <summary>
    /// GVariant 的字符串字面量。命令里写不带引号的值 gsettings 也认，但读回来一律是
    /// 带引号的形状——两边写成一样，快照与命令之间就不会有意外。
    /// </summary>
    public static string Quote(string value)
        => "'" + (value ?? "").Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>要读回来的键：schema、键名、解析方式。</summary>
    public static readonly IReadOnlyList<(string Schema, string Key, string Kind)> ReadSpecs = new[]
    {
        (Schema, "mode", "string"),
        (Schema, "autoconfig-url", "string"),
        (Schema, "use-same-proxy", "boolean"),
        (Schema, "ignore-hosts", "string-array"),
        ($"{Schema}.http", "host", "string"),
        ($"{Schema}.http", "port", "int"),
        ($"{Schema}.https", "host", "string"),
        ($"{Schema}.https", "port", "int"),
        ($"{Schema}.socks", "host", "string"),
        ($"{Schema}.socks", "port", "int")
    };

    /// <summary>
    /// 把 <c>gsettings get</c> 的输出解析成快照。
    ///
    /// <para>
    /// gsettings 输出的是 GVariant 文本：字符串带单引号、数组是
    /// <c>['a', 'b']</c>、布尔是 <c>true</c>/<c>false</c>。解析失败一律按
    /// 「这个键没有值」处理（空串 / 0 / 空数组），绝不让一个读不懂的值变成
    /// 一个我们会写回去的乱码。
    /// </para>
    /// </summary>
    public static GnomeProxyState Parse(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new GnomeProxyState
        {
            Mode = NormalizeMode(Get(values, Schema, "mode")),
            AutoconfigUrl = ParseString(Get(values, Schema, "autoconfig-url")),
            UseSameProxy = ParseBoolean(Get(values, Schema, "use-same-proxy")),
            IgnoreHosts = ParseStringArray(Get(values, Schema, "ignore-hosts")),
            HttpHost = ParseString(Get(values, $"{Schema}.http", "host")),
            HttpPort = ParseInt(Get(values, $"{Schema}.http", "port")),
            HttpsHost = ParseString(Get(values, $"{Schema}.https", "host")),
            HttpsPort = ParseInt(Get(values, $"{Schema}.https", "port")),
            SocksHost = ParseString(Get(values, $"{Schema}.socks", "host")),
            SocksPort = ParseInt(Get(values, $"{Schema}.socks", "port"))
        };
    }

    /// <summary>gsettings 的取值键：schema + 键名。</summary>
    public static string Key(string schema, string key) => schema + "\u0000" + key;

    /// <summary>mode 只认三个值；别的一律当成 none（= 不代理），避免把未知状态写回去。</summary>
    public static string NormalizeMode(string? raw)
    {
        string value = (raw ?? "").Trim().Trim('\'').ToLowerInvariant();
        return value switch
        {
            "manual" => "manual",
            "auto" => "auto",
            _ => "none"
        };
    }

    /// <summary>当前 http/https 段是否指向 <paramref name="host"/>:<paramref name="port"/>。</summary>
    public static bool PointsAt(GnomeProxyState? state, string host, int port)
    {
        if (state is null || !string.Equals(NormalizeMode(state.Mode), "manual", StringComparison.Ordinal))
        {
            return false;
        }

        // socks 段不算：它不是我们写进去的（见 EnableCommands）。
        return Matches(state.HttpHost, state.HttpPort)
            || Matches(state.HttpsHost, state.HttpsPort);

        bool Matches(string candidateHost, int candidatePort)
            => candidatePort == port && string.Equals(candidateHost, host, StringComparison.Ordinal);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string schema, string key)
        => values.TryGetValue(Key(schema, key), out string? value) ? value : "";

    private static string ParseString(string? raw)
    {
        string value = (raw ?? "").Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("\\'", "'", StringComparison.Ordinal);
        }

        // 未设置的字符串键在 gsettings 里回的是 '' 或 @s ''，两种都按空处理。
        return value.StartsWith('@') ? "" : value;
    }

    private static int ParseInt(string? raw)
        => int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static bool ParseBoolean(string? raw)
        => string.Equals((raw ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static List<string> ParseStringArray(string? raw)
    {
        var result = new List<string>();
        string value = (raw ?? "").Trim();
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            return result;
        }

        var current = new StringBuilder();
        bool inString = false;
        for (int index = 1; index < value.Length - 1; index++)
        {
            char character = value[index];
            if (character == '\\' && index + 1 < value.Length - 1)
            {
                current.Append(value[++index]);
                continue;
            }

            if (character == '\'')
            {
                inString = !inString;
                continue;
            }

            if (character == ',' && !inString)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString().Trim());
        }

        return result.Where(value => value.Length > 0).ToList();
    }

    /// <summary>GVariant 字符串数组的字面量：<c>['a', 'b']</c>。</summary>
    private static string FormatStringArray(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder("[");
        for (int index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append('\'').Append(values[index].Replace("'", "\\'", StringComparison.Ordinal)).Append('\'');
        }

        builder.Append(']');
        return builder.ToString();
    }
}
