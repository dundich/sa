using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// The Redis implementation of <see cref="IOutboxDeliveryManager"/>: rent from a consumer group,
/// apply the batch outcome, renew the lease.
/// </summary>
internal sealed class OutboxDeliveryManager(
    RentDeliveryCommand rentCommand,
    FinishDeliveryCommand finishCommand,
    ExtendDeliveryCommand extendCommand) : IOutboxDeliveryManager
{
    public Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => rentCommand.ExecuteFillAsync(writeBuffer, lockDuration, filter, cancellationToken);

    public Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => finishCommand.ExecuteAsync(messages, filter, cancellationToken);

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => extendCommand.ExecuteAsync(lockExpiration, filter, cancellationToken);
}
