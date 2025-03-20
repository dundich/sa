namespace Sa.StateMachine;


public abstract class SmEnumerable<TState>(ISmSettings<TState> settings) : IAsyncEnumerable<ISmContext<TState>>
    where TState : SmState<TState>
{

    sealed class SmEnumerator(ISmSettings<TState> settings, ISmProcessor<TState> processor, CancellationToken cancellationToken)
    : IAsyncEnumerator<ISmContext<TState>>, ISmContext<TState>
    {
        public TState CurrentState { get; private set; } = settings.StartState;
        public Exception? Error { get; private set; }
        public IReadOnlyCollection<TState> NextStates { get; private set; } = [];
        public CancellationToken CancellationToken => cancellationToken;

        ISmContext<TState> IAsyncEnumerator<ISmContext<TState>>.Current => this;

        public override string ToString() => $"{CurrentState}";

        async ValueTask<bool> IAsyncEnumerator<ISmContext<TState>>.MoveNextAsync()
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                NextStates = settings.Graph[CurrentState];

                TState nextState = await processor.MoveNext(this);

                if (!NextStates.Contains(nextState))
                    throw new ArgumentException($"Expected {NextStates} but found {nextState}");

                if (nextState.Kind == StateKind.Finish || settings.Graph.IsLeafNode(nextState))
                {
                    await processor.Finished(this);
                    return false;
                }

                CurrentState = nextState;
                return true;
            }
            catch (Exception error)
            {
                Error = error;
                CurrentState = settings.ErrorState;
            }

            return true;
        }

        ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
    }

    protected abstract ISmProcessor<TState> CreateProcessor();

    public virtual IAsyncEnumerator<ISmContext<TState>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new SmEnumerator(settings, CreateProcessor(), cancellationToken);


    public async virtual ValueTask<(TState state, Exception? error)> Run(CancellationToken cancellationToken = default)
    {
        var inumerator = GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await inumerator.MoveNextAsync())
            {
                // not used
            }

            return (inumerator.Current.CurrentState, inumerator.Current.Error);
        }
        finally
        {
            await inumerator.DisposeAsync();
        }
    }
}
