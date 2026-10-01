using System.Threading;

namespace MyProxy.Services;

public sealed class SingleInstanceService : ISingleInstanceService, IDisposable
{
    private const string MutexName = @"Local\MyProxy_9F2A_0.1";
    private const string ActivateEventName = @"Local\MyProxy_9F2A_0.1_Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _listenerThread;
    private CancellationTokenSource? _cts;
    private bool _ownsMutex;

    public event Action? ActivateRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew || _mutex.WaitOne(0);
        if (!_ownsMutex)
        {
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _cts = new CancellationTokenSource();
        _listenerThread = new Thread(() => Listen(_cts.Token))
        {
            IsBackground = true
        };
        _listenerThread.Start();
        return true;
    }

    public void SignalActivate()
    {
        try
        {
            using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateEvent.Set();
        }
        catch
        {
            // 第二实例 Set 失败时直接退出，不打扰第一实例。
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_mutex is not null && _ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex?.Dispose();
        _activateEvent?.Dispose();
        _cts?.Dispose();
    }

    private void Listen(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _activateEvent is not null)
            {
                if (_activateEvent.WaitOne(500))
                {
                    ActivateRequested?.Invoke();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // 正在释放。
        }
        catch (AbandonedMutexException)
        {
            // 忽略。
        }
    }
}
