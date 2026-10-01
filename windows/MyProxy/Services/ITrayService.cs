using MyProxy.Core;

namespace MyProxy.Services;

public interface ITrayService : IDisposable
{
    void Show();
    void ShowBackgroundHint();
    void UpdateState(AppState state, ProxyMode mode);
    event Action? OpenRequested;
    event Action? StopRequested;
    event Action? ExitRequested;
}
