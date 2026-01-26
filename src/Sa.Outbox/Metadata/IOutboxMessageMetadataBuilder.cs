namespace Sa.Outbox.Metadata;

public interface IOutboxMessageMetadataBuilder
{
    IOutboxMessageMetadataBuilder WithMessageMetadata<TMessage>(string partName, Func<TMessage, string> getPayloadId)
        where TMessage : class;
}
