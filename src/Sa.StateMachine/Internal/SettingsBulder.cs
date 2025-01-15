namespace Sa.StateMachine.Internal;


class SettingsBulder<TState>() : ISettingsBuilder<TState> where TState : SmState<TState>
{
    #region ITransition
    protected class Transition(TState start, TState[] end, object? tag = null) : ITransition<TState>
    {
        public TState Start => start;
        public TState[] End => end;
        public object? Tag => tag;
    }
    #endregion

    private readonly Dictionary<TState, ITransition<TState>> _transitions = [];

    public ISettingsBuilder<TState> Add(ITransition<TState> transition)
    {
        _transitions[transition.Start] = transition;
        return this;
    }

    public ISettingsBuilder<TState> Add(TState start, TState[] ends, object? tag = null)
        => Add(new Transition(start, ends, tag));

    private TransitionGraph<TState> BuildGraph()
        => new(_transitions.Values.SelectMany(c => c.End.Select(end => (c.Start, end))));

    public SmSettings<TState> Build()
    {
        TransitionGraph<TState> graph = BuildGraph();

        bool reload = false;
        // bind transition from start
        foreach (var cRoot in graph.Roots.Where(c => c.Kind != StateKind.Start))
        {
            reload = true;
            Add(SmState<TState>.Start, [cRoot]);
        }

        TState endState = graph.Leaves.FirstOrDefault(c => c.Kind == StateKind.Finish) ?? SmState<TState>.Finish;

        // bind transition to end
        IEnumerable<TState> noEnds = graph.Leaves.Where(c => c.Kind != StateKind.Finish);
        foreach (TState enode in noEnds)
        {
            Add(enode, [endState]);
            reload = true;
        }

        if (reload) graph = BuildGraph();

        return new(SmState<TState>.Start, SmState<TState>.Error, _transitions, graph);
    }
}
