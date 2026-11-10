namespace Sa.Utils.WorkQueue;

/// <summary>
/// Adapter from a processing delegate to <see cref="ISaWork{TInput}"/>, so a caller can
/// queue a lambda instead of declaring a class.
/// </summary>
/// <remarks>
/// Shared by the two entry points that accept a delegate —
/// <see cref="SaWorkQueueOptions.Create{TInput}(Func{TInput, CancellationToken, Task})"/>
/// and <see cref="IWorkQueueBuilder{TInput}.WithProcess"/> — rather than nested privately in
/// each. Both used to declare their own identical copy, which meant a change to the adapter
/// had two places to be made and one of them could be missed silently.
/// </remarks>
/// <param name="process">The delegate that processes a single item.</param>
internal sealed class DelegatingWork<TInput>(Func<TInput, CancellationToken, Task> process) : ISaWork<TInput>
{
    /// <inheritdoc />
    public Task Execute(TInput input, CancellationToken cancellationToken)
        => process(input, cancellationToken);
}
