namespace DarkDesync.Core;

/// <summary>
/// State machine for the packet interception lifecycle.
/// Transitions: IDLE → ACTIVE → RESTORING → IDLE
/// Thread-safe via Interlocked operations.
/// </summary>
public sealed class DesyncStateMachine
{
    public enum State
    {
        /// <summary>No interception active. Ready for next cycle.</summary>
        Idle = 0,
        /// <summary>Actively dropping packets. Timer running.</summary>
        Active = 1,
        /// <summary>Timer expired. Restoring normal traffic flow.</summary>
        Restoring = 2,
    }

    private int _state = (int)State.Idle;

    /// <summary>Current state (thread-safe read).</summary>
    public State Current => (State)Volatile.Read(ref _state);

    /// <summary>Raised when the state changes. Args: (oldState, newState).</summary>
    public event Action<State, State>? StateChanged;

    /// <summary>
    /// Attempts to transition from Idle to Active.
    /// Returns false if not currently Idle (prevents re-entry).
    /// </summary>
    public bool TryActivate()
    {
        int prev = Interlocked.CompareExchange(ref _state, (int)State.Active, (int)State.Idle);
        if (prev == (int)State.Idle)
        {
            StateChanged?.Invoke(State.Idle, State.Active);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Transitions from Active to Restoring.
    /// Returns false if not currently Active.
    /// </summary>
    public bool TryBeginRestore()
    {
        int prev = Interlocked.CompareExchange(ref _state, (int)State.Restoring, (int)State.Active);
        if (prev == (int)State.Active)
        {
            StateChanged?.Invoke(State.Active, State.Restoring);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Transitions from Restoring to Idle.
    /// Returns false if not currently Restoring.
    /// </summary>
    public bool TryComplete()
    {
        int prev = Interlocked.CompareExchange(ref _state, (int)State.Idle, (int)State.Restoring);
        if (prev == (int)State.Restoring)
        {
            StateChanged?.Invoke(State.Restoring, State.Idle);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Force-resets to Idle from any state. Used for error recovery.
    /// </summary>
    public void ForceIdle()
    {
        var old = (State)Interlocked.Exchange(ref _state, (int)State.Idle);
        if (old != State.Idle)
        {
            StateChanged?.Invoke(old, State.Idle);
        }
    }
}
