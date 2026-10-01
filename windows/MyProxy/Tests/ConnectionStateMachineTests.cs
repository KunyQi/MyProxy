using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

[TestClass]
public sealed class ConnectionStateMachineTests
{
    [TestMethod]
    public void InitialState_IsUnbound()
    {
        var machine = new ConnectionStateMachine();
        Assert.AreEqual(AppState.Unbound, machine.Current);
    }

    [TestMethod]
    public void AllLegalTransitions_ProduceExpectedStates()
    {
        Assert.AreEqual(AppState.Disconnected, TransitionFrom(AppState.Unbound, ConnectionStateEvent.BindSuccess));
        Assert.AreEqual(AppState.Connecting, TransitionFrom(AppState.Disconnected, ConnectionStateEvent.Start));
        Assert.AreEqual(AppState.Connected, TransitionFrom(AppState.Connecting, ConnectionStateEvent.Success));
        Assert.AreEqual(AppState.Error, TransitionFrom(AppState.Connecting, ConnectionStateEvent.Failure));
        Assert.AreEqual(AppState.Disconnecting, TransitionFrom(AppState.Connecting, ConnectionStateEvent.StopRequest));
        Assert.AreEqual(AppState.Unbound, TransitionFrom(AppState.Connecting, ConnectionStateEvent.Rebind));
        Assert.AreEqual(AppState.Connecting, TransitionFrom(AppState.Error, ConnectionStateEvent.Retry));
        Assert.AreEqual(AppState.Disconnecting, TransitionFrom(AppState.Error, ConnectionStateEvent.Stop));
        Assert.AreEqual(AppState.Disconnecting, TransitionFrom(AppState.Connected, ConnectionStateEvent.Stop));
        Assert.AreEqual(AppState.Error, TransitionFrom(AppState.Connected, ConnectionStateEvent.SwitchFailure));
        Assert.AreEqual(AppState.Disconnected, TransitionFrom(AppState.Disconnecting, ConnectionStateEvent.Done));
        Assert.AreEqual(AppState.Error, TransitionFrom(AppState.Disconnecting, ConnectionStateEvent.StopFailure));
        Assert.AreEqual(AppState.Unbound, TransitionFrom(AppState.Connected, ConnectionStateEvent.Rebind));
        Assert.AreEqual(AppState.Unbound, TransitionFrom(AppState.Disconnected, ConnectionStateEvent.Rebind));
        Assert.AreEqual(AppState.Unbound, TransitionFrom(AppState.Error, ConnectionStateEvent.Rebind));
    }

    [TestMethod]
    public void StateChangedEvent_RaisesWithOldAndNewState()
    {
        var machine = new ConnectionStateMachine();
        (AppState oldState, AppState newState) = (AppState.Unbound, AppState.Unbound);
        machine.StateChanged += (o, n) => (oldState, newState) = (o, n);

        machine.Transition(ConnectionStateEvent.BindSuccess);

        Assert.AreEqual(AppState.Unbound, oldState);
        Assert.AreEqual(AppState.Disconnected, newState);
    }

    [TestMethod]
    public void IllegalTransitions_Throw()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => new ConnectionStateMachine().Transition(ConnectionStateEvent.Start));
        Assert.ThrowsException<InvalidOperationException>(
            () => TransitionFrom(AppState.Disconnected, ConnectionStateEvent.Success));
        Assert.ThrowsException<InvalidOperationException>(
            () => TransitionFrom(AppState.Disconnecting, ConnectionStateEvent.Start));
        Assert.ThrowsException<InvalidOperationException>(
            () => TransitionFrom(AppState.Unbound, ConnectionStateEvent.Retry));
    }

    [TestMethod]
    public void TryTransition_Illegal_ReturnsFalseAndKeepsState()
    {
        var machine = new ConnectionStateMachine();
        bool result = machine.TryTransition(ConnectionStateEvent.Start, out AppState nextState);

        Assert.IsFalse(result);
        Assert.AreEqual(AppState.Unbound, nextState);
        Assert.AreEqual(AppState.Unbound, machine.Current);
    }

    private static AppState TransitionFrom(AppState current, ConnectionStateEvent stateEvent)
    {
        var machine = new ConnectionStateMachine();

        // 先通过合法路径把状态机移动到指定 current。
        switch (current)
        {
            case AppState.Unbound:
                break;
            case AppState.Disconnected:
                machine.Transition(ConnectionStateEvent.BindSuccess);
                break;
            case AppState.Connecting:
                machine.Transition(ConnectionStateEvent.BindSuccess);
                machine.Transition(ConnectionStateEvent.Start);
                break;
            case AppState.Connected:
                machine.Transition(ConnectionStateEvent.BindSuccess);
                machine.Transition(ConnectionStateEvent.Start);
                machine.Transition(ConnectionStateEvent.Success);
                break;
            case AppState.Disconnecting:
                machine.Transition(ConnectionStateEvent.BindSuccess);
                machine.Transition(ConnectionStateEvent.Start);
                machine.Transition(ConnectionStateEvent.StopRequest);
                break;
            case AppState.Error:
                machine.Transition(ConnectionStateEvent.BindSuccess);
                machine.Transition(ConnectionStateEvent.Start);
                machine.Transition(ConnectionStateEvent.Failure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(current), current, null);
        }

        return machine.Transition(stateEvent);
    }
}
