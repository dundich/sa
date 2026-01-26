namespace Sa.Outbox.Metadata;

internal interface IOutboxMessageMetadataProvider
{
    OutboxMessageMetadata GetMetadata(Type messageType);
    OutboxMessageMetadata GetMetadata<TMessage>() => GetMetadata(typeof(TMessage));
}
