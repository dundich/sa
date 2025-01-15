namespace Sa.StateMachine;

public interface ITransition<out TState> where TState : IComparable<TState>
{
    TState Start { get; }
    TState[] End { get; }
    object? Tag { get; }
}
