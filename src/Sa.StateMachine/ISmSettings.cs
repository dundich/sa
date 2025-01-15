namespace Sa.StateMachine;

public interface ISmSettings<TState> where TState : IComparable<TState>
{
    TState StartState { get; }
    TState ErrorState { get; }
    ITransitionGraph<TState> Graph { get; }
    IReadOnlyDictionary<TState, ITransition<TState>> Transitions { get; }
}
