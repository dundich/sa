namespace Sa.StateMachine.Internal;

record SmSettings<TState>(
    TState StartState,
    TState ErrorState,
    IReadOnlyDictionary<TState, ITransition<TState>> Transitions,
    ITransitionGraph<TState> Graph
) : ISmSettings<TState> where TState : IComparable<TState>;
