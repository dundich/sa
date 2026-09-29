namespace Sa.Utils.WorkQueue;

using System.Threading.Channels;

/// <summary>
/// The producing half of the queue: turning a caller's item into a channel write
/// and deciding what a refusal means.
/// </summary>
/// <remarks>
/// Every path raises the pending count with <see cref="MarkActive"/> <em>before</em>
/// attempting the write, because a refusal is not known until the write is tried.
/// The count therefore always has exactly one matching
/// <see cref="RejectEnqueue"/> or <see cref="EnqueueWaitingAsync"/> on the failure
/// branch — that pairing is what keeps <see cref="SaWorkQueue{TInput}.IsIdle"/>
/// honest.
/// </remarks>
public sealed partial class SaWorkQueue<TInput>
{
    /// <inheritdoc />
    public async ValueTask<bool> Enqueue(TInput input, CancellationToken cancellationToken = default)
    {
        ThrowIfNotActive();

        var item = new WorkItem(input, cancellationToken);

        if (_enqueueStrategy != SaEnqueueStrategy.Wait)
        {
            MarkActive();

            if (_queue.Writer.TryWrite(item))
            {
                return true;
            }

            RejectEnqueue(item);
            return false;
        }

        return await EnqueueWaitingAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> EnqueueMany(IEnumerable<TInput> inputs, CancellationToken cancellationToken = default)
    {
        ThrowIfNotActive();

        var accepted = 0;
        var attempted = 0;

        foreach (var input in inputs)
        {
            attempted++;
            var item = new WorkItem(input, cancellationToken);

            if (_enqueueStrategy == SaEnqueueStrategy.Wait)
            {
                // Handles its own accounting and rethrows on failure, so the only
                // way to get here is success.
                if (await EnqueueWaitingAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    accepted++;
                }

                continue;
            }

            MarkActive();

            if (_queue.Writer.TryWrite(item))
            {
                accepted++;
                continue;
            }

            RejectEnqueue(item, accepted, attempted);
        }

        return accepted;
    }

    /// <inheritdoc />
    public bool TryEnqueue(TInput input)
    {
        ThrowIfNotActive();

        var item = new WorkItem(input, default);

        MarkActive();

        if (_queue.Writer.TryWrite(item))
        {
            return true;
        }

        // A full buffer is false here, whatever the strategy says — that is what the
        // name promises, and a caller reaching for it wants a bool rather than an
        // exception. So this deliberately skips RejectEnqueue's Throw branch. The
        // pending count still has to be given back, and a queue that stopped
        // underneath us is still worth reporting as stopped.
        MarkInactive();

        if (!IsEnabled)
        {
            ThrowHelper.QueueStopped();
        }

        OnStatusChanged(item.Input, SaWorkStatus.Skipped);
        return false;
    }

    /// <summary>
    /// The blocking write, used when the queue is configured with
    /// <see cref="SaEnqueueStrategy.Wait"/>.
    /// </summary>
    /// <remarks>
    /// A producer parked here is released by three things, and only three: the
    /// buffer gaining room (a reader taking an item, or the caller raising
    /// <see cref="ConcurrencyLimit"/>), <c>Shutdown</c> completing the writer,
    /// and the caller's own token.
    /// </remarks>
    private async ValueTask<bool> EnqueueWaitingAsync(WorkItem item, CancellationToken cancellationToken)
    {
        MarkActive();

        try
        {
            await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // The item never reached the channel, so it must not stay counted.
            MarkInactive();

            // Completing the writer is how a shutdown releases a parked producer.
            // That is a stopped queue, not a broken one, and the caller can act on
            // it — so translate it instead of letting a channel exception escape.
            // The cause travels along, because "ChannelClosedException" on its own
            // says nothing about why the queue stopped.
            if (ex is ChannelClosedException or IOException or InvalidOperationException)
            {
                ThrowHelper.QueueStopped(ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Handles an item the channel refused: takes back the pending count that
    /// <see cref="MarkActive"/> raised, then reports the refusal according to the
    /// configured strategy. Does not return — it either throws or reports the item
    /// as <see cref="SaWorkStatus.Skipped"/>.
    /// </summary>
    /// <param name="item">The refused item.</param>
    /// <param name="accepted">
    /// How many items a batch had already taken, for the message
    /// <see cref="EnqueueMany"/> raises. <see langword="null"/> for the
    /// single-item entry points, which name the item instead.
    /// </param>
    /// <param name="attempted">
    /// How many items a batch had tried in total. Must be passed together with
    /// <paramref name="accepted"/>.
    /// </param>
    private void RejectEnqueue(WorkItem item, int? accepted = null, int? attempted = null)
    {
        MarkInactive();

        // A full channel is only one of the two reasons the write failed. The other
        // is a shutdown that completed the writer between our state check and the
        // write; reporting "full" there would send the caller off to fix the wrong
        // thing.
        if (!IsEnabled)
        {
            ThrowHelper.QueueStopped();
        }

        if (_enqueueStrategy == SaEnqueueStrategy.Throw)
        {
            throw FullException(item, accepted, attempted);
        }

        OnStatusChanged(item.Input, SaWorkStatus.Skipped);
    }

    /// <summary>
    /// Builds the "buffer is full" exception, in the shape the caller can use: a
    /// batch reports how far it got, a single enqueue names the item that missed.
    /// </summary>
    private Exception FullException(WorkItem item, int? accepted, int? attempted)
        => attempted is null
            ? new SaWorkQueueFullException(_queueCapacity, _queue.Reader.Count, _getItemDisplayName(item.Input))
            : new SaWorkQueueFullException(_queueCapacity, _queue.Reader.Count, accepted!.Value, attempted.Value);
}
