namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// A processor that blocks inside <see cref="Execute"/> until released, ignoring the
/// cancellation token it is handed.
/// </summary>
/// <remarks>
/// This is the shape of item that makes a bounded wait expire: the reader is asked to
/// stop, and the work it is holding takes no notice. Shared by the timeout tests and
/// the force-cancel drain tests, which need the same thing for different reasons —
/// the first to prove the wait is bounded, the second to prove what survives it.
/// </remarks>
internal sealed class WedgedProcessor : ISaWork<int>
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _executions;

    /// <summary>Completes once an item is inside <see cref="Execute"/>.</summary>
    public Task Entered => _entered.Task;

    /// <summary>How many items have entered <see cref="Execute"/>.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>True once a released item has returned from <see cref="Execute"/>.</summary>
    public bool Left { get; private set; }

    public async Task Execute(int input, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        _entered.TrySetResult();

        // The token is accepted and then ignored, on purpose: honouring it is exactly
        // what would make this processor well-behaved.
        await _gate.Task.ConfigureAwait(false);

        Left = true;
    }

    /// <summary>Lets the wedged item — and any item after it — return.</summary>
    public void Release() => _gate.TrySetResult();
}
