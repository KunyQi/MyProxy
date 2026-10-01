using System.IO;
using System.Text;

namespace MyProxy.Services;

/// <summary>
/// 开机（登录）自启。
///
/// <para>
/// Windows 端因为 manifest 要求 <c>requireAdministrator</c>，<c>HKCU\...\Run</c>
/// 在登录时不会静默提权，所以自启走的是最高权限的计划任务。Linux 没有这个问题：
/// 客户端是普通用户进程，自启就用<b>用户级</b>的机制，绝不写系统级单元、
/// 更不需要 root。
/// </para>
///
/// <list type="number">
/// <item>有 systemd 用户会话时写 <c>~/.config/systemd/user/myproxy.service</c>
/// 并 <c>systemctl --user enable</c>；</item>
/// <item>没有 systemd 的桌面环境（登录时读 XDG autostart）时写
/// <c>~/.config/autostart/myproxy.desktop</c>。</item>
/// </list>
///
/// <para>
/// <b>两个机制只留一个</b>：<see cref="RemoveLegacyAutoStart"/> 删掉另一个——两处
/// 都写着自启会让同一个会话里起两个进程，第二个会撞在单实例锁上（这是安全的，
/// 但用户会看到一个毫无理由的失败）。
/// </para>
/// </summary>
public sealed class LinuxStartupService : IStartupService
{
    private readonly ILogService? _log;
    private readonly string _executablePath;
    private readonly string? _installDir;

    public LinuxStartupService(ILogService? log = null, string? executablePath = null)
    {
        _log = log;
        _executablePath = executablePath ?? ResolveExecutablePath();
        _installDir = Path.GetDirectoryName(_executablePath);
    }

