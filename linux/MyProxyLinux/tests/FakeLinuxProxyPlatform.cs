using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 一套假的外部命令环境：gsettings / kreadconfig / kwriteconfig，以及环境变量文件。
///
/// <para>
/// 它<b>真的维护状态</b>：<c>set</c> 会改掉内部的值，<c>get</c> 读的是改过之后的值。
/// 这一点很重要——<see cref="LinuxProxyService"/> 的所有权判断（「现在指向的还是不是
/// 我们」）只有在读写闭环里才有意义，一个永远回固定值的假实现会让那套判断的测试
/// 全部变成空转。
/// </para>
/// </summary>
internal sealed class FakeLinuxProxyPlatform : ILinuxProxyPlatform
{
    private readonly Dictionary<string, string> _gnome = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kde = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakeLinuxProxyPlatform()
    {
        // 出厂状态：没有任何代理设置。
        _gnome[GnomeProxyCommands.Key(GnomeProxyCommands.Schema, "mode")] = "'none'";
        _gnome[GnomeProxyCommands.Key(GnomeProxyCommands.Schema, "autoconfig-url")] = "''";
        _gnome[GnomeProxyCommands.Key(GnomeProxyCommands.Schema, "use-same-proxy")] = "false";
        _gnome[GnomeProxyCommands.Key(GnomeProxyCommands.Schema, "ignore-hosts")] = "@as []";
        foreach (string section in new[] { "http", "https", "socks" })
        {
            _gnome[GnomeProxyCommands.Key($"{GnomeProxyCommands.Schema}.{section}", "host")] = "''";
            _gnome[GnomeProxyCommands.Key($"{GnomeProxyCommands.Schema}.{section}", "port")] = "0";
        }
    }

    public bool GnomePresent { get; set; } = true;

    public bool KdePresent { get; set; }

    /// <summary>让 gsettings 的写操作失败：用来验证「恢复失败必须保留证据」。</summary>
    public bool GnomeWritesFail { get; set; }

    /// <summary>Inject a persisted environment file deletion failure.</summary>
    public string? EnvironmentFileDeleteFailsAt { get; set; }

    public int GnomeWriteCount { get; private set; }

    public bool CommandExists(string command) => command switch
    {
        "gsettings" => GnomePresent,
        "kwriteconfig5" or "kwriteconfig6" => KdePresent,
        "kreadconfig5" or "kreadconfig6" => KdePresent,
        _ => false
    };

    public (int ExitCode, string StandardOutput)? Run(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        switch (command)
        {
            case "gsettings":
                return RunGnome(arguments);
            case "kwriteconfig5":
            case "kwriteconfig6":
                return RunKdeWrite(arguments);
            case "kreadconfig5":
            case "kreadconfig6":
                return RunKdeRead(arguments);
            default:
                return null;
        }
    }

    public string? ReadEnvironmentFile(string path)
        => _files.TryGetValue(path, out string? contents) ? contents : null;

    public void WriteEnvironmentFile(string path, string contents) => _files[path] = contents;

    public void DeleteEnvironmentFile(string path)
    {
        if (string.Equals(path, EnvironmentFileDeleteFailsAt, StringComparison.Ordinal))
        {
            throw new IOException("simulated environment file deletion failure");
        }

        _files.Remove(path);
    }

    public bool EnvironmentFileExists(string path) => _files.ContainsKey(path);

    /// <summary>外部（第三方程序或用户）改设置：用来验证所有权判断。</summary>
    public void SetGnomeExternally(string schema, string key, string value)
        => _gnome[GnomeProxyCommands.Key(schema, key)] = value;

    public string GetGnome(string schema, string key)
        => _gnome.TryGetValue(GnomeProxyCommands.Key(schema, key), out string? value) ? value : "";

    private (int, string) RunGnome(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2)
        {
            return (1, "");
        }

        string action = arguments[0];
        string schema = arguments[1];

        if (action == "get" && arguments.Count >= 3)
        {
            return (0, GetGnome(schema, arguments[2]));
        }

        if (action == "set" && arguments.Count >= 4)
        {
            if (GnomeWritesFail)
            {
                return (1, "");
            }

            GnomeWriteCount++;
            _gnome[GnomeProxyCommands.Key(schema, arguments[2])] = arguments[3];
            return (0, "");
        }

        return (1, "");
    }

    private (int, string) RunKdeWrite(IReadOnlyList<string> arguments)
    {
        string key = ReadOption(arguments, "--key");
        string value = arguments.Count > 0 ? arguments[^1] : "";
        if (key.Length == 0)
        {
            return (1, "");
        }

        _kde[key] = value;
        return (0, "");
    }

    private (int, string) RunKdeRead(IReadOnlyList<string> arguments)
    {
        string key = ReadOption(arguments, "--key");
        return _kde.TryGetValue(key, out string? value) ? (0, value) : (1, "");
    }

    private static string ReadOption(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == name)
            {
                return arguments[index + 1];
            }
        }

        return "";
    }
}
