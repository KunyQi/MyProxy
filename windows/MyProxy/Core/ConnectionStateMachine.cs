namespace MyProxy.Core;

public enum ConnectionStateEvent
{
    BindSuccess,
    Start,
    Success,
    Failure,
    StopRequest,
    Rebind,
    Retry,
    Stop,
    SwitchFailure,
    StopFailure,
    Done
}

public sealed class ConnectionStateMachine
{
    private AppState _current = AppState.Unbound;

    public AppState Current => _current;

    public event Action<AppState, AppState>? StateChanged;

    public AppState Transition(ConnectionStateEvent stateEvent)
    {
        AppState next = GetNextState(_current, stateEvent);
        AppState old = _current;
        _current = next;
        StateChanged?.Invoke(old, next);
        return next;
    }

    public bool TryTransition(ConnectionStateEvent stateEvent, out AppState nextState)
    {
        if (!IsLegalTransition(_current, stateEvent))
        {
            nextState = _current;
            return false;
        }

        nextState = Transition(stateEvent);
        return true;
    }

    public static bool IsLegalTransition(AppState current, ConnectionStateEvent stateEvent)
    {
        return current switch
        {
            AppState.Unbound => stateEvent == ConnectionStateEvent.BindSuccess,
            AppState.Disconnected => stateEvent == ConnectionStateEvent.Start || stateEvent == ConnectionStateEvent.Rebind,
            AppState.Connecting => stateEvent is ConnectionStateEvent.Success
                or ConnectionStateEvent.Failure
                or ConnectionStateEvent.StopRequest
                or ConnectionStateEvent.Rebind,
            AppState.Connected => stateEvent is ConnectionStateEvent.Stop
                or ConnectionStateEvent.SwitchFailure
                or ConnectionStateEvent.Rebind,
            AppState.Disconnecting => stateEvent is ConnectionStateEvent.Done
                or ConnectionStateEvent.StopFailure,
            AppState.Error => stateEvent is ConnectionStateEvent.Retry
                or ConnectionStateEvent.Stop
                or ConnectionStateEvent.Rebind,
            _ => false
        };
    }

    private static AppState GetNextState(AppState current, ConnectionStateEvent stateEvent)
    {
        if (!IsLegalTransition(current, stateEvent))
        {
            throw new InvalidOperationException(
                $"Illegal connection state transition: {current} -> {stateEvent}");
        }

        return (current, stateEvent) switch
        {
            (AppState.Unbound, ConnectionStateEvent.BindSuccess) => AppState.Disconnected,
            (AppState.Disconnected, ConnectionStateEvent.Start) => AppState.Connecting,
            (AppState.Connecting, ConnectionStateEvent.Success) => AppState.Connected,
            (AppState.Connecting, ConnectionStateEvent.Failure) => AppState.Error,
            (AppState.Connecting, ConnectionStateEvent.StopRequest) => AppState.Disconnecting,
            (AppState.Connecting, ConnectionStateEvent.Rebind) => AppState.Unbound,
            (AppState.Error, ConnectionStateEvent.Retry) => AppState.Connecting,
            (AppState.Error, ConnectionStateEvent.Stop) => AppState.Disconnecting,
            (AppState.Connected, ConnectionStateEvent.Stop) => AppState.Disconnecting,
            (AppState.Connected, ConnectionStateEvent.SwitchFailure) => AppState.Error,
            (AppState.Disconnecting, ConnectionStateEvent.Done) => AppState.Disconnected,
            (AppState.Disconnecting, ConnectionStateEvent.StopFailure) => AppState.Error,
            (AppState.Connected, ConnectionStateEvent.Rebind) => AppState.Unbound,
            (AppState.Disconnected, ConnectionStateEvent.Rebind) => AppState.Unbound,
            (AppState.Error, ConnectionStateEvent.Rebind) => AppState.Unbound,
            _ => throw new InvalidOperationException(
                $"Illegal connection state transition: {current} -> {stateEvent}")
        };
    }
}