    /// <summary>systemd 用户单元是否是本机可用的机制。</summary>
    public static bool HasSystemdUser
    {
        get
        {
            // 用户会话里 systemd 一定会把 XDG_RUNTIME_DIR 给出来；没有它就没有
            // 用户级总线，systemctl --user 会直接失败。
            string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir);
        }
    }

    public bool IsAutoStartEnabled()
    {
        return HasSystemdUser
            ? File.Exists(LinuxPaths.ServiceUnitPath)
            : File.Exists(LinuxPaths.AutostartDesktopPath);
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        if (enabled)
        {
            if (HasSystemdUser)
            {
                WriteServiceUnit();
                RemoveFileNoThrow(LinuxPaths.AutostartDesktopPath);
            }
            else
            {
                WriteAutostartDesktop();
                RemoveFileNoThrow(LinuxPaths.ServiceUnitPath);
            }

            return;
        }

        DisableSystemdUnit();
        RemoveFileNoThrow(LinuxPaths.ServiceUnitPath);
        RemoveFileNoThrow(LinuxPaths.AutostartDesktopPath);
    }

    /// <summary>
    /// 删掉另一个机制留下的自启项。幂等，每次启动都可以跑一遍：
    /// 两处同时存在时会起两个进程，而用户只会看到一个「启动失败」。
    /// </summary>
    public void RemoveLegacyAutoStart()
    {
        if (HasSystemdUser)
        {
            RemoveFileNoThrow(LinuxPaths.AutostartDesktopPath);
        }
    }

    /// <summary>
    /// 已启用的自启项按本版本的内容重写一遍（内容相同就什么都不做）。
    ///
    /// <para>
    /// 单元文件是某个旧版本写下的，之后就一直躺在那里；单元内容一旦改过（例如
    /// <c>KillMode</c>），已经装好的机器只有在这里才拿得到新内容。只在内容真的不同时才
    /// 写盘并 <c>daemon-reload</c>，所以每次启动都调用它是便宜的。
    /// </para>
    /// </summary>
    public void RefreshAutoStartEntry()
    {
        if (HasSystemdUser)
        {
            if (File.Exists(LinuxPaths.ServiceUnitPath) && !HasContent(LinuxPaths.ServiceUnitPath, BuildServiceUnit()))
            {
                LinuxFileSecurity.WriteAtomic(LinuxPaths.ServiceUnitPath, BuildServiceUnit());
                RunSystemctl("--user", "daemon-reload");
            }

            return;
        }

        if (File.Exists(LinuxPaths.AutostartDesktopPath) && !HasContent(LinuxPaths.AutostartDesktopPath, BuildAutostartDesktop()))
        {
            LinuxFileSecurity.WriteAtomic(LinuxPaths.AutostartDesktopPath, BuildAutostartDesktop());
        }
    }

    internal string BuildServiceUnit()
    {
        var unit = new StringBuilder();
        unit.AppendLine("[Unit]");
        unit.AppendLine($"Description={AppInfo.ProductName} 客户端（本地内核与系统代理）");
        unit.AppendLine("Documentation=file://" + Path.Combine(_installDir ?? "", "README.md"));
        unit.AppendLine("After=network-online.target");
        unit.AppendLine("Wants=network-online.target");
        unit.AppendLine();
        unit.AppendLine("[Service]");
        unit.AppendLine("Type=simple");
        unit.AppendLine($"ExecStart={_executablePath} run");
        // 崩溃后拉起来：常驻程序不在时，系统代理会指向一个没人听的本地端口。
        unit.AppendLine("Restart=on-failure");
        unit.AppendLine("RestartSec=5");
        // KillMode=process：停止与崩溃时 systemd 只动主进程，**不**去杀 cgroup 里剩下的
        // xray。fail-open 顺序要求「系统代理恢复失败时保留内核」（否则代理指向一个死端口
        // = 整机断网）；mixed/control-group 会在主进程退出后把内核一并 SIGKILL，
        // 把这条规矩架空。留下的内核由下一次启动的崩溃恢复按 xray.pid 收拾
        // （先恢复代理，成功了才杀它），与 Windows 端同一条路。
        unit.AppendLine("KillMode=process");
        unit.AppendLine("TimeoutStopSec=20");
        unit.AppendLine();
        unit.AppendLine("[Install]");
        unit.AppendLine("WantedBy=default.target");
        return unit.ToString();
    }

    internal string BuildAutostartDesktop()
    {
        var desktop = new StringBuilder();
        desktop.AppendLine("[Desktop Entry]");
        desktop.AppendLine("Type=Application");
        desktop.AppendLine($"Name={AppInfo.ProductName}");
        desktop.AppendLine("Comment=本地内核与系统代理");
        desktop.AppendLine($"Exec={_executablePath} run");
        desktop.AppendLine("Terminal=false");
        desktop.AppendLine("X-GNOME-Autostart-enabled=true");
        // 登录时不要抢焦点：这是个后台服务，不是用户主动打开的窗口。
        desktop.AppendLine("NoDisplay=true");
        return desktop.ToString();
    }

    private void WriteServiceUnit()
    {
        Directory.CreateDirectory(LinuxPaths.UserUnitDir);
        LinuxFileSecurity.WriteAtomic(LinuxPaths.ServiceUnitPath, BuildServiceUnit());
        RunSystemctl("--user", "daemon-reload");
        RunSystemctl("--user", "enable", LinuxPaths.ServiceUnitName);
    }

    private void WriteAutostartDesktop()
    {
        Directory.CreateDirectory(LinuxPaths.AutostartDir);
        LinuxFileSecurity.WriteAtomic(LinuxPaths.AutostartDesktopPath, BuildAutostartDesktop());
    }

    private static bool HasContent(string path, string expected)
    {
        try
        {
            return string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DisableSystemdUnit()
    {
        if (HasSystemdUser)
        {
            RunSystemctl("--user", "disable", LinuxPaths.ServiceUnitName);
            RunSystemctl("--user", "daemon-reload");
        }
    }

    private void RunSystemctl(params string[] arguments)
    {
        if (!HasSystemdUser)
        {
            return;
        }

        (int ExitCode, string StandardOutput)? result = LinuxCommands.Run(
            "systemctl", arguments, TimeSpan.FromSeconds(10));

        if (result is null || result.Value.ExitCode != 0)
        {
            // 不抛：单元文件已经写下/删掉了，systemctl 的失败只影响「这次是否立即生效」，
            // 下一次登录仍然会按文件的存在与否执行。把原因记下来即可。
            _log?.Warn(
                nameof(LinuxStartupService),
                $"systemctl {string.Join(' ', arguments)} 未成功（退出码 {result?.ExitCode.ToString() ?? "无"}）");
        }
    }

    private static string ResolveExecutablePath()
    {
        // 目录发布时 AppContext.BaseDirectory 里就是 myproxy 本体；
        // 单文件发布时它指向那个文件自己。
        string candidate = Path.Combine(LinuxPaths.InstallDir, "myproxy");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        string? processPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(processPath) ? processPath : candidate;
    }

    private static void RemoveFileNoThrow(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉不影响主流程。
        }
    }
}
