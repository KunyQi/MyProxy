using System.Diagnostics;
using System.Text.Json;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Cli;

/// <summary>
/// <c>myproxy</c> 命令行入口。
///
/// <para>
/// 普通界面的用词规矩在这里同样成立：不出现 VLESS / REALITY / UUID / SNI /
/// Inbound / Routing / SOCKS Port。用户看到的是「绑定 / 启动·停止 / 智能分流·全局代理」。
/// 延迟与出口归属属于「检测连接」的结论，可以有。
/// </para>
///
/// <para>
/// 除 <c>run</c> 之外的每条命令都通过控制套接字发给守护进程：连接只有一个持有者，
/// 命令行不该成为第二个。
/// </para>
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitNotRunning = 3;

    public static async Task<int> Main(string[] args)
    {
        // 在任何 I/O 之前把 umask 收成 0077：这样连共享代码（LogService 的 app.log、
        // ConnectionController 的 runtime/config.json）写出来的文件也是 0600，
        // 而不是默认的 0644。原生 Linux 冒烟测试实测过那个 0644。
        LinuxProcess.ApplyDefaults();

        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (MyProxyException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"出错了：{ex.Message}");
            return ExitFailure;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return ExitOk;
        }

        string command = args[0].ToLowerInvariant();
        string argument = args.Length > 1 ? args[1] : "";
        bool json = args.Contains("--json", StringComparer.Ordinal);

        switch (command)
        {
            case "version" or "--version":
                Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}（{AppInfo.Platform}, {LinuxPaths.RuntimeIdentifier}）");
                return ExitOk;

            case "run":
                return await RunDaemonAsync(args).ConfigureAwait(false);

            case "start":
                return await EnsureDaemonThenSendAsync(ControlCommands.Start, "", startIfMissing: true)
                    .ConfigureAwait(false);

            case "logs":
                return TailLogs(args);

            case "status":
                return await SendAsync(ControlCommands.Status, "", json).ConfigureAwait(false);

            case "bind":
                if (argument.Length == 0)
                {
                    Console.Error.WriteLine("用法：myproxy bind <8 位配对码>");
                    return ExitFailure;
                }

                return await EnsureDaemonThenSendAsync(ControlCommands.Bind, argument, startIfMissing: true)
                    .ConfigureAwait(false);

            case "unbind":
                return await SendAsync(ControlCommands.Unbind, "").ConfigureAwait(false);

            case "stop":
                return await SendAsync(ControlCommands.Stop, "").ConfigureAwait(false);

            case "mode":
                if (argument is not ("smart" or "global"))
                {
                    Console.Error.WriteLine("用法：myproxy mode smart|global");
                    return ExitFailure;
                }

                return await SendAsync(ControlCommands.Mode, argument).ConfigureAwait(false);

            case "check":
                return await SendAsync(ControlCommands.Check, "").ConfigureAwait(false);

            case "autostart" or "autoconnect":
                if (argument is not ("on" or "off"))
                {
                    Console.Error.WriteLine($"用法：myproxy {command} on|off");
                    return ExitFailure;
                }

                return await SendAsync(
                    command == "autostart" ? ControlCommands.AutoStart : ControlCommands.AutoConnect,
                    argument).ConfigureAwait(false);

            case "theme":
                if (argument is not ("classic" or "porcelain"))
                {
                    Console.Error.WriteLine("用法：myproxy theme classic|porcelain");
                    return ExitFailure;
                }

                return await SendAsync(ControlCommands.Theme, argument).ConfigureAwait(false);

            case "update":
                return await UpdateAsync(argument).ConfigureAwait(false);

            case "shutdown":
                return await SendAsync(ControlCommands.Shutdown, "").ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"未知命令：{command}");
                PrintUsage();
                return ExitFailure;
        }
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    /// <summary>
    /// 守护进程模式。<c>--socket &lt;path&gt;</c> 与 <c>--data-root &lt;path&gt;</c>
    /// 只给测试与打包用：正常运行时这两个位置由 XDG 决定。
    /// </summary>
    private static async Task<int> RunDaemonAsync(string[] args)
    {
        string? socketPath = ReadOption(args, "--socket");
        string? dataRoot = ReadOption(args, "--data-root");

        var services = new LinuxAppServices(dataRoot, socketPath);
        await using var daemon = new LinuxDaemon(
            services,
            new[] { "run" }.Concat(PassThroughOptions(args)).ToArray());

        using var cts = new CancellationTokenSource();
        return await daemon.RunAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// execv 重启时要把「用哪个 data-root / socket」原样带给新版本，否则更新之后
    /// 新进程会连到另一个（默认的）位置上，看起来像「更新之后状态全丢了」。
    /// </summary>
    private static IEnumerable<string> PassThroughOptions(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] is "--socket" or "--data-root")
            {
                yield return args[index];
                yield return args[index + 1];
            }
        }
    }

    private static async Task<int> UpdateAsync(string argument)
    {
        switch (argument)
        {
            case "check":
                return await SendAsync(ControlCommands.UpdateCheck, "").ConfigureAwait(false);
            case "apply":
                return await SendAsync(ControlCommands.UpdateApply, "").ConfigureAwait(false);
            default:
                Console.Error.WriteLine("用法：myproxy update check|apply");
                return ExitFailure;
        }
    }

    /// <summary>
    /// 把一条命令发给守护进程。<paramref name="startIfMissing"/> 为真时，
    /// 守护进程不在就先把它拉起来——<c>start</c> 与 <c>bind</c> 是「用户明确要求
    /// 开始工作」的两条命令，让它们因为「后台没跑」而失败是没道理的。
    /// </summary>
    private static async Task<int> EnsureDaemonThenSendAsync(
        string command,
        string argument,
        bool startIfMissing)
    {
        var client = new ControlClient();
        if (!client.IsRunning() && startIfMissing && !StartDaemon())
        {
            Console.Error.WriteLine("无法启动后台服务。");
            return ExitFailure;
        }

        return await SendAsync(command, argument, json: false).ConfigureAwait(false);
    }

    private static async Task<int> SendAsync(string command, string argument, bool json = false)
    {
        var client = new ControlClient();
        ControlResponse response = await client
            .SendAsync(command, argument, CancellationToken.None)
            .ConfigureAwait(false);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                response,
                new JsonSerializerOptions { WriteIndented = true }));
            return response.Ok ? ExitOk : ExitFailure;
        }

        if (!response.Ok)
        {
            Console.Error.WriteLine(response.Message.Length > 0
                ? response.Message
                : ErrorCodeMessages.Get(response.ErrorCode));

            // 「没在跑」是一个明确、可预期的状态，给一个单独的退出码：
            // 脚本据此区分「服务没起」与「命令失败」。
            return response.Status is null && response.ErrorCode == ErrorCode.ApiUnreachable
                ? ExitNotRunning
                : ExitFailure;
        }

        if (response.Status is not null)
        {
            PrintStatus(response.Status);
        }

        return ExitOk;
    }

    private static void PrintStatus(StatusSnapshot status)
    {
        Console.WriteLine($"{AppInfo.ProductName} {status.Version}（{status.Platform}）");
        Console.WriteLine($"状态：{StateText(status)}");
        Console.WriteLine($"分流：{ProxyModeText.NameFor(status.Mode)}");

        Console.WriteLine(status.Bound
            ? $"设备：{status.DeviceName}（{Shorten(status.DeviceId)}）· 配置版本 {status.ConfigVersion}"
            : "设备：未绑定");

        if (status.LatencyMs is int latency)
        {
            Console.WriteLine($"延迟：{latency} ms");
        }

        if (status.State == AppState.Connected
            && (status.UplinkBytesPerSecond > 0 || status.DownlinkBytesPerSecond > 0))
        {
            Console.WriteLine(
                $"速率：↑ {Human(status.UplinkBytesPerSecond)}/s  ↓ {Human(status.DownlinkBytesPerSecond)}/s");
        }

        if (status.State is AppState.Error && status.LastErrorMessage.Length > 0)
        {
            Console.WriteLine($"原因：{status.LastErrorMessage}");
        }

        if (status.LastSwitchErrorMessage is { Length: > 0 } switchError)
        {
            Console.WriteLine($"上次切换未成功：{switchError}");
        }

        if (status.ServerIdentityUnverified)
        {
            // 数据面照常可用，但配置更新与吊销到不了这台设备。如实说明。
            Console.WriteLine("提示：服务器身份未能验证，配置更新与吊销同步已暂停。");
        }

        if (status.HasCheck && status.CheckReachable)
        {
            string egress = status.CheckEgress switch
            {
                EgressVerdict.Verified => "出口已确认走服务器",
                EgressVerdict.Bypassed => "出口未走服务器",
                _ => "出口未能判定"
            };
            Console.WriteLine(
                $"检测：{status.CheckLatencyMs} ms（最快 {status.CheckBestLatencyMs} ms，"
                + $"{status.CheckSampleCount} 次采样）· {egress}");
        }
        else if (status.HasCheck)
        {
            Console.WriteLine($"检测：未通过（{ErrorCodeMessages.Get(status.CheckError)}）");
        }

        if (status.UpdateAvailable)
        {
            Console.WriteLine(
                $"更新：有新版本 {status.UpdateVersion}"
                + (status.UpdateMandatory ? "（必须更新）" : "")
                + (status.UpdateMessage.Length > 0 ? $" · {status.UpdateMessage}" : ""));
        }
        else if (status.UpdateMessage.Length > 0)
        {
            Console.WriteLine($"更新：{status.UpdateMessage}");
        }

        Console.WriteLine($"开机自启：{(status.AutoStart ? "开" : "关")}   自动连接：{(status.AutoConnect ? "开" : "关")}");
    }

    private static string StateText(StatusSnapshot status) => status.State switch
    {
        AppState.Unbound => "未绑定",
        AppState.Disconnected => "已停止",
        AppState.Connecting => "正在连接",
        AppState.Connected => "已连接",
        AppState.Disconnecting => "正在停止",
        _ => "连接失败"
    };

    private static string Shorten(string value)
        => value.Length <= 12 ? value : value[..12] + "…";

    private static string Human(double bytesPerSecond)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytesPerSecond;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// 把守护进程拉起来并等它就绪。优先用 <c>setsid</c>：这样它脱离当前终端，
    /// 关掉 shell 不会把服务带走；没有 setsid 时退化成普通子进程。
    /// </summary>
    private static bool StartDaemon()
    {
        string executable = Environment.ProcessPath ?? "myproxy";
        bool hasSetsid = LinuxCommands.Exists("setsid");

        var startInfo = new ProcessStartInfo
        {
            FileName = hasSetsid ? "setsid" : executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (hasSetsid)
        {
            startInfo.ArgumentList.Add(executable);
        }

        startInfo.ArgumentList.Add("run");
        foreach (string argument in PassThroughOptions(Environment.GetCommandLineArgs()))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        // 等控制口就绪：启动流程里有崩溃收尾与更新恢复，给足时间但不要无限等。
        var client = new ControlClient();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (client.IsRunning())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static int TailLogs(string[] args)
    {
        int lines = 50;
        bool follow = false;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] is "-f" or "--follow")
            {
                follow = true;
            }
            else if (int.TryParse(args[index], out int parsed) && parsed > 0)
            {
                lines = parsed;
            }
        }

        string path = Path.Combine(LinuxPaths.LogDir, "app.log");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"还没有日志：{path}");
            return ExitFailure;
        }

        foreach (string line in ReadTail(path, lines))
        {
            Console.WriteLine(line);
        }

        if (!follow)
        {
            return ExitOk;
        }

        // 跟随：轮询文件长度。日志是给人看的，不值得为它引入 FileSystemWatcher。
        long position = new FileInfo(path).Length;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            Thread.Sleep(500);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length < position)
                {
                    position = 0;       // 日志轮转过
                }

                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    Console.WriteLine(line);
                }

                position = stream.Position;
            }
            catch (Exception)
            {
                // 读不到就等下一轮。
            }
        }

        return ExitOk;
    }

    private static string[] ReadTail(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var buffer = new Queue<string>(count + 1);
        while (reader.ReadLine() is { } line)
        {
            buffer.Enqueue(line);
            if (buffer.Count > count)
            {
                buffer.Dequeue();
            }
        }

        return buffer.ToArray();
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            MyProxy —— 本地内核与系统代理

            用法：myproxy <命令> [参数]

              bind <配对码>       用 8 位配对码把本机绑定到服务器
              unbind              解除绑定
              start               连接
              stop                断开（会先把系统代理恢复原样）
              status [--json]     查看当前状态
              mode smart|global   智能分流 / 全局代理
              check               检测连接：实测延迟并核对出口
              autostart on|off    开机（登录）自动启动后台服务
              autoconnect on|off  启动后自动连接
              theme classic|porcelain  界面皮肤（托盘程序用）
              update check|apply  检查 / 安装被指派的更新
              logs [行数] [-f]    查看日志
              run                 前台运行后台服务（systemd 用）
              version             版本

            后台服务是连接的唯一持有者；除 run 之外的命令都通过本机控制口与它通信。
            需要 root 的操作一个都没有。
            """);
    }
}
