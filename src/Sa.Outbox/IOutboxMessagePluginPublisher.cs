using Sa.Outbox.Support;

namespace Sa.Outbox;

/// <summary>
/// Defines a contract for publishing outbox messages.
/// </summary>
public interface IOutboxMessagePublisher
{
    /// <summary>
    /// Publishes a collection of messages.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published, which must implement <see cref="IOutboxPayloadMessage"/>.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation, 
    /// with the number of successfully published messages as the result.</returns>
    ValueTask<ulong> Publish<TMessage>(IReadOnlyCollection<TMessage> messages, CancellationToken cancellationToken = default)
        where TMessage : IOutboxPayloadMessage;

    /// <summary>
    /// Publishes a single message.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message to be published, which must implement <see cref="IOutboxPayloadMessage"/>.</typeparam>
    /// <param name="messages">The message to be published.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation, 
    /// with the number of successfully published messages as the result.</returns>
    ValueTask<ulong> Publish<TMessage>(TMessage messages, CancellationToken cancellationToken = default)
        where TMessage : IOutboxPayloadMessage => Publish([messages], cancellationToken);
}