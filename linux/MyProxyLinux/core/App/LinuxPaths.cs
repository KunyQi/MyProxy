using System.Runtime.InteropServices;

namespace MyProxy;

/// <summary>
/// Linux 端的目录约定（XDG Base Directory）。
///
/// <para>
/// 与 Windows 端的 <c>Environment.SpecialFolder.LocalApplicationData</c> 对应的是
/// <c>$XDG_DATA_HOME</c>（默认 <c>~/.local/share</c>）。
/// </para>
///
/// <para>
/// <b>刻意不用 <c>$XDG_RUNTIME_DIR</c>（/run/user/N，tmpfs）放运行态文件。</b>
/// <c>xray.pid</c> 放在那里没问题（重启即失效，本就该失效），但
/// <c>proxy-applied.marker</c> 与 <c>proxy-backup.json</c> 不行：系统的代理设置
/// 跨重启存活，而 xray 不存活。标记文件若随 tmpfs 消失，崩溃或重启之后就没有任何
/// 证据告诉下一次启动「系统代理还指着那个已经死掉的本地端口」，用户会失去网络而
/// 且不知道原因。这与 Windows 端 fail-open 的顺序是同一条规矩。
/// </para>
/// </summary>
public static class LinuxPaths
{
    /// <summary>应用名：XDG 目录名、systemd 用户单元名、桌面项名都是它。</summary>
    public const string AppName = "myproxy";

    /// <summary>当前用户的主目录。</summary>
    public static string HomeDir { get; } = ResolveHome();

    /// <summary>$XDG_CONFIG_HOME（默认 ~/.config）。</summary>
    public static string XdgConfigHome { get; } = ResolveXdgConfigHome();

    /// <summary>数据根目录：$XDG_DATA_HOME/myproxy（默认 ~/.local/share/myproxy）。</summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>
    /// 应用配置目录：$XDG_CONFIG_HOME/myproxy（默认 ~/.config/myproxy）。
    /// <b>静态字段的初始化顺序就是声明顺序</b>，所以 HomeDir / XdgConfigHome 必须在它前面。
    /// </summary>
    public static string ConfigRoot { get; } = Path.Combine(XdgConfigHome, AppName);

    /// <summary>运行态目录，<b>持久</b>在数据根目录下（见类型注释）。</summary>
    public static string RuntimeDir => Path.Combine(DataRoot, "runtime");

    /// <summary>日志目录。</summary>
    public static string LogDir => Path.Combine(DataRoot, "logs");

    /// <summary>内核资产目录：安装目录下的 <c>Core/</c>。</summary>
    public static string CoreDir => Path.Combine(InstallDir, "Core");

    /// <summary>systemd 用户单元目录：$XDG_CONFIG_HOME/systemd/user。</summary>
    public static string UserUnitDir => Path.Combine(XdgConfigHome, "systemd", "user");

    /// <summary>systemd 用户单元的单元名。</summary>
    public static string ServiceUnitName => $"{AppName}.service";

    /// <summary>systemd 用户单元的完整路径。</summary>
    public static string ServiceUnitPath => Path.Combine(UserUnitDir, ServiceUnitName);

    /// <summary>自启 .desktop 目录（没有 systemd 的桌面环境用）：$XDG_CONFIG_HOME/autostart。</summary>
    public static string AutostartDir => Path.Combine(XdgConfigHome, "autostart");

    /// <summary>自启 .desktop 文件路径。</summary>
    public static string AutostartDesktopPath => Path.Combine(AutostartDir, $"{AppName}.desktop");

    /// <summary>
    /// 安装目录。目录发布时就是可执行文件所在目录；<c>MYPROXY_INSTALL_DIR</c> 可以把它
    /// 指到别处——打包脚本、测试与「把 tarball 解开就地跑」都需要这个口子。
    /// </summary>
    public static string InstallDir { get; } = ResolveInstallDir();

    /// <summary>
    /// 内核只发布 linux-x64 与 linux-arm64 两种架构。别的架构在这里说清楚，
    /// 而不是让 xray 报一个「Exec format error」。
    /// </summary>
    public static string RuntimeIdentifier => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        var other => throw new PlatformNotSupportedException(
            $"客户端只提供 linux-x64 与 linux-arm64 两种内核，当前架构是 {other}。")
    };

    private static string ResolveHome()
    {
        string? home = Environment.GetEnvironmentVariable("HOME");
        return !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string ResolveXdgConfigHome()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(ResolveHome(), ".config");
    }

    private static string ResolveDataRoot()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(ResolveHome(), ".local", "share");
        return Path.Combine(root, AppName);
    }

    private static string ResolveInstallDir()
    {
        string? configured = Environment.GetEnvironmentVariable("MYPROXY_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            return configured.TrimEnd(Path.DirectorySeparatorChar);
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
}
