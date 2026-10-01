namespace MyProxy.Services;

public interface ISingleInstanceService : IDisposable
{
    bool TryAcquire();
    void SignalActivate();
    event Action? ActivateRequested;
}
