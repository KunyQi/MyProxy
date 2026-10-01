using System.Diagnostics;
using System.IO;

namespace MyProxy.Services;

public sealed class CrashRecoveryService : ICrashRecoveryService
{
    private readonly ISystemProxyService _proxy;
    private readonly IStorageService _storage;
    private readonly ILogService? _log;

    public CrashRecoveryService(ISystemProxyService proxy, IStorageService storage, ILogService? log = null)
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
            _log?.Error(nameof(CrashRecoveryService), "TryRecoverFromCrash failed", ex);
        }

        if (proxyRecoveryFailed)
        {
            // The current system proxy may still point at this process.  Keep
            // xray and its pid evidence alive so the next launch can retry
            // restoration instead of turning a recoverable state into a dead
            // local proxy endpoint.
            _log?.Warn(nameof(CrashRecoveryService), "系统代理恢复失败，保留 xray 进程与 pid 供下次重试");
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
                _log?.Warn(nameof(CrashRecoveryService), "xray.pid 内容无效，已删除");
                deletePid = true;
            }
            else
            {
                deletePid = TryStopRecordedXray(pid);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(CrashRecoveryService), $"读取 xray.pid 失败: {ex.Message}");
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
            _log?.Warn(nameof(CrashRecoveryService), $"删除 xray.pid 失败: {ex.Message}");
        }
    }

    private bool TryStopRecordedXray(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            string? actualExe = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(actualExe))
            {
                _log?.Warn(nameof(CrashRecoveryService), "无法确认 pid 对应的可执行文件，保留 xray.pid");
                return false;
            }

            if (!CoreAssets.IsOwnedXrayPath(_storage, actualExe))
            {
                // PID 已复用给其他程序，记录已失效但绝不终止该进程。
                return true;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _log?.Warn(nameof(CrashRecoveryService), $"终止孤儿 xray 失败: {ex.Message}");
                return false;
            }

            if (!process.WaitForExit(3000))
            {
                _log?.Warn(nameof(CrashRecoveryService), "等待孤儿 xray 退出超时，保留 xray.pid");
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(CrashRecoveryService), $"清理孤儿 xray 失败: {ex.Message}");
            return false;
        }
    }
}
