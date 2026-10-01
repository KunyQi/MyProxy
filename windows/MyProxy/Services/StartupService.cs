using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 开机自启动，通过「计划任务」而非 HKCU\Run 实现。
///
/// <para>
/// 应用要求管理员运行（app.manifest），而 <c>HKCU\...\Run</c> 项在登录时**不会**
/// 静默提权——一个 requireAdministrator 的程序放在 Run 里，登录时要么被拦下、要么
/// 什么都不发生。带「最高权限」的计划任务是 Windows 上让提权程序在登录时无提示自启
/// 的标准做法，所以这里改用 <c>schtasks.exe</c>。
/// </para>
///
/// <para>
/// 创建/删除任务本身需要管理员权限——本应用启动即提权，所以这条前提成立。
/// 判定「已启用」时既要任务存在、又要它指向**当前**这个 exe、还要带着当前这一版的设置
/// （见 <see cref="AutostartTask"/>）：更新换过安装目录的、旧版按 schtasks 默认值建的
/// （电池上不启动、拔电源即终止、72 小时后强制结束），都视为未启用，由 <see cref="App"/>
/// 的启动逻辑按需重建。
/// </para>
/// </summary>
public sealed class StartupService : IStartupService
{
    // 任务名带空格无妨；schtasks 以 /TN 精确匹配。改名会让旧任务变成孤儿，
    // 因此这个常量一旦发布就不能轻易动。
    private const string TaskName = "MyProxy Autostart";

    // 本功能之前的自启动写在这里。留着这两个常量不是为了再写它，而是为了删它：
    // 见 RemoveLegacyRunEntry。
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "MyProxy";

    public bool IsAutoStartEnabled()
    {
        // /XML 输出与语言无关：从中取 <Command> 与当前 exe 比对，
        // 比解析本地化的 LIST 文案稳。任务不存在时 schtasks 返回非 0。
        (int exitCode, string stdout) = RunSchTasks($"/Query /TN \"{TaskName}\" /XML ONE");
        return exitCode == 0 && AutostartTask.Matches(stdout, CurrentExecutablePath());
    }

    /// <inheritdoc />
    public void RemoveLegacyAutoStart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            // 只删自己那一个值，不碰 Run 项本身：那是整台机器共用的。
            key?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 清不掉不该让应用起不来。下次启动还会再试一次。
        }
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        if (enabled)
        {
            // 命令行形式只能设触发器与权限，其余全是任务计划程序的默认值；完整定义见
            // AutostartTask。
            RegisterTask(AutostartTask.BuildXml(CurrentExecutablePath(), CurrentUserSid()));
        }
        else
        {
            // 不存在时删除返回非 0，视作已达目标，不抛。
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        }
    }

    // ITaskFolder::RegisterTask 的 flags 与 logonType（taskschd.h）。
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    /// <summary>
    /// 把任务定义以字符串交给任务计划程序的 COM 接口，<b>不落盘</b>。
    ///
    /// <para>
    /// 不用 <c>schtasks /Create /XML &lt;文件&gt;</c>：那要求先把 XML 写进文件，而提权进程
    /// 能写、又能让 schtasks 读到的临时位置（%TEMP%）同一用户下未提权的进程也能写。
    /// 写完到 schtasks 读取之间的几十毫秒里，别的程序就能把 &lt;Command&gt; 换成自己的、
    /// 或把运行身份改掉，得到一个每次登录都静默提权运行的任务。
    /// </para>
    /// </summary>
    private static void RegisterTask(string xml)
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("任务计划程序不可用。");
        object? service = null;
        object? folder = null;
        object? registered = null;
        try
        {
            service = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("任务计划程序不可用。");
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            dynamic root = folder!;
            // 运行身份取自 XML 里的 Principal（当前用户 SID、交互式令牌、最高权限）；
            // 这里不再传用户名与密码。
            registered = root.RegisterTask(
                TaskName, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"无法创建开机自启动计划任务（0x{exception.HResult:X8}）。", exception);
        }
        finally
        {
            foreach (object? comObject in new[] { registered, folder, service })
            {
                if (comObject is not null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
        }
    }

    private static string CurrentExecutablePath()
    {
        // 单文件发布下 ProcessPath 是磁盘上真正被双击的 exe，而不是运行时把内容
        // 解压到的 %TEMP%\.net\ 目录——任务要拉起的正是前者。
        return Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "MyProxy.exe");
    }

    /// <summary>
    /// 触发器与运行身份都绑在当前用户上：只在这个用户登录时拉起，而不是「任何用户登录时」。
    /// 用 SID 而不是 DOMAIN\name：微软账户、Azure AD 账户的显示名形式各不相同，SID 不变。
    /// </summary>
    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("无法取得当前用户的 SID。");
    }

    private static (int ExitCode, string Output) RunSchTasks(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");

        // 两条管道必须并发读。顺序 ReadToEnd 会在子进程写满 stderr 缓冲区
        // （Windows 默认 4KB）时死锁：schtasks 阻塞在写 stderr 上不再退出，
        // 而我们还堵在 stdout 上等 EOF。启动路径上这等于应用永久卡死。
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, combined);
    }
}
