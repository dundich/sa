namespace Sa.Outbox.Metadata;

/// <summary>
/// Builds metadata registrations for outbox message types, including part names and payload ID resolvers.
/// Metadata is used to route messages to the correct partitioned table and extract unique identifiers.
/// </summary>
public interface IOutboxMessageMetadataBuilder
{
    /// <summary>
    /// Registers metadata for a message type using an explicit part name and optional payload ID resolver.
    /// </summary>
    /// <typeparam name="TMessage">The message type to register metadata for.</typeparam>
    /// <param name="partName">The logical partition name associated with the message type (e.g., <c>"orders"</c>).</param>
    /// <param name="getPayloadId">An optional delegate to extract the unique payload identifier from a message.</param>
    /// <returns>The same <see cref="IOutboxMessageMetadataBuilder"/> instance for chaining.</returns>
    IOutboxMessageMetadataBuilder AddMetadata<TMessage>(
        string partName,
        Func<TMessage, string>? getPayloadId = null) where TMessage : class;

    /// <summary>
    /// Registers metadata for a message type that implements <see cref="IOutboxPublishable"/>,
    /// automatically deriving the part name and payload ID resolver from the type itself.
    /// </summary>
    /// <typeparam name="TMessage">A message type implementing <see cref="IOutboxPublishable"/>.</typeparam>
    /// <returns>The same <see cref="IOutboxMessageMetadataBuilder"/> instance for chaining.</returns>
    IOutboxMessageMetadataBuilder AddMetadata<TMessage>() where TMessage : class, IOutboxPublishable
    {
        return AddMetadata<TMessage>(TMessage.PartName, m => m.GetPayloadId());
    }
}
