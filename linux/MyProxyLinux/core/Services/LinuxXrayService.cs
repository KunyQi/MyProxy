using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// Linux 端的内核进程管理。
///
/// <para>
/// 与 Windows 端 <c>XrayService</c> 的职责相同（起进程、写 <c>xray.pid</c>、
/// 意外退出时触发 <see cref="Exited"/>、停止时保留 pid 供下次恢复），
/// 但有两处平台事实让实现更直白：
/// </para>
///
/// <list type="number">
/// <item><b>优雅停止真的能做到。</b>Linux 上用 <c>kill(pid, SIGTERM)</c>，
/// Windows 那边设计成 <c>CTRL_BREAK_EVENT</c> 却因为宿主没有控制台而从未生效、
/// 最终退化成强杀。这里没有控制台可附着的问题，所以顺序是
/// 「SIGTERM → 等 3 秒 → SIGKILL」。</item>
/// <item><b>不用 <c>prctl(PR_SET_PDEATHSIG)</c>。</b>让内核在父进程死掉时顺手杀掉
/// xray 看起来很干净，但它会破坏一条刻意留下的规矩：系统代理恢复失败时必须
/// <b>保留</b> xray 活着（代理指向一个死掉的本地端口 = 整机断网）。父进程死掉时
/// 谁也不知道代理恢复了没有，所以留一个孤儿 xray 加一份 pid 记录，交给下一次启动
/// 的崩溃恢复去判断，是唯一安全的做法。</item>
/// </list>
/// </summary>
public sealed class LinuxXrayService : IXrayService, IDisposable
{
    private const int Sigterm = 15;
    private const int GracefulStopTimeoutMs = 3000;
    private const int KillWaitTimeoutMs = 5000;

    private readonly IStorageService _storage;
    private readonly ILogService? _log;
    private readonly object _sync = new();
    private Process? _process;
    private bool _expectedStop;
    private bool _disposed;

    public LinuxXrayService(IStorageService storage, ILogService? log = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public event EventHandler<XrayExitEventArgs>? Exited;

    public event EventHandler<string>? OutputReceived;

    public Task StartAsync(string configPath, string workingDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        string xray = Path.Combine(workingDir, "xray");
        if (!File.Exists(xray))
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.XrayMissing), ErrorCode.XrayMissing);
        }

        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("xray 已经在运行。");
            }

            _expectedStop = false;
            Process process;
            try
            {
                process = StartProcess(xray, workingDir, configPath);
            }
            catch (MyProxyException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.XrayStartFailed), ErrorCode.XrayStartFailed, ex);
            }

            try
            {
                WritePidFile(process.Id);
            }
            catch (Exception ex)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略清理失败，保留原始异常。
                }

                _process = null;
                throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.XrayStartFailed), ErrorCode.XrayStartFailed, ex);
            }

            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnProcessExited(process);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        Process? process;
        lock (_sync)
        {
            process = _process;
            if (process is null || process.HasExited)
            {
                _process = null;
                DeletePidFile();
                return;
            }

            _expectedStop = true;
        }

        bool signalled = TrySignalTerm(process.Id);
        bool exited = signalled
            && await Task.Run(() => process.WaitForExit(GracefulStopTimeoutMs), ct).ConfigureAwait(false);

        if (!exited)
        {
            if (!signalled)
            {
                _log?.Warn(nameof(LinuxXrayService), $"SIGTERM 未能送达 xray（pid={process.Id}），直接强制结束");
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法终止 xray 进程，已保留 pid 供下次恢复。", ex);
            }

            exited = await Task.Run(() => process.WaitForExit(KillWaitTimeoutMs), ct).ConfigureAwait(false);
            if (!exited)
            {
                throw new InvalidOperationException("等待 xray 进程退出超时，已保留 pid 供下次恢复。");
            }
        }

        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }

        DeletePidFile();
    }

    public async Task RestartAsync(string configPath, string workingDir, CancellationToken ct)
    {
        await StopAsync(ct).ConfigureAwait(false);
        await StartAsync(configPath, workingDir, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Process? process;
        lock (_sync)
        {
            process = _process;
            _expectedStop = true;
        }

        bool exited = process is null || process.HasExited;
        if (!exited && process is not null)
        {
            try
            {
                _ = TrySignalTerm(process.Id);
                exited = process.WaitForExit(GracefulStopTimeoutMs)
                    || KillAndWait(process);
            }
            catch
            {
                exited = false;
            }
        }

        if (!exited)
        {
            // 保留进程引用与 pid，供下次启动的 CrashRecoveryService 继续处理。
            return;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }

        DeletePidFile();
    }

    private static bool KillAndWait(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            return false;
        }

        return process.WaitForExit(KillWaitTimeoutMs);
    }

    private string PidFilePath => Path.Combine(_storage.RuntimeDir, "xray.pid");

    private void WritePidFile(int pid)
    {
        Directory.CreateDirectory(_storage.RuntimeDir);
        LinuxFileSecurity.WriteAtomic(PidFilePath, pid.ToString());
    }

    private void DeletePidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
            {
                File.Delete(PidFilePath);
            }
        }
        catch
        {
            // pid 文件清理失败不能影响停止流程。
        }
    }

    private Process StartProcess(string executable, string workingDir, string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 不读 stdin：xray 不需要它，给它一个空的重定向可以避免它继承终端，
            // 否则在终端里 Ctrl+C 会同时打到我们和它。
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        Process? process = Process.Start(startInfo)
            ?? throw new Win32Exception("无法启动 xray 进程。");

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // 关不掉就算了，xray 不会去读它。
        }

        _ = Task.Run(() => PumpAsync(process.StandardOutput));
        _ = Task.Run(() => PumpAsync(process.StandardError));
        return process;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                OutputReceived?.Invoke(this, line);
            }
        }
        catch (Exception)
        {
            // 进程停止时管道关闭，属正常路径。
        }
    }

    private void OnProcessExited(Process process)
    {
        bool expected;
        lock (_sync)
        {
            expected = _expectedStop || !ReferenceEquals(_process, process);
        }

        if (expected)
        {
            return;
        }

        int exitCode = -1;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            // 进程句柄可能已失效。
        }

        Exited?.Invoke(this, new XrayExitEventArgs(exitCode, expected: false));
    }

    private static bool TrySignalTerm(int pid)
    {
        try
        {
            return kill(pid, Sigterm) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // DllImport 而不是 LibraryImport：后者需要 AllowUnsafeBlocks，本仓库两个客户端
    // 工程都不开 unsafe。
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
