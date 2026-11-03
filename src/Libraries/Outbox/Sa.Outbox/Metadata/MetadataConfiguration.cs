namespace Sa.Outbox.Metadata;

internal sealed class MetadataConfiguration : IOutboxMessageMetadataBuilder, IOutboxMessageMetadataProvider
{
    private readonly Dictionary<Type, OutboxMessageMetadata> _metadata = [];


    public IOutboxMessageMetadataBuilder AddMetadata<T>(string partName, Func<T, string>? getPayloadId = null)
    {
        if (string.IsNullOrWhiteSpace(partName))
        {
            throw new ArgumentException(
                "Part name cannot be null or whitespace",
                nameof(partName));
        }

        // Keep the typed delegate: wrapping it in Func<object, string> boxed a value T on every
        // publish. A null means "use the IOutboxPublishable fallback", resolved by the metadata.
        _metadata[typeof(T)] = new OutboxMessageMetadata(partName, getPayloadId);

        return this;
    }


    public OutboxMessageMetadata GetMetadata(Type messageType)
    {
        if (_metadata.TryGetValue(messageType, out var metadata))
        {
            return metadata;
        }

        return OutboxMessageMetadata.Empty;
    }


    internal void MergeFrom(MetadataConfiguration other)
    {
        foreach (var cmeta in other._metadata)
        {
            _metadata[cmeta.Key] = cmeta.Value;
        }
    }
}
