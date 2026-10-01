using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using MyProxy.Core;

namespace MyProxy.Services;

public sealed class XrayService : IXrayService, IDisposable
{
    private const uint CtrlBreakEvent = 1;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int StdInputHandle = -10;
    private const int GracefulStopTimeoutMs = 3000;
    private const int KillWaitTimeoutMs = 5000;

    private readonly IStorageService _storage;
    private readonly ILogService? _log;
    private readonly object _sync = new();
    private Process? _process;
    private bool _expectedStop;
    private bool _disposed;

    public XrayService(IStorageService storage, ILogService? log = null)
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

        string xrayExe = Path.Combine(workingDir, "xray.exe");
        if (!File.Exists(xrayExe))
        {
            throw new MyProxyException("程序组件缺失，请重新下载", ErrorCode.XrayMissing);
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
                process = StartProcess(xrayExe, workingDir, $"run -c \"{configPath}\"");
            }
            catch (MyProxyException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.XrayStartFailed), ErrorCode.XrayStartFailed, ex);
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
                throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.XrayStartFailed), ErrorCode.XrayStartFailed, ex);
            }

            _process = process;
            process.Exited += (_, _) => OnProcessExited(process);
            process.EnableRaisingEvents = true;
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

        bool signalled = TrySignalGracefulStop(process);
        if (!signalled)
        {
            _log?.Warn(
                nameof(XrayService),
                $"CTRL_BREAK 未能送达 xray（pid={process.Id}）：{_lastSignalDiagnostic}，直接强制结束");
        }

        // 信号没送出去，xray 就不会自己退：再等 3 秒只是让切模式、停止、关机时的收尾
        // 每次白白多卡 3 秒（xray 没有需要落盘的状态，强杀与优雅退出对它没有区别）。
        bool exited = signalled
            && await Task.Run(() => process.WaitForExit(GracefulStopTimeoutMs), ct).ConfigureAwait(false);
        if (!exited)
        {
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
                process.Kill(entireProcessTree: true);
                exited = process.WaitForExit(KillWaitTimeoutMs);
            }
            catch
            {
                exited = false;
            }
        }

        if (!exited)
        {
            // 保留进程引用和 pid，供下次启动的 CrashRecoveryService 继续处理。
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

    private string PidFilePath => Path.Combine(_storage.RuntimeDir, "xray.pid");

    private void WritePidFile(int pid)
    {
        Directory.CreateDirectory(_storage.RuntimeDir);
        string tmpPath = PidFilePath + ".tmp";
        File.WriteAllText(tmpPath, pid.ToString(), new UTF8Encoding(false));
        File.Move(tmpPath, PidFilePath, overwrite: true);
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

    private Process StartProcess(string exePath, string workingDir, string arguments)
    {
        var stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = StartfUseStdHandles,
            hStdInput = GetStdHandle(StdInputHandle),
            hStdOutput = stdoutPipe.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = stderrPipe.ClientSafePipeHandle.DangerousGetHandle()
        };

        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

        bool created;
        PROCESS_INFORMATION processInfo = default;
        try
        {
            created = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateNewProcessGroup | CreateNoWindow,
                IntPtr.Zero,
                workingDir,
                ref startupInfo,
                out processInfo);
        }
        catch
        {
            stdoutPipe.Dispose();
            stderrPipe.Dispose();
            throw;
        }

        if (!created)
        {
            stdoutPipe.Dispose();
            stderrPipe.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 xray.exe。");
        }

        stdoutPipe.DisposeLocalCopyOfClientHandle();
        stderrPipe.DisposeLocalCopyOfClientHandle();

        Process process;
        try
        {
            process = Process.GetProcessById(processInfo.dwProcessId);
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }

        _ = Task.Run(() => ReadLinesAsync(stdoutPipe, process));
        _ = Task.Run(() => ReadLinesAsync(stderrPipe, process));

        return process;
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

    private async Task ReadLinesAsync(AnonymousPipeServerStream pipe, Process process)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                OutputReceived?.Invoke(this, line);
            }
        }
        catch (ObjectDisposedException)
        {
            // 进程停止时管道关闭。
        }
        catch (IOException)
        {
            // 进程停止时管道关闭。
        }
        catch (InvalidOperationException)
        {
            // 读取端已关闭。
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>
    /// 尽力向 xray 发 CTRL_BREAK，并返回信号是否真的送出去了。
    /// </summary>
    /// <remarks>
    /// GenerateConsoleCtrlEvent 要求与目标进程组共享控制台，而宿主是 WinExe
    /// （<c>MyProxy.csproj</c> 的 OutputType）、自身没有控制台，所以必须先附着到
    /// 子进程的控制台。实测（xray 26.3.27）AttachConsole 返回 ERROR_INVALID_HANDLE：
    /// 子进程以 CREATE_NO_WINDOW 启动、父进程又无控制台、STARTUPINFO 传的是重定向
    /// 句柄，Windows 根本没有给它分配控制台，因此没有可附着的对象。
    /// <para>
    /// 结论：当前进程创建方式下优雅停止不可能生效。信号送不出去时 StopAsync 直接强杀，
    /// 不再空等 <see cref="GracefulStopTimeoutMs"/>（以前每次 Stop/Restart 都白等 3 秒）；
    /// 失败照样记日志——原来 BOOL 返回值被直接丢弃，失败完全无声。真正的优雅停止
    /// 需要换停止信号（例如给 xray 一个可控的退出通道），属于设计变更。
    /// </para>
    /// </remarks>
    private static string _lastSignalDiagnostic = "";

    private static bool TrySignalGracefulStop(Process process)
    {
        // 已经附着在别的控制台上时 AttachConsole 会失败（ERROR_ACCESS_DENIED）。
        // WinExe 宿主本来就没有控制台，这一步是空操作；测试宿主则需要它。
        FreeConsole();
        if (!AttachConsole((uint)process.Id))
        {
            _lastSignalDiagnostic = $"AttachConsole failed, err={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            // 附着之后本进程也在这个控制台上，会收到自己发出的事件，
            // 先装上「忽略」处理器再发送。
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            bool sent = GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)process.Id);
            if (!sent)
            {
                _lastSignalDiagnostic = $"GenerateConsoleCtrlEvent failed, err={Marshal.GetLastWin32Error()}";
            }

            return sent;
        }
        finally
        {
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
