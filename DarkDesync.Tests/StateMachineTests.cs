using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public class StateMachineTests
{
    [Fact]
    public void InitialState_IsIdle()
    {
        var sm = new DesyncStateMachine();
        Assert.Equal(DesyncStateMachine.State.Idle, sm.Current);
    }

    [Fact]
    public void StandardLifecycle_TransitionsCorrectly()
    {
        var sm = new DesyncStateMachine();
        var transitions = new List<(DesyncStateMachine.State Old, DesyncStateMachine.State New)>();
        sm.StateChanged += (o, n) => transitions.Add((o, n));

        // 1. TryActivate
        bool actOk = sm.TryActivate();
        Assert.True(actOk);
        Assert.Equal(DesyncStateMachine.State.Active, sm.Current);

        // 2. TryBeginRestore
        bool resOk = sm.TryBeginRestore();
        Assert.True(resOk);
        Assert.Equal(DesyncStateMachine.State.Restoring, sm.Current);

        // 3. TryComplete
        bool compOk = sm.TryComplete();
        Assert.True(compOk);
        Assert.Equal(DesyncStateMachine.State.Idle, sm.Current);

        Assert.Equal(3, transitions.Count);
        Assert.Equal((DesyncStateMachine.State.Idle, DesyncStateMachine.State.Active), transitions[0]);
        Assert.Equal((DesyncStateMachine.State.Active, DesyncStateMachine.State.Restoring), transitions[1]);
        Assert.Equal((DesyncStateMachine.State.Restoring, DesyncStateMachine.State.Idle), transitions[2]);
    }

    [Fact]
    public void TryActivate_WhenAlreadyActive_ReturnsFalse()
    {
        var sm = new DesyncStateMachine();
        Assert.True(sm.TryActivate());

        // Repeated press while active must be rejected
        Assert.False(sm.TryActivate());
        Assert.Equal(DesyncStateMachine.State.Active, sm.Current);
    }

    [Fact]
    public void TryActivate_WhenRestoring_ReturnsFalse()
    {
        var sm = new DesyncStateMachine();
        sm.TryActivate();
        sm.TryBeginRestore();

        // While restoring, new cycle must not start
        Assert.False(sm.TryActivate());
        Assert.Equal(DesyncStateMachine.State.Restoring, sm.Current);
    }

    [Fact]
    public void ConcurrentActivation_OnlyOneSucceeds()
    {
        var sm = new DesyncStateMachine();
        int successCount = 0;
        int failureCount = 0;

        Parallel.For(0, 50, _ =>
        {
            if (sm.TryActivate())
                Interlocked.Increment(ref successCount);
            else
                Interlocked.Increment(ref failureCount);
        });

        Assert.Equal(1, successCount);
        Assert.Equal(49, failureCount);
        Assert.Equal(DesyncStateMachine.State.Active, sm.Current);
    }

    [Fact]
    public void ForceIdle_ResetsStateFromAnywhere()
    {
        var sm = new DesyncStateMachine();
        sm.TryActivate();
        Assert.Equal(DesyncStateMachine.State.Active, sm.Current);

        sm.ForceIdle();
        Assert.Equal(DesyncStateMachine.State.Idle, sm.Current);

        // Can activate again after ForceIdle
        Assert.True(sm.TryActivate());
    }
}
