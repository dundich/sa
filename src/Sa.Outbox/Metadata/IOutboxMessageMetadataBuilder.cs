namespace Sa.Outbox.Metadata;

public interface IOutboxMessageMetadataBuilder
{
    IOutboxMessageMetadataBuilder AddMetadata<TMessage>(string partName, Func<TMessage, string> getPayloadId)
        where TMessage : class;
}
