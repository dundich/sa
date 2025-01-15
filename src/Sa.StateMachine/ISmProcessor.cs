namespace Sa.StateMachine;

public interface ISmProcessor<TState> where TState : IComparable<TState>
{
    ValueTask<TState> MoveNext(ISmContext<TState> context);
    ValueTask Finished(ISmContext<TState> context);
}
