using System.Diagnostics;
using System.IO;

namespace MyProxy.Services;

/// <summary>
/// 起外部命令的最小工具：找命令、跑命令。
///
/// <para>
/// 抽出来是因为有三处要用它（系统代理写 gsettings/kwriteconfig、自启调 systemctl、
/// CLI 找 setsid），而每处各写一份 PATH 查找只会让「命令存不存在」的判断在三个
/// 地方慢慢长歪。
/// </para>
///
/// <para>
/// 全部走 <see cref="ProcessStartInfo.ArgumentList"/> + <c>UseShellExecute = false</c>：
/// 不经 shell，也就没有把参数里的引号、分号、<c>$()</c> 变成一次命令注入的机会。
/// </para>
/// </summary>
public static class LinuxCommands
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, bool> ExistenceCache = new(StringComparer.Ordinal);

    public static bool Exists(string command)
    {
        lock (Sync)
        {
            if (ExistenceCache.TryGetValue(command, out bool known))
            {
                return known;
            }

            bool found = false;
            string? path = Environment.GetEnvironmentVariable("PATH");
            foreach (string directory in (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, command)))
                    {
                        found = true;
                        break;
                    }
                }
                catch (Exception)
                {
                    // 目录名里有非法字符就跳过。
                }
            }

            ExistenceCache[command] = found;
            return found;
        }
    }

    /// <summary>测试用：清掉「命令存不存在」的缓存（PATH 在同一进程里不会变，正常不需要）。</summary>
    internal static void ResetCache()
    {
        lock (Sync)
        {
            ExistenceCache.Clear();
        }
    }

    /// <summary>
    /// 跑一条命令并等它结束；起不来或超时返回 <c>null</c>。
    ///
    /// <para>
    /// 两路输出都<b>异步</b>读，超时由 <c>WaitForExit</c> 与读流共同受同一个期限约束。
    /// 先同步 <c>ReadToEnd</c> 再 <c>WaitForExit</c> 的写法看着有超时，其实没有：
    /// 读 stdout 会一直阻塞到子进程关掉它（通常就是退出），一个卡在 D-Bus 上的
    /// gsettings 能把调用方（例如停止流程里的代理恢复）永远挂住；而先读完 stdout
    /// 再读 stderr，子进程写满 stderr 的管道时两边还会互相等死。
    /// </para>
    /// </summary>
    public static (int ExitCode, string StandardOutput)? Run(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var clock = Stopwatch.StartNew();
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Remaining(timeout, clock)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 超时的进程杀不掉也只能放弃；调用方按「命令失败」处理。
                }

                return null;
            }

            // 进程退出了，但管道可能还被它留下的子孙进程握着：读流同样受期限约束。
            if (!Task.WaitAll(new Task[] { output, errors }, Remaining(timeout, clock)))
            {
                return null;
            }

            return (process.ExitCode, output.Result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Remaining(TimeSpan timeout, Stopwatch clock)
        => (int)Math.Clamp((timeout - clock.Elapsed).TotalMilliseconds, 0, int.MaxValue);
}
