namespace Sa.Outbox.Metadata;

public interface IOutboxMessageMetadataBuilder
{
    IOutboxMessageMetadataBuilder AddMetadata<TMessage>(
        string partName,
        Func<TMessage, string>? getPayloadId = null) where TMessage : class;

    IOutboxMessageMetadataBuilder AddMetadata<TMessage>() where TMessage : class, IOutboxPublishable
    {
        return AddMetadata<TMessage>(TMessage.PartName, m => m.GetPayloadId());
    }
}
