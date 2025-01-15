namespace Sa.StateMachine;

public interface ISettingsBuilder<TState> where TState : SmState<TState>
{
    ISettingsBuilder<TState> Add(ITransition<TState> transition);
    ISettingsBuilder<TState> Add(TState start, TState[] ends, object? tag = null);
    ISettingsBuilder<TState> Add(TState start, Func<TState[]> ends, object? tag = null) => Add(start, ends(), tag);
}
