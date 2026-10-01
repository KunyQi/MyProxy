using System.IO;

namespace MyProxy.Services;

/// <summary>
/// 单实例：同一台机器上只允许一个进程持有连接。
///
/// <para>
/// Windows 端用命名互斥体 + 一个事件对象把已有窗口唤到前台。Linux 上没有
/// <c>SIGUSR1</c> 可用（.NET 的 <see cref="System.Runtime.InteropServices.PosixSignal"/>
/// 里没有它），所以「唤到前台」改成写一个请求文件：运行中的实例本来就在轮询状态，
/// 顺手看一眼这个文件即可。这比引入一个信号处理器更少意外——信号语义会与
/// 「reload」「terminate」这些约定俗成的含义打架。
/// </para>
///
/// <para>
/// 锁本身用 <c>FileShare.None</c> 打开一个文件：.NET 在 Unix 上是用
/// <c>flock(2)</c> 实现文件共享语义的，进程退出（包括被 SIGKILL）时内核会自动释放，
/// 不会像手写的 pid 文件那样留下一个永远解不开的假锁。
/// </para>
/// </summary>
public sealed class LinuxSingleInstanceService : ISingleInstanceService
{
    private readonly string _lockPath;
    private readonly string _activateRequestPath;
    private FileStream? _lock;
    private bool _disposed;

    public LinuxSingleInstanceService(IStorageService storage, string name = "instance")
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(storage.RuntimeDir);
        _lockPath = Path.Combine(storage.RuntimeDir, name + ".lock");
        _activateRequestPath = Path.Combine(storage.RuntimeDir, name + ".activate.request");
    }

    public event Action? ActivateRequested;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _lock = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            // 把自己也写进去：排查「谁占着锁」时不用去翻 /proc。
            _lock.SetLength(0);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString() + "\n" + DateTimeOffset.UtcNow.ToString("O") + "\n");
            _lock.Write(payload);
            _lock.Flush();
            LinuxFileSecurity.TryRestrict(_lockPath);
            return true;
        }
        catch (IOException)
        {
            // 已被别的进程用 flock 占住。
            _lock = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _lock = null;
            return false;
        }
    }

    /// <summary>第二个实例启动时调用：请求已有实例把窗口摆出来，然后自己退出。</summary>
    public void SignalActivate()
    {
        try
        {
            LinuxFileSecurity.WriteAtomic(
                _activateRequestPath,
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            // 唤不起来不是错误：用户再点一次图的图标就是了。
        }
    }

    /// <summary>
    /// 由持有锁的实例轮询调用：发现有新实例请求激活就触发一次
    /// <see cref="ActivateRequested"/>，并把请求文件删掉。
    /// </summary>
    public void PollActivateRequest()
    {
        try
        {
            if (!File.Exists(_activateRequestPath))
            {
                return;
            }

            File.Delete(_activateRequestPath);
        }
        catch (Exception)
        {
            return;
        }

        ActivateRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _lock?.Dispose();
        }
        catch (Exception)
        {
            // 释放失败没有补救动作：进程退出时内核会释放 flock。
        }

        _lock = null;

        try
        {
            if (File.Exists(_lockPath))
            {
                File.Delete(_lockPath);
            }
        }
        catch (Exception)
        {
            // 删不掉就留着，下次打开时会被重新占用。
        }
    }
}
