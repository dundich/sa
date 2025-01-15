namespace Sa.StateMachine;

public interface ISmContext<out TState> where TState : IComparable<TState>
{
    TState CurrentState { get; }
    Exception? Error { get; }
    IReadOnlyCollection<TState> NextStates { get; }
    CancellationToken CancellationToken { get; }
}
