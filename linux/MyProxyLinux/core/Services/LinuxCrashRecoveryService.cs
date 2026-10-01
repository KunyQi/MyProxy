using System.Diagnostics;
using System.IO;

namespace MyProxy.Services;

/// <summary>
/// 启动时的崩溃收尾：先把系统代理恢复原样，确认成功之后才清理孤儿 xray。
///
/// <para>
/// 与 Windows 端 <c>CrashRecoveryService</c> 的顺序完全一致，理由也一样：
/// 代理恢复失败时<b>不能</b>杀掉 xray——系统代理还指着那个本地端口，杀了它等于
/// 把用户的网络彻底断掉。这时保留 xray 与 <c>xray.pid</c>，把状态留给下一次启动
/// 或者用户自己处理。
/// </para>
///
/// <para>
/// 清孤儿进程的判据比 Windows 端更严格一点：<b>既要比路径，也要比哈希</b>。
/// Linux 上 PID 复用极快（默认 <c>pid_max</c> 是 4194304，但在容器与长跑机器上
/// 复用依然常见），只按「进程名是 xray」或「路径在 Core 下」都可能打到一个
/// 恰好也叫 xray 的无关进程上。见 <see cref="CoreAssets.IsOwnedXrayPath"/>。
/// </para>
/// </summary>
public sealed class LinuxCrashRecoveryService : ICrashRecoveryService
{
    private readonly ISystemProxyService _proxy;
    private readonly IStorageService _storage;
    private readonly ILogService? _log;

    public LinuxCrashRecoveryService(
        ISystemProxyService proxy,
        IStorageService storage,
        ILogService? log = null)
    {
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log;
    }

    public bool Run()
    {
        bool recoveredProxy = false;
        bool proxyRecoveryFailed = false;

        try
        {
            recoveredProxy = _proxy.TryRecoverFromCrash();
        }
        catch (Exception ex)
        {
            proxyRecoveryFailed = true;
            _log?.Error(nameof(LinuxCrashRecoveryService), "TryRecoverFromCrash failed", ex);
        }

        if (proxyRecoveryFailed)
        {
            _log?.Warn(
                nameof(LinuxCrashRecoveryService),
                "系统代理恢复失败，保留 xray 进程与 pid 供下次重试");
        }
        else
        {
            CleanupOrphanXray();
        }

        return recoveredProxy;
    }

    private void CleanupOrphanXray()
    {
        string pidPath = Path.Combine(_storage.RuntimeDir, "xray.pid");
        if (!File.Exists(pidPath))
        {
            return;
        }

        bool deletePid = false;
        try
        {
            string pidText = File.ReadAllText(pidPath).Trim();
            if (!int.TryParse(pidText, out int pid) || pid <= 0)
            {
                _log?.Warn(nameof(LinuxCrashRecoveryService), "xray.pid 内容无效，已删除");
                deletePid = true;
            }
            else
            {
                deletePid = TryStopRecordedXray(pid);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxCrashRecoveryService), $"读取 xray.pid 失败: {ex.Message}");
        }

        if (!deletePid)
        {
            return;
        }

        try
        {
            File.Delete(pidPath);
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxCrashRecoveryService), $"删除 xray.pid 失败: {ex.Message}");
        }
    }

    private bool TryStopRecordedXray(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            // Linux 上 MainModule.FileName 读的是 /proc/<pid>/exe。读不到（进程属于
            // 别的用户、或已经退出）时留证据不动手。
            string? actualPath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(actualPath))
            {
                _log?.Warn(nameof(LinuxCrashRecoveryService), "无法确认 pid 对应的可执行文件，保留 xray.pid");
                return false;
            }

            if (!CoreAssets.IsOwnedXrayPath(_storage, actualPath))
            {
                // PID 已被复用给别的程序：记录已失效，但绝不终止那个进程。
                _log?.Info(nameof(LinuxCrashRecoveryService), $"pid {pid} 不是本程序的内核，保留不动");
                return true;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _log?.Warn(nameof(LinuxCrashRecoveryService), $"终止孤儿 xray 失败: {ex.Message}");
                return false;
            }

            if (!process.WaitForExit(3000))
            {
                _log?.Warn(nameof(LinuxCrashRecoveryService), "等待孤儿 xray 退出超时，保留 xray.pid");
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            // 进程不存在：记录本身就是残留。
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxCrashRecoveryService), $"清理孤儿 xray 失败: {ex.Message}");
            return false;
        }
    }
}
